extends "res://DevPreview/WaterLab/WaterDemoBase.gd"
## 变体 3 —— 动态水位（水线随 level 上下移动，岸线/湿痕/石头没入跟着重算）（v2）

func variant_id() -> String:
	return "level_dynamic"

func shader_path() -> String:
	return "res://Shaders/water/level_dynamic.gdshader"

func hud_note() -> String:
	return "water level 0..1 -> rising shoreline + submerged rocks"

func apply_extra(m: ShaderMaterial) -> void:
	_set_uniform("water_a", load(WATER_A_PATH))
	_set_uniform("water_b", load(WATER_B_PATH))
	_set_uniform("rocks_tex", load(ROCKS_PATH))
	_set_uniform("shape_px", Vector2(576.0, 256.0))
	_set_uniform("level", 0.5)
	_set_uniform("auto_cycle", 1.0)
	# demo 用 2s 一次涨落（正好等于截图循环 2s，gif 里能看到完整涨潮+退潮）；
	# 正式工程建议 20-40s，配合雨水事件短暂加快。
	_set_uniform("cycle_period", 2.0)
	_set_uniform("rise_min", -4.0)
	_set_uniform("rise_max", 13.0)
	_set_uniform("deep_k", 0.24)
	_set_uniform("foam_px", 3.0)
	_set_uniform("foam_amount", 0.58)
	_set_uniform("shallow_amount", 0.40)
	_set_uniform("refl_amount", 0.40)
	_set_uniform("glint_amount", 0.07)
