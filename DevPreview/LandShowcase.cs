using System.Collections.Generic;
using System.Threading.Tasks;
using AsahiMono.Core.Save;
using AsahiMono.Core.World;
using Godot;

namespace AsahiMono;

/// <summary>
/// 土地样片：一块手工摆的"各种土地"展示田，用来定 tile 规范与观感。
///
/// 覆盖：草地 / 土壤 / 犁过的田（干）/ 水（湖）/ 沙（湖岸）/ 石板路 / 树石杂草，
/// 以及它们之间的 wang 过渡。它不参与游戏逻辑，只是渲染器的可视检阅台。
/// 整块地是一个不规则的海岛：岛外是虚空，所以下沿会长出参考图那种厚土侧壁。
/// 右下角另有一块"四类交界"测试田（草/沙/田/水各占一个象限），专供 4x 放大检查。
///
/// 用法（带 --shot 会连出七张图并退出）：
///   godot --path . res://DevPreview/LandShowcase.tscn -- --shot
///   产物：tmp/g3-full.png  （zoom 0.46，整座岛）
///         tmp/g3.png       （zoom 1.0，原生像素率）
///         tmp/g3-zoom.png  （zoom 2.5，湖岸 + 岛的右下轮廓）
///         tmp/g3-macro.png （zoom 4.0，对着草地/沙/水/田地四类交界与海岸）
///         tmp/g3-water.png （zoom 4.0，水面特写：波纹不成格）
///         tmp/g3-tilled.png（zoom 4.0，田地特写：犁沟不像木板）
///         tmp/g3-grass.png （zoom 4.0，草地 + 海岸侧壁特写）
/// </summary>
public partial class LandShowcase : Node2D
{
    private const int MapW = 48;
    private const int MapH = 40;

    private const string OutDir = "E:/AsahiMono/tmp";

    /// <summary>四类交界测试田的中心格（草/沙/田/水在这里汇合）。</summary>
    private const int MacroX = 33;
    private const int MacroY = 26;

    /// <summary>参考图（岛屿那版）的底色，让岛的轮廓读起来和参考图对比容易。</summary>
    private static readonly Color Background = new(252 / 255f, 248 / 255f, 237 / 255f);

    private Camera2D _camera = null!;

    /// <inheritdoc />
    public override void _Ready()
    {
        bool wantShot = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--shot") >= 0;

        // 岛的四周是虚空 → 底色用参考图的奶油白，截图和参考图并排时对比才干净。
        RenderingServer.SetDefaultClearColor(Background);

        // 先让世界生成器给一张底图（只为了让 WorldMap 的数据结构合法），随后整体重绘。
        WorldMap map = WorldGen.Generate(new WorldGenOptions(7, MapW, MapH));
        map.ApplyOverrides(BuildLayout());

        var iso = new IsoWorldRenderer { Name = "Iso" };
        AddChild(iso);
        iso.Initialize(map, null);

        _camera = new Camera2D
        {
            Zoom = new Vector2(1f, 1f),
            Position = IsoWorldRenderer.GridToScreen(24f, 20f),
        };
        AddChild(_camera);
        _camera.MakeCurrent();

        GD.Print($"[land] showcase ready {MapW}x{MapH}, camera {_camera.Position}");

        if (wantShot)
        {
            // 存图尺寸固定 1280x720，不跟着窗口大小飘。
            DisplayServer.WindowSetSize(new Vector2I(1280, 720));
            _ = ShootAll();
        }
    }

    private async Task ShootAll()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // 48x40 的菱形总宽 = (48+40)*32 = 2752px、总高 = (48+40)*16 = 1376px，
        // 1280x720 要 0.46 倍才装得下整张图；Zoom 1.0 只能看到中间约 20 格。
        await Shot("g3-full.png", 0.46f, IsoWorldRenderer.GridToScreen(23f, 15f));
        await Shot("g3.png", 1.0f, IsoWorldRenderer.GridToScreen(24f, 18f));

        // 2.5x 对着湖岸 → 岛的右下轮廓，侧壁和水的浅滩同框。
        await Shot("g3-zoom.png", 2.5f, IsoWorldRenderer.GridToScreen(30f, 26f));

        // 4.0x 对着"草/沙/田/水"四类交界，且离海岸只有两格：厚侧壁必须在这个尺度里看得见。
        await Shot("g3-macro.png", 4.0f, IsoWorldRenderer.GridToScreen(MacroX, MacroY));

