extends "res://DevPreview/WaterLab/WaterDemoBase.gd"
## 变体 2 —— 方向性流动 + 沿岸浅色沫带（v2）

func variant_id() -> String:
	return "flow_directional"

func shader_path() -> String:
	return "res://Shaders/water/flow_directional.gdshader"

func hud_note() -> String:
	return "one direction + speed streak + 3px shore band"

func apply_extra(m: ShaderMaterial) -> void:
	_set_uniform("water_a", load(WATER_A_PATH))
	_set_uniform("water_b", load(WATER_B_PATH))
	_set_uniform("rocks_tex", load(ROCKS_PATH))
	_set_uniform("shape_px", Vector2(576.0, 256.0))
	_set_uniform("flow_dir", Vector2(0.8944, -0.4472))   # 等距 -y 主轴
	_set_uniform("flow_speed", 9.0)                      # v2: 14 -> 9（网太粗会糊）
	_set_uniform("streak_gain", 0.22)
	_set_uniform("foam_px", 3.0)                         # REF 岸带只有 2~3px
	_set_uniform("foam_amount", 0.55)
	_set_uniform("deep_k", 0.24)
	_set_uniform("shallow_amount", 0.38)
	_set_uniform("refl_amount", 0.36)
