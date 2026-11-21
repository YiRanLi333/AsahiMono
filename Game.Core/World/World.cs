using System;
using System.Collections.Generic;
using AsahiMono.Core.Save;

namespace AsahiMono.Core.World;

/// <summary>
/// 地表格子类型。数值是存档里的稳定编码，<b>不要改动已有数值</b>。
///
/// 追加规则（硬约束）：<b>只许在尾部追加</b>，禁止重排、禁止复用已删的数值——
/// 存档里的 <c>Overrides[].Tile</c> 既可能写成名字（<c>"Grass"</c>），
/// 也可能写成老版本留下的数字（<c>1</c>），两者都按这里的数值解释。
///
/// 每个成员后面的 <c>slug:</c> 注释是磁盘目录名
/// <c>res://Assets/tiles/iso/ground/&lt;slug&gt;/</c> 的唯一来源：
/// 渲染器（IsoWorldRenderer）按枚举顺序取图，
/// <c>tools/tileset/make_atlas.py</c> 用正则读这些注释，所以<b>改 slug 要同时改目录名</b>。
/// 顺序 = 地面图集里 kind 的顺序 = 美术的交付顺序，见 docs/engine/TILE-KINDS.md。
/// </summary>
public enum TileKind
{
    /// <summary>虚空：地图外或尚未生成。不可站立。</summary>
    Void = 0,

    /// <summary>普通草地，可站立，是默认地表。</summary>
    Grass = 1,          // slug: grass

    /// <summary>裸土（未耕作）。</summary>
    Soil = 2,           // slug: soil

    /// <summary>已耕作的土地，可以播种。</summary>
    TilledSoil = 3,     // slug: tilled

    /// <summary>水。不可站立。</summary>
    Water = 4,          // slug: water

    /// <summary>沙地（水边的过渡带），可站立。</summary>
    Sand = 5,           // slug: sand

    /// <summary>铺过的路，稍后走路会更快。</summary>
    Path = 6,           // slug: path

    /// <summary>野草地：没人打理、草更杂更深的草地。可站立。</summary>
    GrassWild = 7,      // slug: grass_wild

    /// <summary>枯草地：偏黄、干燥的草。可站立。</summary>
    GrassDry = 8,       // slug: grass_dry

    /// <summary>泥地：可站立，将来踩上去会减速（减速尚未实现）。</summary>
    Mud = 9,            // slug: mud

    /// <summary>松土：翻过但没整平、更碎更浅的土。可站立。</summary>
    SoilLoose = 10,     // slug: soil_loose

    /// <summary>碎石地：压实的砂石路肩。可站立。</summary>
    Gravel = 11,        // slug: gravel

    /// <summary>石板地：铺得规整的石地面，适合做院子/广场。可站立。</summary>
    StoneFloor = 12,    // slug: stone_floor

    // 13 号位留给美术已画、用户暂缓的 wood_floor（目录已存在 png，枚举里刻意还没有它）。
}

/// <summary>
/// 叠在格子上的物件。数值同样是存档编码。
/// </summary>
public enum PropKind
{
    /// <summary>没有物件。</summary>
    None = 0,

    /// <summary>树。挡住去路，可以砍。</summary>
    Tree = 1,

    /// <summary>石头。挡住去路，可以敲。</summary>
    Rock = 2,

    /// <summary>杂草。不挡路，可以割。</summary>
    Weed = 3,
}

/// <summary>
/// 一张格子地图，是世界的权威数据。
///
/// 坐标约定：<c>(0,0)</c> 在<b>左上角</b>，<c>x</c> 向右、<c>y</c> 向下（与表现层 TileMap 的约定一致），
/// 有效范围 <c>0 &lt;= x &lt; Width</c>、<c>0 &lt;= y &lt; Height</c>。
///
/// 越界访问一律"安全但无效果"：读返回 <see cref="TileKind.Void"/> / <see cref="PropKind.None"/>，
/// 写直接忽略，<b>不抛异常</b>——这样表现层在视野边缘可以放心地多查几格。
/// </summary>
public sealed class WorldMap
{
    private readonly TileKind[] _tiles;
    private readonly PropKind[] _props;

