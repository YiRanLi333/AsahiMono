shader_type canvas_item;
render_mode blend_mix, unshaded;

// ============================================================================
// water_tile_flow  (r3) —— 正式工程用的「瓦片级动态水」
//
// 与 DevPreview/WaterLab/flow_uv.gdshader 的区别：这里**不需要 pond_shape 距离场**，
// 因为正式地图的水是一格一格（wang 47 套）的，边缘由底下的水瓦自己交代。
// 本 shader 只负责「水面内部」：焦散网 + 缓慢游动 + 深浅两档 + 瓦片细节乘子。
//
// r3 相对 r2 的四处改动（都是"水能看但不像参考图"的直接对策）：
//
//  ① 焦散网的**几何不再被混合打断**。
//     r2 在 A/B 两张几何无关的网之间做 `mix(la, lb, w)`，无论在线性空间还是
//     索引值空间混合，淡化期都会出现"网线各亮一半 → 变细/变双重影/断开"。
//     r3 的 water_a 是**唯一一张网**；water_b 换成了同周期的**平滑扰动场**。
//     两次取样都取自 water_a，只差一个 ≤ net_warp_px(1.2px) 的位移，
//     于是 `mix(water_color(la), water_color(lb), w)` 是**同一张网的两个相位**，
//     网线永远连续 —— 场景里看到的是"网在游"，不是"网在闪"。
//
//  ② 档位映射连续化。r2 用四个 step() 把亮度切成四块平涂（塑料感来源），
//     r3 改成 body/line 两条连续曲线（smoothstep），亮芯依旧吃满 col_light，
//     但中间过渡没有台阶。
//
//  ③ 深水压 V。deep_amt=0 时水面锚在 REF 的中位 V≈0.77；deep_amt 越大越靠 col_deep
//     （#03505C）。深水档建议 0.5~0.7。
//
//  ④ 瓦片细节乘子 tile_detail：把**水瓦自身的 RGB** 当细节乘子接进来。
//     正式渲染里 ground 层的水瓦只贡献 alpha（Shape 已由瓦片 alpha 给出），
//     RGB 本来是浪费的；现在浅水/深水瓦只要把体色画亮/画暗，
//     水面上就自动出现"靠岸更亮更绿、远岸更暗更蓝"的分档，不需要改 C#。
//     ★ 只乘"体色"，乘不到亮芯 ⇒ 深水里的焦散线不会跟着变暗。
//
// 关键约束（改代码前先读）：
//  * 采样一律 filter_nearest；纹理平移量对 tex_px 取模 ⇒ TIME 3600s 回绕不跳变；
//  * 采样坐标用 world_px（世界像素）⇒ 相邻水瓦的花纹天然连成一片，看不出格子；
//  * 形状靠所绘制贴图自身的 alpha（水瓦的菱形），四角透明处直接丢弃；
//  * 亮像素（V>=0.827）占比预算：REF-05 实测 15.6%。本 shader 默认参数实测 ~13%。
// ============================================================================

varying vec2 world_px;

uniform sampler2D water_a : filter_nearest, repeat_enable;   // 768x384 无缝焦散网（r3）
uniform sampler2D water_b : filter_nearest, repeat_enable;   // 768x384 平滑扰动场（r3）

uniform vec2  tex_px = vec2(768.0, 384.0);
uniform float speed_a = 3.2;          // px/s，网本体漂移
uniform float speed_b = -1.2;         // px/s，扰动场漂移（决定"游动方向"的相位）
uniform vec2  dir_a = vec2(1.0, 0.18);
uniform vec2  dir_b = vec2(-0.85, 0.42);
uniform float crossfade_period = 36.0; // 秒：游动/呼吸周期（名字沿用 r2，语义=游动周期）
uniform float glint_amount = 0.0;      // 低频波光带强度；r2 的 0.26 是"塑料感"的一部分，默认关
uniform float alpha_mul = 1.0;
uniform float rim_dark = 0.0;          // 瓦片内沿压暗：默认 0（>0 会在水面上画出菱形网格，像渔网）
uniform float net_warp_px = 0.7;       // 两次取样的最大位移（px）——大于 2 就开始看出重影

