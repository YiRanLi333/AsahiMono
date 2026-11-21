extends "res://DevPreview/WaterLab/WaterDemoBase.gd"
## 变体 4 —— 雨天涟漪（程序化扩散圆环 + 雨天调色）（v2）

func variant_id() -> String:
	return "rain_ripple"

func shader_path() -> String:
	return "res://Shaders/water/rain_ripple.gdshader"

func hud_note() -> String:
	return "procedural poisson ripples + teal rain grade"

func apply_extra(m: ShaderMaterial) -> void:
	_set_uniform("water_a", load(WATER_A_PATH))
	_set_uniform("rocks_tex", load(ROCKS_PATH))
	_set_uniform("shape_px", Vector2(576.0, 256.0))
	_set_uniform("ring_life", 1.0)       # 1s 一轮，2s 截图循环里正好 2 圈
	# v2: 0.45 -> 0.30。REF 池塘 S≈0.99，把水洗灰是最大的塑料感来源
	_set_uniform("rain_grey", 0.12)
	_set_uniform("cell_a", 30.0)         # 两套互质格子 -> 近似泊松分布
	_set_uniform("cell_b", 43.0)
	_set_uniform("ring_max_px", 15.0)
	_set_uniform("ring_width", 1.2)
	_set_uniform("rain_darken", 0.99)
	_set_uniform("deep_k", 0.24)
	_set_uniform("foam_px", 3.0)
	_set_uniform("foam_amount", 0.50)
	_set_uniform("refl_amount", 0.26)
