using Godot;

namespace AsahiMono;

/// <summary>
/// Merges the three ways a player can ask to walk into one stream of intents for
/// <see cref="Player"/>:
///
/// <list type="number">
///   <item><b>virtual stick</b> -- press and drag anywhere in the lower-left quarter:
///         <see cref="VirtualJoystick"/> pops up under the thumb, analog direction out.</item>
///   <item><b>tap / hold the map</b> -- anywhere else: the player walks in a straight
///         line to that point, re-aiming while the finger keeps dragging.</item>
///   <item><b>WASD / arrow keys</b> -- polled every frame, desktop only, highest priority.</item>
/// </list>
///
/// Everything is computed in <b>viewport</b> space (touch and mouse coordinates always
/// are) and only the map taps are converted to world space through the viewport's canvas
/// transform, so the router keeps working at <c>Camera2D.zoom = (2,2)</c> and inside the
/// <c>DevPreview</c> SubViewport.
/// </summary>
public partial class InputRouter : Control
{
    /// <summary>Id used for the (single) mouse pointer; touches use their own index.</summary>
    private const int MousePointerId = -1;

    /// <summary>Sentinel for "no pointer owns this role".</summary>
    private const int NoPointer = -2;

    /// <summary>Fraction of the viewport width that belongs to the joystick zone.</summary>
    private const float JoystickZoneWidth = 0.5f;

    /// <summary>Fraction of the viewport height below which the joystick zone starts.</summary>
    private const float JoystickZoneTop = 0.5f;

    private Player? _player;
    private VirtualJoystick? _joystick;

    private int _stickPointer = NoPointer;
    private int _tapPointer = NoPointer;

    /// <inheritdoc />
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetProcessUnhandledInput(true);
    }

    /// <summary>Wire the router to the things it drives. Called by <see cref="GameRoot"/>.</summary>
    public void Bind(Player player, VirtualJoystick joystick)
    {
        _player = player;
        _joystick = joystick;
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (_player is null)
        {
            return;
        }

        // Priority: keyboard beats stick beats tap target (Player enforces the last two).
        Vector2 keys = ReadKeyboard();
        if (keys != Vector2.Zero)
        {
            _player.SetManualDirection(keys);
            return;
        }

        if (_joystick is { Active: true })
        {
            _player.SetManualDirection(_joystick.Direction);
            return;
        }

        _player.SetManualDirection(Vector2.Zero);
    }

    /// <inheritdoc />
    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventScreenTouch touch:
                if (touch.Pressed)
                {
                    Press(touch.Position, touch.Index);
                }
                else
                {
                    Release(touch.Index);
                }

                break;

            case InputEventScreenDrag drag:
                Move(drag.Position, drag.Index);
                break;

            case InputEventMouseButton button when button.ButtonIndex == MouseButton.Left:
                if (button.Pressed)
                {
                    Press(button.Position, MousePointerId);
                }
                else
                {
                    Release(MousePointerId);
                }

                break;

            case InputEventMouseMotion motion when _stickPointer == MousePointerId || _tapPointer == MousePointerId:
                Move(motion.Position, MousePointerId);
                break;
        }
    }

    private void Press(Vector2 viewportPosition, int pointerId)
    {
        if (_player is null)
        {
            return;
        }

        bool wantsStick = IsInJoystickZone(viewportPosition);

        if (wantsStick && _joystick != null && _stickPointer == NoPointer)
        {
            _stickPointer = pointerId;
            _joystick.Press(viewportPosition);
            _player.ClearTarget();
            return;
        }

        if (_tapPointer == NoPointer)
        {
            _tapPointer = pointerId;
            _player.SetMoveTarget(ToWorld(viewportPosition));
        }
    }

    private void Move(Vector2 viewportPosition, int pointerId)
    {
        if (_player is null)
        {
            return;
        }

        if (pointerId == _stickPointer && _joystick != null)
        {
            _joystick.Drag(viewportPosition);
            _player.SetManualDirection(_joystick.Direction);
            return;
        }

        if (pointerId == _tapPointer)
        {
            _player.SetMoveTarget(ToWorld(viewportPosition));
        }
    }

    private void Release(int pointerId)
    {
        if (pointerId == _stickPointer)
        {
            _stickPointer = NoPointer;
            _joystick?.Release();
            _player?.SetManualDirection(Vector2.Zero);
            return;
        }

        if (pointerId == _tapPointer)
        {
            // Deliberately *not* clearing the target: "tap once, keep walking".
            _tapPointer = NoPointer;
        }
    }

    private bool IsInJoystickZone(Vector2 viewportPosition)
    {
        Vector2 view = GetViewportRect().Size;
        return viewportPosition.X <= view.X * JoystickZoneWidth
            && viewportPosition.Y >= view.Y * JoystickZoneTop;
    }

    /// <summary>
    /// Viewport (touch) coordinates to world coordinates. <c>GetCanvasTransform()</c> maps
    /// world -&gt; viewport (it already contains <see cref="Camera2D"/> position and zoom), so
    /// the inverse is the direction we need here.
    /// </summary>
    private Vector2 ToWorld(Vector2 viewportPosition)
        => GetViewport().GetCanvasTransform().AffineInverse() * viewportPosition;

    private static Vector2 ReadKeyboard()
    {
        float x = 0f;
        float y = 0f;

        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left))
        {
            x -= 1f;
        }

        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right))
        {
            x += 1f;
        }

        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up))
        {
            y -= 1f;
        }

        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down))
        {
            y += 1f;
        }

        var direction = new Vector2(x, y);
        return direction.LengthSquared() > 1f ? direction.Normalized() : direction;
    }
}
