using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AsahiMono.Core.Save;
using AsahiMono.Core.World;
using Xunit;

namespace AsahiMono.Core.Tests
{
    /// <summary>
    /// 地皮种类的存档兼容性 + 可走性表。
    ///
    /// 背景：12 种地皮（枚举 1..12）是后加的，老存档里既有写名字的
    /// （<c>"Tile": "Grass"</c>，见 <see cref="SaveSerializer"/> 的 JsonStringEnumConverter），
    /// 也可能有写数字的（<c>"Tile": 1</c>）。两种都必须解析成同一个种类，
    /// 否则玩家开老存档会看到地面整个换了材质。
    /// 规则写在 <c>docs/engine/TILE-KINDS.md</c>：<b>数值只许在尾部追加</b>。
    /// </summary>
    public sealed class TileKindTests
    {
        /// <summary>12 种可绘制地皮，顺序 = 枚举顺序 = 图集顺序（不含 Void）。</summary>
        private static readonly TileKind[] AllGroundKinds =
        {
            TileKind.Grass, TileKind.Soil, TileKind.TilledSoil,
            TileKind.Water, TileKind.Sand, TileKind.Path,
            TileKind.GrassWild, TileKind.GrassDry, TileKind.Mud,
            TileKind.SoilLoose, TileKind.Gravel, TileKind.StoneFloor,
        };

        [Fact]
        public void EnumCodes_AreAppendOnlyAndContiguous()
        {
            // 1..12 连续、顺序即文档顺序；Void 固定 0。
            Assert.Equal(0, (int)TileKind.Void);
            for (int i = 0; i < AllGroundKinds.Length; i++)
            {
                Assert.Equal(i + 1, (int)AllGroundKinds[i]);
            }

            // 没有两个成员共用一个数值（尾部追加时最常见的抄错）。
            int[] codes = Enum.GetValues<TileKind>().Select(k => (int)k).ToArray();
            Assert.Equal(codes.Length, codes.Distinct().Count());
            Assert.Equal(13, codes.Length);
        }

        [Fact]
        public void SaveJson_RoundTripsEveryTileKindByName()
        {
            var state = new GameState { Seed = 42 };
            for (int i = 0; i < AllGroundKinds.Length; i++)
            {
                state.Overrides.Add(new TileOverride(i, 0, AllGroundKinds[i], PropKind.None));
            }

            string json = SaveSerializer.Serialize(state);

            // 枚举写成名字：手改存档时一眼能看出"这里是野草"。
            Assert.Contains("\"GrassWild\"", json, StringComparison.Ordinal);
            Assert.Contains("\"StoneFloor\"", json, StringComparison.Ordinal);

            GameState loaded = SaveSerializer.Deserialize(json);

            Assert.Equal(state.Overrides, loaded.Overrides);
            Assert.Equal(AllGroundKinds.Length, loaded.Overrides.Count);
        }

        [Theory]
        [InlineData(1, TileKind.Grass)]
        [InlineData(2, TileKind.Soil)]
        [InlineData(3, TileKind.TilledSoil)]
        [InlineData(4, TileKind.Water)]
        [InlineData(5, TileKind.Sand)]
        [InlineData(6, TileKind.Path)]
        [InlineData(7, TileKind.GrassWild)]
        [InlineData(8, TileKind.GrassDry)]
        [InlineData(9, TileKind.Mud)]
        [InlineData(10, TileKind.SoilLoose)]
        [InlineData(11, TileKind.Gravel)]
        [InlineData(12, TileKind.StoneFloor)]
        public void LegacyNumericTileCode_ParsesToSameKind(int code, TileKind expected)
        {
            // 老存档可能把枚举写成数字（手改、或别的工具写的）。JsonStringEnumConverter
            // 默认允许数字，这条测试把"数字仍有同样含义"钉死。
            string json = $"{{ \"Overrides\": [ {{ \"X\": 1, \"Y\": 2, \"Tile\": {code}, \"Prop\": 0 }} ] }}";

            GameState state = SaveSerializer.Deserialize(json);

            Assert.Single(state.Overrides);
            Assert.Equal(expected, state.Overrides[0].Tile);
            Assert.Equal(PropKind.None, state.Overrides[0].Prop);
        }

        [Fact]
        public void WalkableKindTable_MatchesDocumentedRule()
        {
            // 只有 Void（地图外）和 Water 挡路；mud 可走（减速是后续的事）。
            var blocked = new HashSet<TileKind> { TileKind.Void, TileKind.Water };

            foreach (TileKind kind in Enum.GetValues<TileKind>())
            {
                Assert.Equal(!blocked.Contains(kind), WorldMap.IsWalkableKind(kind));
            }

            // 未知数值（比当前版本新的存档）按"可站立"处理，不做隐形墙。
            Assert.True(WorldMap.IsWalkableKind((TileKind)99));
        }
    }
}
