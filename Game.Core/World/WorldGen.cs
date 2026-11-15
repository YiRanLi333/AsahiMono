using System;
using System.Collections.Generic;

namespace AsahiMono.Core.World;

/// <summary>
/// 世界生成参数。<see cref="Seed"/> 相同 + 其余参数相同 ⇒ 逐格完全一致的同一张图。
/// </summary>
/// <param name="Seed">世界种子。</param>
/// <param name="Width">宽（格）。</param>
/// <param name="Height">高（格）。</param>
/// <param name="WaterLevel">0..1，越大水越多：高度小于它的格子变水。</param>
/// <param name="TreeDensity">0..1，树的平均密度。</param>
/// <param name="RockDensity">0..1，石头的平均密度。</param>
public sealed record WorldGenOptions(
    int Seed,
    int Width = 96,
    int Height = 96,
    double WaterLevel = 0.30,
    double TreeDensity = 0.08,
    double RockDensity = 0.03);

/// <summary>
/// 世界生成器：纯 C#、无引擎依赖、<b>跨 .NET 版本与平台确定</b>
/// （不用 BCL 自带的随机数类、不读时钟、不读环境）。
///
/// 流程：
/// <list type="number">
///   <item>两张 4 octave 的低频 value noise（高度、湿度）→ 水 / 沙 / 草地。</item>
///   <item>外围 2 格强制草地，出生点不会贴边。</item>
///   <item>按密度 + 噪声撒树 / 岩石 / 杂草（只撒草地，树四邻不再撒树）。</item>
///   <item>中心螺旋搜索出生点：周围 5×5 必须全部可站。</item>
///   <item>连通性补偿：从出生点 BFS，若可达面积不足可站格的 90%，
///         就把每个孤岛各挖一条走廊接到出生点，直到达标。</item>
/// </list>
/// </summary>
public static class WorldGen
{
    /// <summary>外围几格强制草地。</summary>
    private const int Border = 2;

    /// <summary>水位向外多宽的沙带（相对高度值）。</summary>
    private const double SandBand = 0.05;

    /// <summary>出生点要求的可站方块半径（2 ⇒ 5×5）。</summary>
    private const int SpawnClearance = 2;

    /// <summary>连通率目标，百分比整数（用整数比较，避开浮点边界）。</summary>
    private const int RequiredReachablePercent = 90;

    /// <summary>
    /// 高度对比度：把 value noise 围绕 0.5 做线性拉伸。
    ///
    /// 4 octave 平均下来的 value noise 挤在 0.5 附近（标准差只有 0.09 左右），
    /// 直接拿 0.30 当水位几乎不出水（实测约 1.5%）；拉伸之后
    /// <see cref="WorldGenOptions.WaterLevel"/> 的水量才和它的数值大致成比例。
    /// 纯算术，不影响确定性。
    /// </summary>
    private const double HeightContrast = 2.4;

    // 噪声的空间尺度：一个"噪声格"横跨多少地图格。越小地形越碎。
    private const double HeightScale = 26.0;
    private const double MoistureScale = 34.0;
    private const double ForestScale = 18.0;
    private const double RockScale = 11.0;

    // 各层噪声用不同的派生种子（异或盐），互相独立。
    private const ulong HeightSalt = 0x9E3779B97F4A7C15UL;
    private const ulong MoistureSalt = 0xC2B2AE3D27D4EB4FUL;
    private const ulong ForestSalt = 0x165667B19E3779F9UL;
    private const ulong RockSalt = 0xD6E8FEB86659FD93UL;
    private const ulong RngSalt = 0xA0761D6478BD642FUL;

    /// <summary>连通性补偿最多迭代几轮（正常情况下 1 轮就够）。</summary>
    private const int MaxRepairRounds = 4;

    /// <summary>
    /// 生成一张世界。纯函数式：同 seed + 同 options ⇒ 逐格完全一致。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">宽或高小于 5（放不下外围 2 格草地 + 出生点）。</exception>
    public static WorldMap Generate(WorldGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int minimumSize = (Border * 2) + 1;
        if (options.Width < minimumSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Width, $"Width must be >= {minimumSize}.");
        }

