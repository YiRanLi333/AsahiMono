using AsahiMono.Core.World;
using Godot;

namespace AsahiMono;

/// <summary>
/// 等距（isometric 2:1）渲染器：把方格的 <see cref="WorldMap"/> 画成菱形地面 + 直立物件。
///
/// 设计要点（与 M1 的方格 <see cref="WorldRenderer"/> 并存，由 config.json 的
/// <c>projection</c> 选择）：
/// <list type="bullet">
///   <item>地面：32x32 的方格贴图被斜切成 64x32 的菱形（2:1），所以**美术资产不用重画**。</item>
///   <item>物件：直立画法，锚点在菱形中心，按 <c>rx + ry</c> 深度排序 —— 这就是等距的遮挡规则。</item>
///   <item>投影是线性的，所以"屏幕直线"仍是"网格直线"：点地移动不用改寻路。</item>
/// </list>
/// </summary>
public partial class IsoWorldRenderer : Node2D
{
    /// <summary>菱形瓦片宽（2:1 的 2）。</summary>
    public const int TileW = 64;

    /// <summary>菱形瓦片高（2:1 的 1）。</summary>
    public const int TileH = 32;

    /// <summary>半个菱形宽 —— 也就是相邻格子中心在屏幕 X 上的步长。</summary>
    public const int HalfW = TileW / 2;

    /// <summary>半个菱形高 —— 相邻格子中心在屏幕 Y 上的步长。</summary>
    public const int HalfH = TileH / 2;

    /// <summary>地面类型数量（Grass/Soil/TilledSoil/Water/Sand/Path）。</summary>
    private const int GroundKindCount = 6;

    /// <summary>每种地面的贴图变体数量（和 WorldRenderer 的 VariantsPerKind 保持一致）。</summary>
    private const int VariantsPerKind = 3;

    private WorldMap _map = null!;
    private Player _player = null!;

    private ImageTexture[][] _ground = System.Array.Empty<ImageTexture[]>();
    private Texture2D[] _props = System.Array.Empty<Texture2D>();
    private Texture2D[] _playerDown = System.Array.Empty<Texture2D>();
    private Texture2D[] _playerUp = System.Array.Empty<Texture2D>();
    private Texture2D[] _playerSide = System.Array.Empty<Texture2D>();

    private double _animTime;

    /// <summary>绑定世界与玩家，并预先烘焙好所有菱形贴图（只做一次）。</summary>
    public void Initialize(WorldMap map, Player player)
    {
        _map = map;
        _player = player;
        BakeTiles();

        // 玩家节点本身不参与绘制（由本渲染器按深度画），但位置仍由它算出来，
        // 这样相机跟随、存档取格子都不用改。
        _player.Visible = false;

        GD.Print($"[iso] renderer ready: {map.Width}x{map.Height}, tile {TileW}x{TileH} (2:1)");
    }

    // ---------------------------------------------------------------- 烘焙

    private void BakeTiles()
    {
        Image atlas = WorldRenderer.BuildAtlasImage();

        _ground = new ImageTexture[GroundKindCount][];
        for (int kind = 0; kind < GroundKindCount; kind++)
        {
            _ground[kind] = new ImageTexture[VariantsPerKind];
            for (int variant = 0; variant < VariantsPerKind; variant++)
            {
                int column = WorldRenderer.ColumnOf((TileKind)GroundKindIndexToKind(kind), variant);
                _ground[kind][variant] = MakeDiamond(atlas, column);
            }
        }

        _props = new[]
        {
            Upright(atlas, WorldRenderer.PropColumn(PropKind.Tree)),
            Upright(atlas, WorldRenderer.PropColumn(PropKind.Rock)),
            Upright(atlas, WorldRenderer.PropColumn(PropKind.Weed)),
        };

        _playerDown = LoadFrames("walk-down");
        _playerUp = LoadFrames("walk-up");
        _playerSide = LoadFrames("walk-side");
    }

