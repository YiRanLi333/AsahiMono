extends Node2D
## ============================================================================
## WaterDemoBase —— 所有水效果变体 demo 的公共底座
##
## 场景结构（全部在代码里搭，变体 .tscn 只有一个 root + 脚本）：
##   WaterDemo(root)
##   ├── Backdrop    Sprite2D  pond_backdrop.png  (1 draw call, 静止底图)
##   ├── Water       Sprite2D  pond_shape.png + ShaderMaterial(<variant>.gdshader)
##   │                          (1 draw call, 变体的动态效果全在这)
##   ├── Cam         Camera2D  zoom=2 (640x360 世界像素 -> 1280x720 屏幕, 整数倍)
##   └── HUD         CanvasLayer/Label (变体名 + 实测 fps + draw calls)
##
## 约定：水面着色器一律用世界像素坐标采样（varying world_px），
##       所以 Shape 贴图必须 centered=false 且 position = (shape_box 左上角)。
##
## fps 统计：Engine.get_frames_per_second() 每秒才刷一次，短跑会读到首帧编译卡顿的
## 陈旧值；所以这里另外用 Time.get_ticks_usec() 自己算 real fps（每窗口清零）。
## ============================================================================

const ART := "res://DevPreview/WaterLab/art/"
const FX := "res://Assets/tiles/iso/waterfx/"

const SHAPE_PATH := ART + "pond_shape.png"
const BACKDROP_PATH := ART + "pond_backdrop.png"
const ROCKS_PATH := ART + "pond_rocks.png"
const WATER_A_PATH := FX + "water_a.png"
const WATER_B_PATH := FX + "water_b.png"
const CAUSTIC_PATH := FX + "caustic.png"

const SHAPE_POS := Vector2(-288.0, -128.0)   # shape_box_world 左上角
const BACKDROP_POS := Vector2(-512.0, -320.0)

var _mat: ShaderMaterial
var _label: Label
var _uniforms := {}
var _t := 0.0
var _draw_calls := 0

var _win_usec := 0
var _win_frames := 0
var _real_fps_sum := 0.0
var _real_fps_n := 0
var _real_fps_min := 99999.0
var _real_fps_max := 0.0
var _frames_total := 0
var _usec_start := 0

# 基准模式（--bench=N：跳过前 _bench_skip 帧预热，再统计 N 帧后自己退出）
var _bench_limit := 0
var _bench_skip := 90
var _warm_usec := 0
var _warm_last := 0
var _fps_inst_min := 99999.0
var _fps_inst_max := 0.0
var _fps_inst_sum := 0.0
var _fps_inst_n := 0

# ---------------------------------------------------------------- 子类覆盖点
func variant_id() -> String:
	return "water"

func shader_path() -> String:
	return ""

func hud_note() -> String:
	return ""

func apply_extra(_m: ShaderMaterial) -> void:
	pass

# ---------------------------------------------------------------- 构建
func _ready() -> void:
	# 关垂直同步 -> 跑出真实余量（截图工具会自己再把 Engine.max_fps 限回 60）
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	_build()
	apply_extra(_mat)
	print("[BENCH] %s built, shader=%s, uniforms=%d" % [variant_id(), shader_path(), _uniforms.size()])
	print("[BENCH] %s shader_error_check: no 'SHADER ERROR' line above = compiled ok" % variant_id())
	_usec_start = Time.get_ticks_usec()
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--bench="):
			_bench_limit = int(a.split("=")[1])
	if _bench_limit > 0:
		# 关掉 vsync 后 GPU 会"跳过呈现"，帧计数器能跑到一万多 fps —— 那个数字没有意义。
		# 基准模式改成：vsync 开 + Engine.max_fps=60（和正式游戏同一个 pacing），
		# 然后看这 60 帧/秒的节奏能不能真的跟上 —— 跟不上 real fps 会掉到 60 以下。
		# --nocap：不锁帧，用来看这一帧到底还剩多少余量（3.5ms vs 16.7ms 的区别）。
		if OS.get_cmdline_user_args().has("--nocap"):
			DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
			Engine.max_fps = 0
		else:
			DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_ENABLED)
			Engine.max_fps = 60

func _build() -> void:
	var backdrop := Sprite2D.new()
	backdrop.name = "Backdrop"
	backdrop.texture = load(BACKDROP_PATH)
	backdrop.centered = false
	backdrop.position = BACKDROP_POS
	add_child(backdrop)

	var sh: Shader = load(shader_path())
	_mat = ShaderMaterial.new()
	_mat.shader = sh
	for u in sh.get_shader_uniform_list():
		_uniforms[String(u["name"])] = true

	var spr := Sprite2D.new()
	spr.name = "Water"
	spr.texture = load(SHAPE_PATH)
	spr.centered = false
	spr.position = SHAPE_POS
	spr.material = _mat
	add_child(spr)
	_set_uniform("shape_tex", load(SHAPE_PATH))

	var cam := Camera2D.new()
	cam.name = "Cam"
	cam.position = Vector2.ZERO
	cam.zoom = Vector2(2.0, 2.0)
	add_child(cam)
	cam.make_current()

	_build_hud()

