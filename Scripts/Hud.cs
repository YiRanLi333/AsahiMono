using AsahiMono.Core;
using Godot;

namespace AsahiMono;

/// <summary>
/// Screen overlay: day + wall-clock time top-left, FPS top-right, and the iOS safe-area
/// guides (same trick as <see cref="Preview"/>: two translucent bands, toggled with <c>G</c>).
///
/// The clock never computes time itself -- <see cref="WorldClock"/> owns the tick counter
/// and <see cref="GameRoot"/> advances it. 1440 ticks = 1 day, so tick 600 is 10:00.
/// </summary>
public partial class Hud : Control
{
    /// <summary>iPhone 12 safe-area inset at the top, in points.</summary>
    private const int SafeTop = 47;

    /// <summary>iPhone 12 safe-area inset at the bottom, in points.</summary>
    private const int SafeBottom = 34;

    private static readonly Color GuideColor = new(1f, 0.15f, 0.45f, 0.22f);
    private static readonly Color GuideTextColor = new(1f, 0.45f, 0.65f);

    private Label _timeLabel = null!;
    private Label _fpsLabel = null!;
    private Label _cameraLabel = null!;
    private Control _guides = null!;

    private WorldClock? _clock;
    private int _lastTicks = -1;

    /// <inheritdoc />
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        // Below the notch / safe area, otherwise the iPhone preview body hides it.
        _timeLabel = MakeLabel(new Vector2(12, SafeTop + 5), 16, Colors.White);
        _timeLabel.Name = "TimeLabel";
        _timeLabel.Text = "第 1 天 00:00";

        _fpsLabel = MakeLabel(Vector2.Zero, 12, new Color(0.85f, 0.95f, 0.85f));
        _fpsLabel.Name = "FpsLabel";
        _fpsLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _fpsLabel.SetAnchorsPreset(LayoutPreset.TopWide);
        _fpsLabel.OffsetLeft = 0;
        _fpsLabel.OffsetTop = SafeTop + 5;
        _fpsLabel.OffsetRight = -12;
        _fpsLabel.OffsetBottom = SafeTop + 25;

        _cameraLabel = MakeLabel(Vector2.Zero, 12, new Color(0.92f, 0.9f, 0.75f));
        _cameraLabel.Name = "CameraLabel";
        _cameraLabel.Text = string.Empty;
        _cameraLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _cameraLabel.SetAnchorsPreset(LayoutPreset.TopWide);
        _cameraLabel.OffsetLeft = 0;
        _cameraLabel.OffsetTop = SafeTop + 24;
        _cameraLabel.OffsetRight = -12;
        _cameraLabel.OffsetBottom = SafeTop + 44;

        BuildGuides();
    }

    /// <summary>
    /// Show the current magnification and how much of the map fits on screen. Printed next to
    /// the FPS counter so a framing change can be judged without reading the console.
    /// </summary>
    public void SetCameraInfo(double zoom, int tileSize, Vector2 viewport)
    {
        double tilesX = viewport.X / (tileSize * zoom);
        double tilesY = viewport.Y / (tileSize * zoom);
        _cameraLabel.Text = $"zoom {zoom:0.##}x · {tilesX:0.#}×{tilesY:0.#} 格 · {viewport.X:0}×{viewport.Y:0}";
    }

    /// <summary>Hand the HUD the clock it should display.</summary>
    public void Bind(WorldClock clock)
    {
        _clock = clock;
        _lastTicks = -1;
        Refresh();
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        Refresh();

        if (_fpsLabel.Visible)
        {
            _fpsLabel.Text = $"FPS {Engine.GetFramesPerSecond()}";
        }
    }

    /// <inheritdoc />
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return;
        }

        switch (key.Keycode)
        {
            case Key.G:
                _guides.Visible = !_guides.Visible;
                GD.Print($"[hud] safe-area guides {(_guides.Visible ? "on" : "off")}");
                break;

            case Key.F3:
                _fpsLabel.Visible = !_fpsLabel.Visible;
                GD.Print($"[hud] fps counter {(_fpsLabel.Visible ? "on" : "off")}");
                break;
        }
    }

    private void Refresh()
    {
        if (_clock is null || _clock.TotalTicks == _lastTicks)
        {
            return;
        }

        _lastTicks = _clock.TotalTicks;

        int hour = _clock.TickOfDay / WorldClock.TicksPerHour;
        int minute = _clock.TickOfDay % WorldClock.TicksPerHour;
        _timeLabel.Text = $"第 {_clock.Day} 天 {hour:00}:{minute:00}";
    }

    private Label MakeLabel(Vector2 position, int fontSize, Color color)
    {
        var label = new Label
        {
            Position = position,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        label.AddThemeConstantOverride("outline_size", 4);
        AddChild(label);
        return label;
    }

    private void BuildGuides()
    {
        _guides = new Control
        {
            Name = "SafeAreaGuides",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        _guides.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_guides);

        AddBand(SafeTop, true, "safe top 47pt");
        AddBand(SafeBottom, false, "safe bottom 34pt");
    }

    private void AddBand(int height, bool top, string text)
    {
        var band = new ColorRect
        {
            Color = GuideColor,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        band.SetAnchorsPreset(top ? LayoutPreset.TopWide : LayoutPreset.BottomWide);
        band.OffsetTop = top ? 0 : -height;
        band.OffsetBottom = top ? height : 0;
        _guides.AddChild(band);

        var label = new Label
        {
            Text = text,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", GuideTextColor);
        label.SetAnchorsPreset(top ? LayoutPreset.TopLeft : LayoutPreset.BottomLeft);
        label.Position = new Vector2(8, top ? height + 2 : -height - 16);
        _guides.AddChild(label);
    }
}
