shader_type canvas_item;
render_mode blend_mix, unshaded;

// ============================================================================
// flow_directional —— 方向性流动 + 沿岸浅色沫带（v2，对齐 REF-05）
//
// 原理：水纹只沿一个方向推进（flow_dir，默认等距主轴 -y：屏幕右上），
//       再取一层按 1.35 倍速推进的采样做 max() 叠加 —— 形成"速度拖影"，
//       读起来是有向的流水，而不是乱抖。
//
// v2 改了什么：
//   1. 贴图换成 24px 周期 caustic 网（网周期实测 25.6 / 28.6px），
//      速度快了网线会"糊成流痕"，所以 flow_speed 从 14 降到 9；
//   2. 配色整族下移一档、色相 200° → 186°（REF-05 水面分位色）；
//   3. 岸沿从"11px 蕾丝泡沫带 + 2px 暗轮廓"收成
//      "3px 浅色沫带 + 1px 亮线 + 1px 外缘细暗线"——REF 的岸带只有 2~3px，
//      而且淡色像素占比才 7~9%，宽泡沫带是我们上一版"塑料感"的主要来源；
//   4. 浅水偏亮偏绿 / 深水偏深蓝（col_shallow + deep_k），石头柔和倒影。
// ============================================================================

varying vec2 world_px;

uniform sampler2D shape_tex : filter_nearest, repeat_disable;
uniform sampler2D water_a   : filter_nearest, repeat_enable;
uniform sampler2D water_b   : filter_nearest, repeat_enable;
uniform sampler2D rocks_tex : filter_nearest, repeat_disable;

uniform vec2  tex_px      = vec2(128.0, 64.0);
uniform vec2  shape_px    = vec2(576.0, 256.0);
uniform vec2  flow_dir    = vec2(0.8944, -0.4472);  // 等距 -y 主轴
uniform float flow_speed  = 9.0;                    // px/s
uniform float streak_gain = 0.22;                   // 拖影强度
uniform float foam_px     = 3.0;                    // 岸带宽度（REF 是 2~3px）
uniform float foam_amount = 0.55;
uniform float foam_phase  = 0.0;
uniform float rim_px      = 1.0;
uniform float rim_amount  = 0.30;
uniform float shallow_px  = 13.0;
uniform float shallow_amount = 0.38;
uniform float deep_k    = 0.24;
uniform float refl_amount = 0.36;
uniform float refl_drop_px = 5.0;
uniform float mottle_amount = 0.10;   // 大尺度明暗团幅度
uniform float alpha_mul   = 1.0;

uniform vec4  col_deep    = vec4(0.024, 0.486, 0.565, 1.0);  // #067C90
uniform vec4  col_base    = vec4(0.008, 0.702, 0.769, 1.0);  // #02B3C4
uniform vec4  col_light   = vec4(0.063, 0.784, 0.824, 1.0);  // #10C8D2
uniform vec4  col_high    = vec4(0.498, 0.941, 0.941, 1.0);  // #7FF0F0
uniform vec4  col_shallow = vec4(0.435, 0.875, 0.878, 1.0);  // #6FDFE0
uniform vec4  col_rim     = vec4(0.012, 0.314, 0.361, 1.0);  // #03505C
uniform vec4  col_foam    = vec4(0.894, 0.988, 1.000, 1.0);  // #E4FCFF
uniform vec4  col_shadow  = vec4(0.016, 0.420, 0.494, 1.0);  // #046B7E

// 大尺度明暗团：REF 池塘有成片 100~200px 的亮/暗块，不加这个整片就是一块死色
// （这是"塑料感"的第二个来源 —— 不是颜色不对，是整片没有明暗层次）
vec3 mottle_tint(vec3 c, float amt) {
	float m = sin(world_px.x * 0.052 + 1.7) * sin(world_px.y * 0.037 - 0.6)
	        + 0.6 * sin(world_px.x * 0.021 - world_px.y * 0.026 + 2.3);
	m = 0.5 + 0.5 * clamp(m * 0.55, -1.0, 1.0);
	return c * mix(1.0 - amt, 1.0 + amt, m);
}

