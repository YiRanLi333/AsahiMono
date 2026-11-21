extends SceneTree
# ============================================================================
# _R2Shot —— water r3 的验收抓帧器（不依赖 DevPreview，属于 waterfx/** 领地）
#
# 用法：
#   godot --path E:\AsahiMono\app -s res://Assets/tiles/iso/waterfx/preview/_R2Shot.gd -- \
#         --variant=base --scale=1 --shot=E:\AsahiMono\tmp\water-r2\base-vs-ref05.png
#
# 参数：
#   --variant=base|mid|deep|deepest|shallow|tile_shallow|tile_deep
#   --scale=1|0.75          0.75 = 用 canvas_transform 模拟游戏默认缩放
#   --shot=<png>            单帧存图（第 30 帧）
#   --seq=<dir>             24 帧序列（每 5 帧一张，共 24 张），配合 --scale
#   --bench=<N>             跑 N 帧后打印 avg_fps / draw calls
#   --tile=<res path>       覆盖 alpha 蒙版瓦片
#   --param=k=v;...         覆盖 shader uniform（v 支持 1 个 float 或 "x,y" / "r,g,b"）
# ============================================================================

const TILE := Vector2(128, 64)
const W := 1280
const H := 720

var _mat: ShaderMaterial
var _frames := 0
var _shot := ""
var _seq := ""
var _seq_i := 0
var _bench := 0
var _skip := 90
var _warm_usec := 0
var _warm_last := 0
var _scale := 1.0
var _tile_path := "res://Assets/tiles/iso/ground/water/water_w00.png"
var _scene := "plane"

var WANG_TABLE := {}   # raw mask -> wang index，启动时按 IsoWangTable.cs 的折叠规则算出来

# 推荐值（WATER-R3.md §2）—— 正式工程照这张表设即可
const P := {
	"tex_px": Vector2(1536, 768),
	"speed_a": 3.2,
	"speed_b": -1.2,
	"dir_a": Vector2(1.0, 0.18),
	"dir_b": Vector2(-0.85, 0.42),
	"crossfade_period": 36.0,
	"glint_amount": 0.0,
	"alpha_mul": 1.0,
	"rim_dark": 0.0,
	"net_warp_px": 0.7,
	"deep_amt": 0.0,
	"tile_detail": 0.85,
}

const PAL := {
	"col_deep": Color(0.012, 0.314, 0.361),   # #03505C
	"col_base": Color(0.008, 0.702, 0.769),   # #02B3C4
	"col_light": Color(0.184, 0.851, 0.875),  # #2FD9DF
	"col_high": Color(0.561, 0.949, 0.949),   # #8FF2F2
	"tile_ref": Color(0.008, 0.702, 0.769),   # #02B3C4
}


