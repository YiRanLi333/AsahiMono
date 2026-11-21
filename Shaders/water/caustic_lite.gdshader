shader_type canvas_item;
render_mode blend_mix, unshaded;

// ============================================================================
// caustic_lite —— 浅水见底 + 轻折射 + 移动焦散网（v2，对齐 REF-05）
//
// 原理：水面不是"刷一层蓝"，而是给底下的东西做了一次（弱的）折射。
//       1) 用 hint_screen_texture 取本帧水下已经画好的东西（沙/石/水草）；
//       2) 用 caustic 贴图当平滑噪声场，算出每个像素 ≤1.5px 的采样偏移，
//          四舍五入到整像素（像素风：不引入亚像素模糊）；
//       3) 按 pond_shape.G 的深度做"水的吸收"：浅处几乎透明（沙清晰可见），
//          深处乘一个暗色 —— 这是唯一让"潜水区/深水区"分开的东西；
//       4) 焦散是 caustic 贴图的两层反向滚动取 max。
//
// v2 改了什么：
//   1. caustic.png 重新生成成"多边形网"（实测网周期 23.3px，REF 频谱主峰 29.4px），
//      v1 那张是 64px 周期的柔和团块——读不出来"网"，正是塑料感来源之一；
//   2. 阈值仍压在 0.84~1.0（新贴图 ≥0.86 占 6.9%，接近 REF 亮脊覆盖率）；
//   3. 吸收色换成 REF 分位色（深 #067A93 / 浅 #CCF7EC 偏绿），浅水偏绿、深水偏蓝；
//   4. 岸沿 3px 暗轮廓 -> 3px 浅色沫带 + 1px 亮线 + 1px 细暗线；
//   5. 石头在水下被水吸收后仍需"柔和倒影"：用 rocks_tex 取一团下沉的暗斑。
// ============================================================================

varying vec2 world_px;

uniform sampler2D screen_tex  : hint_screen_texture, filter_nearest;
uniform sampler2D shape_tex   : filter_nearest, repeat_disable;
uniform sampler2D caustic_tex : filter_nearest, repeat_enable;
uniform sampler2D rocks_tex   : filter_nearest, repeat_disable;

uniform vec2  caustic_px = vec2(256.0, 256.0);
uniform vec2  shape_px   = vec2(576.0, 256.0);
uniform float refr_px    = 1.5;      // 折射位移上限
uniform float refr_speed = 4.0;
uniform float caustic_amount = 0.30;
uniform float caustic_speed  = 4.0;
uniform float absorb_boost   = 1.02; // 吸收系数整体放大，免得深水太黑
uniform float glint_amount   = 0.06;
uniform float mottle_amount  = 0.10;
uniform float alpha_mul      = 1.0;

uniform float foam_px     = 3.0;
uniform float foam_amount = 0.52;
uniform float rim_px      = 1.0;
uniform float rim_amount  = 0.30;
uniform float refl_amount = 0.30;
uniform float refl_drop_px = 5.0;

uniform vec4  col_deep    = vec4(0.024, 0.478, 0.577, 1.0);  // #067A93 深水吸收色
uniform vec4  col_shallow = vec4(0.900, 1.000, 0.970, 1.0);  // 近岸吸收色（几乎全透，只压一点红）
uniform vec4  col_base    = vec4(0.008, 0.675, 0.788, 1.0);  // #02ACC9 吸收色基准
uniform vec4  col_high    = vec4(0.498, 0.941, 0.941, 1.0);  // #7FF0F0 焦散亮脊
uniform vec4  col_rim     = vec4(0.012, 0.314, 0.361, 1.0);  // #03505C
uniform vec4  col_foam    = vec4(0.894, 0.988, 1.000, 1.0);  // #E4FCFF
uniform vec4  col_shadow  = vec4(0.016, 0.420, 0.494, 1.0);  // #046B7E

