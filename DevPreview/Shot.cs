using System;
using System.Globalization;
using Godot;

namespace AsahiMono;

/// <summary>
/// 开发期截图工具：实例化一个场景，跑若干帧后把**根视口**存成 PNG。
///
/// 两种模式（用法全文见 <c>docs/engine/PREVIEW-SHOT.md</c>）：
/// <list type="bullet">
///   <item><b>无参数 = 老行为</b>（逐像素不变）：实例化 <c>res://Scenes/Main.tscn</c>，第 20 帧让玩家往
///         +5,+5 走，第 45 帧存 <c>E:/AsahiMono/tmp/shot-iso.png</c>，第 130 帧存 <c>shot-iso-walk.png</c> 并退出。</item>
///   <item><b>带任一参数 = still 模式</b>：不动玩家，相机锁在指定格 / 指定 zoom，跑到第 N 帧存一张就退出。
///         这是给美术部门用的那条路 —— 以前相机和场景都写死，每次只能整屏截图再裁图放大。</item>
/// </list>
///
/// 参数（写在 <c>--</c> 之后，Godot 的 <see cref="OS.GetCmdlineUserArgs"/> 只收这部分）：
/// <code>
/// --camera-cell=6,6      相机对准这一格菱形的中心（格坐标，可小数）
/// --zoom=0.75            相机 zoom（夹 0.25..8；0.75 = 默认档，1 / 2 用来和极限对比）
/// --out=E:/x/shot.png    截图落盘路径（目录不存在会自动建）
/// --frames=45            跑到第几帧截图（截完立刻退出；≥2）
/// --scene=res://...      要实例化的场景（默认主场景）
/// --window=1280x720      截之前把窗口改成这个尺寸（默认不动，用命令行 --resolution 也行）
/// </code>
/// </summary>
public partial class Shot : Node
{
    private const string DefaultScene = "res://Scenes/Main.tscn";
    private const string DefaultOut = "E:/AsahiMono/tmp/shot-iso.png";
    private const string DefaultWalkOut = "E:/AsahiMono/tmp/shot-iso-walk.png";

    /// <summary>老行为的时间线（帧号），别改 —— 无参数时的输出要和历史截图逐像素一致。</summary>
    private const int LegacyWalkFrame = 20;
    private const int DefaultShotFrame = 45;
    private const int LegacyQuitFrame = 130;

    private int _frames;
    private Player? _player;
    private Camera2D? _camera;
    private bool _warnedNoCamera;

    // ---- 命令行解析结果 ----
    private bool _stillMode;
    private bool _helpOnly;
    private string _scenePath = DefaultScene;
    private string _outPath = DefaultOut;
    private int _shotFrame = DefaultShotFrame;
    private bool _hasCell;
    private Vector2 _cameraCell;
    private bool _hasZoom;
    private float _zoom = 1f;
    private Vector2I _windowSize;

    /// <inheritdoc />
    public override void _Ready()
    {
        ParseArgs(OS.GetCmdlineUserArgs());

        if (_helpOnly)
        {
            GD.Print(Usage());
            GetTree().Quit();
            return;
        }

        if (_windowSize.X > 0 && _windowSize.Y > 0)
        {
            GetWindow().Size = _windowSize;
        }

        var scene = GD.Load<PackedScene>(_scenePath);
        if (scene is null)
        {
            GD.PushError($"[shot] cannot load packed scene '{_scenePath}'");
            GetTree().Quit(2);
            return;
        }

        Node main = scene.Instantiate();
        AddChild(main);
        _player = main.GetNodeOrNull<Player>("Player");

        // GameRoot._Process 每帧把相机钉回玩家身上（像素对齐跟随），所以本节点必须比它**晚**跑，
        // 否则 --camera-cell 当场被覆盖。Godot 里 process_priority 越大 = 越晚处理。
        ProcessPriority = 1000;

        GD.Print($"[shot] scene {_scenePath}, mode {(_stillMode ? "still" : "legacy")}"
                  + (_hasCell ? $", camera-cell ({_cameraCell.X:0.##},{_cameraCell.Y:0.##})" : string.Empty)
                  + (_hasZoom ? $", zoom {_zoom:0.###}x" : string.Empty)
                  + (_stillMode ? $", frame {_shotFrame}, out {_outPath}" : string.Empty)
                  + (_windowSize.X > 0 ? $", window {_windowSize.X}x{_windowSize.Y}" : string.Empty));
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        _frames++;

        if (_stillMode)
        {
            // 每帧都压一次：GameRoot 的跟随在同一个 _Process 里跑过了。
            ApplyCameraOverride();

            if (_frames == _shotFrame)
            {
                SaveShot(_outPath);
                GetTree().Quit();
            }

            return;
        }

        // ---------------- 以下是无参数时的老行为，逐行保持 ----------------

        // 第 20 帧：让玩家斜着走 5 格，用来验证等距行走 + 遮挡 + 走路动画。
        if (_frames == LegacyWalkFrame && _player is not null && _player.IsoMode)
        {
            Vector2 here = _player.IsoGrid;
            Vector2 target = IsoWorldRenderer.GridToScreen(here.X + 5f, here.Y + 5f);
            _player.SetMoveTarget(target);
            GD.Print($"[shot] walk from {here} toward +5,+5");
        }

        if (_frames == DefaultShotFrame)
        {
            SaveShot(DefaultOut);
        }

        if (_frames == LegacyQuitFrame)
        {
            SaveShot(DefaultWalkOut);
            GD.Print($"[shot] walking grid = {_player?.IsoGrid}, facing = {_player?.IsoFacing}, moving = {_player?.IsoMoving}");
            GetTree().Quit();
        }
    }