        // 4.0x 三种材质的特写：水面波纹不成格 / 犁沟不像木板 / 草地看不出重复单元。
        await Shot("g3-water.png", 4.0f, IsoWorldRenderer.GridToScreen(33f, 12f));
        await Shot("g3-tilled.png", 4.0f, IsoWorldRenderer.GridToScreen(10f, 12f));
        await Shot("g3-grass.png", 4.0f, IsoWorldRenderer.GridToScreen(22f, 26.5f));

        GetTree().Quit();
    }

    private async Task Shot(string name, float zoom, Vector2 position)
    {
        _camera.Zoom = new Vector2(zoom, zoom);
        _camera.Position = position;

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        Image image = GetViewport().GetTexture().GetImage();
        string path = $"{OutDir}/{name}";
        image.SavePng(path);
        GD.Print($"[land] saved {path} {image.GetWidth()}x{image.GetHeight()} zoom={zoom}");
    }

    /// <summary>
    /// 岛的形状：地图中上部一块不规则的陆地，四周（尤其是朝屏幕下方的两条边）是起伏的
    /// 海岸线。只有「邻居是虚空」的 se/sw 边才会长侧壁，所以这张图的下沿就是参考图里
    /// 那种一截一截的厚土剖面。四类交界测试田强制算陆地，保证 4x 那张一定同时拍到
    /// 交界点与海岸，不会拍到半个悬空的测试块。
    /// </summary>
    private static bool[,] IslandMask()
    {
        var land = new bool[MapW, MapH];
        for (int y = 0; y < MapH; y++)
        {
            for (int x = 0; x < MapW; x++)
            {
                float west = 1.4f + 1.3f * Mathf.Sin(y * 0.45f + 0.6f);
                float east = 45.0f - 2.2f * Mathf.Sin(y * 0.31f + 1.2f) - 1.5f * Mathf.Sin(y * 0.67f);
                float north = 1.2f + 1.1f * Mathf.Sin(x * 0.37f + 2.0f);
                float south = 29.0f - 2.4f * Mathf.Sin(x * 0.26f + 0.3f) - 1.3f * Mathf.Sin(x * 0.57f + 2.1f);
                land[x, y] = x >= west && x <= east && y >= north && y <= south;
            }
        }

        // 斜着相接的 1 格缺口会让侧壁中间露出底色（看着像轮廓漏了一块），
        // 所以先把"只靠对角相连"的缺口补成陆地，再摘掉孤零零的单格。
        for (int pass = 0; pass < 2; pass++)
        {
            var next = (bool[,])land.Clone();
            for (int y = 0; y < MapH; y++)
            {
                for (int x = 0; x < MapW; x++)
                {
                    bool n = y > 0 && land[x, y - 1];
                    bool s = y < MapH - 1 && land[x, y + 1];
                    bool w = x > 0 && land[x - 1, y];
                    bool e = x < MapW - 1 && land[x + 1, y];

                    if (!land[x, y])
                    {
                        next[x, y] = (n && s) || (w && e);
                    }
                    else if (!n && !s && !w && !e)
                    {
                        next[x, y] = false;
                    }
                }
            }

            land = next;
        }

        // 测试田整块钉死在陆地上，再在它的下方和右侧各挖掉一条（海湾/悬崖），
        // 这样 4x 那张一定同时拍到"四类交界"和两段厚侧壁。
        for (int y = MacroY - 6; y <= MacroY + 5; y++)
        {
            for (int x = MacroX - 6; x <= MacroX + 5; x++)
            {
                if (x < 0 || x >= MapW || y < 0 || y >= MapH)
                {
                    continue;
                }

                bool notch = (y >= MacroY + 4 && y <= MacroY + 6) || (x >= MacroX + 4 && x <= MacroX + 6);
                land[x, y] = !notch;
            }
        }

        return land;
    }

    /// <summary>整块地的手工布局：先在岛上铺草（岛外是虚空），再挖湖、铺沙岸、修路、圈田，最后摆交界测试田。</summary>
    private static List<TileOverride> BuildLayout()
    {
        bool[,] land = IslandMask();
        var overrides = new List<TileOverride>(MapW * MapH);

        // 1) 岛内全草地，岛外虚空（虚空才是侧壁的来源），物件清空。
        for (int y = 0; y < MapH; y++)
        {
            for (int x = 0; x < MapW; x++)
            {
                overrides.Add(new TileOverride(x, y, land[x, y] ? TileKind.Grass : TileKind.Void, PropKind.None));
            }
        }

        // 2) 湖 + 一圈沙岸（用椭圆距离，边界不是死板的正圆）。
        Vector2 lake = new(33.5f, 12.5f);
        for (int y = 0; y < MapH; y++)
        {
            for (int x = 0; x < MapW; x++)
            {
                if (!land[x, y] || InMacroBlock(x, y))
                {
                    continue;
                }

                float dx = (x - lake.X) / 1.35f;
                float dy = y - lake.Y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                if (dist <= 7.0f)
                {
                    overrides.Add(new TileOverride(x, y, TileKind.Water, PropKind.None));
                }
                else if (dist <= 8.6f)
                {
                    overrides.Add(new TileOverride(x, y, TileKind.Sand, PropKind.None));
                }
            }
        }

        // 3) 斜穿全图的石板路（-0.62 的斜率，等距下看起来接近 45°）。
        //    湖和沙岸上不铺路 —— 先铺路再挖湖的话，水面中间会横着一条灰带。
        for (int x = 0; x < MapW; x++)
        {
            float yc = 34.5f - x * 0.62f;
            for (int y = 0; y < MapH; y++)
            {
                if (Mathf.Abs(y - yc) > 1.15f)
                {
                    continue;
                }

                float dx = (x - lake.X) / 1.35f;
                float dist = Mathf.Sqrt(dx * dx + (y - lake.Y) * (y - lake.Y));
                if (!land[x, y] || dist <= 8.8f || InMacroBlock(x, y))
                {
                    continue;
                }

                overrides.Add(new TileOverride(x, y, TileKind.Path, PropKind.None));
            }
        }

        // 4) 一块田：外圈土壤、里面犁过的垄。
        for (int y = 7; y <= 17; y++)
        {
            for (int x = 4; x <= 16; x++)
            {
                if (!land[x, y])
                {
                    continue;
                }

                bool inner = x >= 5 && x <= 15 && y >= 8 && y <= 16;
                overrides.Add(new TileOverride(x, y, inner ? TileKind.TilledSoil : TileKind.Soil, PropKind.None));
            }
        }

        // 5) 四类交界测试田：以 (MacroX, MacroY) 为界的 8x8 两块拼四块。
        //    左上草、右上沙、左下犁田、右下水域 —— 一处同时含四条边、四个外角和
        //    中心那个四角交汇点，4x 放大时所有 wang 形态都在一屏里。
        for (int y = MacroY - 4; y <= MacroY + 3; y++)
        {
            for (int x = MacroX - 4; x <= MacroX + 3; x++)
            {
                TileKind kind = x < MacroX
                    ? (y < MacroY ? TileKind.Grass : TileKind.TilledSoil)
                    : (y < MacroY ? TileKind.Sand : TileKind.Water);
                overrides.Add(new TileOverride(x, y, kind, PropKind.None));
            }
        }

        // 6) 撒树 / 石 / 草，避开湖、路、田、交界测试田，并且只撒在陆地上。
        for (int y = 0; y < MapH; y++)
        {
            for (int x = 0; x < MapW; x++)
            {
                if (!land[x, y])
                {
                    continue;
                }

                bool inLake = Mathf.Sqrt(Mathf.Pow((x - lake.X) / 1.35f, 2f) + Mathf.Pow(y - lake.Y, 2f)) <= 8.8f;
                bool onPath = Mathf.Abs(y - (34.5f - x * 0.62f)) <= 1.3f;
                bool inField = x >= 3 && x <= 17 && y >= 6 && y <= 18;
                if (inLake || onPath || inField || InMacroBlock(x, y))
                {
                    continue;
                }

                int hash = (x * 73856093) ^ (y * 19349663);
                int bucket = (hash & 0x7fffffff) % 23;

                if (bucket == 0)
                {
                    overrides.Add(new TileOverride(x, y, TileKind.Grass, PropKind.Tree));
                }
                else if (bucket == 1)
                {
                    overrides.Add(new TileOverride(x, y, TileKind.Grass, PropKind.Rock));
                }
                else if (bucket <= 3)
                {
                    overrides.Add(new TileOverride(x, y, TileKind.Grass, PropKind.Weed));
                }
            }
        }

        return overrides;
    }

    /// <summary>交界测试田占的 8x8 块（含 1 格外扩，给 wang 边界留出草地）。</summary>
    private static bool InMacroBlock(int x, int y)
        => x >= MacroX - 5 && x <= MacroX + 4 && y >= MacroY - 5 && y <= MacroY + 5;
}
