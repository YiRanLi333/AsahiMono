shader_type canvas_item;
render_mode blend_mix, unshaded;

// ============================================================================
// level_dynamic —— 动态水位（涨落）（v2，对齐 REF-05）
//
// 原理：pond_shape 的 R/B 两个通道合起来是一个"到水边的有符号距离场"（px）：
//         塘内 sd = +距岸 px，塘外 sd = -距岸 px
//       于是"水面高度"就退化成一句话：cov = step(0.0, sd + rise)。
//       rise > 0 水漫过岸 -> 淹掉一圈沙滩；rise < 0 水退走 -> 塘边露出一圈湿泥。
//       rise 上下再乘一个按屏幕 y 的偏置（上沿岸多涨、下沿岸少涨），
//       读起来才是"水面整体抬高"，而不是"图形向外糊了一圈"。
//       岸上留一条 10px 的半透明湿痕（不管水位高低都在），
//       水线本身是 |sd+rise| 的窄带高光 —— 所以水线上移时能看清它在动。
//       塘里的石头按 rocks_tex 的 G 通道（各自的淹没阈值）决定没入/露出。
//
// v2 改了什么：
//   1. 配色换成 REF-05 分位色（色相 186°，比 v1 的 200° 更青）：体色 #02B3C4、
//      暗部 #067C90、亮脊 #7FF0F0；水涨时不再整体压暗（v1 那 0.10 的压暗
//      会让水越涨越"脏"），改成往 col_deep 只走 0.06 并且提一点饱和度；
//   2. 水线亮带跟着水线走：内侧 2px 浅色沫带 + 1px 亮线（REF 岸带只有 2~3px）；
//   3. 水下石头的倒影：淹到水下的石头改为"颜色被水吸收 + 竖向拖尾暗斑"，
//      而不是 v1 那种整块涂蓝（那样读起来像贴纸）；
//   4. 湿痕色从灰绿换成湿沙（#6E7A63 → 偏暖偏灰），不然和青绿水打架。
// ============================================================================

varying vec2 world_px;

uniform sampler2D shape_tex : filter_nearest, repeat_disable;
uniform sampler2D water_a   : filter_nearest, repeat_enable;
uniform sampler2D water_b   : filter_nearest, repeat_enable;
uniform sampler2D rocks_tex : filter_nearest, repeat_disable;

uniform vec2  shape_px   = vec2(576.0, 256.0);   // pond_shape / rocks 贴图尺寸
uniform vec2  tex_px     = vec2(128.0, 64.0);

uniform float level      : hint_range(0.0, 1.0) = 0.5;
uniform float auto_cycle = 1.0;                  // 1 = 自动涨落（demo 用）
uniform float cycle_period = 12.0;               // 秒（3600 能被整除 -> TIME 回绕不跳）
uniform float rise_min   = -4.0;                 // level=0 时水线内缩 px
uniform float rise_max   = 13.0;                 // level=1 时水线外扩 px
uniform float ybias      = 0.35;                 // 屏幕上方多涨的比例
uniform float wet_px     = 10.0;                 // 岸上湿痕宽度 px
uniform float wet_alpha  = 0.42;
uniform float flow_speed = 5.0;
uniform float crossfade_period = 12.0;
uniform float mottle_amount = 0.10;   // 大尺度明暗团幅度
uniform float alpha_mul  = 1.0;
uniform float deep_k    = 0.24;

uniform float foam_px    = 3.0;
uniform float foam_amount = 0.58;
uniform float rim_px     = 1.0;
uniform float rim_amount = 0.32;
uniform float shallow_px = 13.0;
uniform float shallow_amount = 0.40;
uniform float refl_amount = 0.40;
uniform float refl_drop_px = 5.0;
uniform float glint_amount = 0.07;

uniform vec4  col_deep    = vec4(0.024, 0.486, 0.565, 1.0);  // #067C90
uniform vec4  col_base    = vec4(0.008, 0.702, 0.769, 1.0);  // #02B3C4
uniform vec4  col_light   = vec4(0.063, 0.784, 0.824, 1.0);  // #10C8D2
uniform vec4  col_high    = vec4(0.498, 0.941, 0.941, 1.0);  // #7FF0F0
uniform vec4  col_shallow = vec4(0.435, 0.875, 0.878, 1.0);  // #6FDFE0
uniform vec4  col_foam    = vec4(0.894, 0.988, 1.000, 1.0);  // #E4FCFF
uniform vec4  col_rim     = vec4(0.012, 0.314, 0.361, 1.0);  // #03505C
uniform vec4  col_shadow  = vec4(0.016, 0.420, 0.494, 1.0);  // #046B7E
uniform vec4  col_wet     = vec4(0.318, 0.333, 0.271, 1.0);  // #515544 湿沙

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

