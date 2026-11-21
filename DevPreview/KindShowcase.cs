using System;
using AsahiMono.Core.World;
using Godot;

namespace AsahiMono;

/// <summary>
/// 地面材质**样张**：把 12 种 <see cref="TileKind"/> 各铺一块 6×6 的试验田，同一画面里并排看。
///
/// 用途：用户/美术/工程师对"这 12 种地皮各长什么样"有同一个参照物，不用去翻 assets 目录。
/// 只画地面（没有装饰、没有玩家），所以看到的差异全部来自瓦片本身 + 低频光照 + 过渡带。
///
/// 用法（配 <c>DevPreview/IsoDecoShot.tscn</c> 截图）：
/// <code>
/// Godot_v4.5.2-stable_mono_win64_console.exe --path E:\AsahiMono\app res://DevPreview/IsoDecoShot.tscn \
///   -- --scene=res://DevPreview/KindShowcase.tscn --zoom=1.0 --camera-cell=16,13 \
///      --out=E:/AsahiMono/tmp/kinds.png --window=1600,1000 --frames=45
/// </code>
/// </summary>
public partial class KindShowcase : Node2D
{
    /// <summary>每块试验田的边长（格）。</summary>
    private const int Patch = 6;

    /// <summary>块与块之间的草地间隔（格）。</summary>
    private const int Gutter = 2;

    /// <summary>四周留白（格）。</summary>
    private const int Margin = 2;

    private const int Columns = 4;

    /// <summary>要展出的材质，顺序 = 铺放顺序（跳过 Void）。</summary>
    private static readonly TileKind[] Kinds =
    {
        TileKind.Grass, TileKind.GrassWild, TileKind.GrassDry, TileKind.Soil,
        TileKind.TilledSoil, TileKind.SoilLoose, TileKind.Mud, TileKind.Sand,
        TileKind.Gravel, TileKind.Path, TileKind.StoneFloor, TileKind.Water,
    };

    /// <inheritdoc />
    public override void _Ready()
    {
        int rows = (Kinds.Length + Columns - 1) / Columns;
        int width = (Margin * 2) + (Columns * Patch) + ((Columns - 1) * Gutter);
        int height = (Margin * 2) + (rows * Patch) + ((rows - 1) * Gutter);

        var map = new WorldMap(width, height, 20261118);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                map.SetTile(x, y, TileKind.Grass);
            }
        }

        for (int i = 0; i < Kinds.Length; i++)
        {
            int col = i % Columns;
            int row = i / Columns;
            int x0 = Margin + (col * (Patch + Gutter));
            int y0 = Margin + (row * (Patch + Gutter));

            for (int dy = 0; dy < Patch; dy++)
            {
                for (int dx = 0; dx < Patch; dx++)
                {
                    map.SetTile(x0 + dx, y0 + dy, Kinds[i]);
                }
            }

            AddLabel(Kinds[i].ToString(), x0, y0, Patch);
        }

        var iso = new IsoWorldRenderer
        {
            Name = "IsoWorld",
            ZIndex = 0,
        };
        AddChild(iso);
        iso.Initialize(map, null, null, null, null, "off");

        var camera = new Camera2D
        {
            Name = "Camera",
            Zoom = Vector2.One,
            Position = IsoWorldRenderer.GridToScreen(width * 0.5f, height * 0.5f),
        };
        AddChild(camera);
        camera.MakeCurrent();

        GD.Print($"[kinds] showcase {width}x{height}, {Kinds.Length} patch(es) of {Patch}x{Patch}");
    }

    /// <summary>在每块试验田的上顶点放一个材质名，方便对着图说话。</summary>
    private void AddLabel(string text, int x0, int y0, int patch)
    {
        var label = new Label
        {
            Text = text,
            Position = IsoWorldRenderer.GridToScreen(x0 + (patch * 0.5f), y0 - 1f) + new Vector2(-40f, -8f),
            Modulate = new Color(1f, 1f, 1f, 0.92f),
            ZIndex = 100,
        };
        label.AddThemeColorOverride("font_color", new Color(0.06f, 0.06f, 0.08f));
        label.AddThemeColorOverride("font_shadow_color", new Color(1f, 1f, 1f, 0.55f));
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 1);
        label.AddThemeFontSizeOverride("font_size", 18);
        AddChild(label);
    }
}
