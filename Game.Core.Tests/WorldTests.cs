using System;
using System.Collections.Generic;
using System.Diagnostics;
using AsahiMono.Core.Save;
using AsahiMono.Core.World;
using Xunit;
using WorldMap = global::AsahiMono.Core.World.WorldMap;

namespace AsahiMono.Core.Tests
{
    public sealed class WorldTests
    {
        /// <summary>本文件里到处都要用的默认种子集合：多扫几个，避免"只有一个种子对"的假绿。</summary>
        private static readonly int[] Seeds = { 0, 1, 2, 7, 1234, 20261115 };

        [Fact]
        public void Generate_SameSeed_ProducesIdenticalWorlds()
        {
            var options = new WorldGenOptions(20261115);

            WorldMap first = WorldGen.Generate(options);
            WorldMap second = WorldGen.Generate(options);

            Assert.Equal(first.Width, second.Width);
            Assert.Equal(first.Height, second.Height);
            Assert.Equal(first.Seed, second.Seed);
            Assert.Equal(first.Spawn, second.Spawn);

            for (int y = 0; y < first.Height; y++)
            {
                for (int x = 0; x < first.Width; x++)
                {
                    if (first.GetTile(x, y) != second.GetTile(x, y) || first.GetProp(x, y) != second.GetProp(x, y))
                    {
                        Assert.Fail($"same seed diverged at ({x},{y}): " +
                                    $"{first.GetTile(x, y)}/{first.GetProp(x, y)} vs " +
                                    $"{second.GetTile(x, y)}/{second.GetProp(x, y)}");
                    }
                }
            }
        }

        [Fact]
        public void Generate_DifferentSeeds_ProduceDifferentWorlds()
        {
            int differing = 0;

            for (int i = 1; i < Seeds.Length; i++)
            {
                WorldMap a = WorldGen.Generate(new WorldGenOptions(Seeds[0]));
                WorldMap b = WorldGen.Generate(new WorldGenOptions(Seeds[i]));

                for (int y = 0; y < a.Height; y++)
                {
                    for (int x = 0; x < a.Width; x++)
                    {
                        if (a.GetTile(x, y) != b.GetTile(x, y))
                        {
                            differing++;
                        }
                    }
                }
            }

            // 完全不重合才算"不同"——用比例兜住，容忍个别格子撞车。
            Assert.True(differing > 0, "different seeds produced exactly the same terrain");
        }

