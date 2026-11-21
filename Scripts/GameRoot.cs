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
	private const double FallbackCameraZoom = 1.0;

	/// <summary>
	/// Vertical squash used to emulate a lower camera pitch (2:1 -> 2.5:1 / 3:1) without
	/// redrawing the tile art. 1.0 = the true 2:1 diamonds; smaller = flatter = lower view.
	/// </summary>
	private const double FallbackIsoFlatten = 1.0;

	/// <summary>
	/// Default world source when config.json says nothing.
	/// <list type="bullet">
	///   <item><c>refblock_scene</c> = read <c>Maps/Refblock.tscn</c> itself — the map you paint in
	///   the editor <i>is</i> the map the game shows after F5 (default, because the editor is the
	///   one place the map is actually edited);</item>
	///   <item><c>refblock</c> = read <c>Assets/maps/refblock.json</c> (the older hand-composed island);</item>
	///   <item><c>random</c> = the generated world.</item>
	/// </list>
	/// Semantics and pitfalls: <c>docs/engine/MAP-SOURCES.md</c>.
	/// </summary>
	private const string FallbackMapSource = "refblock_scene";

	/// <summary>Default res:// path of the hand-composed island json.</summary>
	private const string FallbackRefblockPath = "res://Assets/maps/refblock.json";

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
	private IsoWorldRenderer? _iso;

	private double _ticksPerSecond;
	private double _tickAccumulator;
	private double _autoSaveTimer;
	private int _seed;
	private bool _wroteFinalSave;

	/// <summary>Zoom levels the <c>Z</c> key cycles through (gameplay framing presets).</summary>
	private static readonly double[] ZoomSteps = { 1.0, 2.0, 3.0, 4.0 };

	private double _cameraZoom = FallbackCameraZoom;

	/// <summary>
	/// Vertical squash of the whole iso scene (1.0 = 2:1 diamonds, 0.8 = 2.5:1, 0.667 = 3:1).
	/// </summary>
	private double _isoFlatten = FallbackIsoFlatten;

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
		int seed = requestedSeed ?? save?.Seed ?? DefaultSeed;
		_seed = seed;

		// Map source (three values, see docs/engine/MAP-SOURCES.md):
		//   refblock_scene = read Maps/Refblock.tscn itself: Ground layer -> tile kinds,
		//                    YSort/Deco,Deco2,Deco3,Deco4 -> decorations. Default: the editor is
		//                    the single source of truth, so "brush it, press F5, same map".
		//   refblock       = read the json Assets/maps/refblock.json (older pipeline, kept working).
		//   random         = WorldGen.Generate(seed).
		// Both hand-made sources also suppress the save's tile overrides: a stale save must never
		// repaint a map the user just edited in the editor.
		WorldMap world;
		bool handMade = false;
		System.Collections.Generic.List<RefblockMapLoader.DecoPlacement>? sceneDeco = null;
		string? decoSourceLabel = null;

		if (config.IsRefblockScene)
		{
			RefblockSceneLoader.Result? scene = RefblockSceneLoader.TryLoad(this, RefblockSceneLoader.ScenePath, seed);
			if (scene is not null)
			{
				world = scene.Map;
				sceneDeco = scene.Deco;
				decoSourceLabel = RefblockSceneLoader.ScenePath;
				handMade = true;
			}
			else
			{
				GD.PushWarning($"[map] mapSource=refblock_scene but {RefblockSceneLoader.ScenePath} yielded no ground; using the generated world");
				world = GenerateWorld(seed, config);
			}
		}
		else if (config.IsRefblock && RefblockMapLoader.TryReadSize(config.RefblockPath, out int rw, out int rh))
		{
			world = new WorldMap(rw, rh, seed);
			handMade = RefblockMapLoader.TryLoad(config.RefblockPath, world);
		}
		else
		{
			world = GenerateWorld(seed, config);
		}

		_world = world;
		(int X, int Y) spawnCell = handMade ? RefblockMapLoader.FindSpawn(_world) : _world.Spawn;
		GD.Print($"[map] mapSource={config.MapSource} (hand-made {handMade}) -> {_world.Width}x{_world.Height}");

		bool useSave = !handMade && save is not null && (requestedSeed is null || requestedSeed == save.Seed);

		if (save is not null && !useSave && !handMade)
		{
			GD.Print($"[save] --seed {requestedSeed} overrides the save (seed {save.Seed}); starting a fresh world");
		}

		if (useSave)
		{
			_world.ApplyOverrides(save!.Overrides);
			GD.Print($"[world] applied {save.Overrides.Count} tile override(s) from the save");
		}

		WorldRenderer.Paint(_ground, _props, _world);

		// Projection switch: "grid" = the classic square 3/4 tiles, "iso" = 2:1 diamonds.
		// The isometric path replaces the two TileMapLayers with a single drawing node, so
		// world data, generation, saving and the clock stay exactly the same.
		if (config.IsIsometric)
		{
			_ground.Visible = false;
			_props.Visible = false;

			_iso = new IsoWorldRenderer { Name = "IsoWorld" };
			AddChild(_iso);
			// 装饰来源随 mapSource 走：
			//   refblock_scene -> 内存表（从场景 Deco 系列层读出来的，sceneDeco）
			//   refblock       -> json 路径（渲染器自己读 deco[]）
			//   random         -> 都没有（生成世界不带装饰）
			string? decoPath = handMade && !config.IsRefblockScene ? config.RefblockPath : null;
			// decoRules：手画地图（refblock_scene）默认 "off" —— 编辑器里放几个就画几个；
			// 生成/老地图默认 "auto" —— 配额 + 间距 + 出生点净空照旧。config.json 写死了就按写的来。
			_iso.Initialize(_world, _player, decoPath, sceneDeco, decoSourceLabel, config.EffectiveDecoRules);
			_player.EnableIso(_iso, _world, spawnCell);
			GD.Print("[iso] projection = iso (2:1 diamonds), TileMapLayers hidden");
		}

		if (useSave)
		{
			PlacePlayerAt(SaveCell(save!));
			_clock.Advance((int)Math.Clamp(save!.Ticks, 0, int.MaxValue - 1));
			GD.Print($"[save] resumed at day {_clock.Day} {_clock.TickOfDay / WorldClock.TicksPerHour:00}:{_clock.TickOfDay % WorldClock.TicksPerHour:00} ({_clock.TotalTicks} ticks)");
		}
		else
		{
			PlacePlayerAt(spawnCell);
		}

		_clock.NewDay += (_, e) => GD.Print($"[clock] new day {e.Day}");
		_hud.Bind(_clock);
		_inputRouter.Bind(_player, _joystick);

		_camera.MakeCurrent();
		_isoFlatten = config.IsoFlatten;
		// Without this the world is drawn 1:1, i.e. a 96x96 map reads as a minimap.
		ApplyCameraZoom(config.CameraZoom);
		_camera.GlobalPosition = SnapToPixelStep(_player.GlobalPosition);

		GD.Print($"[world] {(_world.Width)}x{_world.Height} seed {_seed}, spawn ({spawnCell.X},{spawnCell.Y}), player at ({_player.GlobalPosition.X:0.#},{_player.GlobalPosition.Y:0.#})");

		// A save right away means the very first headless run leaves a readable save.json
		// behind even if the process is killed with --quit-after.
		SaveNow("startup");
	}

	/// <summary>
	/// Apply a camera magnification, tell the HUD about it and log how much world fits on
	/// screen. Clamped to 1..4: below 1 is a minimap, above 4 a 96x96 farm is unplayable.
	/// </summary>
	private void ApplyCameraZoom(double zoom)
	{
		// 只允许整数倍：像素画在非整数缩放下会出现 1px/2px 混排（发糊、脏点），
		// 这是"粗糙"的机械成因之一。取最近的整数并在偏离时提示。
		double snapped = Math.Round(Math.Clamp(zoom, 1.0, 4.0));
		if (Math.Abs(snapped - zoom) > 0.001)
		{
			GD.PushWarning($"[camera] zoom {zoom:0.##}x is not an integer; snapped to {snapped:0}x (pixel art needs integer scale)");
		}
		_cameraZoom = snapped;
		// Non-uniform zoom: Y is squashed by _isoFlatten, which reads as a lower pitch.
		_camera.Zoom = new Vector2((float)_cameraZoom, (float)(_cameraZoom * _isoFlatten));

		Vector2 viewport = GetViewportRect().Size;

		// Grid: one tile = TileSize px on both axes. Iso: neighbouring cells are half a
		// diamond apart (32 px across, 16 px down), so those are the real screen steps.
		int stepX = _iso is not null ? IsoWorldRenderer.HalfW : WorldRenderer.TileSize;
		int stepY = _iso is not null ? IsoWorldRenderer.HalfH : WorldRenderer.TileSize;
		double tilesX = viewport.X / (stepX * _cameraZoom);
		double tilesY = viewport.Y / (stepY * _cameraZoom * _isoFlatten);
		GD.Print($"[camera] zoom {_cameraZoom:0.##}x -> {tilesX:0.#} x {tilesY:0.#} cells visible ({viewport.X:0}x{viewport.Y:0} px)");
		_hud.SetCameraInfo(_cameraZoom, stepX, viewport);
	}

	/// <inheritdoc />
	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo)
		{
			return;
		}

		if (key.Keycode != Key.Z)
		{
			return;
		}

		// Cycle the gameplay framing presets: 1x -> 1.5x -> 2x -> 3x -> 1x ...
		int next = 0;
		for (int i = 0; i < ZoomSteps.Length; i++)
		{
			if (ZoomSteps[i] > _cameraZoom + 0.001)
			{
				next = i;
				break;
			}
		}

		ApplyCameraZoom(ZoomSteps[next]);
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

	/// <summary>Drop the player on a cell: grid projection -> pixel centre, iso -> grid cell.</summary>
	private void PlacePlayerAt((int X, int Y) cell)
	{
		if (_iso is not null)
		{
			_player.PlaceIso(cell);
			return;
		}

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
		Vector2I cell = _player.IsoMode
			? new Vector2I(Mathf.RoundToInt(_player.IsoGrid.X), Mathf.RoundToInt(_player.IsoGrid.Y))
			: WorldRenderer.PixelToCell(_player.GlobalPosition);

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

		/// <summary>
		/// Vertical squash of the iso scene from config.json (isoFlatten): 1.0 keeps the true
		/// 2:1 diamonds (pitch ~26.6 deg); 0.8 ~ 2.5:1 (~21.8 deg); 0.667 ~ 3:1 (~18.4 deg).
		/// It stretches the art, so it is a look-test knob, not a shipping setting.
		/// </summary>
		public double IsoFlatten { get; private set; } = FallbackIsoFlatten;

		/// <summary>Projection name from config.json: "grid" (square 3/4) or "iso" (2:1 diamonds).</summary>
		public string Projection { get; private set; } = "grid";

		/// <summary>Convenience flag: true when <see cref="Projection"/> is "iso".</summary>
		public bool IsIsometric => string.Equals(Projection, "iso", StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Where the world comes from:
		/// <c>refblock_scene</c> = the editor scene <see cref="RefblockSceneLoader.ScenePath"/> itself
		/// (Ground layer = tile kinds, YSort/Deco,Deco2,Deco3,Deco4 = decorations) — what you paint in
		/// the editor is what the game shows; <c>refblock</c> = the json
		/// (<see cref="RefblockPath"/>, the older hand-composed island); <c>random</c> = the generated world.
		/// </summary>
		public string MapSource { get; private set; } = FallbackMapSource;

		/// <summary>Convenience flag: true when <see cref="MapSource"/> is "refblock_scene".</summary>
		public bool IsRefblockScene => string.Equals(MapSource, "refblock_scene", StringComparison.OrdinalIgnoreCase);

		/// <summary>Convenience flag: true when <see cref="MapSource"/> is "refblock" (the json path).</summary>
		public bool IsRefblock => string.Equals(MapSource, "refblock", StringComparison.OrdinalIgnoreCase);

		/// <summary>res:// path of the hand-composed island json.</summary>
		public string RefblockPath { get; private set; } = FallbackRefblockPath;

		/// <summary>
		/// Decoration admission rules from config.json (<c>decoRules</c>):
		/// <c>auto</c> = spawn clearance (3 cells) + per-family quota + 0.85-cell spacing — the
		/// generated/long-standing behaviour; <c>off</c> = only the two safety filters (outside the
		/// map / <c>Void</c>, and a stem with no texture) so a hand-painted map draws exactly what
		/// was painted. Empty = derive the default from <see cref="MapSource"/>.
		/// </summary>
		public string DecoRules { get; private set; } = string.Empty;

		/// <summary>
		/// The value actually handed to <see cref="IsoWorldRenderer.Initialize"/>: hand-painted
		/// (<c>refblock_scene</c>) defaults to <c>off</c> — what the user dropped in the editor is
		/// sacred; <c>refblock</c> / <c>random</c> default to <c>auto</c>.
		/// </summary>
		public string EffectiveDecoRules
			=> DecoRules.Length > 0 ? DecoRules : (IsRefblockScene ? "off" : "auto");

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
					&& zoom >= 0.5
					&& zoom <= 8.0)
				{
					config.CameraZoom = zoom;
				}

				if (root.TryGetProperty("isoFlatten", out JsonElement flattenValue)
					&& flattenValue.TryGetDouble(out double flatten)
					&& flatten >= 0.4
					&& flatten <= 1.0)
				{
					config.IsoFlatten = flatten;
				}

				if (root.TryGetProperty("mapSource", out JsonElement mapSourceValue)
					&& mapSourceValue.ValueKind == JsonValueKind.String)
				{
					string name = mapSourceValue.GetString() ?? FallbackMapSource;
					if (!string.Equals(name, "refblock_scene", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(name, "refblock", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(name, "random", StringComparison.OrdinalIgnoreCase))
					{
						GD.PushWarning($"[config] unknown mapSource '{name}'; expected 'refblock_scene', 'refblock' or 'random' -> using {FallbackMapSource}");
					}
					else
					{
						config.MapSource = name;
					}
				}

				if (root.TryGetProperty("refblockPath", out JsonElement refblockValue)
					&& refblockValue.ValueKind == JsonValueKind.String)
				{
					string path = refblockValue.GetString() ?? FallbackRefblockPath;
					if (path.StartsWith("res://", StringComparison.Ordinal))
					{
						config.RefblockPath = path;
					}
					else
					{
						GD.PushWarning($"[config] refblockPath must start with res:// ('{path}'); using {FallbackRefblockPath}");
					}
				}

				// 装饰准入规则：解析完 mapSource 之后才能算默认值，所以放在这一块。
				if (root.TryGetProperty("decoRules", out JsonElement decoRulesValue)
					&& decoRulesValue.ValueKind == JsonValueKind.String)
				{
					string name = decoRulesValue.GetString() ?? string.Empty;
					if (string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(name, "off", StringComparison.OrdinalIgnoreCase))
					{
						config.DecoRules = name.ToLowerInvariant();
					}
					else
					{
						GD.PushWarning($"[config] unknown decoRules '{name}'; expected 'auto' or 'off' -> using {config.EffectiveDecoRules}");
					}
				}

				if (root.TryGetProperty("projection", out JsonElement projectionValue)
					&& projectionValue.ValueKind == JsonValueKind.String)
				{
					string name = projectionValue.GetString() ?? "grid";
					if (!string.Equals(name, "grid", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(name, "iso", StringComparison.OrdinalIgnoreCase))
					{
						GD.PushWarning($"[config] unknown projection '{name}'; expected 'grid' or 'iso' -> using grid");
					}
					else
					{
						config.Projection = name;
					}
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
