using AsahiMono.Core.World;
using Godot;

namespace AsahiMono;

/// <summary>
/// Turns a <see cref="WorldMap"/> into pixels without shipping a single art asset.
///
/// The whole tileset is generated at runtime: one 16 px tall strip of flat colours,
/// seven columns for <see cref="TileKind"/> (so the enum value <i>is</i> the atlas
/// column) plus three more columns for the blocking props, wrapped in an
/// <see cref="ImageTexture"/> and an <see cref="TileSetAtlasSource"/>.
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
    /// <summary>Edge length of one tile in pixels. Vertical screen is 390 x 844 points.</summary>
    public const int TileSize = 16;

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

    /// <summary>Atlas column of <see cref="TileKind.Grass"/>..<see cref="TileKind.Path"/>; <c>1 + column</c>.</summary>
    private const int TileColumns = 7;

    /// <summary>Atlas columns for <see cref="PropKind.Tree"/>..<see cref="PropKind.Weed"/> start here.</summary>
    private const int PropColumnOffset = TileColumns - 1;

    /// <summary>Atlas width in tiles: 7 tiles + 3 props.</summary>
    private const int AtlasColumns = TileColumns + 3;

    /// <summary>Source id of the single generated atlas inside the tileset.</summary>
    private const int AtlasSourceId = 0;

    /// <summary>
    /// Build the runtime tileset: flat-colour atlas + 2 physics layers + collision polygons
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

        for (int column = 0; column < AtlasColumns; column++)
        {
            var atlasCoords = new Vector2I(column, 0);
            source.CreateTile(atlasCoords);
        }

        // Water blocks movement on physics layer 0.
        AddFullCellCollision(source, ColumnOf(TileKind.Water), WaterPhysicsLayer);

        // Trees and rocks block movement on physics layer 1.
        AddFullCellCollision(source, PropColumn(PropKind.Tree), PropPhysicsLayer);
        AddFullCellCollision(source, PropColumn(PropKind.Rock), PropPhysicsLayer);

        return tileSet;
    }

    /// <summary>Atlas column that draws <paramref name="kind"/>. Void is the transparent column 0.</summary>
    public static int ColumnOf(TileKind kind) => (int)kind;

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

                TileKind kind = world.GetTile(x, y);
                if (kind != TileKind.Void)
                {
                    ground.SetCell(coords, AtlasSourceId, new Vector2I(ColumnOf(kind), 0));
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

        GD.Print($"[world] painted {cells} cells ({world.Width}x{world.Height} map, {TileSize}px tiles)");
    }

    /// <summary>Colour of each generated tile, by atlas column.</summary>
    private static Color ColorOfColumn(int column) => column switch
    {
        0 => new Color(0, 0, 0, 0),          // Void - never drawn
        1 => new Color("4e8c3a"),            // Grass
        2 => new Color("6f4f2c"),            // Soil
        3 => new Color("54391d"),            // TilledSoil
        4 => new Color("2b6ca3"),            // Water
        5 => new Color("d9c68b"),            // Sand
        6 => new Color("a0906f"),            // Path
        7 => new Color("163a1f"),            // Tree
        8 => new Color("7d7d85"),            // Rock
        9 => new Color("7f9440"),            // Weed
        _ => new Color(1, 0, 1),             // magenta: an atlas column nobody defined
    };

    /// <summary>Build the flat-colour 16 px tall atlas: 10 columns x 16 px = 160 x 16.</summary>
    private static ImageTexture BuildAtlasTexture()
    {
        Image image = Image.CreateEmpty(AtlasColumns * TileSize, TileSize, false, Image.Format.Rgba8);
        image.Fill(new Color(0, 0, 0, 0));

        for (int column = 0; column < AtlasColumns; column++)
        {
            image.FillRect(new Rect2I(column * TileSize, 0, TileSize, TileSize), ColorOfColumn(column));
        }

        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Give one atlas tile a full-cell collision polygon on one physics layer.
    ///
    /// The polygon is expressed in the tile's local space, whose origin is the tile
    /// <b>centre</b> (that is the same transform <c>TileSet.MapToLocal</c> returns), so a
    /// full 16x16 cell is <c>-8..+8</c> on both axes. <c>SetCollisionPolygonPoints</c> in
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
}