void vertex() {	world_px = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
}

// 先按深度压暗水体，再让网线盖上去（否则深水吃掉亮线 -> 均匀深青 = 塑料感）
vec3 water_color(float l, float deep_amt) {
	float dark_cell = 1.0 - step(0.20, l);
	vec3 body = mix(col_base.rgb, col_deep.rgb,
	                clamp(deep_amt * deep_k + dark_cell * 0.16, 0.0, 1.0));
	vec3 line = mix(col_light.rgb, col_high.rgb, step(0.82, l));
	return mix(body, line, step(0.55, l) * mix(1.0, 0.90, deep_amt));
}

vec2 flow_off(float speed) {
	return mod(normalize(flow_dir) * (speed * TIME), tex_px);
}

float rock_refl(vec2 uv) {
	vec2 d = 1.0 / shape_px;
	float s = 0.0;
	s += texture(rocks_tex, uv + d * vec2( 0.0, refl_drop_px)).a;
	s += texture(rocks_tex, uv + d * vec2(-1.5, refl_drop_px + 3.0)).a;
	s += texture(rocks_tex, uv + d * vec2( 1.5, refl_drop_px + 3.0)).a;
	s += texture(rocks_tex, uv + d * vec2( 0.0, refl_drop_px + 6.0)).a;
	return s * 0.25;
}

void fragment() {
	vec4 sh = texture(shape_tex, UV);
	float inside = step(0.5, sh.a);
	float in_px  = sh.r * 32.0;
	float depth  = sh.g;

	// --- 有向流动 + 1.35 倍速拖影 -----------------------------------------
	float la  = texture(water_a, (world_px + flow_off(flow_speed)) / tex_px).r;
	float la2 = texture(water_a, (world_px + flow_off(flow_speed * 1.35) + vec2(0.0, 2.0)) / tex_px).r;
	float lvl = max(la, la2 * 0.92);
	lvl = mix(la, lvl, clamp(0.60 + streak_gain, 0.0, 1.0));  // 主要吃拖影，留一点底纹

	vec3 col = water_color(lvl, smoothstep(0.30, 1.0, depth));
	col = mottle_tint(col, mottle_amount);

	// --- 石头柔和倒影 -------------------------------------------------------
	float refl = rock_refl(UV) * inside;
	col = mix(col, col_shadow.rgb, refl * refl_amount);

	// --- 浅滩：偏亮偏绿 -----------------------------------------------------
	float shallow = 1.0 - smoothstep(2.0, shallow_px, in_px);
	col = mix(col, col_shallow.rgb, shallow * shallow_amount);

	// --- 沿岸浅色沫带（3px，比重按网贴图抖动，让它顺岸"爬"） ----------------
	float band  = 1.0 - smoothstep(0.6, foam_px, in_px);
	float noise = texture(water_b, (world_px + flow_off(flow_speed * 1.8)) / tex_px).r;
	float pulse = 0.55 + 0.45 * sin(in_px * 1.15 - TIME * 2.4 + foam_phase);
	float lacy  = smoothstep(0.34, 0.86, noise * 1.30 * pulse);
	float foam  = band * (0.62 + 0.38 * lacy);
	col = mix(col, col_foam.rgb, clamp(foam, 0.0, 1.0) * foam_amount);

	// --- 水线：贴岸 1px 亮线 + 1px 外缘细暗线 -------------------------------
	float line = 1.0 - smoothstep(0.0, 1.2, in_px);
	col = mix(col, col_foam.rgb, line * 0.55);
	float rim = 1.0 - smoothstep(0.0, rim_px, in_px);
	col = mix(col, col_rim.rgb, rim * rim_amount);

	// --- 顺流方向的高光条（比波光更"长"，强化方向感） ----------------------
	float streak = sin((world_px.x * 0.06 + world_px.y * 0.11) - TIME * 2.2);
	streak = pow(max(streak, 0.0), 9.0);
	streak = floor(streak * 5.0) / 5.0;
	col += col_high.rgb * streak * 0.10;

	COLOR = vec4(col, inside * alpha_mul);
}
