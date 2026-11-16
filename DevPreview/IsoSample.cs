using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AsahiMono.Core.World;
using Godot;

namespace AsahiMono.DevPreview;

/// <summary>
/// 等距（isometric）投影 + 4 向视角旋转的**技术样片**。
/// 只用来回答一个问题：这种视角转起来是什么手感、边缘伪影到什么程度。
/// 不参与正式游戏逻辑（正式工程仍是方格 TileMap）。
///
/// 运行：
///   godot --path . res://DevPreview/IsoSample.tscn          手动看（R 键转视角，S 存截图）
///   godot --path . res://DevPreview/IsoSample.tscn -- --iso-shot   自动存 4 张旋转截图后退出
/// </summary>
public partial class IsoSample : Node2D
{
    // 一次铺多少格（20 x 20 正好铺满 1280x720 的一块菱形岛）
    private const int Cells = 20;

    // 2:1 菱形瓦片
    private const int TileW = 64;
    private const int TileH = 32;
    private const int HalfW = TileW / 2;
    private const int HalfH = TileH / 2;

    private const string SheetLand = "res://Assets/input/lemmi-32x32-water-land/tilesheet-land-v5.png";
    private const string SheetWater = "res://Assets/input/lemmi-32x32-water-land/tilesheet-water.png";
    private const string BaseAnsimuz = "res://Assets/input/ansimuz-legend-of-faune/extracted/legend of faune files/";
    private const string TreePath = BaseAnsimuz + "Environment/PNG/tree.png";
    private const string ShrubPath = BaseAnsimuz + "Environment/PNG/shrub.png";
    private const string PlayerDownPath = BaseAnsimuz + "Sprites/Player/sprites/walk-down/walk-down-1.png";
    private const string PlayerUpPath = BaseAnsimuz + "Sprites/Player/sprites/walk-up/walk-up-1.png";
    private const string PlayerSidePath = BaseAnsimuz + "Sprites/Player/sprites/walk-side/walk-side-1.png";

    private const string OutDir = "E:/AsahiMono/tmp";

    private WorldMap _map = null!;
    private ImageTexture[] _grass = Array.Empty<ImageTexture>();
    private ImageTexture[] _sand = Array.Empty<ImageTexture>();
    private ImageTexture[] _water = Array.Empty<ImageTexture>();
    private Texture2D _tree = null!;
    private Texture2D _shrub = null!;
    private Texture2D _playerDown = null!;
    private Texture2D _playerUp = null!;
    private Texture2D _playerSide = null!;

    private int _rot;

    public override void _Ready()
    {
        _map = WorldGen.Generate(new WorldGenOptions(
            Seed: 20261115,
            Width: Cells,
            Height: Cells,
            WaterLevel: 0.30,
            TreeDensity: 0.12,
            RockDensity: 0.03));

        var land = GD.Load<Texture2D>(SheetLand);
        var water = GD.Load<Texture2D>(SheetWater);
        _grass = new[]
        {
            MakeDiamond(land, new Rect2I(0, 96, 32, 32)),
            MakeDiamond(land, new Rect2I(32, 96, 32, 32)),
            MakeDiamond(land, new Rect2I(64, 96, 32, 32)),
        };
        _sand = new[]
        {
            MakeDiamond(land, new Rect2I(0, 256, 32, 32)),
            MakeDiamond(land, new Rect2I(32, 256, 32, 32)),
        };
        _water = new[]
        {
            MakeDiamond(water, new Rect2I(64, 64, 32, 32)),
            MakeDiamond(water, new Rect2I(96, 96, 32, 32)),
            MakeDiamond(water, new Rect2I(128, 128, 32, 32)),
        };

        _tree = GD.Load<Texture2D>(TreePath);
        _shrub = GD.Load<Texture2D>(ShrubPath);
        _playerDown = GD.Load<Texture2D>(PlayerDownPath);
        _playerUp = GD.Load<Texture2D>(PlayerUpPath);
        _playerSide = GD.Load<Texture2D>(PlayerSidePath);

        GD.Print($"[iso] sample ready: {Cells}x{Cells}, spawn {_map.Spawn}, tiles {TileW}x{TileH}");

        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--iso-shot") >= 0)
            _ = ShootAll();
    }

    // ---- 生成菱形瓦片 ----------------------------------------------------

