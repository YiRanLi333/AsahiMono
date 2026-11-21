# refblock_fill.gd -- 把 res://Assets/maps/refblock.json 灌进 TileMapLayer（地面/侧壁/装饰）
#
# 设计原则：**不在这份脚本里手抄任何表**。
#   * wang 掩码 -> 图集坐标：从 ground_tileset.tres 里每张瓦片的 peering bit 反推
#     （peering == -1 表示「这个方向有暴露面」）。所以重跑 make_atlas.py / build_tileset.gd
#     之后这里不用改，加了新变体套数也自动认。
#   * 侧壁：TileSet 里侧壁瓦片没有 terrain，用 TileData 的 meta（slug / dir）查坐标。
#   * 装饰：用 TileData 的 meta（stem）查坐标。
#
# 用法（见 app/Maps/RefblockBuilder.gd 与 tools/tileset/bake_refblock.gd）：
#   var F = preload("res://Maps/refblock_fill.gd")
#   F.bake(F.load_map(), ground_layer, cliff_layer, [deco1, deco2, ...])
extends RefCounted

const MAP_PATH := "res://Assets/maps/refblock.json"
const CLIFF_SOURCE_ID := 100
const DECO_VAR_SUFFIX := ["", "_a", "_b", "_c"]   # refblock.json 的 variant 0/1/2 -> 名字后缀

# 8 个方向的网格偏移，位序与 IsoWangTable.cs / AUTOTILE.md §4 完全一致
const DIRS := [
	Vector2i(0, -1), Vector2i(-1, 0), Vector2i(1, 0), Vector2i(0, 1),
	Vector2i(-1, -1), Vector2i(-1, 1), Vector2i(1, 1), Vector2i(1, -1),
]
# 角位 bit4..7 只有在它相邻的两条边都暴露时才保留（IsoWangTable 的 256->47 折叠规则）
const CORNER_SIDES := [[0, 1], [1, 3], [2, 3], [0, 2]]

# wang bit -> CellNeighbor；等距实测表，见 tools/tileset/calibrate.gd 与 neighbor-map.txt
const PHB := [
	TileSet.CELL_NEIGHBOR_TOP_RIGHT_SIDE,     # bit0 ne  (0,-1)
	TileSet.CELL_NEIGHBOR_TOP_LEFT_SIDE,      # bit1 nw  (-1,0)
	TileSet.CELL_NEIGHBOR_BOTTOM_RIGHT_SIDE,  # bit2 se  (1,0)
	TileSet.CELL_NEIGHBOR_BOTTOM_LEFT_SIDE,   # bit3 sw  (0,1)
	TileSet.CELL_NEIGHBOR_TOP_CORNER,         # bit4     (-1,-1)
	TileSet.CELL_NEIGHBOR_LEFT_CORNER,        # bit5     (-1,1)
	TileSet.CELL_NEIGHBOR_BOTTOM_CORNER,      # bit6     (1,1)
	TileSet.CELL_NEIGHBOR_RIGHT_CORNER,       # bit7     (1,-1)
]

# World.TileKind（C# 枚举）-> 瓦片目录名
const KIND_BY_CODE := {1: "grass", 2: "soil", 3: "tilled", 4: "water", 5: "sand", 6: "path"}


# ---------------------------------------------------------------- 读地图

static func load_map(path: String = MAP_PATH) -> Dictionary:
	if not FileAccess.file_exists(path):
		push_error("refblock_fill: 找不到地图 json: " + path)
		return {}
	var txt := FileAccess.get_file_as_string(path)
	var parsed = JSON.parse_string(txt)
	if typeof(parsed) != TYPE_DICTIONARY:
		push_error("refblock_fill: json 解析失败: " + path)
		return {}
	return parsed


# ---------------------------------------------------------------- 建索引

# slug -> {"source_id": int, "masks": {mask: [Vector2i, ...]}}
static func ground_index(ts: TileSet) -> Dictionary:
	var out := {}
	var slug_of_terrain := {}
	if ts.get_terrain_sets_count() > 0:
		for tid in range(ts.get_terrains_count(0)):
			slug_of_terrain[tid] = String(ts.get_terrain_name(0, tid))
	for si in range(ts.get_source_count()):
		var sid := ts.get_source_id(si)
		var src: TileSetSource = ts.get_source(sid)
		if not (src is TileSetAtlasSource):
			continue
		for i in range(src.get_tiles_count()):
			var coords: Vector2i = src.get_tile_id(i)
			var td: TileData = src.get_tile_data(coords, 0)
			if td == null or td.terrain < 0:
				continue
			var slug: String = slug_of_terrain.get(td.terrain, "")
			if slug == "":
				continue
			# 由 peering bit 反推暴露掩码
			var mask := 0
			for b in range(8):
				if td.get_terrain_peering_bit(PHB[b]) == -1:
					mask |= (1 << b)
			if not out.has(slug):
				out[slug] = {"source_id": sid, "masks": {}}
			var bucket: Dictionary = out[slug]["masks"]
			if not bucket.has(mask):
				bucket[mask] = []
			(bucket[mask] as Array).append(coords)
	return out


