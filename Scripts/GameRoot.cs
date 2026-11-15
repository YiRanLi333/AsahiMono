using System;
using System.Diagnostics;
using System.Text.Json;
using AsahiMono.Adapters;
using AsahiMono.Core;
using AsahiMono.Core.Save;
using AsahiMono.Core.World;
using Godot;

namespace AsahiMono;

/// <summary>
/// The world root scene (<c>res://Scenes/Main.tscn</c>): the one place that knows the
/// order of operations.
///
/// <code>
/// config.json -> seed -> save.json -> WorldGen.Generate -> ApplyOverrides
///             -> runtime TileSet -> paint cells -> place the player
///             -> clock -> save (close / every 60 s)
/// </code>
///
/// <c>Main.tscn</c> is the project's <c>run/main_scene</c> <i>and</i> the scene
/// <see cref="Preview"/> instantiates, so <c>run.ps1 preview</c> shows this world inside
/// the fake iPhone 12 body with no extra wiring.
///
/// Determinism: the seed comes from <c>--seed</c>, then the save, then
/// <see cref="DefaultSeed"/>. Nothing here reads the wall clock or a <c>Guid</c>.
/// </summary>
public partial class GameRoot : Node2D
{
    /// <summary>Seed used when neither the command line nor a save provides one.</summary>
    public const int DefaultSeed = 20261115;

    /// <summary>Logical save file name inside the save location.</summary>
    public const string SaveFileName = "save.json";

    /// <summary>Where <see cref="GameConfig"/> lives, relative to the project.</summary>
    public const string ConfigPath = "res://config.json";

    /// <summary>Real seconds between two background saves.</summary>
    private const double AutoSaveIntervalSeconds = 60.0;

    /// <summary>Used when config.json is missing or broken.</summary>
    private const double FallbackMinutesPerDay = 10.0;

    /// <summary>Used when config.json is missing or broken.</summary>
    private const int FallbackWorldSize = 96;

    /// <summary>Used when config.json is missing or broken.</summary>
    private const double FallbackCameraZoom = 3.0;

    /// <summary>Player position is snapped to this fraction of a pixel (anti-jitter).</summary>
    private const float PixelSnapStep = 1f / 16f;

    private readonly WorldClock _clock = new();
    private readonly ISaveLocation _save = new GodotSaveLocation("user://");

    private WorldMap _world = null!;
    private TileMapLayer _ground = null!;
    private TileMapLayer _props = null!;
    private Player _player = null!;
    private Camera2D _camera = null!;
    private Hud _hud = null!;
    private VirtualJoystick _joystick = null!;
    private InputRouter _inputRouter = null!;

    private double _ticksPerSecond;
    private double _tickAccumulator;
    private double _autoSaveTimer;
    private int _seed;
    private bool _wroteFinalSave;

