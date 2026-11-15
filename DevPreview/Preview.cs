using Godot;

namespace AsahiMono;

/// <summary>
/// Desktop-only preview harness that runs the real game inside a fake iPhone 12
/// body, so that pressing "preview" gives the same "a phone is running on my
/// desktop" feeling as Apple's iOS Simulator (which does not exist on Windows).
///
/// It is a pure skin: the game is a real instance of <c>res://Scenes/Main.tscn</c>
/// running in a 390x844 SubViewport, the bezel / notch / status bar / safe-area
/// guides are drawn around it. Nothing here ships to the device -- the iOS build
/// uses Scenes/Main.tscn directly.
///
/// Keys: G toggles the safe-area guides, F11 toggles a zoomed 1:1 pixel view.
/// </summary>
public partial class Preview : Control
{
    // iPhone 12 logical resolution in points.
    private const int ScreenW = 390;
    private const int ScreenH = 844;

    // iOS safe-area insets for this device class (points).
    private const int SafeTop = 47;
    private const int SafeBottom = 34;

    private const int Bezel = 15;
    private const int BodyRadius = 54;
    private const int ScreenRadius = 40;

    private SubViewport _viewport = null!;
    private SubViewportContainer _screenHost = null!;
    private Control _guides = null!;
    private Panel _body = null!;

    public override void _Ready()
    {
        var win = GetWindow();
        win.Title = "AsahiMono - iPhone 12 preview (G = safe-area guides, F11 = zoom)";
        var frame = new Vector2I(ScreenW + 2 * Bezel + 96, ScreenH + 2 * Bezel + 132);
        win.MinSize = frame;
        win.Size = frame;

        // Dark studio background.
        var bg = new ColorRect { Color = new Color("14161a") };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        // Device body (bezel) = rounded black panel.
        _body = new Panel
        {
            CustomMinimumSize = new Vector2(ScreenW + 2 * Bezel, ScreenH + 2 * Bezel),
        };
        var bodyStyle = new StyleBoxFlat
        {
            BgColor = new Color("050608"),
            BorderColor = new Color("2a2d33"),
            AntiAliasing = true,
        };
        bodyStyle.SetCornerRadiusAll(BodyRadius);
        bodyStyle.SetBorderWidthAll(2);
        bodyStyle.ShadowColor = new Color(0, 0, 0, 0.55f);
        bodyStyle.ShadowSize = 24;
        _body.AddThemeStyleboxOverride("panel", bodyStyle);
        center.AddChild(_body);

        // Inner margin = bezel thickness, then the screen.
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", Bezel);
        margin.AddThemeConstantOverride("margin_right", Bezel);
        margin.AddThemeConstantOverride("margin_top", Bezel);
        margin.AddThemeConstantOverride("margin_bottom", Bezel);
        _body.AddChild(margin);

        // Screen: SubViewportContainer forwards mouse/touch input into the viewport.
        _screenHost = new SubViewportContainer { Stretch = true };
        margin.AddChild(_screenHost);

        _viewport = new SubViewport
        {
            Size = new Vector2I(ScreenW, ScreenH),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            HandleInputLocally = true,
            TransparentBg = false,
        };
        _screenHost.AddChild(_viewport);

        var scene = GD.Load<PackedScene>("res://Scenes/Main.tscn");
        if (scene == null)
        {
            GD.PushError("Preview: res://Scenes/Main.tscn not found");
            return;
        }
        _viewport.AddChild(scene.Instantiate());

        // Rounded screen corners (drawn on top so the viewport looks inset).
        var screenMask = new Panel
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ShowBehindParent = false,
        };
        screenMask.SetAnchorsPreset(LayoutPreset.FullRect);
        var maskStyle = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = new Color("050608"),
            AntiAliasing = true,
        };
        maskStyle.SetCornerRadiusAll(ScreenRadius);
        maskStyle.SetBorderWidthAll(10);
        screenMask.AddThemeStyleboxOverride("panel", maskStyle);
        margin.AddChild(screenMask);

        // Notch.
        var notch = new Panel
        {
            CustomMinimumSize = new Vector2(156, 30),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var notchStyle = new StyleBoxFlat { BgColor = new Color("050608"), AntiAliasing = true };
        notchStyle.CornerRadiusBottomLeft = 18;
        notchStyle.CornerRadiusBottomRight = 18;
        notch.AddThemeStyleboxOverride("panel", notchStyle);
        _body.AddChild(notch);
        notch.SetAnchorsPreset(LayoutPreset.TopLeft);
        notch.Position = new Vector2((ScreenW + 2 * Bezel - 156) / 2f, Bezel);
        notch.Size = new Vector2(156, 30);

        // Status bar (fake): time on the left, battery glyph on the right.
        var clock = new Label
        {
            Text = "9:41",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        clock.AddThemeFontSizeOverride("font_size", 15);
        clock.AddThemeColorOverride("font_color", Colors.White);
        _body.AddChild(clock);
        clock.Position = new Vector2(Bezel + 30, Bezel + 6);

        var battery = new Label
        {
            Text = "100%",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        battery.AddThemeFontSizeOverride("font_size", 13);
        battery.AddThemeColorOverride("font_color", Colors.White);
        _body.AddChild(battery);
        battery.Position = new Vector2(Bezel + ScreenW - 74, Bezel + 8);

        // Home indicator.
        var homeBar = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        var barStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.85f), AntiAliasing = true };
        barStyle.SetCornerRadiusAll(3);
        homeBar.AddThemeStyleboxOverride("panel", barStyle);
        _body.AddChild(homeBar);
        homeBar.Position = new Vector2((ScreenW + 2 * Bezel - 140) / 2f, Bezel + ScreenH - 12);
        homeBar.Size = new Vector2(140, 5);

        // Safe-area guides (toggled with G). Off by default so the frame looks clean.
        _guides = new Control { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _guides.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddChild(_guides);

        AddGuideBand(SafeTop, true);
        AddGuideBand(SafeBottom, false);

        GD.Print($"[preview] iPhone 12 frame: screen {ScreenW}x{ScreenH}, safe area top {SafeTop} / bottom {SafeBottom}");
    }

    private void AddGuideBand(int height, bool top)
    {
        var band = new ColorRect
        {
            Color = new Color(1f, 0.15f, 0.45f, 0.22f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        band.SetAnchorsPreset(LayoutPreset.TopLeft);
        band.Position = new Vector2(0, top ? 0 : ScreenH - height);
        band.Size = new Vector2(ScreenW, height);
        _guides.AddChild(band);

        var label = new Label
        {
            Text = top ? $"safe top {height}pt" : $"safe bottom {height}pt",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", new Color(1, 0.45f, 0.65f));
        _guides.AddChild(label);
        label.Position = new Vector2(8, top ? height + 2 : ScreenH - height - 16);
    }

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
                GD.Print($"[preview] safe-area guides {(_guides.Visible ? "on" : "off")}");
                break;
            case Key.F11:
                Zoom();
                break;
        }
    }

    private void Zoom()
    {
        var win = GetWindow();
        var frame = new Vector2I(ScreenW + 2 * Bezel + 96, ScreenH + 2 * Bezel + 132);
        var zoomed = new Vector2I(ScreenW * 2 + 2 * Bezel + 96, ScreenH + 2 * Bezel + 132);
        win.Size = win.Size == frame ? zoomed : frame;
        GD.Print($"[preview] window {win.Size.X}x{win.Size.Y}");
    }
}
