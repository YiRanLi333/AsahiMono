shader_type canvas_item;
render_mode blend_mix, unshaded;

// ============================================================================
// flow_uv —— 双 caustic 网反向滚动 + sin 交叉淡化 + 石头倒影（v2，对齐 REF-05）
//
// v2 改了什么（v1 的"细碎波纹"读起来是塑料感，参考池塘是"亮线组成的网"）：
//   1. 贴图从"短划水纹"换成 24px 周期的 caustic 网（water_a/b 已重生成，
//      实测网周期 25.6 / 28.6 px，REF-05 频谱主峰 29.4px）；
//   2. 配色整族下移一档并把色相从 200° 拉到 186°：body #02B3C4 / 暗 #067C90 /
//      亮脊 #7FF0F0（取自 REF-05 水面像素 p10/p50/p97 分位）；
//   3. 岸边从"2.5px 暗轮廓"换成"2~3px 浅色沫带 + 1px 细暗线"；
//      浅水偏亮偏绿（col_shallow），深水偏深蓝（deep_k 加大）；
//   4. 新增石头柔和倒影（rocks_tex 4 tap 竖向下沉采样）。
//
// 原理不变：两层网按不同方向/速度平移，sin 交叉淡化 —— 相位永不同步，
// 所以看不到"整片图案一起循环"。采样一律 filter_nearest，滚动量对 tex_px 取模。
// ============================================================================

varying vec2 world_px;

uniform sampler2D shape_tex : filter_nearest, repeat_disable;   // pond_shape.png
uniform sampler2D water_a   : filter_nearest, repeat_enable;    // 128x64 caustic 网 A
uniform sampler2D water_b   : filter_nearest, repeat_enable;    // 128x64 caustic 网 B
uniform sampler2D rocks_tex : filter_nearest, repeat_disable;   // pond_rocks.png（倒影用）

uniform vec2  tex_px        = vec2(128.0, 64.0);  // 网贴图尺寸（1 texel = 1 世界像素）
uniform vec2  shape_px      = vec2(576.0, 256.0); // pond_shape / rocks 贴图尺寸

uniform float speed_a       = 5.0;                // px/s（网爬得慢才像水面，不像传送带）
uniform float speed_b       = -3.5;
uniform vec2  dir_a         = vec2(1.0, 0.18);
uniform vec2  dir_b         = vec2(-0.85, 0.42);
uniform float crossfade_period = 12.0;            // 交叉淡化周期（秒）
uniform float glint_amount  = 0.08;               // REF 里高光很少，别开大
uniform float mottle_amount = 0.10;   // 大尺度明暗团幅度
uniform float alpha_mul     = 1.0;

uniform float foam_px       = 2.5;                // 岸边浅色沫带宽度
uniform float foam_amount   = 0.78;
uniform float rim_px        = 1.0;                // 最外 1px 细暗线（把水和沙分开）
uniform float rim_amount    = 0.45;
uniform float shallow_px    = 13.0;
uniform float shallow_amount = 0.42;
uniform float deep_k    = 0.24;
uniform float refl_amount   = 0.38;               // 石头倒影强度
uniform float refl_drop_px  = 5.0;                // 倒影在水面下移多少 px

uniform vec4  col_deep    = vec4(0.024, 0.486, 0.565, 1.0);  // #067C90 暗部
uniform vec4  col_base    = vec4(0.008, 0.702, 0.769, 1.0);  // #02B3C4 体色
uniform vec4  col_light   = vec4(0.063, 0.784, 0.824, 1.0);  // #10C8D2 网线
uniform vec4  col_high    = vec4(0.498, 0.941, 0.941, 1.0);  // #7FF0F0 亮脊
uniform vec4  col_shallow = vec4(0.435, 0.875, 0.878, 1.0);  // #6FDFE0 浅水（偏亮偏绿）
uniform vec4  col_foam    = vec4(0.894, 0.988, 1.000, 1.0);  // #E4FCFF 岸沫
uniform vec4  col_rim     = vec4(0.012, 0.314, 0.361, 1.0);  // #03505C 外缘细暗线
uniform vec4  col_shadow  = vec4(0.016, 0.420, 0.494, 1.0);  // #046B7E 石头倒影底色

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