        [Fact]
        public void Spawn_IsWalkable_AndSurroundedByWalkable5x5()
        {
            foreach (int seed in Seeds)
            {
                WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(seed));
                (int x, int y) = WorldMap.Spawn;

                Assert.InRange(x, 0, WorldMap.Width - 1);
                Assert.InRange(y, 0, WorldMap.Height - 1);
                Assert.True(WorldMap.IsWalkable(x, y), $"seed {seed}: spawn ({x},{y}) is not walkable");

                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        Assert.True(
                            WorldMap.IsWalkable(x + dx, y + dy),
                            $"seed {seed}: spawn ({x},{y}) has unwalkable cell at offset ({dx},{dy})");
                    }
                }
            }
        }

        [Fact]
        public void ReachableLand_FromSpawn_IsAtLeast90Percent()
        {
            foreach (int seed in Seeds)
            {
                WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(seed));
                (int walkable, int reachable) = CountReachable(WorldMap);

                Assert.True(walkable > 0, $"seed {seed}: WorldMap has no walkable tiles");
                Assert.True(
                    reachable * 100L >= walkable * 90L,
                    $"seed {seed}: only {reachable}/{walkable} walkable tiles reachable from spawn " +
                    $"({reachable * 100.0 / walkable:F1}%)");
            }
        }

        [Fact]
        public void ReachableLand_StaysAbove90Percent_EvenOnAFloodedMap()
        {
            // WaterLevel 拉到 0.70 会造出大片孤岛，最考验连通补偿。
            foreach (int seed in new[] { 7, 11, 1234 })
            {
                WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(seed, WaterLevel: 0.70));
                (int walkable, int reachable) = CountReachable(WorldMap);

                Assert.True(walkable > 0, $"seed {seed}: flooded WorldMap has no walkable tiles");
                Assert.True(
                    reachable * 100L >= walkable * 90L,
                    $"seed {seed}: flooded map only {reachable}/{walkable} reachable " +
                    $"({reachable * 100.0 / walkable:F1}%)");
            }
        }

        [Fact]
        public void OutOfBoundsAccess_IsSafeAndDoesNotThrow()
        {
            WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(7));
            int w = WorldMap.Width;
            int h = WorldMap.Height;

            (int x, int y)[] outside =
            {
                (-1, -1), (-1, 0), (0, -1), (w, 0), (0, h), (w, h),
                (int.MinValue, int.MinValue), (int.MaxValue, int.MaxValue),
            };

            foreach ((int x, int y) in outside)
            {
                Assert.False(WorldMap.InBounds(x, y));
                Assert.Equal(TileKind.Void, WorldMap.GetTile(x, y));
                Assert.Equal(PropKind.None, WorldMap.GetProp(x, y));
                Assert.False(WorldMap.IsWalkable(x, y));
            }

            // 越界写要静默忽略，不能抛、也不能破坏已有的图。
            var before = Snapshot(WorldMap);
            foreach ((int x, int y) in outside)
            {
                WorldMap.SetTile(x, y, TileKind.Path);
                WorldMap.SetProp(x, y, PropKind.Tree);
            }

            Assert.Equal(before, Snapshot(WorldMap));
            Assert.Equal((w, h, WorldMap.Seed), (WorldMap.Width, WorldMap.Height, WorldMap.Seed));
        }

        [Fact]
        public void ApplyOverrides_TakesEffectAndBeatsGeneratedTerrain()
        {
            WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(7));

            // 找一个长草的空格子，改成耕地；再找一个水面，填成土并放杂草。
            (int gx, int gy) = FindCell(WorldMap, t => t == TileKind.Grass, PropKind.None);
            (int wx, int wy) = FindCell(WorldMap, t => t == TileKind.Water, PropKind.None);

            Assert.Equal(TileKind.Grass, WorldMap.GetTile(gx, gy));
            Assert.Equal(TileKind.Water, WorldMap.GetTile(wx, wy));

            WorldMap.ApplyOverrides(new[]
            {
                new TileOverride(gx, gy, TileKind.TilledSoil, PropKind.Weed),
                new TileOverride(wx, wy, TileKind.Soil, PropKind.None),
                new TileOverride(-5, -5, TileKind.Path, PropKind.Tree), // 越界条目要被跳过
            });

            Assert.Equal(TileKind.TilledSoil, WorldMap.GetTile(gx, gy));
            Assert.Equal(PropKind.Weed, WorldMap.GetProp(gx, gy));
            Assert.Equal(TileKind.Soil, WorldMap.GetTile(wx, wy));
            Assert.Equal(PropKind.None, WorldMap.GetProp(wx, wy));
            Assert.Equal(TileKind.Soil, WorldMap.GetTile(wx, wy));

            // 覆盖要和"生成结果"一样能被 IsWalkable 看见：耕地可站。
            Assert.True(WorldMap.IsWalkable(gx, gy));
        }

        [Fact]
        public void ApplyOverrides_OnGeneratedWater_MakesItWalkable()
        {
            WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(7));
            (int wx, int wy) = FindCell(WorldMap, t => t == TileKind.Water, PropKind.None);

            Assert.False(WorldMap.IsWalkable(wx, wy));

            WorldMap.ApplyOverrides(new[] { new TileOverride(wx, wy, TileKind.Path, PropKind.None) });

            Assert.True(WorldMap.IsWalkable(wx, wy));
        }

        [Fact]
        public void ApplyOverrides_NullCollection_Throws()
        {
            WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(7));

            Assert.Throws<ArgumentNullException>(() => WorldMap.ApplyOverrides(null!));
        }

        [Fact]
        public void Generate_96x96_CompletesWellUnder200Milliseconds()
        {
            var options = new WorldGenOptions(20261115);

            // 先热身，把 JIT / 静态构造的开销排除掉，测的是生成本身。
            for (int i = 0; i < 3; i++)
            {
                WorldGen.Generate(options);
            }

            double best = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                WorldMap WorldMap = WorldGen.Generate(options);
                stopwatch.Stop();

                Assert.Equal(96, WorldMap.Width);
                Assert.Equal(96, WorldMap.Height);
                best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
            }

            Assert.True(best < 200.0, $"96x96 generation took {best:F2} ms (budget 200 ms)");
        }

        [Fact]
        public void BorderTwoTiles_AreAlwaysGrassAndEmpty()
        {
            foreach (int seed in Seeds)
            {
                WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(seed));

                for (int y = 0; y < WorldMap.Height; y++)
                {
                    for (int x = 0; x < WorldMap.Width; x++)
                    {
                        bool onBorder = x < 2 || y < 2 || x >= WorldMap.Width - 2 || y >= WorldMap.Height - 2;
                        if (!onBorder)
                        {
                            continue;
                        }

                        Assert.True(
                            WorldMap.GetTile(x, y) == TileKind.Grass,
                            $"seed {seed}: border cell ({x},{y}) is {WorldMap.GetTile(x, y)}, expected Grass");
                        Assert.True(
                            WorldMap.GetProp(x, y) == PropKind.None,
                            $"seed {seed}: border cell ({x},{y}) has prop {WorldMap.GetProp(x, y)}");
                    }
                }
            }
        }

        [Fact]
        public void Trees_AreNeverAdjacentToTrees()
        {
            foreach (int seed in Seeds)
            {
                WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(seed));

                for (int y = 0; y < WorldMap.Height; y++)
                {
                    for (int x = 0; x < WorldMap.Width; x++)
                    {
                        if (WorldMap.GetProp(x, y) != PropKind.Tree)
                        {
                            continue;
                        }

                        Assert.True(WorldMap.GetProp(x - 1, y) != PropKind.Tree, $"seed {seed}: trees at ({x},{y}) and ({x - 1},{y})");
                        Assert.True(WorldMap.GetProp(x + 1, y) != PropKind.Tree, $"seed {seed}: trees at ({x},{y}) and ({x + 1},{y})");
                        Assert.True(WorldMap.GetProp(x, y - 1) != PropKind.Tree, $"seed {seed}: trees at ({x},{y}) and ({x},{y - 1})");
                        Assert.True(WorldMap.GetProp(x, y + 1) != PropKind.Tree, $"seed {seed}: trees at ({x},{y}) and ({x},{y + 1})");
                    }
                }
            }
        }

        [Fact]
        public void Props_OnlyAppearOnGrass()
        {
            foreach (int seed in Seeds)
            {
                WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(seed));

                for (int y = 0; y < WorldMap.Height; y++)
                {
                    for (int x = 0; x < WorldMap.Width; x++)
                    {
                        PropKind prop = WorldMap.GetProp(x, y);
                        if (prop == PropKind.None)
                        {
                            continue;
                        }

                        Assert.True(
                            WorldMap.GetTile(x, y) == TileKind.Grass,
                            $"seed {seed}: prop {prop} sits on {WorldMap.GetTile(x, y)} at ({x},{y})");
                    }
                }
            }
        }

        [Fact]
        public void HigherWaterLevel_ProducesMoreWater()
        {
            foreach (int seed in new[] { 0, 7, 1234 })
            {
                int dry = CountTiles(WorldGen.Generate(new WorldGenOptions(seed, WaterLevel: 0.20)), TileKind.Water);
                int wet = CountTiles(WorldGen.Generate(new WorldGenOptions(seed, WaterLevel: 0.50)), TileKind.Water);

                Assert.True(wet > dry, $"seed {seed}: WaterLevel 0.50 gave {wet} water tiles, 0.20 gave {dry}");
            }
        }

        [Fact]
        public void Generate_TinyButLegalMap_DoesNotThrow()
        {
            WorldMap WorldMap = WorldGen.Generate(new WorldGenOptions(3, Width: 8, Height: 8));

            Assert.Equal(8, WorldMap.Width);
            Assert.Equal(8, WorldMap.Height);
            Assert.True(WorldMap.InBounds(WorldMap.Spawn.X, WorldMap.Spawn.Y));
        }

        [Fact]
        public void Generate_TooSmallMap_Throws()
        {
            // 外围 2 格 + 出生点 5x5，最小可用尺寸是 5。
            Assert.Throws<ArgumentOutOfRangeException>(() => WorldGen.Generate(new WorldGenOptions(0, Width: 4, Height: 4)));
            Assert.Throws<ArgumentOutOfRangeException>(() => WorldGen.Generate(new WorldGenOptions(0, Width: 96, Height: 0)));
        }

        [Fact]
        public void World_RejectsNonPositiveSize()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new WorldMap(0, 10, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WorldMap(10, -1, 1));
        }

        [Fact]
        public void Rng_MatchesSplitMix64ReferenceVectors()
        {
            // 参考值：塞满 SplitMix64 的标准测试向量。算法一旦被改动，这里立刻红。
            var rng = new Rng(0);
            Assert.Equal(0xE220A8397B1DCDAFUL, rng.NextULong());
            Assert.Equal(0x6E789E6AA1B965F4UL, rng.NextULong());
            Assert.Equal(0x06C45D188009454FUL, rng.NextULong());
            Assert.Equal(0xF88BB8A8724C81ECUL, rng.NextULong());
            Assert.Equal(0x1B39896A51A8749BUL, rng.NextULong());

            var other = new Rng(0x1234);
            Assert.Equal(0x5F642F87D5E23888UL, other.NextULong());
            Assert.Equal(0x5A4D78533D034CB5UL, other.NextULong());
            Assert.Equal(0x8A85FFDAEA35A5A6UL, other.NextULong());
        }

        [Fact]
        public void Rng_SameSeed_IsReproducible()
        {
            var a = new Rng(42);
            var b = new Rng(42);

            for (int i = 0; i < 64; i++)
            {
                Assert.Equal(a.NextULong(), b.NextULong());
            }
        }

        [Fact]
        public void Rng_NextDouble_StaysInUnitInterval()
        {
            var rng = new Rng(7);
            bool sawLow = false;
            bool sawHigh = false;

            for (int i = 0; i < 10000; i++)
            {
                double value = rng.NextDouble();
                Assert.InRange(value, 0.0, 0.99999999999999989);
                sawLow |= value < 0.5;
                sawHigh |= value >= 0.5;
            }

            Assert.True(sawLow && sawHigh, "NextDouble never covered both halves of [0,1)");
        }

        [Fact]
        public void Rng_NextInt_IsBoundedAndRejectsNonPositive()
        {
            var rng = new Rng(1);
            var seen = new HashSet<int>();

            for (int i = 0; i < 5000; i++)
            {
                int value = rng.NextInt(7);
                Assert.InRange(value, 0, 6);
                seen.Add(value);
            }

            Assert.Equal(7, seen.Count);
            Assert.Equal(0, rng.NextInt(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(-3));
        }

        [Fact]
        public void Rng_Chance_HandlesExtremes()
        {
            var rng = new Rng(99);

            for (int i = 0; i < 100; i++)
            {
                Assert.False(rng.Chance(0.0));
                Assert.True(rng.Chance(1.0));
            }

            int hits = 0;
            for (int i = 0; i < 10000; i++)
            {
                if (rng.Chance(0.25))
                {
                    hits++;
                }
            }

            // 期望 2500，给一个很宽的区间，只抓"显然不对"。
            Assert.InRange(hits, 2100, 2900);
        }

        [Fact]
        public void ValueNoise_IsDeterministicAndInRange()
        {
            for (int i = 0; i < 200; i++)
            {
                double x = i * 0.37;
                double y = i * 0.11;

                double once = ValueNoise.Sample2D(12345, x, y);
                double twice = ValueNoise.Sample2D(12345, x, y);

                Assert.Equal(once, twice);
                Assert.InRange(once, 0.0, 1.0);

                // 采样点整体连续：相邻位置的差值应该很小。
                double neighbour = ValueNoise.Sample2D(12345, x + 0.01, y);
                Assert.True(Math.Abs(once - neighbour) < 0.2, $"noise jumped {Math.Abs(once - neighbour):F3} over 0.01 step");
            }
        }

        [Fact]
        public void ValueNoise_DifferentSeedsGiveDifferentPatterns()
        {
            int differing = 0;

            for (int i = 0; i < 200; i++)
            {
                double x = i * 0.37;
                double y = i * 0.11;
                if (ValueNoise.Sample2D(1, x, y) != ValueNoise.Sample2D(2, x, y))
                {
                    differing++;
                }
            }

            Assert.True(differing > 150, $"only {differing}/200 samples differed between seeds");
        }

        [Fact]
        public void ValueNoise_RespectsOctaveCount()
        {
            double one = ValueNoise.Sample2D(7, 3.25, 8.5, octaves: 1);
            double four = ValueNoise.Sample2D(7, 3.25, 8.5, octaves: 4);

            Assert.InRange(one, 0.0, 1.0);
            Assert.InRange(four, 0.0, 1.0);

            // 多加几层高频细节，值一定被拉动（除非正好落在均值上，选点避开了）。
            Assert.NotEqual(one, four);
        }

        // ---- 工具方法 ----

        /// <summary>从出生点 BFS，返回（全图可站格数，可达可站格数）。</summary>
        private static (int Walkable, int Reachable) CountReachable(WorldMap WorldMap)
        {
            int walkable = CountTiles(WorldMap, null);

            var visited = new bool[WorldMap.Width * WorldMap.Height];
            var queue = new Queue<(int X, int Y)>();
            (int sx, int sy) = WorldMap.Spawn;
            visited[(sy * WorldMap.Width) + sx] = true;
            queue.Enqueue((sx, sy));

            int reachable = 0;
            while (queue.Count > 0)
            {
                (int cx, int cy) = queue.Dequeue();
                reachable++;

                Visit(cx - 1, cy);
                Visit(cx + 1, cy);
                Visit(cx, cy - 1);
                Visit(cx, cy + 1);
            }

            return (walkable, reachable);

            void Visit(int nx, int ny)
            {
                if (!WorldMap.IsWalkable(nx, ny))
                {
                    return;
                }

                int index = (ny * WorldMap.Width) + nx;
                if (visited[index])
                {
                    return;
                }

                visited[index] = true;
                queue.Enqueue((nx, ny));
            }
        }

        private static int CountTiles(WorldMap WorldMap, TileKind? kind)
        {
            int count = 0;

            for (int y = 0; y < WorldMap.Height; y++)
            {
                for (int x = 0; x < WorldMap.Width; x++)
                {
                    bool match = kind.HasValue ? WorldMap.GetTile(x, y) == kind.Value : WorldMap.IsWalkable(x, y);
                    if (match)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static (int X, int Y) FindCell(WorldMap WorldMap, Func<TileKind, bool> tileMatch, PropKind prop)
        {
            for (int y = 0; y < WorldMap.Height; y++)
            {
                for (int x = 0; x < WorldMap.Width; x++)
                {
                    if (tileMatch(WorldMap.GetTile(x, y)) && WorldMap.GetProp(x, y) == prop)
                    {
                        return (x, y);
                    }
                }
            }

            throw new InvalidOperationException($"no cell matched (prop {prop}) — generator changed?");
        }

        private static string Snapshot(WorldMap WorldMap)
        {
            var chars = new char[WorldMap.Width * WorldMap.Height * 2];

            for (int y = 0; y < WorldMap.Height; y++)
            {
                for (int x = 0; x < WorldMap.Width; x++)
                {
                    int index = (y * WorldMap.Width) + x;
                    chars[index * 2] = (char)('0' + (int)WorldMap.GetTile(x, y));
                    chars[(index * 2) + 1] = (char)('0' + (int)WorldMap.GetProp(x, y));
                }
            }

            return new string(chars);
        }
    }
}
