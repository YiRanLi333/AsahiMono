using AsahiMono.Core.World;
using Godot;

namespace AsahiMono;

/// <summary>
/// Turns a <see cref="WorldMap"/> into pixels without shipping a single art asset.
///
/// The whole tileset is generated at runtime from code: a 32 px tall strip of
/// procedurally drawn tiles (grass with blades, rippled water, pebbled path, a tree
/// with a trunk and a cast shadow, ...) wrapped in an <see cref="ImageTexture"/> and
/// a <see cref="TileSetAtlasSource"/>.
///
/// Every walkable kind gets <see cref="VariantsPerKind"/> variants so a flat field does
/// not read as one repeated stamp; <see cref="Paint"/> picks the variant from the cell
/// coordinate, so the same seed always paints the same picture.
///
/// Collision lives on the tileset (not on hand-placed bodies), so the physics server
/// can merge it per quadrant:
/// <list type="bullet">
///   <item>physics layer 0 -> water, full-cell polygon, on the world collision bit <c>1&lt;&lt;0</c></item>
///   <item>physics layer 1 -> tree / rock, full-cell polygon, on the world collision bit <c>1&lt;&lt;1</c></item>
/// </list>
/// <see cref="Player"/> masks both bits and never walks through water, trees or rocks.
/// Weeds and every ground tile stay walkable.
/// </summary>
public static class WorldRenderer
{
    /// <summary>
    /// Edge length of one tile in pixels. 32 px keeps a readable amount of detail at
    /// 390 x 844 points: at zoom 1.5 one tile is 48 pt on screen, the same framing a
    /// 16 px tile gave at zoom 3, but with four times the source detail.
    /// </summary>
    public const int TileSize = 32;

    /// <summary>How many interchangeable variants each walkable ground kind gets.</summary>
    public const int VariantsPerKind = 3;

    /// <summary>Physics layer index used for water on the tileset.</summary>
    public const int WaterPhysicsLayer = 0;

    /// <summary>Physics layer index used for blocking props (tree / rock) on the tileset.</summary>
    public const int PropPhysicsLayer = 1;

    /// <summary>World collision bit for water. Matches <see cref="WaterPhysicsLayer"/>.</summary>
    public const uint WaterCollisionBit = 1u << 0;

    /// <summary>World collision bit for blocking props. Matches <see cref="PropPhysicsLayer"/>.</summary>
    public const uint PropCollisionBit = 1u << 1;

    /// <summary>What <see cref="Player"/>'s body sits on.</summary>
    public const uint PlayerCollisionLayer = 1u << 2;

    /// <summary>What <see cref="Player"/> collides with: water + blocking props.</summary>
    public const uint PlayerCollisionMask = WaterCollisionBit | PropCollisionBit;

    /// <summary>Number of walkable ground kinds that get variants (Grass..Path).</summary>
    private const int GroundKindCount = 6;

    /// <summary>
    /// Atlas column where the props start. Ground columns are <c>1..18</c>, so the first
    /// prop (<see cref="PropKind.Tree"/>) lands on 19: ground columns are
    /// <c>GroundKindCount * VariantsPerKind</c> wide and prop columns are 1-based.
    /// </summary>
    private const int PropColumnOffset = GroundKindCount * VariantsPerKind;

    /// <summary>Atlas width in tiles: 19 ground columns + 1 transparent void column + 3 props = 22.</summary>
    private const int AtlasColumns = PropColumnOffset + 4;

    /// <summary>Source id of the single generated atlas inside the tileset.</summary>
    private const int AtlasSourceId = 0;