    /// <summary>GroundKind 索引 → TileKind（跳过 Void，因为 Void 永不绘制）。</summary>
    private static TileKind GroundKindIndexToKind(int index) => index switch
    {
        0 => TileKind.Grass,
        1 => TileKind.Soil,
        2 => TileKind.TilledSoil,
        3 => TileKind.Water,
        4 => TileKind.Sand,
        _ => TileKind.Path,
    };

    private static Texture2D Upright(Image atlas, int column)
        => ImageTexture.CreateFromImage(atlas.GetRegion(new Rect2I(column * WorldRenderer.TileSize, 0, WorldRenderer.TileSize, WorldRenderer.TileSize)));

    private const string FauneSprites = "res://Assets/input/ansimuz-legend-of-faune/extracted/legend of faune files/Sprites/Player/sprites/";

    private static Texture2D[] LoadFrames(string dir)
    {
        // 只取第 1 / 3 帧做两帧循环：够表达"在走"，也不让包体多几十张贴图。
        var frames = new Texture2D[2];
        frames[0] = GD.Load<Texture2D>($"{FauneSprites}{dir}/{dir}-1.png");
        frames[1] = GD.Load<Texture2D>($"{FauneSprites}{dir}/{dir}-3.png");
        return frames;
    }

    /// <summary>把 32x32 方格贴图重采样成 64x32 菱形（双线性，避免 45° 条纹）。</summary>
    private static ImageTexture MakeDiamond(Image atlas, int column)
    {
        var dst = Image.CreateEmpty(TileW, TileH, false, Image.Format.Rgba8);
        int ox = column * WorldRenderer.TileSize;
        const int source = WorldRenderer.TileSize;

        for (int py = 0; py < TileH; py++)
        {
            for (int px = 0; px < TileW; px++)
            {
                double dx = px - (TileW - 1) * 0.5;
                double dy = py - (TileH - 1) * 0.5;
                double u = dx / (TileW * 0.5);
                double v = dy / (TileH * 0.5);

                if (System.Math.Abs(u) + System.Math.Abs(v) > 1.0)
                {
                    dst.SetPixel(px, py, Colors.Transparent);
                    continue;
                }

                double fx = (u + v + 1.0) * 0.5 * (source - 1);
                double fy = (v - u + 1.0) * 0.5 * (source - 1);
                dst.SetPixel(px, py, SampleBilinear(atlas, ox, fx, fy, source));
            }
        }

        return ImageTexture.CreateFromImage(dst);
    }

    private static Color SampleBilinear(Image img, int ox, double fx, double fy, int size)
    {
        int x0 = Mathf.Clamp((int)fx, 0, size - 1);
        int y0 = Mathf.Clamp((int)fy, 0, size - 1);
        int x1 = System.Math.Min(x0 + 1, size - 1);
        int y1 = System.Math.Min(y0 + 1, size - 1);
        float tx = (float)(fx - x0);
        float ty = (float)(fy - y0);

        Color c00 = img.GetPixel(ox + x0, y0);
        Color c10 = img.GetPixel(ox + x1, y0);
        Color c01 = img.GetPixel(ox + x0, y1);
        Color c11 = img.GetPixel(ox + x1, y1);

        return c00.Lerp(c10, tx).Lerp(c01.Lerp(c11, tx), ty);
    }

    // ---------------------------------------------------------------- 投影

    /// <summary>网格坐标（可为小数）→ 屏幕坐标。线性投影，方格直线在屏幕上也还是直线。</summary>
    public static Vector2 GridToScreen(float x, float y)
        => new((x - y) * HalfW, (x + y) * HalfH);

    /// <summary>屏幕坐标 → 网格坐标（<see cref="GridToScreen"/> 的逆）。</summary>
    public static Vector2 ScreenToGrid(Vector2 screen)
    {
        // screen.x = (x - y) * HalfW ; screen.y = (x + y) * HalfH
        float a = screen.X / HalfW;
        float b = screen.Y / HalfH;
        return new Vector2((a + b) * 0.5f, (b - a) * 0.5f);
    }