# "slug/dir" -> Vector2i
static func cliff_index(ts: TileSet) -> Dictionary:
	var out := {}
	var src: TileSetAtlasSource = ts.get_source(CLIFF_SOURCE_ID)
	if src == null or not (src is TileSetAtlasSource):
		return out
	for i in range(src.get_tiles_count()):
		var coords: Vector2i = src.get_tile_id(i)
		var td: TileData = src.get_tile_data(coords, 0)
		var slug: String = String(td.get_meta("slug", ""))
		var dir: String = String(td.get_meta("dir", ""))
		if slug != "" and dir != "":
			out[slug + "/" + dir] = coords
	return out


# stem -> Vector2i
static func deco_index(ts: TileSet) -> Dictionary:
	var out := {}
	for si in range(ts.get_source_count()):
		var sid := ts.get_source_id(si)
		var src: TileSetSource = ts.get_source(sid)
		if not (src is TileSetAtlasSource):
			continue
		for i in range(src.get_tiles_count()):
			var coords: Vector2i = src.get_tile_id(i)
			var td: TileData = src.get_tile_data(coords, 0)
			var stem: String = String(td.get_meta("stem", ""))
			if stem != "":
				out[stem] = Vector2i(coords)
	return out


# ---------------------------------------------------------------- 掩码

static func exposed_mask(cells: Array, w: int, h: int, x: int, y: int) -> int:
	var code := int(cells[y][x])
	var raw := 0
	for b in range(8):
		var d: Vector2i = DIRS[b]
		var nx := x + d.x
		var ny := y + d.y
		if nx < 0 or ny < 0 or nx >= w or ny >= h:
			raw |= (1 << b)     # 出界 = 虚空 = 暴露
		elif int(cells[ny][nx]) != code:
			raw |= (1 << b)
	# 折叠角位：只有相邻两条边都暴露才保留
	for ci in range(4):
		var bit := 4 + ci
		if (raw & (1 << bit)) != 0:
			var a: int = CORNER_SIDES[ci][0]
			var b2: int = CORNER_SIDES[ci][1]
			if (raw & (1 << a)) == 0 or (raw & (1 << b2)) == 0:
				raw &= ~(1 << bit)
	return raw


# ---------------------------------------------------------------- 烘焙