    /// <summary>
    /// Build the runtime tileset: procedural atlas + 2 physics layers + collision polygons
    /// on every tile that must block.
    /// </summary>
    public static TileSet BuildTileSet()
    {
        var tileSet = new TileSet
        {
            TileSize = new Vector2I(TileSize, TileSize),
        };

        tileSet.AddPhysicsLayer();

        // Physics layer 0: water. Physics layer 1: trees and rocks.
        // Godot 4.5.2 has no TileSet.SetPhysicsLayerName (layers are index-only), so the
        // layer meaning lives in these constants instead.
        tileSet.SetPhysicsLayerCollisionLayer(WaterPhysicsLayer, WaterCollisionBit);
        tileSet.SetPhysicsLayerCollisionMask(WaterPhysicsLayer, 0);

        tileSet.AddPhysicsLayer();
        tileSet.SetPhysicsLayerCollisionLayer(PropPhysicsLayer, PropCollisionBit);
        tileSet.SetPhysicsLayerCollisionMask(PropPhysicsLayer, 0);

        var source = new TileSetAtlasSource
        {
            Texture = BuildAtlasTexture(),
            TextureRegionSize = new Vector2I(TileSize, TileSize),
        };

        tileSet.AddSource(source, AtlasSourceId);

        for (int row = 0; row < AtlasRows(); row++)
        {
            for (int column = 0; column < AtlasColumns; column++)
            {
                source.CreateTile(new Vector2I(column, row));
            }
        }

        // Every water variant blocks on physics layer 0.
        for (int variant = 0; variant < VariantsPerKind; variant++)
        {
            AddFullCellCollision(source, ColumnOf(TileKind.Water, variant), WaterPhysicsLayer);
        }

        // Trees and rocks block on physics layer 1. A tree keeps its trunk passable at
        // the very bottom of the cell so the player can stand "behind" it.
        AddFullCellCollision(source, PropColumn(PropKind.Rock), PropPhysicsLayer);
        AddTrunkCollision(source, PropColumn(PropKind.Tree), PropPhysicsLayer);

        return tileSet;
    }

    /// <summary>Atlas row count (one row today; kept so the loose ends stay honest).</summary>
    private static int AtlasRows() => 1;

    /// <summary>
    /// Atlas column that draws <paramref name="kind"/> with <paramref name="variant"/>.
    /// Void is the transparent column 0 and has no variants.
    /// </summary>
    public static int ColumnOf(TileKind kind, int variant = 0)
    {
        int index = (int)kind;
        if (index <= 0)
        {
            return 0;
        }

        int clamped = Mathf.Clamp(index, 1, GroundKindCount);
        return 1 + ((clamped - 1) * VariantsPerKind) + Mathf.PosMod(variant, VariantsPerKind);
    }

    /// <summary>Atlas column that draws <paramref name="prop"/>. <see cref="PropKind.None"/> has none.</summary>
    public static int PropColumn(PropKind prop) => PropColumnOffset + (int)prop;

    /// <summary>Top-left pixel of a cell in world space.</summary>
    public static Vector2 CellToPixel(int x, int y) => new(x * TileSize, y * TileSize);

    /// <summary>Centre pixel of a cell -- also the spot a <see cref="CharacterBody2D"/> is dropped at.</summary>
    public static Vector2 CellCenterToPixel(int x, int y)
        => new((x * TileSize) + (TileSize / 2f), (y * TileSize) + (TileSize / 2f));

    /// <summary>Pixel position -> cell coordinate.</summary>
    public static Vector2I PixelToCell(Vector2 pixel)
        => new(Mathf.FloorToInt(pixel.X / TileSize), Mathf.FloorToInt(pixel.Y / TileSize));

    /// <summary>Deterministic variant for a cell, so the same cell always looks the same.</summary>
    public static int VariantFor(int x, int y)
    {
        unchecked
        {
            uint hash = ((uint)x * 73856093u) ^ ((uint)y * 19349663u);
            hash ^= hash >> 13;
            return (int)(hash % VariantsPerKind);
        }
    }

