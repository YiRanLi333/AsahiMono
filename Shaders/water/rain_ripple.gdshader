shader_type canvas_item;
render_mode blend_mix, unshaded;

// ============================================================================
// rain_ripple —— 雨天涟漪 + 雨天调色（v2，对齐 REF-05）
//
// 原理：不需要任何"贴图动画"，涟漪是纯程序化的。
//       把世界像素切成两套互质大小的格子（44px / 67px），每格用 hash 抖动出
//       一个圆心和相位，圆环半径 0 -> 24px 匀速扩张、alpha 随半径平方衰减。
//       两套格子错位叠加 ≈ 泊松分布（不会像单一网格那样读出棋盘节奏）。
//       环宽只有 1.5px 并量化为 2 阶 —— 保持像素风，不做柔和光晕。
//       再叠整体降饱和 + 冷色偏移 + 轻微压暗 = 雨天色调。
//
// v2 改了什么：
//   1. 底纹换成 24px 周期 caustic 网（网周期实测 25.6px），涟漪是"叠加物"，
//      所以网本身仍要看得出来；速度 5 px/s；
//   2. 雨天调色不能把水洗成灰——REF 的池塘 S≈0.99，比什么都饱和。
//      v1 的 rain_grey=0.55 是把水往灰蓝拖，读起来像塑料布。
//      这里 rain_grey 压到 0.30，冷偏色也换成青蓝（#4FA8B4）而不是灰蓝（#A3BFD3）；
//   3. 岸沿 2.5px 暗轮廓 -> 3px 浅色沫带 + 1px 亮线 + 1px 细暗线；
//   4. 石头倒影被雨打散：只留很淡的一团（refl_amount 0.26）。
// ============================================================================

varying vec2 world_px;

uniform sampler2D shape_tex : filter_nearest, repeat_disable;
uniform sampler2D water_a   : filter_nearest, repeat_enable;
uniform sampler2D water_b   : filter_nearest, repeat_enable;
uniform sampler2D rocks_tex : filter_nearest, repeat_disable;

uniform vec2  shape_px    = vec2(576.0, 256.0);
uniform vec2  tex_px      = vec2(128.0, 64.0);
uniform float cell_a      = 44.0;
uniform float cell_b      = 67.0;
uniform float ring_life   = 1.5;    // 秒（整除 3600，避免 TIME 回绕跳变）
uniform float ring_max_px = 24.0;
uniform float ring_width  = 1.5;
uniform float ripple_amount = 0.85;
uniform float rain_grey   = 0.18;   // 降饱和（别太大：REF 池塘饱和度高）
uniform float rain_darken = 0.99;
uniform float glint_amount = 0.06;
uniform float flow_speed  = 5.0;
uniform float deep_k    = 0.24;
uniform float mottle_amount = 0.10;   // 大尺度明暗团幅度
uniform float alpha_mul   = 1.0;

uniform float foam_px     = 3.0;
uniform float foam_amount = 0.50;
uniform float rim_px      = 1.0;
uniform float rim_amount  = 0.28;
uniform float shallow_px  = 13.0;
uniform float shallow_amount = 0.34;
uniform float refl_amount = 0.26;
uniform float refl_drop_px = 5.0;

uniform vec4  col_deep    = vec4(0.024, 0.486, 0.565, 1.0);  // #067C90
uniform vec4  col_base    = vec4(0.008, 0.702, 0.769, 1.0);  // #02B3C4
uniform vec4  col_light   = vec4(0.063, 0.784, 0.824, 1.0);  // #10C8D2
uniform vec4  col_high    = vec4(0.498, 0.941, 0.941, 1.0);  // #7FF0F0
uniform vec4  col_shallow = vec4(0.435, 0.875, 0.878, 1.0);  // #6FDFE0
uniform vec4  col_rim     = vec4(0.012, 0.314, 0.361, 1.0);  // #03505C
uniform vec4  col_foam    = vec4(0.894, 0.988, 1.000, 1.0);  // #E4FCFF
uniform vec4  col_shadow  = vec4(0.016, 0.420, 0.494, 1.0);  // #046B7E
uniform vec4  col_rain    = vec4(0.310, 0.659, 0.706, 1.0);  // #4FA8B4 雨天压色（青蓝，不是灰）
uniform vec4  col_cool    = vec4(0.561, 0.784, 0.847, 1.0);  // #8FC8D8 冷偏色

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

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