        if (options.Height < minimumSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Height, $"Height must be >= {minimumSize}.");
        }

        var WorldMap = new WorldMap(options.Width, options.Height, options.Seed);
        ulong seed = unchecked((ulong)(uint)options.Seed);

        PaintTerrain(WorldMap, options, seed);
        ForceBorderToGrass(WorldMap);
        ScatterProps(WorldMap, options, seed);
        WorldMap.Spawn = FindSpawn(WorldMap);
        EnsureConnectivity(WorldMap);

        return WorldMap;
    }

    /// <summary>第一步：高度噪声决定水 / 沙 / 草。</summary>
    private static void PaintTerrain(WorldMap WorldMap, WorldGenOptions options, ulong seed)
    {
        double waterLevel = options.WaterLevel;
        double sandLevel = waterLevel + SandBand;

        for (int y = 0; y < WorldMap.Height; y++)
        {
            for (int x = 0; x < WorldMap.Width; x++)
            {
                double noise = ValueNoise.Sample2D(seed ^ HeightSalt, x / HeightScale, y / HeightScale);
                double height = Clamp01(0.5 + ((noise - 0.5) * HeightContrast));

                TileKind kind = height < waterLevel
                    ? TileKind.Water
                    : height < sandLevel
                        ? TileKind.Sand
                        : TileKind.Grass;

                WorldMap.SetTile(x, y, kind);
            }
        }
    }

    /// <summary>第二步：外围 <see cref="Border"/> 格永远是草地，出生点不会贴边。</summary>
    private static void ForceBorderToGrass(WorldMap WorldMap)
    {
        int rightEdge = WorldMap.Width - Border;
        int bottomEdge = WorldMap.Height - Border;

        for (int y = 0; y < WorldMap.Height; y++)
        {
            for (int x = 0; x < WorldMap.Width; x++)
            {
                if (x < Border || y < Border || x >= rightEdge || y >= bottomEdge)
                {
                    WorldMap.SetTile(x, y, TileKind.Grass);
                    WorldMap.SetProp(x, y, PropKind.None);
                }
            }
        }
    }

    /// <summary>
    /// 第三步：撒道具。密度乘上同种噪声当局部倍率，树才会成片而不是均匀撒盐。
    /// 只有 <see cref="TileKind.Grass"/> 上会长东西，外围草地也留空。
    /// </summary>
    private static void ScatterProps(WorldMap WorldMap, WorldGenOptions options, ulong seed)
    {
        var rng = new Rng(unchecked((seed * 0x9E3779B97F4A7C15UL) ^ RngSalt));
        int rightEdge = WorldMap.Width - Border;
        int bottomEdge = WorldMap.Height - Border;

        for (int y = 0; y < WorldMap.Height; y++)
        {
            for (int x = 0; x < WorldMap.Width; x++)
            {
                if (x < Border || y < Border || x >= rightEdge || y >= bottomEdge)
                {
                    continue;
                }

                if (WorldMap.GetTile(x, y) != TileKind.Grass)
                {
                    continue;
                }

                double forest = ValueNoise.Sample2D(seed ^ ForestSalt, x / ForestScale, y / ForestScale);
                double treeChance = Clamp01(options.TreeDensity * forest * 2.0);

                // 注意：Chance 无论结果如何都会消耗一次随机数，所以这里的序列是稳定的。
                if (rng.Chance(treeChance) && !HasNeighbourTree(WorldMap, x, y))
                {
                    WorldMap.SetProp(x, y, PropKind.Tree);
                    continue;
                }

                double rock = ValueNoise.Sample2D(seed ^ RockSalt, x / RockScale, y / RockScale);
                if (rng.Chance(Clamp01(options.RockDensity * rock * 2.0)))
                {
                    WorldMap.SetProp(x, y, PropKind.Rock);
                    continue;
                }

                double moisture = ValueNoise.Sample2D(seed ^ MoistureSalt, x / MoistureScale, y / MoistureScale);
                if (rng.Chance(0.03 + (moisture * 0.04)))
                {
                    WorldMap.SetProp(x, y, PropKind.Weed);
                }
            }
        }
    }

    /// <summary>四邻里有没有树（树的四邻不再撒树，避免把路堵死）。</summary>
    private static bool HasNeighbourTree(WorldMap WorldMap, int x, int y)
    {
        return WorldMap.GetProp(x - 1, y) == PropKind.Tree
            || WorldMap.GetProp(x + 1, y) == PropKind.Tree
            || WorldMap.GetProp(x, y - 1) == PropKind.Tree
            || WorldMap.GetProp(x, y + 1) == PropKind.Tree;
    }

    /// <summary>
    /// 第四步：从地图中心向外螺旋找第一格满足"周围 5×5 全部可站"的位置；
    /// 找不到就退化为地图中心。
    /// </summary>
    private static (int X, int Y) FindSpawn(WorldMap WorldMap)
    {
        int centerX = WorldMap.Width / 2;
        int centerY = WorldMap.Height / 2;
        int maxRadius = Math.Max(WorldMap.Width, WorldMap.Height);

        for (int radius = 0; radius <= maxRadius; radius++)
        {
            foreach ((int x, int y) in Ring(centerX, centerY, radius))
            {
                if (HasClearSquare(WorldMap, x, y))
                {
                    return (x, y);
                }
            }
        }

        return (centerX, centerY);
    }

    /// <summary>以 (x,y) 为中心、边长 5 的方块是否全在地图内且全部可站。</summary>
    private static bool HasClearSquare(WorldMap WorldMap, int x, int y)
    {
        if (!WorldMap.InBounds(x, y))
        {
            return false;
        }

        if (x - SpawnClearance < 0 || y - SpawnClearance < 0
            || x + SpawnClearance >= WorldMap.Width || y + SpawnClearance >= WorldMap.Height)
        {
            return false;
        }

        for (int dy = -SpawnClearance; dy <= SpawnClearance; dy++)
        {
            for (int dx = -SpawnClearance; dx <= SpawnClearance; dx++)
            {
                if (!WorldMap.IsWalkable(x + dx, y + dy))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>从 (cx,cy) 出发、半径 radius 那一圈格子，顺序固定（上→右→下→左）。</summary>
    private static (int X, int Y)[] Ring(int cx, int cy, int radius)
    {
        if (radius == 0)
        {
            return new[] { (cx, cy) };
        }

        int left = cx - radius;
        int right = cx + radius;
        int top = cy - radius;
        int bottom = cy + radius;
        var cells = new (int X, int Y)[radius * 8];

        int i = 0;
        for (int x = left; x <= right; x++)
        {
            cells[i++] = (x, top);
        }

        for (int y = top + 1; y <= bottom; y++)
        {
            cells[i++] = (right, y);
        }

        for (int x = right - 1; x >= left; x--)
        {
            cells[i++] = (x, bottom);
        }

        for (int y = bottom - 1; y > top; y--)
        {
            cells[i++] = (left, y);
        }

        return cells;
    }

    /// <summary>
    /// 第五步：连通性补偿。
    /// 从出生点 BFS；若可达面积不到可站格的 <see cref="RequiredReachablePercent"/>%，
    /// 就把每个不可达的"孤岛"各挖一条走廊接到出生点，然后重新检查。
    /// 补偿只把水改成草地、只清掉挡路的树/石头，所以出生点周围的空地只会变多不会变少。
    /// </summary>
    private static void EnsureConnectivity(WorldMap WorldMap)
    {
        int cellCount = WorldMap.Width * WorldMap.Height;
        var distance = new int[cellCount];
        var queue = new int[cellCount];
        var label = new int[cellCount];

        for (int round = 0; round < MaxRepairRounds; round++)
        {
            int walkable = CountWalkable(WorldMap);
            if (walkable == 0)
            {
                return;
            }

            int reachable = BreadthFirstFromSpawn(WorldMap, distance, queue);
            if ((long)reachable * 100L >= (long)walkable * RequiredReachablePercent)
            {
                return;
            }

            ConnectIslands(WorldMap, distance, label, queue);
        }
    }

    /// <summary>
    /// 把每一个不可达的可站连通块（4 邻域）用一条 L 形走廊接到出生点。
    /// 走廊起点取块内离出生点曼哈顿距离最近的那格。
    /// </summary>
    private static void ConnectIslands(WorldMap WorldMap, int[] distance, int[] label, int[] queue)
    {
        int width = WorldMap.Width;
        int spawnX = WorldMap.Spawn.X;
        int spawnY = WorldMap.Spawn.Y;
        Array.Fill(label, -1);

        var corridorStarts = new List<int>();

        for (int y = 0; y < WorldMap.Height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                if (!WorldMap.IsWalkable(x, y) || distance[index] >= 0 || label[index] >= 0)
                {
                    continue;
                }

                int id = corridorStarts.Count;
                corridorStarts.Add(FloodComponent(WorldMap, index, id, distance, label, queue, spawnX, spawnY));
            }
        }

        for (int id = 0; id < corridorStarts.Count; id++)
        {
            int from = corridorStarts[id];
            CarveCorridor(WorldMap, from % width, from / width, spawnX, spawnY);
        }
    }

    /// <summary>
    /// 从 <paramref name="startIndex"/> 洪泛标出同一个不可达连通块（4 邻域），
    /// 返回块内离出生点曼哈顿距离最近的那一格下标。
    /// </summary>
    private static int FloodComponent(
        WorldMap WorldMap,
        int startIndex,
        int id,
        int[] distance,
        int[] label,
        int[] queue,
        int spawnX,
        int spawnY)
    {
        int width = WorldMap.Width;
        int bestIndex = startIndex;
        int bestScore = Math.Abs((startIndex % width) - spawnX) + Math.Abs((startIndex / width) - spawnY);

        int head = 0;
        int tail = 0;
        label[startIndex] = id;
        queue[tail++] = startIndex;

        while (head < tail)
        {
            int current = queue[head++];
            int currentX = current % width;
            int currentY = current / width;

            Visit(currentX - 1, currentY);
            Visit(currentX + 1, currentY);
            Visit(currentX, currentY - 1);
            Visit(currentX, currentY + 1);
        }

        return bestIndex;

        void Visit(int nx, int ny)
        {
            if (!WorldMap.IsWalkable(nx, ny))
            {
                return;
            }

            int neighbourIndex = (ny * width) + nx;
            if (distance[neighbourIndex] >= 0 || label[neighbourIndex] >= 0)
            {
                return;
            }

            label[neighbourIndex] = id;
            int score = Math.Abs(nx - spawnX) + Math.Abs(ny - spawnY);
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = neighbourIndex;
            }

            queue[tail++] = neighbourIndex;
        }
    }

    /// <summary>
    /// 从 (x0,y0) 走一条 4 邻接的 L 形走廊到 (x1,y1)，路上挡道的东西清掉。
    /// </summary>
    private static void CarveCorridor(WorldMap WorldMap, int x0, int y0, int x1, int y1)
    {
        int x = x0;
        int y = y0;

        while (true)
        {
            ClearCell(WorldMap, x, y);

            if (x == x1 && y == y1)
            {
                return;
            }

            if (x != x1)
            {
                x += Math.Sign(x1 - x);
            }
            else
            {
                y += Math.Sign(y1 - y);
            }
        }
    }

    /// <summary>把一格变成可站：水 → 草地，树/石头清掉。</summary>
    private static void ClearCell(WorldMap WorldMap, int x, int y)
    {
        if (!WorldMap.InBounds(x, y))
        {
            return;
        }

        if (WorldMap.GetTile(x, y) == TileKind.Water)
        {
            WorldMap.SetTile(x, y, TileKind.Grass);
        }

        PropKind prop = WorldMap.GetProp(x, y);
        if (prop == PropKind.Tree || prop == PropKind.Rock)
        {
            WorldMap.SetProp(x, y, PropKind.None);
        }
    }

    /// <summary>可站格总数。</summary>
    private static int CountWalkable(WorldMap WorldMap)
    {
        int count = 0;
        for (int y = 0; y < WorldMap.Height; y++)
        {
            for (int x = 0; x < WorldMap.Width; x++)
            {
                if (WorldMap.IsWalkable(x, y))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// 从出生点做一次 4 邻域 BFS，<paramref name="distance"/> 里写下步数（-1 表示没走到）。
    /// 返回可达格数。
    /// </summary>
    private static int BreadthFirstFromSpawn(WorldMap WorldMap, int[] distance, int[] queue)
    {
        Array.Fill(distance, -1);
        int width = WorldMap.Width;
        int spawnX = WorldMap.Spawn.X;
        int spawnY = WorldMap.Spawn.Y;

        if (!WorldMap.IsWalkable(spawnX, spawnY))
        {
            return 0;
        }

        int head = 0;
        int tail = 0;
        int startIndex = (spawnY * width) + spawnX;
        distance[startIndex] = 0;
        queue[tail++] = startIndex;

        while (head < tail)
        {
            int current = queue[head++];
            int currentX = current % width;
            int currentY = current / width;
            int step = distance[current] + 1;

            Enqueue(currentX - 1, currentY, step);
            Enqueue(currentX + 1, currentY, step);
            Enqueue(currentX, currentY - 1, step);
            Enqueue(currentX, currentY + 1, step);
        }

        return tail;

        void Enqueue(int nx, int ny, int step)
        {
            if (!WorldMap.IsWalkable(nx, ny))
            {
                return;
            }

            int index = (ny * width) + nx;
            if (distance[index] >= 0)
            {
                return;
            }

            distance[index] = step;
            queue[tail++] = index;
        }
    }

    /// <summary>把 <c>[0,1]</c> 之外的值夹回来。</summary>
    private static double Clamp01(double value)
    {
        if (value < 0.0)
        {
            return 0.0;
        }

        return value > 1.0 ? 1.0 : value;
    }
}