    /// <summary>
    /// Push every cell of <paramref name="world"/> into the two tile map layers.
    /// Ground first (water / grass / sand ...), then the props on top.
    /// </summary>
    public static void Paint(TileMapLayer ground, TileMapLayer props, WorldMap world)
    {
        ground.Clear();
        props.Clear();

        int cells = 0;
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var coords = new Vector2I(x, y);
                int variant = VariantFor(x, y);

                TileKind kind = world.GetTile(x, y);
                if (kind != TileKind.Void)
                {
                    ground.SetCell(coords, AtlasSourceId, new Vector2I(ColumnOf(kind, variant), 0));
                    cells++;
                }

                PropKind prop = world.GetProp(x, y);
                if (prop != PropKind.None)
                {
                    props.SetCell(coords, AtlasSourceId, new Vector2I(PropColumn(prop), 0));
                    cells++;
                }
            }
        }

        GD.Print($"[world] painted {cells} cells ({world.Width}x{world.Height} map, {TileSize}px tiles, {VariantsPerKind} variants)");
    }

    /// <summary>
    /// Build the whole atlas from code: 22 columns x 32 px wide, one row tall.
    /// Everything is drawn with rectangles and single pixels, seeded so the atlas is
    /// byte-identical on every run.
    /// </summary>
    private static ImageTexture BuildAtlasTexture()
        => ImageTexture.CreateFromImage(BuildAtlasImage());

    /// <summary>
    /// The same atlas as a raw <see cref="Image"/>, so the isometric renderer can shear the
    /// 32x32 cells into 2:1 diamonds instead of duplicating the drawing code.
    /// </summary>
    public static Image BuildAtlasImage()
    {
        Image image = Image.CreateEmpty(AtlasColumns * TileSize, TileSize, false, Image.Format.Rgba8);
        image.Fill(new Color(0, 0, 0, 0));

        DrawGrass(image, ColumnOf(TileKind.Grass, 0), 11);
        DrawGrass(image, ColumnOf(TileKind.Grass, 1), 23);
        DrawGrass(image, ColumnOf(TileKind.Grass, 2), 37);

        DrawSoil(image, ColumnOf(TileKind.Soil, 0), 41);
        DrawSoil(image, ColumnOf(TileKind.Soil, 1), 53);
        DrawSoil(image, ColumnOf(TileKind.Soil, 2), 67);

        DrawTilled(image, ColumnOf(TileKind.TilledSoil, 0), 71);
        DrawTilled(image, ColumnOf(TileKind.TilledSoil, 1), 83);
        DrawTilled(image, ColumnOf(TileKind.TilledSoil, 2), 97);

        DrawWater(image, ColumnOf(TileKind.Water, 0), 101);
        DrawWater(image, ColumnOf(TileKind.Water, 1), 113);
        DrawWater(image, ColumnOf(TileKind.Water, 2), 127);

        DrawSand(image, ColumnOf(TileKind.Sand, 0), 131);
        DrawSand(image, ColumnOf(TileKind.Sand, 1), 139);
        DrawSand(image, ColumnOf(TileKind.Sand, 2), 149);

        DrawPath(image, ColumnOf(TileKind.Path, 0), 151);
        DrawPath(image, ColumnOf(TileKind.Path, 1), 157);
        DrawPath(image, ColumnOf(TileKind.Path, 2), 163);

        DrawTree(image, PropColumn(PropKind.Tree));
        DrawRock(image, PropColumn(PropKind.Rock));
        DrawWeed(image, PropColumn(PropKind.Weed));

        return image;
    }

    // ---------------------------------------------------------------- ground tiles

    private static void DrawGrass(Image image, int column, int seed)
    {
        var rng = Seeded(seed);
        Fill(image, column, new Color("4e8c3a"));

        // Fine dither instead of big rectangles: bigger patches read as dirty blocks when
        // the camera is close, single-pixel noise reads as texture.
        for (int i = 0; i < 120; i++)
        {
            int x = rng.RandiRange(0, TileSize - 1);
            int y = rng.RandiRange(0, TileSize - 1);
            var tone = rng.Randf() switch
            {
                < 0.34f => new Color("498536"),
                < 0.68f => new Color("539240"),
                _ => new Color("488132"),
            };
            Pixel(image, column, x, y, tone);
        }

        // A few small rounded tufts break up the dither without forming a pattern.
        for (int i = 0; i < 6; i++)
        {
            int cx = rng.RandiRange(3, TileSize - 4);
            int cy = rng.RandiRange(3, TileSize - 4);
            int radius = rng.RandiRange(2, 3);
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if ((dx * dx) + (dy * dy) > radius * radius)
                    {
                        continue;
                    }

                    Pixel(image, column, Mathf.Clamp(cx + dx, 0, TileSize - 1),
                        Mathf.Clamp(cy + dy, 0, TileSize - 1),
                        rng.Randf() < 0.5f ? new Color("437c30") : new Color("4a8434"));
                }
            }
        }

        // Blades: a couple of pixels each, lighter on top, darker at the root.
        for (int i = 0; i < 16; i++)
        {
            int x = rng.RandiRange(1, TileSize - 2);
            int y = rng.RandiRange(2, TileSize - 1);
            int height = rng.RandiRange(2, 5);
            var tip = rng.Randf() < 0.5f ? new Color("63a349") : new Color("57a03f");
            for (int step = 0; step < height; step++)
            {
                int py = y - step;
                if (py >= 0)
                {
                    Pixel(image, column, x, py, step == height - 1 ? tip : new Color("437c30"));
                }
            }
        }
    }

    private static void DrawSoil(Image image, int column, int seed)
    {
        var rng = Seeded(seed);
        Fill(image, column, new Color("6f4f2c"));

        for (int i = 0; i < 18; i++)
        {
            int x = rng.RandiRange(0, TileSize - 1);
            int y = rng.RandiRange(0, TileSize - 1);
            var tone = rng.Randf() < 0.5f ? new Color("654726") : new Color("7a5833");
            Pixel(image, column, x, y, tone);
            if (rng.Randf() < 0.4f)
            {
                Pixel(image, column, Mathf.Min(x + 1, TileSize - 1), y, tone);
            }
        }
    }

    private static void DrawTilled(Image image, int column, int seed)
    {
        var rng = Seeded(seed);
        Fill(image, column, new Color("54391d"));

        // Ploughed rows: a lighter ridge every 8 px, offset per variant.
        int offset = seed % 4;
        for (int row = 0; row < TileSize; row += 8)
        {
            int y = (row + offset) % TileSize;
            Rect(image, column, 0, y, TileSize, 2, new Color("6b4a28"));
            Rect(image, column, 0, Mathf.Min(y + 2, TileSize - 1), TileSize, 1, new Color("472e16"));
        }

        for (int i = 0; i < 10; i++)
        {
            int x = rng.RandiRange(0, TileSize - 1);
            int y = rng.RandiRange(0, TileSize - 1);
            Pixel(image, column, x, y, new Color("7d5730"));
        }
    }

    private static void DrawWater(Image image, int column, int seed)
    {
        var rng = Seeded(seed);
        Fill(image, column, new Color("2b6ca3"));

        // Wave crests: short horizontal highlights, staggered per variant.
        int phase = seed % 12;
        for (int y = 0; y < TileSize; y += 6)
        {
            int length = rng.RandiRange(6, 12);
            int x = (y * 3 + phase) % (TileSize - length);
            Rect(image, column, x, y, length, 1, new Color("3f83bd"));
        }

        for (int i = 0; i < 8; i++)
        {
            int x = rng.RandiRange(0, TileSize - 1);
            int y = rng.RandiRange(0, TileSize - 1);
            Pixel(image, column, x, y, new Color("24588036"));
        }

        // A single bright sparkle so the surface reads as water, not blue plastic.
        int sx = rng.RandiRange(4, TileSize - 6);
        int sy = rng.RandiRange(4, TileSize - 4);
        Rect(image, column, sx, sy, 3, 1, new Color("8cc6ea"));
    }

    private static void DrawSand(Image image, int column, int seed)
    {
        var rng = Seeded(seed);
        Fill(image, column, new Color("d9c68b"));

        for (int i = 0; i < 22; i++)
        {
            int x = rng.RandiRange(0, TileSize - 1);
            int y = rng.RandiRange(0, TileSize - 1);
            Pixel(image, column, x, y, rng.Randf() < 0.5f ? new Color("cbb67a") : new Color("e4d5a1"));
        }

        // A ripple line, so sand does not read as flat beige.
        int ry = rng.RandiRange(4, TileSize - 6);
        for (int x = 2; x < TileSize - 2; x++)
        {
            int y = ry + (int)(2f * Mathf.Sin(x * 0.6f));
            Pixel(image, column, x, Mathf.Clamp(y, 0, TileSize - 1), new Color("c6b077"));
        }
    }

    private static void DrawPath(Image image, int column, int seed)
    {
        var rng = Seeded(seed);
        Fill(image, column, new Color("a0906f"));

        for (int i = 0; i < 9; i++)
        {
            int x = rng.RandiRange(0, TileSize - 3);
            int y = rng.RandiRange(0, TileSize - 3);
            int w = rng.RandiRange(2, 4);
            int h = rng.RandiRange(2, 3);
            Rect(image, column, x, y, w, h, new Color("8d7d5e"));
            Pixel(image, column, x, y, new Color("b3a382"));
        }
    }

    // ---------------------------------------------------------------- props

    private static void DrawTree(Image image, int column)
    {
        var rng = Seeded(211);

        // Cast shadow on the ground, bottom third.
        for (int y = 23; y < TileSize; y++)
        {
            int inset = Mathf.Max(0, 3 - (y - 23) / 3);
            Rect(image, column, 4 + inset, y, TileSize - 8 - (inset * 2), 1, new Color(0, 0, 0, 0.20f));
        }

        // Trunk first, canopy is drawn over its top.
        Rect(image, column, 14, 20, 4, 11, new Color("4a3320"));
        Rect(image, column, 15, 20, 1, 11, new Color("5f452c"));
        Rect(image, column, 12, 29, 8, 2, new Color("3a2716"));

        // Canopy: one squashed blob with a light source in the upper left, a dark rim and a
        // ragged edge, so it reads as a leafy volume instead of a green circle.
        var rim = new Color("143f22");
        var dark = new Color("1f5a30");
        var mid = new Color("2f8442");
        var light = new Color("5cbf6a");
        const float radiusX = 14.5f;
        const float radiusY = 12.5f;
        const float centerX = 15.5f;
        const float centerY = 12.5f;
        for (int y = 0; y < TileSize; y++)
        {
            for (int x = 0; x < TileSize; x++)
            {
                float nx = (x - centerX) / radiusX;
                float ny = (y - centerY) / radiusY;
                float d = Mathf.Sqrt((nx * nx) + (ny * ny));
                // Ragged silhouette: a per-pixel wobble of about one pixel.
                float wobble = (Hash01(x, y) - 0.5f) * 0.14f;
                if (d > 1.0f + wobble)
                {
                    continue;
                }

                if (d > 0.87f)
                {
                    Pixel(image, column, x, y, rim);
                    continue;
                }

                // Light from the upper left: brighter where the offset points that way.
                float lit = Mathf.Clamp(0.5f - ((nx * 0.55f) + (ny * 0.75f)) * 0.5f, 0f, 1f);
                lit = Mathf.Clamp((lit + 0.15f) - (d * 0.55f), 0f, 1f);
                var color = lit > 0.66f
                    ? mid.Lerp(light, (lit - 0.66f) / 0.34f)
                    : dark.Lerp(mid, lit / 0.66f);
                Pixel(image, column, x, y, color);
            }
        }

        // Two leaf notches on the rim keep the outline from being an ellipse.
        for (int i = 0; i < 10; i++)
        {
            int x = rng.RandiRange(3, TileSize - 4);
            int y = rng.RandiRange(1, 9);
            Pixel(image, column, x, y, rim);
        }
    }

    /// <summary>Stable per-pixel hash in <c>[0,1)</c>; used for silhouettes.</summary>
    private static float Hash01(int x, int y)
    {
        uint h = (uint)((x * 374761393) + (y * 668265263));
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h & 0xFFFFFF) / 16777216f;
    }

    private static void DrawRock(Image image, int column)
    {
        Rect(image, column, 6, 24, 20, 6, new Color(0, 0, 0, 0.18f));
        Disc(image, column, 16, 20, 10, new Color("5c5c63"));
        Disc(image, column, 15, 18, 8, new Color("7d7d85"));
        Disc(image, column, 13, 16, 5, new Color("9a9aa3"));
        Rect(image, column, 11, 14, 3, 2, new Color("b5b5bd"));
        Pixel(image, column, 20, 22, new Color("4a4a52"));
    }

    private static void DrawWeed(Image image, int column)
    {
        var rng = Seeded(307);
        for (int blade = 0; blade < 4; blade++)
        {
            int baseX = 10 + (blade * 4);
            int height = rng.RandiRange(8, 14);
            int lean = blade % 2 == 0 ? 1 : -1;
            for (int step = 0; step < height; step++)
            {
                int x = baseX + ((step / 4) * lean);
                int y = TileSize - 4 - step;
                Pixel(image, column, Mathf.Clamp(x, 0, TileSize - 1), Mathf.Clamp(y, 0, TileSize - 1),
                    step > height - 3 ? new Color("a8c05e") : new Color("7f9440"));
            }
        }
    }

    // ---------------------------------------------------------------- drawing helpers

    private static RandomNumberGenerator Seeded(int seed)
    {
        var rng = new RandomNumberGenerator();
        rng.Seed = (ulong)seed;
        return rng;
    }

    private static void Fill(Image image, int column, Color color)
        => image.FillRect(new Rect2I(column * TileSize, 0, TileSize, TileSize), color);

    private static void Pixel(Image image, int column, int x, int y, Color color)
    {
        if (x < 0 || y < 0 || x >= TileSize || y >= TileSize)
        {
            return;
        }

        image.SetPixel((column * TileSize) + x, y, color);
    }

    private static void Rect(Image image, int column, int x, int y, int w, int h, Color color)
    {
        for (int py = y; py < y + h; py++)
        {
            for (int px = x; px < x + w; px++)
            {
                Pixel(image, column, px, py, color);
            }
        }
    }

    private static void Disc(Image image, int column, int cx, int cy, int radius, Color color)
    {
        int rr = radius * radius;
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if ((x * x) + (y * y) <= rr)
                {
                    Pixel(image, column, cx + x, cy + y, color);
                }
            }
        }
    }

    // ---------------------------------------------------------------- collision

    /// <summary>
    /// Give one atlas tile a full-cell collision polygon on one physics layer.
    ///
    /// The polygon is expressed in the tile's local space, whose origin is the tile
    /// <b>centre</b> (that is the same transform <c>TileSet.MapToLocal</c> returns), so a
    /// full 32x32 cell is <c>-16..+16</c> on both axes. <c>SetCollisionPolygonPoints</c> in
    /// Godot 4.5 takes a <see cref="Vector2"/>[] and the point count is implied by the array,
    /// not passed separately.
    /// </summary>
    private static void AddFullCellCollision(TileSetAtlasSource source, int column, int physicsLayer)
    {
        TileData data = source.GetTileData(new Vector2I(column, 0), 0);
        data.AddCollisionPolygon(physicsLayer);

        float half = TileSize / 2f;
        data.SetCollisionPolygonPoints(
            physicsLayer,
            0,
            new Vector2[]
            {
                new(-half, -half),
                new(half, -half),
                new(half, half),
                new(-half, half),
            });
    }

    /// <summary>
    /// Tree collision: a narrow trunk box in the lower middle, not the whole cell, so the
    /// canopy can overlap the player's head and the player can walk right up to the trunk.
    /// </summary>
    private static void AddTrunkCollision(TileSetAtlasSource source, int column, int physicsLayer)
    {
        TileData data = source.GetTileData(new Vector2I(column, 0), 0);
        data.AddCollisionPolygon(physicsLayer);

        float half = TileSize / 2f;
        data.SetCollisionPolygonPoints(
            physicsLayer,
            0,
            new Vector2[]
            {
                new(-6, -2),
                new(6, -2),
                new(6, half),
                new(-6, half),
            });
    }
}