// ---- 焦散网三参数（r4）------------------------------------------------------
// 之前网线的阈值是**写死的**（0.46/0.56/0.62/0.72），于是"网太亮、铺开就看见格子"
// 只能靠改 shader 源码调。现在抽成 uniform，由 C# 从 config.json 灌进来：
//   net_period   ：世界像素坐标的整数倍放大。>1 = 网眼变大、单位屏幕面积上的线更少。
//                  1.0 是 art 原始网眼（128px 一格上大约 3~4 条线），1.35 让一格内只剩 1~2 条。
//   net_width    ：线的过渡窗宽（亮度域）。越小线越硬，越大越糊；线亮不亮由 net_contrast 决定。
//   net_contrast ：线权重上限 0~1。0.5 = 亮芯只走一半强度 ⇒ 近看一格内仍可辨，铺开看不见。
uniform float net_period = 1.60;
uniform float net_width = 0.045;
uniform float net_contrast = 0.30;
uniform float deep_amt = 0.0;          // 0=浅/中水，0.5~0.7=深水
uniform float tile_detail = 0.85;      // 水瓦 RGB 当细节乘子的强度（0=完全不用瓦片 RGB）

uniform vec4 col_deep  = vec4(0.012, 0.314, 0.361, 1.0);  // #03505C  REF p03
uniform vec4 col_base  = vec4(0.008, 0.702, 0.769, 1.0);  // #02B3C4  REF 水体锚点
uniform vec4 col_light = vec4(0.133, 0.769, 0.816, 1.0);  // #22C4D0  亮芯（r4 压一档饱和/明度：原 #2FD9DF 在 zoom1.5 下偏"泳池亮青"）
uniform vec4 col_high  = vec4(0.431, 0.859, 0.886, 1.0);  // #6EDBE2  脊/最亮（r4：原 #8FF2F2 几乎纯白，是"渔网白线"的主因）
uniform vec4 tile_ref  = vec4(0.008, 0.702, 0.769, 1.0);  // #02B3C4  瓦片体色的"1.0"基准

const float TAU_R3 = 6.2831853;

void vertex() {
	world_px = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
}

float sstep(float e0, float e1, float x) {
	float t = clamp((x - e0) / max(e1 - e0, 1e-6), 0.0, 1.0);
	return t * t * (3.0 - 2.0 * t);
}

// 索引 -> "线权重"：肩 0.22，亮芯 1.0。连续，没有 step 平台。
// r4：阈值由 net_width 给出的窗宽决定（原写死 ±0.05），整体再乘 net_contrast。
float line_mask(float l) {
	float w = max(net_width, 0.0) * 0.5;
	float k = clamp(net_contrast, 0.0, 1.0);
	float lo = clamp(0.22 * sstep(0.51 - w, 0.51 + w, l) + 0.78 * sstep(0.67 - w, 0.67 + w, l), 0.0, 1.0);
	return k * lo;
}

// 体色：base 往 deep 压。暗团 + deep_amt 共同决定压多少。
vec3 body_of(float l, float deep) {
	float dark = 1.0 - sstep(0.06, 0.34, l);
	float k = clamp(dark * (0.42 + 0.58 * deep) + deep * 0.35, 0.0, 1.0);
	return mix(col_base.rgb, col_deep.rgb, k);
}

// 亮芯色：light 往 high 提（只有脊那一层吃到 high）。
vec3 line_of(float l) {
	return mix(col_light.rgb, col_high.rgb, sstep(0.86, 1.0, l));
}

// = 补丁 A 的目标形态：颜色空间里的水色。线在颜色空间里插到体色上，
// 而不是先把两个索引值混成一个再查表（那会把"线"糊成"半线"）。
vec3 water_color(float l, float deep) {
	return mix(body_of(l, deep), line_of(l), line_mask(l));
}