# cliff_layers / deco_layers: 按「同一格堆叠深度」排列的 TileMapLayer 数组
#   （一个 TileMapLayer 每格只能放一张瓦片；侧壁在 (12,12) 这种角格要 se+sw 两块，
#     装饰同一格最多堆 5 件，所以要按深度分到多层）
static func bake(map: Dictionary, ground, cliff_layers: Array, deco_layers: Array) -> Dictionary:
	var res := {
		"ground": 0, "cliff": 0, "deco": 0,
		"missing_masks": {}, "missing_stems": [], "max_depth": 0, "cliff_depth": 0,
		"deco_drop": [], "warnings": [],
	}
	if map.is_empty() or ground == null:
		res["warnings"].append("地图或 ground 层为空，什么都没灌")
		return res
	var w := int(map["width"])
	var h := int(map["height"])
	var cells: Array = map["cells"]
	var gts: TileSet = ground.tile_set
	if gts == null:
		res["warnings"].append("ground 层没有 tile_set")
		return res
	var gidx := ground_index(gts)

	# ---- 地面 ----
	for y in range(h):
		for x in range(w):
			var code := int(cells[y][x])
			var slug: String = KIND_BY_CODE.get(code, "")
			if slug == "":
				continue
			var info: Dictionary = gidx.get(slug, {})
			if info.is_empty():
				var k := "kind:" + slug
				res["missing_masks"][k] = int(res["missing_masks"].get(k, 0)) + 1
				continue
			var mask := exposed_mask(cells, w, h, x, y)
			var bucket: Dictionary = info["masks"]
			if not bucket.has(mask):
				var k2 := "%s:%d" % [slug, mask]
				res["missing_masks"][k2] = int(res["missing_masks"].get(k2, 0)) + 1
				# 退化：取掩码最接近的一张（按不同位数最少）
				var best := -1
				var best_d := 99
				for m in bucket.keys():
					var d := _popcount(int(m) ^ mask)
					if d < best_d:
						best_d = d
						best = int(m)
				if best >= 0:
					mask = best
				else:
					continue
			var choices: Array = bucket[mask]
			# 确定性挑变体：同一格每次烘焙都挑同一张（不然每跑一次画面都变）
			var pick: Vector2i = choices[abs(hash(Vector2i(x, y))) % choices.size()]
			ground.set_cell(Vector2i(x, y), int(info["source_id"]), pick)
			res["ground"] = int(res["ground"]) + 1

	# ---- 侧壁：邻居是虚空才画（与 IsoWorldRenderer 的 se/sw 规则一致）----
	if not cliff_layers.is_empty():
		var cts: TileSet = cliff_layers[0].tile_set
		if cts != null:
			var cidx := cliff_index(cts)
			var cliff_depth := {}
			for y in range(h):
				for x in range(w):
					var code := int(cells[y][x])
					var slug: String = KIND_BY_CODE.get(code, "")
					if slug == "":
						continue
					for pair in [[Vector2i(1, 0), "se"], [Vector2i(0, 1), "sw"]]:
						var d: Vector2i = pair[0]
						var nx := x + d.x
						var ny := y + d.y
						var void_neighbor := nx < 0 or ny < 0 or nx >= w or ny >= h \
							or int(cells[ny][nx]) == 0
						if not void_neighbor:
							continue
						var key := slug + "/" + String(pair[1])
						if not cidx.has(key):
							res["missing_masks"][key] = int(res["missing_masks"].get(key, 0)) + 1
							continue
						var cell := Vector2i(x, y)
						var depth := int(cliff_depth.get(cell, 0))
						if depth >= cliff_layers.size():
							res["warnings"].append("侧壁层数不够：cell %s" % str(cell))
							continue
						cliff_layers[depth].set_cell(cell, CLIFF_SOURCE_ID, cidx[key])
						cliff_depth[cell] = depth + 1
						res["cliff_depth"] = maxi(int(res["cliff_depth"]), depth + 1)
						res["cliff"] = int(res["cliff"]) + 1

	# ---- 装饰：同格可能堆多个 -> 按深度分到不同 layer ----
	var deco_list: Array = map.get("deco", [])
	if not deco_list.is_empty() and not deco_layers.is_empty():
		var dts: TileSet = deco_layers[0].tile_set
		if dts == null:
			res["warnings"].append("deco 层没有 tile_set")
		else:
			var didx := deco_index(dts)
			var by_cell := {}
			for item in deco_list:
				var name := String(item["name"])
				var variant := int(item.get("variant", 0))
				var suffix := String(DECO_VAR_SUFFIX[variant]) if variant < DECO_VAR_SUFFIX.size() else ""
				var stem := name + suffix
				if not didx.has(stem):
					(res["missing_stems"] as Array).append(stem)
					continue
				var fx := float(item["x"])
				var fy := float(item["y"])
				var cx := clampi(int(round(fx)), 0, w - 1)
				var cy := clampi(int(round(fy)), 0, h - 1)
				# 记录吸附误差（格单位），报告里量化用
				(res["deco_drop"] as Array).append([fx - cx, fy - cy])
				if not by_cell.has(Vector2i(cx, cy)):
					by_cell[Vector2i(cx, cy)] = []
				(by_cell[Vector2i(cx, cy)] as Array).append(stem)
			var cells_keys := by_cell.keys()
			cells_keys.sort()
			for key in cells_keys:
				var stems: Array = by_cell[key]
				stems.sort()
				res["max_depth"] = maxi(int(res["max_depth"]), stems.size())
				for depth in range(stems.size()):
					if depth >= deco_layers.size():
						res["warnings"].append("deco 层数不够：cell %s 需要 %d 层" % [str(key), stems.size()])
						break
					var layer = deco_layers[depth]
					layer.set_cell(key, 0, didx[stems[depth]])
					res["deco"] = int(res["deco"]) + 1
	return res


static func _popcount(v: int) -> int:
	var n := 0
	var x := v
	while x != 0:
		n += (x & 1)
		x = x >> 1
	return n


# ---------------------------------------------------------------- 工具

static func layers_empty(layers: Array) -> bool:
	for l in layers:
		if l != null and l.get_used_cells().size() > 0:
			return false
	return true


static func stats(layers: Array) -> Dictionary:
	var out := {}
	for l in layers:
		if l == null:
			continue
		out[String(l.name)] = l.get_used_cells().size()
	return out
