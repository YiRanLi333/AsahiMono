using Godot;

namespace AsahiMono;

/// <summary>
/// A floating touch stick drawn entirely with <see cref="CanvasItem._Draw"/> calls --
/// no texture, no theme, no scene file needed.
///
/// It appears wherever the finger (or the mouse) lands inside the joystick zone and
/// vanishes on release, so it never covers the world when nobody is holding it.
/// <see cref="Direction"/> is the clamped offset of the knob, magnitude 0..1, with a
/// small dead zone so a resting thumb does not make the player crawl.
/// </summary>
public partial class VirtualJoystick : Control
{
    /// <summary>Radius of the static base plate, in points.</summary>
    public const float BaseRadius = 48f;

    /// <summary>Radius of the draggable knob, in points.</summary>
    public const float KnobRadius = 22f;

    /// <summary>Fraction of the base radius that is ignored as thumb jitter.</summary>
    public const float DeadZone = 0.15f;

    /// <summary>Full square size of the control: room for the base plus a soft margin.</summary>
    private const float BoxSize = 2f * (BaseRadius + 8f);

    private static readonly Color BaseFill = new(0.10f, 0.13f, 0.18f, 0.30f);
    private static readonly Color BaseRim = new(1f, 1f, 1f, 0.32f);
    private static readonly Color KnobFill = new(0.72f, 0.84f, 1f, 0.45f);
    private static readonly Color KnobRim = new(0.90f, 0.95f, 1f, 0.55f);

    private Vector2 _knobOffset;

    /// <summary>Clamped stick direction, magnitude 0..1. Zero when idle or inside the dead zone.</summary>
    public Vector2 Direction { get; private set; }

    /// <summary>True while the stick is held down.</summary>
    public bool Active { get; private set; }

    /// <inheritdoc />
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(BoxSize, BoxSize);
        Size = new Vector2(BoxSize, BoxSize);
        Visible = false;
    }

    /// <summary>Pop the stick up centred on a viewport-space point (usually where the touch started).</summary>
    public void Press(Vector2 viewportPosition)
    {
        Position = viewportPosition - new Vector2(BoxSize / 2f, BoxSize / 2f);
        Size = new Vector2(BoxSize, BoxSize);
        _knobOffset = Vector2.Zero;
        Direction = Vector2.Zero;
        Active = true;
        Visible = true;
        QueueRedraw();
    }

    /// <summary>Move the knob toward a viewport-space point.</summary>
    public void Drag(Vector2 viewportPosition)
    {
        if (!Active)
        {
            return;
        }

        Vector2 center = Position + new Vector2(BoxSize / 2f, BoxSize / 2f);
        _knobOffset = (viewportPosition - center).LimitLength(BaseRadius);

        float magnitude = _knobOffset.Length() / BaseRadius;
        Direction = magnitude <= DeadZone
            ? Vector2.Zero
            : _knobOffset.Normalized() * Mathf.Min(1f, magnitude);

        QueueRedraw();
    }

    /// <summary>Release the stick and hide it again.</summary>
    public void Release()
    {
        Active = false;
        Visible = false;
        _knobOffset = Vector2.Zero;
        Direction = Vector2.Zero;
        QueueRedraw();
    }

    /// <inheritdoc />
    public override void _Draw()
    {
        if (!Active)
        {
            return;
        }

        var center = new Vector2(BoxSize / 2f, BoxSize / 2f);

        DrawCircle(center, BaseRadius, BaseFill);
        DrawArc(center, BaseRadius - 1f, 0f, Mathf.Tau, 48, BaseRim, 2f, true);

        Vector2 knob = center + _knobOffset;
        DrawCircle(knob, KnobRadius, KnobFill);
        DrawArc(knob, KnobRadius - 1f, 0f, Mathf.Tau, 40, KnobRim, 2f, true);
    }
}
