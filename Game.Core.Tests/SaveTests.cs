using System;
using System.Collections.Generic;
using System.Text.Json;
using AsahiMono.Core.Save;
using AsahiMono.Core.World;
using Xunit;
using WorldMap = global::AsahiMono.Core.World.WorldMap;

namespace AsahiMono.Core.Tests
{
    public sealed class SaveTests
    {
        private static GameState NewState()
        {
            return new GameState
            {
                Version = 1,
                Seed = 20261115,
                Day = 3,
                Ticks = 777,
                PlayerX = 47,
                PlayerY = 48,
                Overrides =
                {
                    new TileOverride(10, 11, TileKind.TilledSoil, PropKind.None),
                    new TileOverride(20, 21, TileKind.Path, PropKind.None),
                    new TileOverride(30, 31, TileKind.Grass, PropKind.Tree),
                },
            };
        }

        [Fact]
        public void FreshState_HasDocumentedDefaults()
        {
            var state = new GameState();

            Assert.Equal(1, state.Version);
            Assert.Equal(1, state.Day);
            Assert.Equal(0, state.Ticks);
            Assert.Equal(0, state.Seed);
            Assert.Equal(0, state.PlayerX);
            Assert.Equal(0, state.PlayerY);
            Assert.NotNull(state.Overrides);
            Assert.Empty(state.Overrides);
        }

        [Fact]
        public void Serialize_ThenDeserialize_RoundTripsEverything()
        {
            GameState original = NewState();

            string json = SaveSerializer.Serialize(original);
            GameState loaded = SaveSerializer.Deserialize(json);

            Assert.Equal(original.Version, loaded.Version);
            Assert.Equal(original.Seed, loaded.Seed);
            Assert.Equal(original.Day, loaded.Day);
            Assert.Equal(original.Ticks, loaded.Ticks);
            Assert.Equal(original.PlayerX, loaded.PlayerX);
            Assert.Equal(original.PlayerY, loaded.PlayerY);
            Assert.Equal(original.Overrides.Count, loaded.Overrides.Count);

            for (int i = 0; i < original.Overrides.Count; i++)
            {
                // TileOverride 是 record，值相等即可（比逐字段断言更能抓到顺序错位）。
                Assert.Equal(original.Overrides[i], loaded.Overrides[i]);
            }
        }

        [Fact]
        public void Serialize_IsStableAndReadable()
        {
            GameState state = NewState();

            string first = SaveSerializer.Serialize(state);
            string second = SaveSerializer.Serialize(state);

            // 没有时间戳 / Guid / 哈希随机化：同一份状态永远序列化成同一串字。
            Assert.Equal(first, second);

            // 缩进可读：多行，且枚举写名字。
            Assert.Contains("\n", first);
            Assert.Contains("TilledSoil", first);
            Assert.Contains("Path", first);
            Assert.Contains("\"Seed\"", first);
            Assert.Contains("\"Overrides\"", first);
        }