    /// <inheritdoc />
    public override void _Ready()
    {
        GameConfig config = GameConfig.Load(ConfigPath);

        // 1 game day = minutesPerDay real minutes, so ticksPerSecond = 1440 / (m * 60).
        // 10 minutes -> 2.4 ticks per second. Nothing here is a magic number.
        _ticksPerSecond = WorldClock.TicksPerDay / (config.MinutesPerDay * 60.0);
        GD.Print($"[config] minutesPerDay={config.MinutesPerDay:0.###} -> {_ticksPerSecond:0.####} ticks/s, world {config.WorldWidth}x{config.WorldHeight}");

        _ground = GetNode<TileMapLayer>("Ground");
        _props = GetNode<TileMapLayer>("Props");
        _player = GetNode<Player>("Player");
        _camera = GetNode<Camera2D>("Camera2D");
        _hud = GetNode<Hud>("HudLayer/Hud");
        _joystick = GetNode<VirtualJoystick>("HudLayer/Hud/Joystick");
        _inputRouter = GetNode<InputRouter>("InputRouter");

        TileSet tileSet = WorldRenderer.BuildTileSet();
        _ground.TileSet = tileSet;
        _props.TileSet = tileSet;

        GameState? save = TryLoadSave();
        int? requestedSeed = ReadSeedArgument();
        bool useSave = save is not null && (requestedSeed is null || requestedSeed == save.Seed);
        int seed = requestedSeed ?? save?.Seed ?? DefaultSeed;

        if (save is not null && !useSave)
        {
            GD.Print($"[save] --seed {requestedSeed} overrides the save (seed {save.Seed}); starting a fresh world");
        }

        _seed = seed;
        _world = GenerateWorld(seed, config);

        if (useSave)
        {
            _world.ApplyOverrides(save!.Overrides);
            GD.Print($"[world] applied {save.Overrides.Count} tile override(s) from the save");
        }

        WorldRenderer.Paint(_ground, _props, _world);

        if (useSave)
        {
            PlacePlayerAt(SaveCell(save!));
            _clock.Advance((int)Math.Clamp(save!.Ticks, 0, int.MaxValue - 1));
            GD.Print($"[save] resumed at day {_clock.Day} {_clock.TickOfDay / WorldClock.TicksPerHour:00}:{_clock.TickOfDay % WorldClock.TicksPerHour:00} ({_clock.TotalTicks} ticks)");
        }
        else
        {
            PlacePlayerAt(_world.Spawn);
        }

        _clock.NewDay += (_, e) => GD.Print($"[clock] new day {e.Day}");
        _hud.Bind(_clock);
        _inputRouter.Bind(_player, _joystick);

        _camera.MakeCurrent();
        // Without this the world is drawn 1:1, i.e. a 96x96 map reads as a minimap.
        _camera.Zoom = new Vector2((float)config.CameraZoom, (float)config.CameraZoom);
        _camera.GlobalPosition = SnapToPixelStep(_player.GlobalPosition);
        GD.Print($"[camera] zoom {config.CameraZoom:0.##}x -> {390.0 / (16.0 * config.CameraZoom):0.#} x {844.0 / (16.0 * config.CameraZoom):0.#} tiles visible");

        GD.Print($"[world] {(_world.Width)}x{_world.Height} seed {_seed}, spawn ({_world.Spawn.X},{_world.Spawn.Y}), player at ({_player.GlobalPosition.X:0.#},{_player.GlobalPosition.Y:0.#})");

        // A save right away means the very first headless run leaves a readable save.json
        // behind even if the process is killed with --quit-after.
        SaveNow("startup");
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (_world is null)
        {
            return;
        }

        _tickAccumulator += delta * _ticksPerSecond;
        int wholeTicks = (int)_tickAccumulator;
        if (wholeTicks > 0)
        {
            _tickAccumulator -= wholeTicks;
            _clock.Advance(wholeTicks);
        }

        // Pixel-perfect follow: snap to 1/16 px so nearest-neighbour tiles never shimmer.
        _camera.GlobalPosition = SnapToPixelStep(_player.GlobalPosition);

        _autoSaveTimer += delta;
        if (_autoSaveTimer >= AutoSaveIntervalSeconds)
        {
            _autoSaveTimer = 0.0;
            SaveNow("autosave");
        }
    }

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            SaveFinal("window close");
        }
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        // Also covers --quit-after, which never sends a window-close notification.
        SaveFinal("exit tree");
    }

    /// <summary>Generate the map and log how long it took (timing is never persisted).</summary>
    private WorldMap GenerateWorld(int seed, GameConfig config)
    {
        long start = Stopwatch.GetTimestamp();
        WorldMap world = WorldGen.Generate(new WorldGenOptions(seed, config.WorldWidth, config.WorldHeight));
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        GD.Print($"[world] generated new world seed {seed} in {ms:0.0} ms");
        return world;
    }

    /// <summary>Drop the player on a cell: cell -> pixel centre (cell * 16 + half a tile).</summary>
    private void PlacePlayerAt((int X, int Y) cell)
    {
        _player.GlobalPosition = WorldRenderer.CellCenterToPixel(cell.X, cell.Y);
        _player.ClearTarget();
    }

    /// <summary>The saved cell, or the generated spawn when the save points somewhere unwalkable.</summary>
    private (int X, int Y) SaveCell(GameState save)
    {
        if (_world.InBounds(save.PlayerX, save.PlayerY) && _world.IsWalkable(save.PlayerX, save.PlayerY))
        {
            return (save.PlayerX, save.PlayerY);
        }

        GD.PushWarning($"[save] player cell ({save.PlayerX},{save.PlayerY}) is not walkable; using the spawn point");
        return _world.Spawn;
    }

    private GameState? TryLoadSave()
    {
        if (!_save.Exists(SaveFileName))
        {
            return null;
        }

        try
        {
            GameState state = SaveSerializer.Deserialize(_save.ReadAllText(SaveFileName));
            GD.Print($"[save] loaded save from {_save.DisplayName}{SaveFileName}");
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            GD.PushWarning($"[save] {SaveFileName} unusable ({ex.Message}); ignoring it");
            return null;
        }
    }

    private void SaveFinal(string reason)
    {
        if (_wroteFinalSave)
        {
            return;
        }

        _wroteFinalSave = true;
        SaveNow(reason);
    }

    private void SaveNow(string reason)
    {
        if (_world is null)
        {
            return;
        }

        try
        {
            GameState state = CaptureState();
            _save.WriteAllText(SaveFileName, SaveSerializer.Serialize(state));
            GD.Print($"[save] wrote {_save.DisplayName}{SaveFileName} ({reason}): day {state.Day}, {state.Ticks} ticks, player ({state.PlayerX},{state.PlayerY})");
        }
        catch (Exception ex)
        {
            GD.PushError($"[save] write failed ({reason}): {ex.Message}");
        }
    }

    /// <summary>Snapshot the world into a save document.</summary>
    private GameState CaptureState()
    {
        Vector2I cell = WorldRenderer.PixelToCell(_player.GlobalPosition);

        return new GameState
        {
            Version = 1,
            Seed = _seed,
            Day = _clock.Day,
            Ticks = _clock.TotalTicks,
            PlayerX = cell.X,
            PlayerY = cell.Y,

            // M1-b does not edit the map yet (no tilling, no chopping), so the diff list
            // stays empty. WorldGen + seed is still enough to reproduce the world exactly.
            Overrides = new System.Collections.Generic.List<TileOverride>(),
        };
    }

    /// <summary>Round a world position to a 1/16 px grid.</summary>
    private static Vector2 SnapToPixelStep(Vector2 position)
        => new(Mathf.Round(position.X / PixelSnapStep) * PixelSnapStep, Mathf.Round(position.Y / PixelSnapStep) * PixelSnapStep);

    /// <summary>Read <c>--seed N</c> / <c>--seed=N</c> from the user arguments (the part after <c>--</c>).</summary>
    private static int? ReadSeedArgument()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg == "--seed" && i + 1 < args.Length && int.TryParse(args[i + 1], out int next))
            {
                return next;
            }

            const string Prefix = "--seed=";
            if (arg.StartsWith(Prefix, StringComparison.Ordinal) && int.TryParse(arg[Prefix.Length..], out int inline))
            {
                return inline;
            }
        }

        return null;
    }

    /// <summary>
    /// The tuning numbers that must not be baked into code: how long a day lasts and how
    /// big the world is. Read from <c>res://config.json</c> with Godot's own file access so
    /// it works in an exported <c>.ipa</c> too (the file is packed into the pck).
    /// </summary>
    private sealed class GameConfig
    {
        public double MinutesPerDay { get; private set; } = FallbackMinutesPerDay;

        public int WorldWidth { get; private set; } = FallbackWorldSize;

        public int WorldHeight { get; private set; } = FallbackWorldSize;

        /// <summary>
        /// Camera magnification. At 16 px tiles a zoom of 3 shows about 8 x 17 tiles on a
        /// 390 x 844 portrait screen; zoom 2 shows about 12 x 26. Anything else and the
        /// world reads as a minimap instead of a game.
        /// </summary>
        public double CameraZoom { get; private set; } = FallbackCameraZoom;

        public static GameConfig Load(string resourcePath)
        {
            var config = new GameConfig();

            if (!Godot.FileAccess.FileExists(resourcePath))
            {
                GD.PushWarning($"[config] {resourcePath} is missing; using defaults");
                return config;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(resourcePath));
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("minutesPerDay", out JsonElement minutes)
                    && minutes.TryGetDouble(out double perDay)
                    && perDay > 0.0)
                {
                    config.MinutesPerDay = perDay;
                }

                config.WorldWidth = ReadSize(root, "worldWidth");
                config.WorldHeight = ReadSize(root, "worldHeight");

                if (root.TryGetProperty("cameraZoom", out JsonElement zoomValue)
                    && zoomValue.TryGetDouble(out double zoom)
                    && zoom >= 1.0
                    && zoom <= 8.0)
                {
                    config.CameraZoom = zoom;
                }
            }
            catch (JsonException ex)
            {
                GD.PushWarning($"[config] {resourcePath} is not valid JSON ({ex.Message}); using defaults");
            }

            return config;
        }

        private static int ReadSize(JsonElement root, string name)
        {
            if (root.TryGetProperty(name, out JsonElement value)
                && value.TryGetInt32(out int size)
                && size >= 16
                && size <= 512)
            {
                return size;
            }

            GD.PushWarning($"[config] '{name}' missing or out of range (16..512); using {FallbackWorldSize}");
            return FallbackWorldSize;
        }
    }
}