    /// <summary>
    /// 建一张全是 <see cref="TileKind.Void"/> 的空地图。地形由 <see cref="WorldGen"/> 填，
    /// 出生点先暂定在地图中心，生成结束时再改写。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> 或 <paramref name="height"/> 不是正数。</exception>
    public WorldMap(int width, int height, int seed)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "width must be > 0.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "height must be > 0.");
        }

        Width = width;
        Height = height;
        Seed = seed;
        _tiles = new TileKind[width * height];
        _props = new PropKind[width * height];
        Spawn = (width / 2, height / 2);
    }

    /// <summary>地图宽度（格）。</summary>
    public int Width { get; }

    /// <summary>地图高度（格）。</summary>
    public int Height { get; }

    /// <summary>生成这张图用的种子。重放同一张图需要它。</summary>
    public int Seed { get; }

    /// <summary>出生格。由 <see cref="WorldGen"/> 在生成结束时写入。</summary>
    public (int X, int Y) Spawn { get; internal set; }

    /// <summary>坐标是否落在地图内。</summary>
    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    /// <summary>取地表类型；越界返回 <see cref="TileKind.Void"/>。</summary>
    public TileKind GetTile(int x, int y)
    {
        int index = IndexOf(x, y);
        return index < 0 ? TileKind.Void : _tiles[index];
    }

    /// <summary>写地表类型；越界写入被忽略（不抛异常）。</summary>
    public void SetTile(int x, int y, TileKind kind)
    {
        int index = IndexOf(x, y);
        if (index < 0)
        {
            return;
        }

        _tiles[index] = kind;
    }

    /// <summary>取格子上的物件；越界返回 <see cref="PropKind.None"/>。</summary>
    public PropKind GetProp(int x, int y)
    {
        int index = IndexOf(x, y);
        return index < 0 ? PropKind.None : _props[index];
    }

    /// <summary>写格子上的物件；越界写入被忽略（不抛异常）。</summary>
    public void SetProp(int x, int y, PropKind prop)
    {
        int index = IndexOf(x, y);
        if (index < 0)
        {
            return;
        }

        _props[index] = prop;
    }

    /// <summary>
    /// 某种地表本身能不能站人（<b>不看</b>格子上的物件）。权威表见 docs/engine/TILE-KINDS.md。
    ///
    /// 12 种地皮里只有 <see cref="TileKind.Void"/>（地图外）和 <see cref="TileKind.Water"/> 挡路；
    /// 其余都可站立。其中 <see cref="TileKind.Mud"/> 是"能走但将来要减速"，
    /// 减速还没实现——加的时候在这里返回速度系数，别改这张布尔表。
    ///
    /// 新增 kind 时请显式列出，别依赖默认分支：默认分支是"可站立"，这样美术新交的地皮
    /// 哪怕代码还没跟上也不会变成隐形墙；同时它也是老存档里出现未知数值时的安全行为。
    /// </summary>
    public static bool IsWalkableKind(TileKind kind) => kind switch
    {
        TileKind.Void => false,
        TileKind.Water => false,

        // 草地三兄弟（普通 / 野生 / 枯黄）。
        TileKind.Grass or TileKind.GrassWild or TileKind.GrassDry => true,

        // 土壤三兄弟（裸土 / 松土 / 已耕作）。
        TileKind.Soil or TileKind.SoilLoose or TileKind.TilledSoil => true,

        // 硬化地面（沙 / 铺路 / 碎石 / 石板）。
        TileKind.Sand or TileKind.Path or TileKind.Gravel or TileKind.StoneFloor => true,

        // 泥地：可站立，将来做"踩上去变慢"。
        TileKind.Mud => true,

        _ => true,
    };

    /// <summary>可站立：地表可站立（<see cref="IsWalkableKind"/>）且该格无 Tree/Rock。越界恒为 <c>false</c>。</summary>
    public bool IsWalkable(int x, int y)
    {
        int index = IndexOf(x, y);
        if (index < 0)
        {
            return false;
        }

        if (!IsWalkableKind(_tiles[index]))
        {
            return false;
        }

        PropKind prop = _props[index];
        return prop != PropKind.Tree && prop != PropKind.Rock;
    }

    /// <summary>
    /// 把存档里的改动盖回去（先生成再覆盖）。
    /// 逐条按顺序应用；越界条目静默跳过（存档可能来自尺寸不同的地图）。
    /// </summary>
    public void ApplyOverrides(IEnumerable<TileOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        foreach (TileOverride? item in overrides)
        {
            if (item is null || !InBounds(item.X, item.Y))
            {
                continue;
            }

            SetTile(item.X, item.Y, item.Tile);
            SetProp(item.X, item.Y, item.Prop);
        }
    }

    /// <summary>把二维坐标折成一维下标；越界返回 -1。</summary>
    private int IndexOf(int x, int y) => InBounds(x, y) ? (y * Width) + x : -1;
}