func _initialize() -> void:
	var variant := "base"
	var over := {}
	for a in OS.get_cmdline_user_args():
		var kv := a.split("=", true, 1)
		var key := kv[0].trim_prefix("--")
		var val := kv[1] if kv.size() > 1 else ""
		match key:
			"variant": variant = val
			"shot": _shot = val
			"seq": _seq = val
			"bench": _bench = int(val)
			"scale": _scale = float(val)
			"tile": _tile_path = val
			"scene": _scene = val
			"param":
				for pair in val.split(";", false):
					var p := pair.split("=", true, 1)
					if p.size() == 2:
						over[p[0]] = _parse(p[1])
	DisplayServer.window_set_size(Vector2i(W, H))
	root.size = Vector2i(W, H)
	Engine.max_fps = 60
	if _bench > 0:
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
		Engine.max_fps = 0

	_mat = ShaderMaterial.new()
	_mat.shader = load("res://Shaders/water/water_tile_flow.gdshader")
	_mat.set_shader_parameter("water_a", load("res://Assets/tiles/iso/waterfx/water_a_12x.png"))
	_mat.set_shader_parameter("water_b", load("res://Assets/tiles/iso/waterfx/water_b_12x.png"))
	for k in P:
		_mat.set_shader_parameter(k, P[k])
	for k in PAL:
		_mat.set_shader_parameter(k, PAL[k])

	match variant:
		"mid":
			_mat.set_shader_parameter("deep_amt", 0.35)
		"deep":
			_mat.set_shader_parameter("deep_amt", 0.7)
		"deepest":
			_mat.set_shader_parameter("deep_amt", 1.0)
		"shallow":
			_mat.set_shader_parameter("deep_amt", 0.0)
			_tile_path = "res://Assets/tiles/iso/ground/water_shallow/water_w00.png"
		"tile_shallow":
			_tile_path = "res://Assets/tiles/iso/ground/water_shallow/water_w00.png"
		"tile_deep":
			_tile_path = "res://Assets/tiles/iso/ground/water_deep/water_w00.png"
	for k in over:
		_mat.set_shader_parameter(k, over[k])

	if _scale != 1.0:
		root.canvas_transform = Transform2D(0.0, Vector2(_scale, _scale), 0.0, Vector2.ZERO)

	if _scene == "shore":
		_rank_wang()
		_build_shore()
	else:
		_build()
	print("[R2SHOT] variant=%s scene=%s scale=%.2f tile=%s" % [variant, _scene, _scale, _tile_path])


func _parse(v: String) -> Variant:
	if v.contains(","):
		var parts := v.split(",")
		if parts.size() == 2:
			return Vector2(float(parts[0]), float(parts[1]))
		return Color(float(parts[0]), float(parts[1]), float(parts[2]))
	return float(v)


func _build() -> void:
	var tex: Texture2D = load(_tile_path)
	if tex == null:
		print("[R2SHOT] !! tile missing: %s" % _tile_path)
		return
	var root2d := Node2D.new()
	root2d.position = Vector2(W * 0.5, H * 0.5 - 520.0)
	root.add_child(root2d)
	var n := 30
	for gy in range(-n, n + 1):
		for gx in range(-n, n + 1):
			var s := Sprite2D.new()
			s.texture = tex
			s.centered = true
			s.material = _mat
			s.position = Vector2((gx - gy) * 64.0, (gx + gy) * 32.0)
			root2d.add_child(s)


# ---------------------------------------------------------------------------
# 岸线场景：一块沙地 + 中央水池。水格按 8 邻居算 wang mask 取图，
# 并且**贴岸一圈用 water_shallow、内部用 water_deep**
# （这就是给 C# 的接法示范：同一套 wang，只是取图目录随"是否贴岸"切换）。
# ---------------------------------------------------------------------------
func _rank_wang() -> void:
	# 复刻 IsoWangTable.cs 的折叠：角位只有当它的两条边都暴露时才看得见，
	# 于是 256 个 raw mask 折叠成 47 个唯一值，按升序就是 w00..w46。
	WANG_TABLE = {}
	var uniq := {}
	for m in 256:
		var f: int = m & 0x0F
		if (m & 16) and (m & 1) and (m & 2): f |= 16
		if (m & 32) and (m & 2) and (m & 8): f |= 32
		if (m & 64) and (m & 4) and (m & 8): f |= 64
		if (m & 128) and (m & 1) and (m & 4): f |= 128
		uniq[f] = true
	var keys := uniq.keys()
	keys.sort()
	for i in keys.size():
		WANG_TABLE[keys[i]] = i
	var folded := {}
	for m in 256:
		var f: int = m & 0x0F
		if (m & 16) and (m & 1) and (m & 2): f |= 16
		if (m & 32) and (m & 2) and (m & 8): f |= 32
		if (m & 64) and (m & 4) and (m & 8): f |= 64
		if (m & 128) and (m & 1) and (m & 4): f |= 128
		folded[m] = WANG_TABLE[f]
	WANG_TABLE = folded
	print("[R2SHOT] wang table: %d unique -> %s" % [keys.size(), str(keys.slice(0, 4))])


