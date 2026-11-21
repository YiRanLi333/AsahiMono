# RefblockBuilder.gd -- 挂在 Maps/Refblock.tscn 根节点上
#
# 作用只有两个：
#   1. 场景被清空（三/四层都没格子）时，从 Assets/maps/refblock.json 重新灌一遍，
#      这样「删光了想恢复」有救。**只要地面层还有格子就一个字都不动**，绝不覆盖手改。
#   2. 运行时打印每层的 get_used_cells() 数量，供命令行验证用。
#
# 灌格子/挑瓦片的逻辑全在 Maps/refblock_fill.gd，这里只是调用方。
@tool
extends Node2D

const F = preload("res://Maps/refblock_fill.gd")

## 关掉它就不会自动补内容（想从空白开始刷就关掉）
@export var auto_fill_when_empty := true


func _ready() -> void:
	var layers := _layers()
	if auto_fill_when_empty and F.layers_empty(layers):
		var ground := get_node_or_null("Ground")
		var cliff_layers: Array = _prefixed_layers("Cliff")
		var deco_layers: Array = _prefixed_layers("Deco")
		if ground != null and not deco_layers.is_empty() and not cliff_layers.is_empty():
			var map: Dictionary = F.load_map()
			if not map.is_empty():
				var st: Dictionary = F.bake(map, ground, cliff_layers, deco_layers)
				print("[RefblockBuilder] 场景是空的，已从 refblock.json 补内容: ", st)
	if not Engine.is_editor_hint():
		print_layer_stats()


## 打印每层格子数（命令行验证用）
func print_layer_stats() -> void:
	var total := 0
	var parts: Array = []
	for l in _layers():
		if l == null:
			continue
		var n: int = l.get_used_cells().size()
		parts.append("%s=%d" % [l.name, n])
		total += n
	print("[Refblock] used_cells per layer: ", ", ".join(parts), "  total=", total, " (used_rect=", _union_rect(), ")")


func _union_rect() -> String:
	for l in _layers():
		if l != null:
			return str(l.get_used_rect())
	return "-"


func _layers() -> Array:
	var out: Array = []
	var g := get_node_or_null("Ground")
	if g != null:
		out.append(g)
	var ysort := get_node_or_null("YSort")
	if ysort != null:
		for c in ysort.get_children():
			if c is TileMapLayer:
				out.append(c)
	return out


func _deco_layers() -> Array:
	return _prefixed_layers("Deco")


func _prefixed_layers(prefix: String) -> Array:
	var out: Array = []
	var ysort := get_node_or_null("YSort")
	if ysort != null:
		for c in ysort.get_children():
			if c is TileMapLayer and String(c.name).begins_with(prefix):
				out.append(c)
	return out