    /// <summary>
    /// 把一张 32x32 的方形俯视贴图，重采样成 64x32 的 2:1 菱形。
    /// 这就是"用正方形素材做等距"的全部代价所在：斜切带来的锯齿与纹理拉伸。
    /// </summary>
    private static ImageTexture MakeDiamond(Texture2D sheet, Rect2I src)
    {
        var srcImage = sheet.GetImage();
        var dst = Image.CreateEmpty(TileW, TileH, false, Image.Format.Rgba8);
        for (var py = 0; py < TileH; py++)
        {
            for (var px = 0; px < TileW; px++)
            {
                var dx = px - (TileW - 1) * 0.5;
                var dy = py - (TileH - 1) * 0.5;
                var u = dx / (TileW * 0.5);
                var v = dy / (TileH * 0.5);
                if (Math.Abs(u) + Math.Abs(v) > 1.0)
                {
                    dst.SetPixel(px, py, Colors.Transparent);
                    continue;
                }

                // 菱形坐标 -> 正方形贴图坐标（相当于旋转 45 度再压扁一半）
                var fx = (u + v + 1.0) * 0.5 * (src.Size.X - 1);
                var fy = (v - u + 1.0) * 0.5 * (src.Size.Y - 1);
                dst.SetPixel(px, py, SampleBilinear(srcImage, src.Position.X, src.Position.Y, src.Size, fx, fy));
            }
        }

        return ImageTexture.CreateFromImage(dst);
    }

    /// <summary>
    /// 双线性采样（Godot 的 Image.GetPixel 只有最近邻；斜切时最近邻会产生
    /// 明显的对角条纹，双线性能把条纹摊平成纹理感）。
    /// </summary>
    private static Color SampleBilinear(Image img, int ox, int oy, Vector2I size, double fx, double fy)
    {
        fx = Mathf.Clamp((float)fx, 0f, size.X - 1.001f);
        fy = Mathf.Clamp((float)fy, 0f, size.Y - 1.001f);
        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, size.X - 1);
        var y1 = Math.Min(y0 + 1, size.Y - 1);
        var tx = (float)(fx - x0);
        var ty = (float)(fy - y0);

        var c00 = img.GetPixel(ox + x0, oy + y0);
        var c10 = img.GetPixel(ox + x1, oy + y0);
        var c01 = img.GetPixel(ox + x0, oy + y1);
        var c11 = img.GetPixel(ox + x1, oy + y1);