vec2 scroll(vec2 dir, float speed) {
	return mod(normalize(dir) * (speed * TIME), tex_px);
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
	float out_px = sh.b * 28.0;
	float depth  = sh.g;

	// 有符号距离：正 = 在水面内（按当前水位）
	float sd = mix(-out_px, in_px, inside);

	// --- 水位 ---------------------------------------------------------------
	float lv = level;
	if (auto_cycle > 0.5) {
		lv = 0.5 + 0.5 * sin(6.2831853 * TIME / cycle_period - 1.5707963);
	}
	float rise = mix(rise_min, rise_max, lv);
	// 屏幕上方（world_px.y 小）涨得多一点 -> 像水面在垂直方向上抬起来
	float yb = clamp(-(world_px.y + 64.0) / 160.0, -1.0, 1.0);
	float rise_eff = rise * (1.0 + ybias * yb);

	float cov = step(0.0, sd + rise_eff);        // 1 = 这里有水
	// 湿痕只出现在水线外侧 0..wet_px 这一圈（step 的参数顺序别写反，
	// 写反的后果是整个 shape 矩形全被当成"湿" -> 整屏糊一层墨绿）。
	float outd = -(sd + rise_eff);               // 水线外的距离 px（内侧为负）
	float wet = (1.0 - cov) * step(0.0, outd)
	          * (1.0 - smoothstep(wet_px * 0.55, wet_px, outd));

	// --- 水面色：两层网交叉淡化（和 flow_uv 同一套天花板） ------------------
	float w  = 0.5 + 0.5 * sin(6.2831853 * TIME / crossfade_period);
	float la = texture(water_a, (world_px + scroll(vec2(0.8944, -0.4472), flow_speed)) / tex_px).r;
	float lb = texture(water_b, (world_px + scroll(vec2(-0.85, 0.42), -flow_speed * 0.7)) / tex_px).r;
	vec3 col = mix(water_color(la, smoothstep(0.30, 1.0, depth)),
	               water_color(lb, smoothstep(0.30, 1.0, depth)), w);
	col = mix(col, col_deep.rgb, lv * 0.06);
	col = mottle_tint(col, mottle_amount);      // 水涨 -> 略沉（别压太狠）

	// --- 石头柔和倒影（在水下石头之前，落在水色上） -------------------------
	float refl = rock_refl(UV) * cov;
	col = mix(col, col_shadow.rgb, refl * refl_amount);

	// --- 石头：没入/露出 ----------------------------------------------------
	vec4 rk = texture(rocks_tex, UV);
	float rock  = step(0.5, rk.a);
	float thr   = rk.g;
	float sub   = smoothstep(thr - 0.04, thr + 0.04, lv);   // 1 = 整块没入
	float ring  = clamp(rock - texture(rocks_tex, UV + vec2(0.0, 2.0) / shape_px).a, 0.0, 1.0);
	// 水下的石头：保留石头自己的明暗，只被水整体吸收一层（v1 是整块涂蓝，像贴纸）
	vec3 rock_self = vec3(rk.r) * 0.85 + 0.05;
	vec3 rock_sub  = mix(rock_self, col_base.rgb, 0.55) * mix(1.0, 0.72, smoothstep(0.2, 1.0, depth));
	col = mix(col, rock_sub, rock * sub * cov * 0.60);
	// 石头顶沿一圈碎泡沫
	col = mix(col, col_foam.rgb, ring * sub * cov * 0.75);

	// --- 浅滩亮带（跟着水线走，偏亮偏绿） -----------------------------------
	float shallow = 1.0 - smoothstep(1.0, shallow_px, sd + rise_eff);
	col = mix(col, col_shallow.rgb, clamp(shallow, 0.0, 1.0) * shallow_amount);

	// --- 水线：2~3px 浅色沫带 + 1px 亮线 + 1px 外缘细暗线 -------------------
	float foam = cov * (1.0 - smoothstep(0.6, foam_px, sd + rise_eff));
	col = mix(col, col_foam.rgb, foam * foam_amount);
	float line = 1.0 - smoothstep(0.0, 1.2, abs(sd + rise_eff));
	col = mix(col, col_foam.rgb, line * cov * 0.55);
	float rim = cov * (1.0 - smoothstep(0.0, rim_px, sd + rise_eff));
	col = mix(col, col_rim.rgb, rim * rim_amount);

	// --- 岸上湿痕 -----------------------------------------------------------
	col = mix(col, col_wet.rgb, wet * wet_alpha);

	// --- 极淡高光 -----------------------------------------------------------
	vec2 gp = world_px + vec2(TIME * 4.0, -TIME * 2.0);
	float g = sin(gp.x * 0.055 + gp.y * 0.11 - TIME * 1.1);
	g = pow(max(g, 0.0), 4.0);
	g = floor(g * 3.0) / 3.0;
	col += col_high.rgb * g * glint_amount * cov;

	float a = max(cov, wet);
	COLOR = vec4(col, a * alpha_mul);
}
