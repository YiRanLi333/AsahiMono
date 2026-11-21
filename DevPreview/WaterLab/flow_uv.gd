extends "res://DevPreview/WaterLab/WaterDemoBase.gd"
## 变体 1 —— 双 caustic 网反向滚动 + sin 交叉淡化 + 石头倒影（v2）

func variant_id() -> String:
	return "flow_uv"

func shader_path() -> String:
	return "res://Shaders/water/flow_uv.gdshader"

func hud_note() -> String:
	return "2 caustic nets, opposite scroll + crossfade + rock refl"

func apply_extra(m: ShaderMaterial) -> void:
	_set_uniform("water_a", load(WATER_A_PATH))
	_set_uniform("water_b", load(WATER_B_PATH))
	_set_uniform("rocks_tex", load(ROCKS_PATH))
	_set_uniform("shape_px", Vector2(576.0, 256.0))
	# 4s = 2 x 2s(截图循环)，无可见接缝
	_set_uniform("crossfade_period", 4.0)
	# v2 的网比 v1 的短划粗，滚太快会糊成流痕 -> 5~7 px/s 才像水面
	_set_uniform("speed_a", 7.0)
	_set_uniform("speed_b", -5.0)
	_set_uniform("glint_amount", 0.08)
	_set_uniform("deep_k", 0.24)
	_set_uniform("foam_px", 3.0)
	_set_uniform("foam_amount", 0.62)
	_set_uniform("shallow_px", 13.0)
	_set_uniform("shallow_amount", 0.42)
	_set_uniform("refl_amount", 0.38)
	_set_uniform("refl_drop_px", 5.0)
