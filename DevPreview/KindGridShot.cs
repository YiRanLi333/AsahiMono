using AsahiMono.Core.World;
using Godot;

namespace AsahiMono;

/// <summary>
/// 临时截图工具（用完即删）：12 种地皮各一块 5×5，块间留 1 格虚空，
/// 所以每块自带一圈侧壁，用来在真引擎里看 v5 地皮与三档壁厚。
/// 用法：godot --path . res://DevPreview/KindGridShot.tscn
/// </summary>
public partial class KindGridShot : Node2D
{
    private const int Block = 5;
    private const int Gap = 1;
    private const int Cols = 4;
    private const int Rows = 3;
    private const string Out = "E:/AsahiMono/tmp/kinds-engine.png";

    private static readonly TileKind[] Kinds =
    {
        TileKind.Grass, TileKind.Soil, TileKind.TilledSoil, TileKind.Water,
        TileKind.Sand, TileKind.Path, TileKind.GrassWild, TileKind.GrassDry,
        TileKind.Mud, TileKind.SoilLoose, TileKind.Gravel, TileKind.StoneFloor,
    };

    private int _frames;

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(new Color(252 / 255f, 248 / 255f, 237 / 255f));

        int w = Cols * (Block + Gap) + Gap;
        int h = Rows * (Block + Gap) + Gap;
        WorldMap map = WorldGen.Generate(new WorldGenOptions(4242, w, h));

        for (int i = 0; i < Kinds.Length; i++)
        {
            int cx = i % Cols;
            int cy = i / Cols;
            int ox = Gap + cx * (Block + Gap);
            int oy = Gap + cy * (Block + Gap);
            for (int y = 0; y < Block; y++)
            {
                for (int x = 0; x < Block; x++)
                {
                    map.SetTile(ox + x, oy + y, Kinds[i]);
                    map.SetProp(ox + x, oy + y, PropKind.None);
                }
            }
        }

        var renderer = new IsoWorldRenderer();
        AddChild(renderer);
        renderer.Initialize(map, null);

        var cam = new Camera2D();
        AddChild(cam);
        cam.Zoom = new Vector2(0.42f, 0.42f);
        float midX = (w - 1) / 2f;
        float midY = (h - 1) / 2f;
        cam.Position = IsoWorldRenderer.GridToScreen(midX, midY);
        cam.MakeCurrent();

        GD.Print($"[kinds] grid {w}x{h}, 12 kinds, camera {cam.Position}");
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_frames != 45)
        {
            return;
        }

        var image = GetViewport().GetTexture().GetImage();
        image.SavePng(Out);
        GD.Print($"[kinds] saved {image.GetWidth()}x{image.GetHeight()} -> {Out}");
        GetTree().Quit();
    }
}