// 大尺度明暗团（同 flow_uv）
vec3 mottle_tint(vec3 c, float amt) {
	float m = sin(world_px.x * 0.052 + 1.7) * sin(world_px.y * 0.037 - 0.6)
	        + 0.6 * sin(world_px.x * 0.021 - world_px.y * 0.026 + 2.3);
	m = 0.5 + 0.5 * clamp(m * 0.55, -1.0, 1.0);
	return c * mix(1.0 - amt, 1.0 + amt, m);
}

void vertex() {
	world_px = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
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

	// --- 折射场：caustic 贴图当平滑噪声 -------------------------------------
	float c1 = texture(caustic_tex, (world_px + vec2(refr_speed, refr_speed * 0.4) * TIME) / caustic_px).r;
	float c2 = texture(caustic_tex, (world_px + vec2(-refr_speed * 0.8, refr_speed * 0.9) * TIME + vec2(83.0, 137.0)) / caustic_px).r;
	vec2 r = (vec2(c1, c2) - 0.5) * (2.0 * refr_px);
	float rl = length(r);
	r *= min(1.0, refr_px / max(rl, 0.0001));
	r = sign(r) * floor(abs(r) + 0.5);              // 整像素，保持锐利

	vec3 bottom = texture(screen_tex, SCREEN_UV + r * SCREEN_PIXEL_SIZE).rgb;

	// --- 水体吸收：浅处几乎透明，越深越只剩水色 -----------------------------
	// 不能一直做乘法：沙的蓝通道只有 0.66，乘下去永远到不了 #02B3C4 的蓝，
	// 结果是"墨绿泥水"（上一版就是这样）。改成浅处乘法、深处混到纯水色。
	vec3 shallow_view = bottom * col_shallow.rgb * absorb_boost;
	vec3 deep_view = mix(col_base.rgb, col_deep.rgb, 0.22);
	vec3 col = mix(shallow_view, deep_view, smoothstep(0.08, 0.70, depth) * 0.99);
	col = mottle_tint(col, mottle_amount);

	// --- 石头柔和倒影 -------------------------------------------------------
	float refl = rock_refl(UV) * inside;
	col = mix(col, col * 0.55 + col_shadow.rgb * 0.45, refl * refl_amount);

	// --- 焦散：两层取 max（网周期 23.3px），3 阶量化，压在亮脊尾部 ----------
	float cw1 = texture(caustic_tex, (world_px + vec2(caustic_speed, -caustic_speed * 0.5) * TIME) / caustic_px).r;
	float cw2 = texture(caustic_tex, (world_px + vec2(-caustic_speed * 0.6, caustic_speed * 0.8) * TIME + vec2(37.0, 191.0)) / caustic_px).r;
	// caustic.png 是"到脊线的距离"场（>0.86 占 6.9%），阈值必须压在很硬的尾部，
	// 否则两层 max 之后有大半面积被判成亮线 -> 整片糊成雾。
	float web = smoothstep(0.84, 1.0, max(cw1, cw2 * 0.96));
	web = floor(web * 3.0) / 3.0;                   // 3 阶量化
	float cw_mask = 1.0 - smoothstep(0.06, 0.62, depth);   // 深塘焦散减弱
	col = mix(col, col + col_high.rgb * caustic_amount, web * cw_mask);

	// --- 水面反光 -----------------------------------------------------------
	float sp = sin((world_px.x * 0.55 + world_px.y * 0.85) * 0.055 - TIME * 1.4);
	sp = pow(max(sp, 0.0), 16.0);
	sp = floor(sp * 3.0) / 3.0;
	col += col_high.rgb * sp * glint_amount;

	// --- 岸沿：3px 浅色沫带 + 1px 亮线 + 1px 细暗线 -------------------------
	float foam = 1.0 - smoothstep(0.6, foam_px, in_px);
	col = mix(col, col_foam.rgb, foam * foam_amount);
	float line = 1.0 - smoothstep(0.0, 1.2, in_px);
	col = mix(col, col_foam.rgb, line * 0.45);
	float rim = 1.0 - smoothstep(0.0, rim_px, in_px);
	col = mix(col, col_rim.rgb, rim * rim_amount);

	COLOR = vec4(col, inside * alpha_mul);
}