        var top = c00.Lerp(c10, tx);
        var bottom = c01.Lerp(c11, tx);
        return top.Lerp(bottom, ty);
    }

    // ---- 坐标变换 --------------------------------------------------------

    private static (int X, int Y) Rotate(int x, int y, int rot)
    {
        var n = Cells - 1;
        return rot switch
        {
            1 => (n - y, x),
            2 => (n - x, n - y),
            3 => (y, n - x),
            _ => (x, y),
        };
    }

    /// <summary>稳定的每格哈希，用来选地面贴图变体（不用 System.Random，保持可复现）。</summary>
    private static int Hash(int x, int y)
    {
        var h = x * 73856093 ^ y * 19349663;
        h ^= h >> 13;
        return Math.Abs(h);
    }

    private Vector2 Origin(Vector2 viewport)
    {
        // 让整块菱形地图在画面里竖直居中（菱形高 = 2*(Cells-1)*HalfH + TileH）
        return new Vector2(viewport.X * 0.5f, viewport.Y * 0.5f - (Cells - 1) * HalfH);
    }

    private Vector2 Center(int x, int y, Vector2 origin)
    {
        var (rx, ry) = Rotate(x, y, _rot);
        return origin + new Vector2((rx - ry) * HalfW, (rx + ry) * HalfH);
    }

    // ---- 绘制 ------------------------------------------------------------

    public override void _Draw()
    {
        var viewport = GetViewportRect().Size;
        var origin = Origin(viewport);

        DrawRect(new Rect2(Vector2.Zero, viewport), new Color("#0d1a12"));

        // 第一遍：地面菱形（相邻菱形无缝拼合）
        for (var y = 0; y < Cells; y++)
        {
            for (var x = 0; x < Cells; x++)
            {
                var tile = _map.GetTile(x, y);
                var (variant, variantCount) = tile switch
                {
                    TileKind.Water => (Hash(x, y) % _water.Length, _water.Length),
                    TileKind.Sand => (Hash(x, y) % _sand.Length, _sand.Length),
                    _ => (Hash(x, y) % _grass.Length, _grass.Length),
                };
                _ = variantCount;
                var tex = tile switch
                {
                    TileKind.Water => _water[variant],
                    TileKind.Sand => _sand[variant],
                    _ => _grass[variant],
                };
                var c = Center(x, y, origin);
                DrawTexture(tex, c - new Vector2(HalfW, HalfH));
            }
        }

        // 第二遍：物件（竖直画法，按深度排序 —— 越靠下越后画）
        var props = new List<(int Depth, int X, int Y)>();
        for (var y = 0; y < Cells; y++)
        {
            for (var x = 0; x < Cells; x++)
            {
                if (_map.GetProp(x, y) == PropKind.None)
                    continue;
                var (rx, ry) = Rotate(x, y, _rot);
                props.Add((rx + ry, x, y));
            }
        }

        props.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        foreach (var (_, x, y) in props)
        {
            var c = Center(x, y, origin);
            var prop = _map.GetProp(x, y);
            if (prop == PropKind.Tree)
            {
                // 树 105x96，锚点放在菱形中心（树干底）；缩到约 1.2 格宽
                const float treeScale = 0.55f;
                var tw = _tree.GetWidth() * treeScale;
                var th = _tree.GetHeight() * treeScale;
                DrawEllipseShadow(c + new Vector2(0, -2), 14, 6);
                DrawSetTransform(c + new Vector2(0, -6), 0f, new Vector2(treeScale, treeScale));
                DrawTexture(_tree, new Vector2(-_tree.GetWidth() * 0.5f, -_tree.GetHeight()));
                DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
                _ = tw;
                _ = th;
            }
            else
            {
                DrawEllipseShadow(c + new Vector2(0, -2), 8, 4);
                DrawTexture(_shrub, c - new Vector2(_shrub.GetWidth() * 0.5f, _shrub.GetHeight() - 4));
            }
        }

        // 玩家（竖直画法，按旋转挑朝向）
        var (sx, sy) = _map.Spawn;
        var pc = Center(sx, sy, origin);
        var sprite = _rot switch
        {
            0 => _playerDown,
            2 => _playerUp,
            _ => _playerSide,
        };
        const float playerScale = 1.4f;
        DrawEllipseShadow(pc + new Vector2(0, -2), 11, 5);
        DrawSetTransform(pc + new Vector2(0, -2), 0f, new Vector2(playerScale, playerScale));
        DrawTexture(sprite, new Vector2(-sprite.GetWidth() * 0.5f, -sprite.GetHeight() + 2));
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);

        // HUD
        var font = ThemeDB.FallbackFont;
        var deg = _rot * 90;
        DrawString(font, new Vector2(24, 40), $"ISO SAMPLE  rot {_rot} ({deg} deg)   R = rotate", HorizontalAlignment.Left, -1, 26, new Color("#e8f4e0"));
        DrawString(font, new Vector2(24, 70), $"tile {TileW}x{TileH} (2:1)   upright props   ground = square art sheared into diamond", HorizontalAlignment.Left, -1, 16, new Color("#9fb59a"));
    }

    private void DrawEllipseShadow(Vector2 center, float rx, float ry)
    {
        const int segments = 16;
        var pts = new Vector2[segments];
        for (var i = 0; i < segments; i++)
        {
            var a = Mathf.Tau * i / segments;
            pts[i] = center + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
        }

        DrawColoredPolygon(pts, new Color(0f, 0f, 0f, 0.22f));
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        switch (key.Keycode)
        {
            case Key.R:
                _rot = (_rot + 1) % 4;
                QueueRedraw();
                break;
            case Key.S:
                SaveShot($"iso-manual-rot{_rot}.png");
                break;
        }
    }

    private void SaveShot(string name)
    {
        var img = GetViewport().GetTexture().GetImage();
        var path = $"{OutDir}/{name}";
        img.SavePng(path);
        GD.Print($"[iso] saved {path}");
    }

    private async Task ShootAll()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        for (var r = 0; r < 4; r++)
        {
            _rot = r;
            QueueRedraw();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            SaveShot($"iso-rot{r}.png");
        }

        GetTree().Quit();
    }
}