        [Fact]
        public void Serialize_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => SaveSerializer.Serialize(null!));
        }

        [Fact]
        public void Deserialize_CorruptText_ThrowsJsonException()
        {
            Assert.ThrowsAny<JsonException>(() => SaveSerializer.Deserialize("{ this is not json"));
            Assert.ThrowsAny<JsonException>(() => SaveSerializer.Deserialize("{\"Seed\": }"));
            Assert.ThrowsAny<JsonException>(() => SaveSerializer.Deserialize("{\"Ticks\": \"not a number\"}"));
            Assert.ThrowsAny<JsonException>(() => SaveSerializer.Deserialize("{\"Seed\": 1} trailing"));
        }

        [Fact]
        public void Deserialize_EmptyText_ThrowsJsonException()
        {
            Assert.Throws<JsonException>(() => SaveSerializer.Deserialize(string.Empty));
            Assert.Throws<JsonException>(() => SaveSerializer.Deserialize("   \r\n\t "));
            Assert.Throws<JsonException>(() => SaveSerializer.Deserialize(null!));
            Assert.Throws<JsonException>(() => SaveSerializer.Deserialize("null"));
        }

        [Fact]
        public void Deserialize_PartialJson_KeepsDefaults()
        {
            // 旧存档缺字段时不能炸，缺的走属性初始值。
            GameState state = SaveSerializer.Deserialize("{\"Seed\": 42}");

            Assert.Equal(42, state.Seed);
            Assert.Equal(1, state.Version);
            Assert.Equal(1, state.Day);
            Assert.Empty(state.Overrides);
        }

        [Fact]
        public void Deserialize_ExplicitNullOverrides_BecomesEmptyList()
        {
            GameState state = SaveSerializer.Deserialize("{\"Seed\": 42, \"Overrides\": null}");

            Assert.NotNull(state.Overrides);
            Assert.Empty(state.Overrides);
        }

        [Fact]
        public void Deserialize_ToleratesCommentsAndTrailingCommas()
        {
            const string json = """
                {
                  // 手改过的存档也要能读
                  "Seed": 7,
                  "Overrides": [
                    { "X": 1, "Y": 2, "Tile": "Water", "Prop": "None" },
                  ],
                }
                """;

            GameState state = SaveSerializer.Deserialize(json);

            Assert.Equal(7, state.Seed);
            Assert.Single(state.Overrides);
            Assert.Equal(new TileOverride(1, 2, TileKind.Water, PropKind.None), state.Overrides[0]);
        }

        [Fact]
        public void Deserialize_RejectsUnknownEnumName()
        {
            const string json = "{ \"Overrides\": [ { \"X\": 1, \"Y\": 2, \"Tile\": \"Lava\", \"Prop\": \"None\" } ] }";

            Assert.ThrowsAny<JsonException>(() => SaveSerializer.Deserialize(json));
        }

        [Fact]
        public void SaveAndLoad_RestoresPlayerEditsOnTopOfGeneratedWorld()
        {
            var options = new WorldGenOptions(20261115);
            WorldMap WorldMap = WorldGen.Generate(options);

            // 找一格水面，填成土；这就是"玩家填湖"的那一步。
            (int wx, int wy) = (0, 0);
            bool found = false;
            for (int y = 0; y < WorldMap.Height && !found; y++)
            {
                for (int x = 0; x < WorldMap.Width; x++)
                {
                    if (WorldMap.GetTile(x, y) == TileKind.Water)
                    {
                        (wx, wy) = (x, y);
                        found = true;
                        break;
                    }
                }
            }

            Assert.True(found, "generated WorldMap had no water to fill — generator changed?");

            var state = new GameState { Seed = options.Seed, PlayerX = wx, PlayerY = wy };
            state.Overrides.Add(new TileOverride(wx, wy, TileKind.Soil, PropKind.None));
            string json = SaveSerializer.Serialize(state);

            // "重新开局"：用同一个 seed 再生成一份，然后把存档里的改动盖回去。
            GameState loaded = SaveSerializer.Deserialize(json);
            WorldMap rebuilt = WorldGen.Generate(new WorldGenOptions(loaded.Seed));
            Assert.Equal(TileKind.Water, rebuilt.GetTile(wx, wy));

            rebuilt.ApplyOverrides(loaded.Overrides);

            Assert.Equal(TileKind.Soil, rebuilt.GetTile(wx, wy));
            Assert.True(rebuilt.IsWalkable(wx, wy));
            Assert.Equal(wx, loaded.PlayerX);
            Assert.Equal(wy, loaded.PlayerY);
        }

        [Fact]
        public void Deserialize_DuplicateKeys_TakeLastValue()
        {
            // System.Text.Json 的默认行为；写下来是为了以后别被"顺手改成报错"惊到。
            GameState state = SaveSerializer.Deserialize("{\"Seed\": 1, \"Seed\": 2}");

            Assert.Equal(2, state.Seed);
        }

        [Fact]
        public void RoundTrip_WithManyOverrides_KeepsOrderAndValues()
        {
            var state = new GameState { Seed = 5 };
            var kinds = new[]
            {
                TileKind.Grass, TileKind.Soil, TileKind.TilledSoil,
                TileKind.Water, TileKind.Sand, TileKind.Path,
            };

            for (int i = 0; i < 200; i++)
            {
                state.Overrides.Add(new TileOverride(i % 96, i / 96, kinds[i % kinds.Length], PropKind.None));
            }

            GameState loaded = SaveSerializer.Deserialize(SaveSerializer.Serialize(state));

            Assert.Equal(200, loaded.Overrides.Count);
            Assert.Equal(state.Overrides, loaded.Overrides);
        }
    }
}