vec2 scroll(vec2 dir, float speed) {
	vec2 v = normalize(dir) * (speed * TIME);
	return mod(v, tex_px);
}

void fragment() {
	float mask = texture(TEXTURE, UV).a;
	if (mask < 0.5) {
		COLOR = vec4(0.0);
	} else {
		// --- 漂移 -------------------------------------------------------------
		// net_period 只作用于**网本体**的采样坐标（>1 ⇒ 网眼变大、线变稀疏），
		// 扰动场 water_b 仍按世界像素取，这样"游动"速度与 net_period 解耦。
		vec2 net_px = world_px / max(net_period, 0.25);
		vec2 flow = scroll(dir_a, speed_a);
		vec2 uv_a = (net_px + flow) / tex_px;
		// 平滑扰动场：只用来决定"游动方向"和位移幅度，不进颜色。
		float nz = texture(water_b, (world_px + scroll(dir_b, speed_b)) / tex_px).r;

		// --- 呼吸相位 ---------------------------------------------------------
		float ph = TIME / max(crossfade_period, 0.5);
		float w = 0.5 + 0.5 * sin(TAU_R3 * ph);

		// --- 同一张网的两个相位（位移 <= net_warp_px） --------------------------
		vec2 wob = vec2(cos(TAU_R3 * ph * 0.37 + nz * TAU_R3), sin(TAU_R3 * ph * 0.29 + nz * TAU_R3));
		vec2 off = wob * (net_warp_px * (0.45 + 0.55 * nz));
		float la = texture(water_a, uv_a).r;
		float lb = texture(water_a, uv_a + off / tex_px).r;

		// --- 瓦片细节（加色差值，只叠体色） ------------------------------------
		// 用"差值"而不是"乘法"：岸带泡沫要往白里提，乘法一旦夹到 1.08 就看不见了；
		// 差值在 tile_ref 处恰好为 0，浅/深/泡沫三种瓦片都能表达，且叠不到亮芯。
		vec4 tx = texture(TEXTURE, UV);
		vec3 dlt = vec3(0.0);
		if (tx.b > tx.r + 0.04 && tx.g > tx.r + 0.04) {
			dlt = clamp((tx.rgb - tile_ref.rgb) * tile_detail, vec3(-0.45), vec3(0.55));
		}

		// 宏观浓淡：nz 是平滑场，拿它让焦散成片地变亮/变暗 —— REF 的水不是均质的，
		// 一块一块的亮/暗才是"活水"。lg 只缩放线的混合权重，不改线的颜色，所以
		// 亮像素预算基本不动（均值 lg = 0.72+0.56*0.42 ≈ 0.955）。
		float lg = 0.72 + 0.56 * nz;
		float bm = 0.900 + 0.060 * nz;   // r4 压一档：原 0.955+0.080 把体色整体抬到 1.03，铺开偏亮青
		vec3 ca = mix(body_of(la, deep_amt) * bm + dlt, line_of(la), line_mask(la) * lg);
		vec3 cb = mix(body_of(lb, deep_amt) * bm + dlt, line_of(lb), line_mask(lb) * lg);
		vec3 col = mix(ca, cb, w);          // ← 颜色空间混合（补丁 A）

		// --- 低频波光带（默认关；要开就 <= 0.05，别用 r2 的 0.26） -------------
		if (glint_amount > 0.0001) {
			vec2 gp = world_px + vec2(TIME * 4.0, -TIME * 2.0);
			float g = sin(gp.x * 0.055 + gp.y * 0.11 - TIME * 1.1);
			g = g * g; g = g * g;                     // pow(...,4) 但不用 pow()
			col = mix(col, col_high.rgb, g * glint_amount);
		}

		// --- 内沿压暗（默认 0） -----------------------------------------------
		float edge = abs(UV.x - 0.5) + abs(UV.y - 0.5);
		col *= 1.0 - rim_dark * smoothstep(0.42, 0.5, edge);

		COLOR = vec4(col, alpha_mul * mask);
	}
}
