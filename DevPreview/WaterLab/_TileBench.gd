extends Node2D
# _TileBench —— 临时压测：用正式工程的 water_tile_flow.gdshader + WATER-V2.md 的推荐值
# 铺一片 12x12 的菱形水瓦（世界像素 128x64 一格），量 fps / draw calls。
# 用法：godot --path E:\AsahiMono\app res://DevPreview/WaterLab/_TileBench.tscn -- --bench=300 [--nohud]

const GRID := 12
const TILE := Vector2(128, 64)

var _frames := 0
var _bench_limit := 0
var _skip := 90
var _warm_usec := 0
var _warm_last := 0
var _shot := ""
var _mat: ShaderMaterial


func _ready() -> void:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--shot="):
			_shot = a.split("=")[1]
		if a.begins_with("--bench="):
			_bench_limit = int(a.split("=")[1])
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_ENABLED)
	Engine.max_fps = 60
	DisplayServer.window_set_size(Vector2i(1280, 720))
	_build()
	print("[TILEBENCH] tiles=%d draws_hint=1 uniforms=%d" % [GRID * GRID, _set_count()])


func _set_count() -> int:
	return 16


func _build() -> void:
	_mat = ShaderMaterial.new()
	_mat.shader = load("res://Shaders/water/water_tile_flow.gdshader")
	# ---- WATER-V2.md 推荐值（原样照抄，用来验证这套值能跑） ----
	_mat.set_shader_parameter("water_a", load("res://Assets/tiles/iso/waterfx/water_a_6x.png"))
	_mat.set_shader_parameter("water_b", load("res://Assets/tiles/iso/waterfx/water_b_6x.png"))
	_mat.set_shader_parameter("tex_px", Vector2(768, 384))
	_mat.set_shader_parameter("speed_a", 3.2)
	_mat.set_shader_parameter("speed_b", -1.2)
	_mat.set_shader_parameter("dir_a", Vector2(1.0, 0.18))
	_mat.set_shader_parameter("dir_b", Vector2(-0.85, 0.42))
	_mat.set_shader_parameter("crossfade_period", 36.0)
	_mat.set_shader_parameter("glint_amount", 0.04)
	_mat.set_shader_parameter("alpha_mul", 1.0)
	_mat.set_shader_parameter("rim_dark", 0.0)
	_mat.set_shader_parameter("col_deep", Color(0.0, 0.235, 0.310))   # #003C4F
	_mat.set_shader_parameter("col_base", Color(0.008, 0.702, 0.769)) # #02B3C4
	_mat.set_shader_parameter("col_light", Color(0.063, 0.784, 0.824))# #10C8D2
	_mat.set_shader_parameter("col_high", Color(0.498, 0.941, 0.941)) # #7FF0F0

	var tex: Texture2D = load("res://DevPreview/WaterLab/art/_bench_tile.png")
	var root := Node2D.new()
	root.position = Vector2(640, 160)
	add_child(root)
	for gy in GRID:
		for gx in GRID:
			var s := Sprite2D.new()
			s.texture = tex
			s.centered = true
			s.material = _mat
			# 等距格 -> 屏幕：(x-y)*64, (x+y)*32
			s.position = Vector2((gx - gy) * 64.0, (gx + gy) * 32.0)
			root.add_child(s)


func _process(_d: float) -> void:
	_frames += 1
	if _shot != "" and _frames == 30:
		var img := get_viewport().get_texture().get_image()
		DirAccess.make_dir_recursive_absolute(_shot.get_base_dir())
		img.save_png(_shot)
		print("[TILEBENCH] shot -> %s" % _shot)
		if _bench_limit == 0:
			get_tree().quit()
	var now := Time.get_ticks_usec()
	if _bench_limit > 0 and _frames > _skip and now > _warm_usec:
		if _warm_usec == 0:
			_warm_usec = now
		else:
			_warm_last = now
	if _bench_limit > 0 and _frames >= _bench_limit and _warm_usec > 0:
		var warm := (_warm_last - _warm_usec) / 1e6
		var n := _bench_limit - _skip - 1
		var favg: float = float(n) / max(warm, 1e-6)
		print("[FINAL] tilebench frames=%d warm=%.3fs avg_fps=%.1f draws=%d" % [
			_bench_limit, warm, favg,
			int(Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))])
		get_tree().quit()