func _build_hud() -> void:
	if OS.get_cmdline_user_args().has("--nohud"):
		return   # 纯性能基准：只留 底图 + 水面（2 draw calls）
	var layer := CanvasLayer.new()
	layer.name = "HUD"
	add_child(layer)
	_label = Label.new()
	_label.position = Vector2(10.0, 8.0)
	_label.add_theme_font_size_override("font_size", 18)
	_label.add_theme_color_override("font_color", Color(1.0, 1.0, 1.0, 1.0))
	_label.add_theme_color_override("font_outline_color", Color(0.0, 0.0, 0.0, 0.75))
	_label.add_theme_constant_override("outline_size", 4)
	_label.text = "%s\n%s" % [variant_id(), hud_note()]
	layer.add_child(_label)

# ---------------------------------------------------------------- 工具
func _set_uniform(key: String, value: Variant) -> void:
	if _uniforms.has(key):
		_mat.set_shader_parameter(key, value)
	else:
		push_warning("%s: uniform '%s' not found in %s" % [variant_id(), key, shader_path()])

func _has_uniform(key: String) -> bool:
	return _uniforms.has(key)

# ---------------------------------------------------------------- 运行期统计
func _process(delta: float) -> void:
	_t += delta
	_frames_total += 1
	_win_frames += 1
	var now := Time.get_ticks_usec()
	if _win_usec == 0:
		_win_usec = now
	var dt := float(now - _win_usec) / 1000000.0
	if dt >= 1.0:
		var real := float(_win_frames) / dt
		_win_usec = now
		_win_frames = 0
		if _t > 1.0:   # 丢掉第一秒（着色器编译/首帧缓存）
			_real_fps_sum += real
			_real_fps_n += 1
			_real_fps_min = minf(_real_fps_min, real)
			_real_fps_max = maxf(_real_fps_max, real)
		_draw_calls = int(RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_DRAW_CALLS_IN_FRAME))
		var avg := _real_fps_sum / maxf(1.0, float(_real_fps_n))
		print("[BENCH] %s t=%.1fs real_fps=%.1f avg=%.1f min=%.1f max=%.1f engine_fps=%d draws=%d" % [
			variant_id(), _t, real, avg, _real_fps_min, _real_fps_max,
			int(Engine.get_frames_per_second()), _draw_calls])
		if _label != null:
			_label.text = "%s\n%.0f fps (avg %.0f)  ·  draws %d\n%s" % [
				variant_id(), real, avg, _draw_calls, hud_note()]
		var elapsed := float(now - _usec_start) / 1000000.0
		print("[RESULT] %s frames=%d elapsed=%.1fs avg_real_fps=%.1f draws=%d" % [
			variant_id(), _frames_total, elapsed, avg, _draw_calls])

	# ---- --bench=N 模式：跳预热 -> 统计瞬时帧率 -> 打印汇总并退出 ----------
	if _bench_limit > 0:
		if _frames_total == _bench_skip:
			_warm_usec = now
			_warm_last = now
			_win_usec = now
			_win_frames = 0
		elif _frames_total > _bench_skip and _warm_last > 0:
			var fdt := float(now - _warm_last) / 1000000.0
			_warm_last = now
			if fdt > 0.0:
				var inst := 1.0 / fdt
				_fps_inst_sum += inst
				_fps_inst_n += 1
				_fps_inst_min = minf(_fps_inst_min, inst)
				_fps_inst_max = maxf(_fps_inst_max, inst)
		if _frames_total >= _bench_limit and _warm_usec > 0:
			var wsec := float(now - _warm_usec) / 1000000.0
			var wn := _bench_limit - _bench_skip
			var favg := float(wn) / maxf(1e-6, wsec)
			_draw_calls = int(RenderingServer.get_rendering_info(
				RenderingServer.RENDERING_INFO_TOTAL_DRAW_CALLS_IN_FRAME))
			print("[FINAL] %s frames=%d warmup_skip=%d warm_elapsed=%.3fs avg_fps=%.1f min_inst=%.1f max_inst=%.1f draws=%d" % [
				variant_id(), _frames_total, _bench_skip, wsec, favg,
				_fps_inst_min, _fps_inst_max, _draw_calls])
			get_tree().quit()
