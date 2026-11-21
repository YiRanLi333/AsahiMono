extends Node
## 临时抓帧工具（用完删）。用法：
##   godot --path E:\AsahiMono\app -s res://DevPreview/WaterLab/_WaterShot.gd  (不行：需要场景)
## 实际用场景方式：
##   godot --path E:\AsahiMono\app res://DevPreview/WaterLab/_WaterShot.tscn -- --variant=flow_uv --out=E:/AsahiMono/tmp/water-v2/flow_uv
##
## 每 5 帧抓一张，共 24 张（= 12fps 下 2 秒循环）；输出 1280x720（相机 zoom=2，世界 640x360）。

var _variant := "flow_uv"
var _out := ""
var _frames := 0
var _saved := 0
var _stage: Node = null

func _ready() -> void:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--variant="):
			_variant = a.split("=")[1]
		elif a.begins_with("--out="):
			_out = a.split("=")[1]
	if _out == "":
		_out = "E:/AsahiMono/tmp/water-v2/" + _variant
	DirAccess.make_dir_recursive_absolute(_out)
	DisplayServer.window_set_size(Vector2i(1280, 720))
	_stage = load("res://DevPreview/WaterLab/%s.tscn" % _variant).instantiate()
	add_child(_stage)
	# max_fps 必须在 add_child 之后设：场景 _ready 里会关 vsync 跑满速
	Engine.max_fps = 60
	print("[SHOT] variant=%s out=%s" % [_variant, _out])

func _process(_delta: float) -> void:
	_frames += 1
	if _frames % 5 != 0:
		return
	var img := get_viewport().get_texture().get_image()
	img.save_png("%s/f%02d.png" % [_out, _saved])
	_saved += 1
	if _saved >= 24:
		print("[SHOT] DONE saved=%d" % _saved)
		get_tree().quit()
