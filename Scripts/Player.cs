using Godot;

namespace AsahiMono;

/// <summary>
/// The player avatar: a <see cref="CharacterBody2D"/> whose origin sits at the
/// character's <b>feet</b> (a 16x24 placeholder block is drawn upward from there, the
/// 12x10 collision box sits in the bottom 10 px).
///
/// Movement is fed by <see cref="InputRouter"/> through two channels that never fight
/// each other:
/// <list type="bullet">
///   <item><see cref="SetManualDirection"/> -- virtual joystick or WASD/arrows, cancels a target</item>
///   <item><see cref="SetMoveTarget"/> -- tap/hold on the map, walks in a straight line and stops</item>
/// </list>
/// The body is in <see cref="WorldRenderer.PlayerCollisionLayer"/> and masks water plus
/// blocking props, so <see cref="MoveAndSlide"/> alone keeps the player dry.
/// </summary>
public partial class Player : CharacterBody2D
{
    /// <summary>Walk speed in pixels per second (60 px/s = 3.75 tiles/s).</summary>
    public const float Speed = 60f;

    /// <summary>Distance at which a click-to-move target counts as reached.</summary>
    public const float ArriveEpsilon = 0.75f;

    private Vector2 _manualDirection;
    private bool _hasTarget;
    private Vector2 _target;

    /// <summary>True while the player is still walking toward a tapped point.</summary>
    public bool IsWalkingToTarget => _hasTarget;

    /// <summary>Where the player is heading, when <see cref="IsWalkingToTarget"/>.</summary>
    public Vector2 Target => _target;

    /// <inheritdoc />
    public override void _Ready()
    {
        // Top-down: no gravity, no "grounded" bookkeeping.
        MotionMode = MotionModeEnum.Floating;
        CollisionLayer = WorldRenderer.PlayerCollisionLayer;
        CollisionMask = WorldRenderer.PlayerCollisionMask;
    }

    /// <summary>Joystick / keyboard direction. A non-zero direction cancels any tap target.</summary>
    public void SetManualDirection(Vector2 direction)
    {
        if (direction.LengthSquared() > 1f)
        {
            direction = direction.Normalized();
        }

        _manualDirection = direction;
        if (direction != Vector2.Zero)
        {
            _hasTarget = false;
        }
    }

    /// <summary>Walk in a straight line toward a world position.</summary>
    public void SetMoveTarget(Vector2 worldPosition)
    {
        _target = worldPosition;
        _hasTarget = true;
        _manualDirection = Vector2.Zero;
    }

    /// <summary>Stop walking toward the tap target.</summary>
    public void ClearTarget() => _hasTarget = false;

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        Vector2 direction = _manualDirection;
        bool arriving = false;
        float remaining = 0f;

        if (direction.LengthSquared() < 0.0001f && _hasTarget)
        {
            Vector2 toTarget = _target - GlobalPosition;
            remaining = toTarget.Length();
            if (remaining <= ArriveEpsilon)
            {
                _hasTarget = false;
                Velocity = Vector2.Zero;
                return;
            }

            direction = toTarget / remaining;
            arriving = true;
        }

        if (direction.LengthSquared() < 0.0001f)
        {
            Velocity = Vector2.Zero;
            return;
        }

        // Never overshoot the last few pixels of a tap target.
        float speed = arriving ? Mathf.Min(Speed, remaining / Mathf.Max((float)delta, 0.0001f)) : Speed;
        Velocity = direction * speed;
        MoveAndSlide();
    }
}