// 先按深度压暗水体，再让网线盖上去（否则深水吃掉亮线）
vec3 water_color(float l, float deep_amt) {
	float dark_cell = 1.0 - step(0.20, l);
	vec3 body = mix(col_base.rgb, col_deep.rgb,
	                clamp(deep_amt * deep_k + dark_cell * 0.16, 0.0, 1.0));
	vec3 line = mix(col_light.rgb, col_high.rgb, step(0.82, l));
	return mix(body, line, step(0.55, l) * mix(1.0, 0.90, deep_amt));
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

// 返回 vec2(环像素值, 已含衰减的 alpha)
vec2 ripple(vec2 wp, float cell, float seed) {
	vec2 p  = wp / cell;
	vec2 id = floor(p);
	vec2 f  = fract(p);
	float h1 = hash12(id + vec2(seed, seed * 1.7));
	float h2 = hash12(id + vec2(seed + 17.3, 3.1));
	float h3 = hash12(id + vec2(41.7, seed + 8.9));
	vec2 c  = vec2(0.18) + vec2(h1, h2) * 0.64;      // 圆心在格内抖动
	float d = length((f - c) * cell);                // px
	float t = fract(TIME / ring_life + h3);          // 每格独立相位
	float r = t * ring_max_px;
	float ring = 1.0 - smoothstep(0.0, ring_width, abs(d - r));
	ring = floor(ring * 2.0) * 0.5;                  // 量化成 0 / 0.5 / 1
	float fade = (1.0 - t) * (1.0 - t) * smoothstep(0.0, 0.10, t);
	return vec2(ring, ring * fade);
}

void fragment() {
	vec4 sh = texture(shape_tex, UV);
	float inside = step(0.5, sh.a);
	float in_px  = sh.r * 32.0;
	float depth  = sh.g;

	vec2 off = mod(normalize(vec2(0.8944, -0.4472)) * (flow_speed * TIME), tex_px);
	float lvl = texture(water_a, (world_px + off) / tex_px).r;
	vec3 col = water_color(lvl, smoothstep(0.30, 1.0, depth));
	col = mottle_tint(col, mottle_amount);

	// --- 石头倒影（雨里只剩很淡一团） ---------------------------------------
	float refl = rock_refl(UV) * inside;
	col = mix(col, col_shadow.rgb, refl * refl_amount);

	// --- 浅滩：偏亮偏绿 -----------------------------------------------------
	float shallow = 1.0 - smoothstep(2.0, shallow_px, in_px);
	col = mix(col, col_shallow.rgb, shallow * shallow_amount);

	// --- 雨天调色：轻微降饱和 + 青蓝偏移 + 很轻的压暗 -----------------------
	float lum = dot(col, vec3(0.299, 0.587, 0.114));
	col = mix(col, vec3(lum) * col_cool.rgb * 1.16, rain_grey * 0.55);
	col = mix(col, col_rain.rgb, rain_grey * 0.22);
	col *= rain_darken;

	// --- 雨点涟漪（两套错位格子） -------------------------------------------
	vec2 r1 = ripple(world_px, cell_a, 0.0);
	vec2 r2 = ripple(world_px + vec2(29.0, -17.0), cell_b, 5.0);
	vec2 r3 = ripple(world_px * 1.7 + vec2(-53.0, 71.0), cell_a * 1.9, 11.0);
	float ring = max(max(r1.y, r2.y), r3.y * 0.7);
	col = mix(col, col_foam.rgb, clamp(ring, 0.0, 1.0) * ripple_amount);
	// 环内侧的暗边（让环看起来是凹进去的，而不是贴了一张白线）
	float dark = clamp(max(r1.x, r2.x) * (1.0 - ring), 0.0, 1.0);
	col = mix(col, col_rim.rgb, dark * 0.20);

	// --- 稀疏镜面高光 -------------------------------------------------------
	float sp = sin((world_px.x * 0.42 + world_px.y * 0.9) * 0.06 - TIME * 1.9);
	sp = pow(max(sp, 0.0), 14.0);
	sp = floor(sp * 4.0) / 4.0;
	col += col_high.rgb * sp * glint_amount;

	// --- 岸沿：3px 浅色沫带 + 1px 亮线 + 1px 细暗线 -------------------------
	float foam = 1.0 - smoothstep(0.6, foam_px, in_px);
	col = mix(col, col_foam.rgb, foam * foam_amount);
	float line = 1.0 - smoothstep(0.0, 1.2, in_px);
	col = mix(col, col_foam.rgb, line * 0.50);
	float rim = 1.0 - smoothstep(0.0, rim_px, in_px);
	col = mix(col, col_rim.rgb, rim * rim_amount);

	COLOR = vec4(col, inside * alpha_mul);
}
