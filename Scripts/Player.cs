using AsahiMono.Core.World;
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
    /// <summary>Walk speed in pixels per second (96 px/s = 3 tiles/s on 32 px tiles).</summary>
    public const float Speed = 96f;

    /// <summary>Distance at which a click-to-move target counts as reached.</summary>
    public const float ArriveEpsilon = 0.75f;

    private Vector2 _manualDirection;
    private bool _hasTarget;
    private Vector2 _target;

    /// <summary>Grid cells per second while in isometric mode (3 cells/s ≈ the old 96 px/s feel).</summary>
    public const float IsoGridSpeed = 3.0f;

    /// <summary>Distance in grid cells at which an isometric tap target counts as reached.</summary>
    public const float IsoArriveEpsilon = 0.06f;

    private IsoWorldRenderer? _isoRenderer;
    private WorldMap? _isoMap;
    private Vector2 _isoTarget;
    private bool _isoHasTarget;
    private Vector2 _isoManual;

    /// <summary>True when the player is driven by the isometric renderer instead of Godot physics.</summary>
    public bool IsoMode => _isoRenderer is not null;

    /// <summary>Position in grid cells (fractional). Only meaningful in isometric mode.</summary>
    public Vector2 IsoGrid { get; private set; }

    /// <summary>0 = facing the camera, 1 = facing right, 2 = facing away, 3 = facing left.</summary>
    public int IsoFacing { get; private set; }

    /// <summary>True while the avatar is actually walking (drives the two-frame animation).</summary>
    public bool IsoMoving { get; private set; }

    /// <summary>
    /// Switch to isometric movement: grid-space stepping with <see cref="WorldMap.IsWalkable"/>
    /// checks instead of <see cref="CharacterBody2D.MoveAndSlide"/>. The node keeps a screen
    /// position derived from the projection so the camera and the save code do not change shape.
    /// </summary>
    public void EnableIso(IsoWorldRenderer renderer, WorldMap map, (int X, int Y) cell)
    {
        _isoRenderer = renderer;
        _isoMap = map;
        IsoGrid = new Vector2(cell.X, cell.Y);
        _isoTarget = IsoGrid;
        _isoHasTarget = false;
        _isoManual = Vector2.Zero;
        CollisionLayer = 0;
        CollisionMask = 0;
        GlobalPosition = IsoWorldRenderer.GridToScreen(IsoGrid.X, IsoGrid.Y);
    }

    /// <summary>Teleport in grid space (new game / load).</summary>
    public void PlaceIso((int X, int Y) cell)
    {
        IsoGrid = new Vector2(cell.X, cell.Y);
        _isoTarget = IsoGrid;
        _isoHasTarget = false;
        GlobalPosition = IsoWorldRenderer.GridToScreen(IsoGrid.X, IsoGrid.Y);
    }

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

        if (IsoMode)
        {
            // Screen-space direction (x right, y down) -> grid-space direction.
            // The projection is linear, so a straight screen push stays a straight grid push.
            _isoManual = IsoWorldRenderer.ScreenToGrid(direction);
            if (_isoManual.LengthSquared() > 1e-6f)
            {
                _isoManual = _isoManual.Normalized();
                _isoHasTarget = false;
            }
        }
    }

    /// <summary>Walk in a straight line toward a world position.</summary>
    public void SetMoveTarget(Vector2 worldPosition)
    {
        _target = worldPosition;
        _hasTarget = true;
        _manualDirection = Vector2.Zero;

        if (IsoMode)
        {
            // The click arrives in screen (world) pixels; convert to grid cells and clamp
            // into the map so a stray click outside the world cannot send us into the void.
            Vector2 grid = IsoWorldRenderer.ScreenToGrid(worldPosition);
            if (_isoMap is not null)
            {
                grid.X = Mathf.Clamp(grid.X, 0f, _isoMap.Width - 1);
                grid.Y = Mathf.Clamp(grid.Y, 0f, _isoMap.Height - 1);
            }

            _isoTarget = grid;
            _isoHasTarget = true;
            _isoManual = Vector2.Zero;
        }
    }

    /// <summary>Stop walking toward the tap target.</summary>
    public void ClearTarget()
    {
        _hasTarget = false;
        _isoHasTarget = false;
    }

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        if (IsoMode)
        {
            ProcessIso(delta);
            return;
        }

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

    /// <summary>
    /// Isometric movement: step in grid space, ask the map whether the target cell is walkable,
    /// and derive the node position from the projection. Axes are resolved separately so a
    /// blocked diagonal slides along the coastline instead of stopping dead.
    /// </summary>
    private void ProcessIso(double delta)
    {
        if (_isoMap is null)
        {
            return;
        }

        Vector2 direction = _isoManual;
        float cellsPerSecond = IsoGridSpeed;

        if (direction.LengthSquared() < 1e-6f && _isoHasTarget)
        {
            Vector2 toTarget = _isoTarget - IsoGrid;
            float remaining = toTarget.Length();
            if (remaining <= IsoArriveEpsilon)
            {
                _isoHasTarget = false;
                IsoGrid = _isoTarget;
                IsoMoving = false;
                GlobalPosition = IsoWorldRenderer.GridToScreen(IsoGrid.X, IsoGrid.Y);
                return;
            }

            direction = toTarget / remaining;
            cellsPerSecond = Mathf.Min(IsoGridSpeed, remaining / Mathf.Max((float)delta, 0.0001f));
        }

        if (direction.LengthSquared() < 1e-6f)
        {
            IsoMoving = false;
            return;
        }

        Vector2 step = direction * cellsPerSecond * (float)delta;
        Vector2 next = IsoGrid;

        if (_isoMap.IsWalkable(Mathf.FloorToInt(next.X + step.X), Mathf.FloorToInt(next.Y)))
        {
            next.X += step.X;
        }

        if (_isoMap.IsWalkable(Mathf.FloorToInt(next.X), Mathf.FloorToInt(next.Y + step.Y)))
        {
            next.Y += step.Y;
        }

        IsoMoving = next != IsoGrid;
        IsoGrid = next;
        GlobalPosition = IsoWorldRenderer.GridToScreen(IsoGrid.X, IsoGrid.Y);

        // Facing: on diamonds the screen-space delta is the honest one (pushing "down" on the
        // keyboard walks diagonally on screen, and the sprite should show the down frames).
        Vector2 screenDelta = IsoWorldRenderer.GridToScreen(step.X, step.Y);
        if (Mathf.Abs(screenDelta.X) >= Mathf.Abs(screenDelta.Y))
        {
            IsoFacing = screenDelta.X >= 0f ? 1 : 3;
        }
        else
        {
            IsoFacing = screenDelta.Y >= 0f ? 0 : 2;
        }
    }
}