func _is_water(gx: int, gy: int) -> bool:
	var dx := float(gx) - 7.5
	var dy := float(gy) - 7.5
	if gx < 0 or gy < 0 or gx > 15 or gy > 15:
		return false
	return (dx * dx) / (5.6 * 5.6) + (dy * dy) / (3.4 * 3.4) <= 1.0


func _mask_of(gx: int, gy: int, want: bool) -> int:
	# 与 IsoWangTable.cs 的位定义一致：bit=1 表示该邻居不是同一种材质
	var bits := 0
	if _is_water(gx, gy - 1) != want: bits |= 1
	if _is_water(gx - 1, gy) != want: bits |= 2
	if _is_water(gx + 1, gy) != want: bits |= 4
	if _is_water(gx, gy + 1) != want: bits |= 8
	if _is_water(gx - 1, gy - 1) != want: bits |= 16
	if _is_water(gx - 1, gy + 1) != want: bits |= 32
	if _is_water(gx + 1, gy + 1) != want: bits |= 64
	if _is_water(gx + 1, gy - 1) != want: bits |= 128
	return bits


func _shore_edge(gx: int, gy: int) -> bool:
	return not (_is_water(gx, gy - 1) and _is_water(gx - 1, gy)
			and _is_water(gx + 1, gy) and _is_water(gx, gy + 1))


func _build_shore() -> void:
	var root2d := Node2D.new()
	root2d.position = Vector2(W * 0.5, H * 0.5 - 480.0)
	root.add_child(root2d)
	var order: Array = []
	for gy in 16:
		for gx in 16:
			order.append(Vector2i(gx, gy))
	order.sort_custom(func(a: Vector2i, b: Vector2i) -> bool: return a.x + a.y < b.x + b.y)
	for c in order:
		var gx: int = c.x
		var gy: int = c.y
		var s := Sprite2D.new()
		s.centered = true
		s.position = Vector2((gx - gy) * 64.0, (gx + gy) * 32.0)
		if _is_water(gx, gy):
			var m := _mask_of(gx, gy, true)
			var t: int = WANG_TABLE[m]
			var dir := "water_shallow" if _shore_edge(gx, gy) else "water_deep"
			s.texture = load("res://Assets/tiles/iso/ground/%s/water_w%02d.png" % [dir, t])
			s.material = _mat
		else:
			var m2 := _mask_of(gx, gy, false)
			var t2: int = WANG_TABLE[m2]
			s.texture = load("res://Assets/tiles/iso/ground/sand/sand_w%02d.png" % t2)
		root2d.add_child(s)


func _process(_d: float) -> bool:
	_frames += 1
	if _shot != "" and _frames == 30:
		_save(_shot)
		print("[R2SHOT] shot -> %s" % _shot)
		if _bench == 0 and _seq == "":
			quit()
	if _seq != "" and _frames > 10 and (_frames - 10) % 20 == 0 and _seq_i < 24:
		_save("%s/f%02d.png" % [_seq, _seq_i])
		_seq_i += 1
		if _seq_i >= 24:
			print("[R2SHOT] seq -> %s (24)" % _seq)
			if _bench == 0:
				quit()
	var now := Time.get_ticks_usec()
	if _bench > 0 and _frames > _skip and now > _warm_usec:
		if _warm_usec == 0:
			_warm_usec = now
		else:
			_warm_last = now
	if _bench > 0 and _frames >= _bench and _warm_usec > 0:
		var warm := (_warm_last - _warm_usec) / 1e6
		var cnt := _bench - _skip - 1
		print("[FINAL] r2shot frames=%d warm=%.3fs avg_fps=%.1f draws=%d" % [
			_bench, warm, float(cnt) / max(warm, 1e-6),
			int(Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))])
		quit()
	return false


func _save(path: String) -> void:
	var img := root.get_texture().get_image()
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	img.save_png(path)