// 4 阶取色：网贴图里 R 通道是 4 阶灰度索引（40 / 85 / 170 / 255）。
// 关键顺序：先按深度把"水体"压暗，再让网线盖上去 —— 否则深水会把亮线一起吃掉，
// 整片塌成一张均匀的深青，正是 v1 那个"塑料感"的直接来源。
vec3 water_color(float l, float deep_amt) {
	float dark_cell = 1.0 - step(0.20, l);                 // 40 = 大片偏深的格心
	vec3 body = mix(col_base.rgb, col_deep.rgb,
	                clamp(deep_amt * deep_k + dark_cell * 0.16, 0.0, 1.0));
	vec3 line = mix(col_light.rgb, col_high.rgb, step(0.82, l));
	return mix(body, line, step(0.55, l) * mix(1.0, 0.90, deep_amt));
}

// 把滚动量按贴图周期取模 —— 无缝且不会因 TIME 回绕而跳
vec2 scroll(vec2 dir, float speed) {
	vec2 v = normalize(dir) * (speed * TIME);
	return mod(v, tex_px);
}

// 石头倒影：4 个竖向偏移的采样取平均 = 一条"往下拖"的柔边暗斑。
// 用 nearest 采样也不会糊，因为每个 tap 落在不同 texel 上，叠出来是像素阶梯。
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
	float in_px  = sh.r * 32.0;      // 距水边向内距离（px）
	float depth  = sh.g;             // 0 岸边 -> 1 塘心

	// --- 两层网反向滚动 + 交叉淡化 -----------------------------------------
	// 注意是在"颜色空间"里混，不是先混索引再取色：先混索引会把 170/255 拉成
	// 中间值（0.49），落进体色档，网线就碎成断断续续的裂纹 —— 上一版就是这个毛病。
	float w = 0.5 + 0.5 * sin(6.2831853 * TIME / crossfade_period);
	float la = texture(water_a, (world_px + scroll(dir_a, speed_a)) / tex_px).r;
	float lb = texture(water_b, (world_px + scroll(dir_b, speed_b)) / tex_px).r;
	float deep_amt = smoothstep(0.30, 1.0, depth);
	vec3 col = mix(water_color(la, deep_amt), water_color(lb, deep_amt), w);
	col = mottle_tint(col, mottle_amount);

	// --- 石头柔和倒影（画在体色之上、浅滩亮带之下） -------------------------
	float refl = rock_refl(UV) * inside;
	col = mix(col, col_shadow.rgb, refl * refl_amount);

	// --- 浅滩：偏亮偏绿（REF 浅水比塘心更"青"） -----------------------------
	float shallow = 1.0 - smoothstep(2.0, shallow_px, in_px);
	col = mix(col, col_shallow.rgb, shallow * shallow_amount);

	// --- 岸沫 2~3px + 最外 1px 细暗线 ---------------------------------------
	float foam = 1.0 - smoothstep(1.0, foam_px, in_px);
	col = mix(col, col_foam.rgb, foam * foam_amount);
	float rim = 1.0 - smoothstep(0.0, rim_px, in_px);
	col = mix(col, col_rim.rgb, rim * rim_amount);

	// --- 极淡的高光带（REF 网线本身已经够亮，这里只补一点流动感） ----------
	vec2 gp = world_px + vec2(TIME * 4.0, -TIME * 2.0);
	float g = sin(gp.x * 0.055 + gp.y * 0.11 - TIME * 1.1);
	g = pow(max(g, 0.0), 4.0);
	g = floor(g * 3.0) / 3.0;
	col += col_high.rgb * g * glint_amount;

	COLOR = vec4(col, inside * alpha_mul);
}
