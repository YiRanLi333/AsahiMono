extends "res://DevPreview/WaterLab/WaterDemoBase.gd"
## 变体 5 —— 浅水见底 + 轻折射 + 移动焦散网（v2）

func variant_id() -> String:
	return "caustic_lite"

func shader_path() -> String:
	return "res://Shaders/water/caustic_lite.gdshader"

func hud_note() -> String:
	return "see-through bottom + <=1.5px refract + 23px caustic net"

func apply_extra(m: ShaderMaterial) -> void:
	_set_uniform("caustic_tex", load(CAUSTIC_PATH))
	_set_uniform("rocks_tex", load(ROCKS_PATH))
	_set_uniform("shape_px", Vector2(576.0, 256.0))
	_set_uniform("refr_px", 1.5)
	_set_uniform("caustic_px", Vector2(256.0, 256.0))
	_set_uniform("caustic_amount", 0.30)
	_set_uniform("caustic_speed", 4.0)
	_set_uniform("absorb_boost", 1.02)
	_set_uniform("foam_px", 3.0)
	_set_uniform("foam_amount", 0.52)
	_set_uniform("refl_amount", 0.30)