    /// <summary>把相机拉到命令行指定的机位（没给 --camera-cell / --zoom 的项不动）。</summary>
    private void ApplyCameraOverride()
    {
        if (_camera is null)
        {
            _camera = GetViewport().GetCamera2D();
            if (_camera is null)
            {
                if (!_warnedNoCamera && (_hasCell || _hasZoom))
                {
                    _warnedNoCamera = true;
                    GD.PushWarning("[shot] this scene has no current Camera2D; --camera-cell/--zoom ignored");
                }

                return;
            }
        }

        if (_hasZoom)
        {
            _camera.Zoom = new Vector2(_zoom, _zoom);
        }

        if (_hasCell)
        {
            _camera.GlobalPosition = IsoWorldRenderer.GridToScreen(_cameraCell.X, _cameraCell.Y);
        }
    }

    /// <summary>存一张 PNG：路径里的目录不存在就建（截图工具最常见的失败就是目录没建）。</summary>
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
                    GD.PushWarning($"[shot] cannot create '{dir}' ({dirError}); writing anyway");
                }
            }
        }

        Error error = image.SavePng(target);
        GD.Print($"[shot] saved {image.GetWidth()}x{image.GetHeight()} -> {target} ({error})"
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
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                // 也接受 `--out path` 这种空格写法（美术部门十有八九会这么打）。
                value = args[++i];
            }

            switch (key)
            {
                case "--help":
                case "-h":
                    _helpOnly = true;
                    return;

                case "--camera-cell":
                {
                    if (TryParseCell(value, out Vector2 cell))
                    {
                        _cameraCell = cell;
                        _hasCell = true;
                        _stillMode = true;
                    }
                    else
                    {
                        GD.PushWarning($"[shot] bad --camera-cell '{value}', expected x,y (e.g. --camera-cell=6,6)");
                    }

                    break;
                }

                case "--zoom":
                {
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float zoom))
                    {
                        _zoom = Mathf.Clamp(zoom, 0.25f, 8f);
                        _hasZoom = true;
                        _stillMode = true;
                    }
                    else
                    {
                        GD.PushWarning($"[shot] bad --zoom '{value}', expected a number (e.g. --zoom=0.75)");
                    }

                    break;
                }

                case "--out":
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _outPath = value!;
                        _stillMode = true;
                    }
                    else
                    {
                        GD.PushWarning("[shot] --out needs a path (e.g. --out=E:/AsahiMono/tmp/a.png)");
                    }

                    break;
                }

                case "--frames":
                {
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames) && frames >= 2)
                    {
                        _shotFrame = frames;
                        _stillMode = true;
                    }
                    else
                    {
                        GD.PushWarning($"[shot] bad --frames '{value}', expected an integer >= 2");
                    }

                    break;
                }

                case "--scene":
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _scenePath = value!;
                    }
                    else
                    {
                        GD.PushWarning("[shot] --scene needs a res:// path");
                    }

                    break;
                }

                case "--window":
                {
                    if (TryParseWindow(value, out Vector2I size))
                    {
                        _windowSize = size;
                    }
                    else
                    {
                        GD.PushWarning($"[shot] bad --window '{value}', expected WxH (e.g. --window=1280x720)");
                    }

                    break;
                }

                default:
                    GD.PushWarning($"[shot] unknown argument '{raw}' (see docs/engine/PREVIEW-SHOT.md)");
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

    private static string Usage() =>
        "[shot] DevPreview/Shot.tscn — 开发期截图工具\n"
        + "  无参数: 老行为（Main.tscn → 第 45 帧 tmp/shot-iso.png → 第 130 帧 shot-iso-walk.png 退出）\n"
        + "  --camera-cell=x,y  相机对准该格中心\n"
        + "  --zoom=0.75        相机 zoom（0.25..8）\n"
        + "  --out=PATH         截图路径（目录自动建）\n"
        + "  --frames=N         跑到第 N 帧截图并退出（default 45）\n"
        + "  --scene=res://…    要实例化的场景（default res://Scenes/Main.tscn）\n"
        + "  --window=1280x720  截之前改窗口尺寸\n"
        + "  带任一参数即进入 still 模式：不动玩家、只截一张。详见 docs/engine/PREVIEW-SHOT.md";
}