    // ---------------------------------------------------------------- 绘制

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        _animTime += delta;
        QueueRedraw();
    }

    /// <inheritdoc />
    public override void _Draw()
    {
        if (_map is null)
        {
            return;
        }

        DrawGround();

        // 物件 + 玩家统一按深度排序：rx + ry 越大越靠前（越后画）。
        // 用一层临时列表而不是每次分配两个：格子最多几万，够用了。
        var order = new System.Collections.Generic.List<(int Depth, int X, int Y, PropKind Prop)>();
        for (int y = 0; y < _map.Height; y++)
        {
            for (int x = 0; x < _map.Width; x++)
            {
                PropKind prop = _map.GetProp(x, y);
                if (prop == PropKind.None)
                {
                    continue;
                }

                order.Add((x + y, x, y, prop));
            }
        }

        Vector2 playerGrid = _player.IsoGrid;
        order.Add(((int)playerGrid.X + (int)playerGrid.Y, (int)playerGrid.X, (int)playerGrid.Y, PropKind.None));

        order.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        foreach ((int depth, int x, int y, PropKind prop) in order)
        {
            Vector2 center = GridToScreen(x, y);
            _ = depth;

            if (prop == PropKind.None)
            {
                DrawPlayer(playerGrid.X, playerGrid.Y);
            }
            else
            {
                DrawProp(prop, center);
            }
        }
    }

    private void DrawGround()
    {
        // 先画所有地面：菱形之间没有互相遮挡，顺序无所谓。
        for (int y = 0; y < _map.Height; y++)
        {
            for (int x = 0; x < _map.Width; x++)
            {
                TileKind tile = _map.GetTile(x, y);
                if (tile == TileKind.Void)
                {
                    continue;
                }

                int kindIndex = KindToGroundIndex(tile);
                int variant = WorldRenderer.VariantFor(x, y) % VariantsPerKind;
                ImageTexture tex = _ground[kindIndex][variant];
                DrawTexture(tex, GridToScreen(x, y) - new Vector2(HalfW, HalfH));
            }
        }
    }

    private static int KindToGroundIndex(TileKind kind) => kind switch
    {
        TileKind.Grass => 0,
        TileKind.Soil => 1,
        TileKind.TilledSoil => 2,
        TileKind.Water => 3,
        TileKind.Sand => 4,
        _ => 5,
    };

    private void DrawProp(PropKind prop, Vector2 center)
    {
        Texture2D tex = _props[(int)prop - 1];
        const float scale = 1.5f;
        float w = tex.GetWidth() * scale;

        // 影子先画（贴在菱形上），再画直立的物件本体。
        DrawShadow(center + new Vector2(0, -2), w * 0.34f, w * 0.16f);

        DrawSetTransform(center + new Vector2(0, -4), 0f, new Vector2(scale, scale));
        DrawTexture(tex, new Vector2(-tex.GetWidth() * 0.5f, -tex.GetHeight()));
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    private void DrawPlayer(float gx, float gy)
    {
        Vector2 center = GridToScreen(gx, gy);
        int facing = _player.IsoFacing;
        Texture2D[] frames = facing switch
        {
            0 => _playerDown,
            2 => _playerUp,
            _ => _playerSide,
        };

        bool moving = _player.IsoMoving;
        int frame = moving ? (int)(_animTime * 6.0) % frames.Length : 0;
        Texture2D tex = frames[frame];
        const float scale = 1.5f;
        bool flip = facing == 3;

        DrawShadow(center + new Vector2(0, -2), 7f, 3.5f);
        DrawSetTransform(center + new Vector2(0, -2), 0f, new Vector2(flip ? -scale : scale, scale));
        DrawTexture(tex, new Vector2(-tex.GetWidth() * 0.5f, -tex.GetHeight() + 2));
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    private void DrawShadow(Vector2 center, float rx, float ry)
    {
        const int segments = 14;
        var pts = new Vector2[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = Mathf.Tau * i / segments;
            pts[i] = center + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
        }

        DrawColoredPolygon(pts, new Color(0f, 0f, 0f, 0.22f));
    }
}
