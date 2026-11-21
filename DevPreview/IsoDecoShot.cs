using System.Globalization;
using Godot;

namespace AsahiMono;

/// <summary>
/// 等距**装饰**验收截图工具（工程的 code 部门自测用，和 <see cref="Shot"/> 同一套路，但独立一份，
/// 免得改动 <see cref="Shot"/> 的逐像素历史行为）。
///
/// 做的事：实例化 <c>res://Scenes/Main.tscn</c> → 每一帧把相机钉到指定格/指定 zoom →
/// 跑到第 <c>--frames</c> 帧（默认 60，够让 <c>IsoWorldRenderer</c> 打印第一行 draw calls）→
/// 把根视口存成 PNG → 退出。
///
/// 参数（写在 <c>--</c> 之后，<see cref="OS.GetCmdlineUserArgs"/> 只收这部分）：
/// <code>
/// --zoom=1.5              相机 zoom（夹 0.25..8；默认 1.5 = 新默认档）
/// --camera-cell=6,6       相机对准该格菱形中心（格坐标，可小数；默认 6.5,6.5 = 岛心）
/// --out=E:/x/a.png        截图落盘路径（目录不存在会自动建）
/// --frames=60             跑到第几帧截图（≥2）
/// --window=1280x720       截之前改窗口尺寸（可选）
/// --scene=res://…         要实例化的场景（默认主场景）
/// </code>
/// 例：
/// <code>
/// Godot_v4.5.2-stable_mono_win64_console.exe --path E:\AsahiMono\app res://DevPreview/IsoDecoShot.tscn \
///   -- --zoom=0.75 --camera-cell=6.5,6.5 --out=E:/AsahiMono/tmp/deco-full.png
/// </code>
/// </summary>
public partial class IsoDecoShot : Node
{
    private const string DefaultScene = "res://Scenes/Main.tscn";
    private const string DefaultOut = "E:/AsahiMono/tmp/deco-z15.png";
    private const float DefaultZoom = 1.5f;
    private const int DefaultFrame = 60;

    /// <summary>岛心（13×13 的 refblock 岛 = 6.5,6.5）。</summary>
    private static readonly Vector2 DefaultCell = new(6.5f, 6.5f);

    private int _frames;
    private Camera2D? _camera;
    private bool _warnedNoCamera;

    private string _scenePath = DefaultScene;
    private string _outPath = DefaultOut;
    private float _zoom = DefaultZoom;
    private Vector2 _cell = DefaultCell;
    private int _shotFrame = DefaultFrame;
    private Vector2I _windowSize;

    /// <inheritdoc />
    public override void _Ready()
    {
        ParseArgs(OS.GetCmdlineUserArgs());

        if (_windowSize.X > 0 && _windowSize.Y > 0)
        {
            GetWindow().Size = _windowSize;
        }

        var scene = GD.Load<PackedScene>(_scenePath);
        if (scene is null)
        {
            GD.PushError($"[decoshot] cannot load packed scene '{_scenePath}'");
            GetTree().Quit(2);
            return;
        }

        Node main = scene.Instantiate();
        AddChild(main);

        // GameRoot._Process 每帧把相机钉回玩家身上；本节点必须比它晚跑才盖得住。
        ProcessPriority = 1000;

        GD.Print($"[decoshot] scene {_scenePath}, camera-cell ({_cell.X:0.##},{_cell.Y:0.##}),"
                  + $" zoom {_zoom:0.###}x, frame {_shotFrame}, out {_outPath}");
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        _frames++;
        ApplyCameraOverride();

        if (_frames == _shotFrame)
        {
            SaveShot(_outPath);
            GetTree().Quit();
        }
    }

    private void ApplyCameraOverride()
    {
        if (_camera is null)
        {
            _camera = GetViewport().GetCamera2D();
            if (_camera is null)
            {
                if (!_warnedNoCamera)
                {
                    _warnedNoCamera = true;
                    GD.PushWarning("[decoshot] this scene has no current Camera2D; camera overrides ignored");
                }

                return;
            }
        }

        _camera.Zoom = new Vector2(_zoom, _zoom);
        _camera.GlobalPosition = IsoWorldRenderer.GridToScreen(_cell.X, _cell.Y);
    }

    private void SaveShot(string path)
    {
        string target = path.Replace('\\', '/');
        Image image = GetViewport().GetTexture().GetImage();

        int slash = target.LastIndexOf('/');
        if (slash > 0)
        {
            string dir = target[..slash];
            if (!DirAccess.DirExistsAbsolute(dir))
            {
                Error dirError = DirAccess.MakeDirRecursiveAbsolute(dir);
                if (dirError != Error.Ok)
                {
                    GD.PushWarning($"[decoshot] cannot create '{dir}' ({dirError}); writing anyway");
                }
            }
        }

        Error error = image.SavePng(target);
        GD.Print($"[decoshot] saved {image.GetWidth()}x{image.GetHeight()} -> {target} ({error})"
                  + (_camera is not null
                      ? $" camera ({_camera.GlobalPosition.X:0.#},{_camera.GlobalPosition.Y:0.#}) zoom {_camera.Zoom.X:0.###}x"
                      : string.Empty));
    }

    private void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string raw = args[i];
            if (raw.Length == 0)
            {
                continue;
            }

            string key = raw;
            string? value = null;
            int eq = raw.IndexOf('=');
            if (eq > 0)
            {
                key = raw[..eq];
                value = raw[(eq + 1)..];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", System.StringComparison.Ordinal))
            {
                value = args[++i];
            }

            switch (key)
            {
                case "--zoom":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float zoom))
                    {
                        _zoom = Mathf.Clamp(zoom, 0.25f, 8f);
                    }
                    else
                    {
                        GD.PushWarning($"[decoshot] bad --zoom '{value}', expected a number");
                    }

                    break;

                case "--camera-cell":
                    if (TryParseCell(value, out Vector2 cell))
                    {
                        _cell = cell;
                    }
                    else
                    {
                        GD.PushWarning($"[decoshot] bad --camera-cell '{value}', expected x,y");
                    }

                    break;

                case "--out":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _outPath = value!;
                    }
                    else
                    {
                        GD.PushWarning("[decoshot] --out needs a path");
                    }

                    break;

                case "--frames":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames) && frames >= 2)
                    {
                        _shotFrame = frames;
                    }
                    else
                    {
                        GD.PushWarning($"[decoshot] bad --frames '{value}', expected an integer >= 2");
                    }

                    break;

                case "--window":
                    if (TryParseWindow(value, out Vector2I size))
                    {
                        _windowSize = size;
                    }
                    else
                    {
                        GD.PushWarning($"[decoshot] bad --window '{value}', expected WxH");
                    }

                    break;

                case "--scene":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _scenePath = value!;
                    }
                    else
                    {
                        GD.PushWarning("[decoshot] --scene needs a res:// path");
                    }

                    break;

                default:
                    GD.PushWarning($"[decoshot] unknown argument '{raw}'");
                    break;
            }
        }
    }

    private static bool TryParseCell(string? text, out Vector2 cell)
    {
        cell = Vector2.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text!.Split(',');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
        {
            return false;
        }

        cell = new Vector2(x, y);
        return true;
    }

    private static bool TryParseWindow(string? text, out Vector2I size)
    {
        size = Vector2I.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text!.Split('x', 'X');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0].Trim(), out int w) || !int.TryParse(parts[1].Trim(), out int h) || w < 16 || h < 16)
        {
            return false;
        }

        size = new Vector2I(w, h);
        return true;
    }
}
