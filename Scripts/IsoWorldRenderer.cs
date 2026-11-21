using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using AsahiMono.Core.World;
using Godot;

namespace AsahiMono;

/// <summary>
/// 等距（isometric 2:1）渲染器：把方格的 <see cref="WorldMap"/> 画成菱形地面 + 直立物件。
///
/// 设计要点（与 M1 的方格 <see cref="WorldRenderer"/> 并存，由 config.json 的
/// <c>projection</c> 选择）：
/// <list type="bullet">
///   <item>地面：wang-47 autotile。每种地面 47 张
///   <c>res://Assets/tiles/iso/ground/&lt;kind&gt;/&lt;kind&gt;_w00..w46.png</c>（64x32 RGBA，由
///   <c>tools/art/gen_iso_tiles.py</c> 生成），索引来自 8 邻接"暴露掩码"，映射表在
///   <see cref="IsoWangTable.RawMaskToTile"/>，规则写在 <c>docs/art/AUTOTILE.md</c>。
///   掩码 0 = 四周同材质 = 纯内部块（平草地没有网格感就靠这张）。</item>
///   <item>过渡：不再叠 <c>edge_*.png</c>。<c>Assets/tiles/iso/ground/&lt;kind&gt;/edge_&lt;kind&gt;_*.png</c>
///   这些旧文件保留在磁盘上（旧的 B-soft 风格产物，别处可能还有引用），本渲染器已完全不引用它们。</item>
///   <item>物件：直立画法，锚点在菱形中心，按 <c>rx + ry</c> 深度排序 —— 这就是等距的遮挡规则。
///   树 = 树干 + 树冠两张（<c>tree_a_trunk/canopy</c>、<c>tree_b_trunk/canopy</c>），
///   树冠锚点在自己底部中心，以后做风吹摆动只转树冠不碰树干。</item>
///   <item>投影是线性的，所以"屏幕直线"仍是"网格直线"：点地移动不用改寻路。</item>
/// </list>
/// </summary>
public partial class IsoWorldRenderer : Node2D
{
    /// <summary>菱形瓦片宽（2:1 的 2）。</summary>
    public const int TileW = 128;

    /// <summary>菱形瓦片高（2:1 的 1）。</summary>
    public const int TileH = 64;

    /// <summary>半个菱形宽 —— 也就是相邻格子中心在屏幕 X 上的步长。</summary>
    public const int HalfW = TileW / 2;

    /// <summary>半个菱形高 —— 相邻格子中心在屏幕 Y 上的步长。</summary>
    public const int HalfH = TileH / 2;

    /// <summary>
    /// 地面类型数量：<see cref="TileKind"/> 去掉 <see cref="TileKind.Void"/> 之后的成员数
    /// （Void=0 永不绘制，所以 GroundKind 索引 = <see cref="KindToGroundIndex"/>）。
    /// 加第 13 种地皮时：枚举尾部追加 → 这个数字 +1 → <see cref="GroundSlugs"/> 尾部追加 slug，
    /// 顺序必须和枚举一致（<see cref="GroundKindIndexToKind"/> 会真的校验）。
    /// </summary>
    private const int GroundKindCount = 12;

    /// <summary>老 6 种地皮的个数（枚举 1..6）。程序化图集只画了这 6 种。</summary>
    private const int LegacyGroundKindCount = 6;

    /// <summary>
    /// 老 6 种地皮的变体集是否钉住 <see cref="GroundVariantSuffixes"/> 的静态表。
    /// <c>true</c> = 只看表（现有画面逐像素不变）；<c>false</c> = 和新增地皮一样扫磁盘，
    /// 会把美术已经画好的 soil_a/b/c 等变体集也用上（只换细节位置、不换色板，但画面会变）。
    /// 详见 docs/engine/TILE-KINDS.md“变体集”一节。
    /// </summary>
    /// <remarks>
    /// 2026-11-16 夜：美术-土地交付 v5 后改为 <c>false</c> —— soil/tilled/sand/path 盘上已有
    /// <c>_a/_b/_c</c> 四套变体，钉表会让"拼接有变化"这条需求落空（引擎只画基础集）。
    /// 改回 <c>true</c> 可让画面逐像素回到 v4 观感。
    /// </remarks>
    private const bool PinnedLegacyGroundSets = false;

    /// <summary>每种地面有多少种物件造型（树 2、石头 2、草 1，取最大值）。</summary>
    private const int PropShapes = 2;

    private WorldMap _map = null!;
    private Player? _player;

    /// <summary>[kind][variant][wang index]；缺图的那格是 null，绘制时回退到该 kind 的基础集。</summary>
    private Texture2D?[][][] _ground = System.Array.Empty<Texture2D?[][]>();

    // ── 草簇散布层 ────────────────────────────────────────────────────────────
    // 星露谷的草坪瓦片本身不画亮草，亮草是**独立 sprite** 撒在世界坐标上。
    // 把亮草烘进 32×16 瓦片会让"每格一丛"变成低频结构，铺开必然看出周期（实测 lag 0.30–0.58）。
    // 所以这里反过来：瓦片只留底色与暗部，亮草由这一层按世界坐标哈希撒，天然无周期。
    private const string ScatterDir = "res://Assets/tiles/iso/props/scatter/";

    private static readonly string[] GrassTuftPaths =    {
        "res://Assets/tiles/iso/props/scatter/grass_tuft_0.png",
        "res://Assets/tiles/iso/props/scatter/grass_tuft_1.png",
        "res://Assets/tiles/iso/props/scatter/grass_tuft_2.png",
        "res://Assets/tiles/iso/props/scatter/grass_tuft_3.png",
    };

    private Texture2D?[] _grassTufts = System.Array.Empty<Texture2D?>();
    private readonly System.Collections.Generic.List<(float Gx, float Gy, int Variant)> _tuftSpots = new();
    private float _grassTuftDensity = 0.18f;

    /// <summary>
    /// 草簇绘制倍率（<c>config.json</c> 的 <c>grassTuftScale</c>，默认 1.75）。16×16 的草簇按 1:1
    /// 只占 1/8 格宽，zoom 1 时屏幕上是 16 px 的小点读不出来，所以统一放大后按脚底对齐。
    /// </summary>
    private float _grassTuftScale = 1.75f;

    /// <summary>
    /// 侧壁贴图，分「档 × 朝向 × 材质」三层：<c>_wallSeThin[kind]</c> 是某材质朝屏幕右下（se）的
    /// 20px 薄壁，<c>_wallSwThick[kind]</c> 是朝屏幕左下（sw）的 72px 厚壁。
    /// 档位常量见 <see cref="WallTierThin"/> / <see cref="WallTierMid"/> / <see cref="WallTierThick"/>，
    /// 尺寸与文件名规则见 <c>docs/engine/WALL-TIERS.md</c>。
    /// 中档的 canonical 文件名就是旧的 <c>&lt;slug&gt;_wall_{se,sw}.png</c>，所以美术没交三档之前
    /// 只有这三个 mid 数组有内容，绘制结果与之前逐像素一致。
    /// </summary>
    private Texture2D?[] _wallSeThin = System.Array.Empty<Texture2D?>();
    private Texture2D?[] _wallSeMid = System.Array.Empty<Texture2D?>();
    private Texture2D?[] _wallSeThick = System.Array.Empty<Texture2D?>();
    private Texture2D?[] _wallSwThin = System.Array.Empty<Texture2D?>();
    private Texture2D?[] _wallSwMid = System.Array.Empty<Texture2D?>();
    private Texture2D?[] _wallSwThick = System.Array.Empty<Texture2D?>();

    private Texture2D?[] _treeTrunk = System.Array.Empty<Texture2D?>();
    private Texture2D?[] _treeCanopy = System.Array.Empty<Texture2D?>();
    private Texture2D?[] _rocks = System.Array.Empty<Texture2D?>();
    private Texture2D? _weed;

    // ---- 装饰精灵（refblock.json 的 deco[]，贴图走 atlas_deco.png）----

    /// <summary>待画的装饰摆放（<see cref="LoadDeco"/> 从地图 json 读一次）。</summary>
    private readonly List<RefblockMapLoader.DecoPlacement> _deco = new();

    /// <summary>stem → (图集列, 图集行, texture_origin)。来自 <c>deco_tileset.tres</c> 的文本，见 <see cref="LoadStemTable"/>。</summary>
    private readonly Dictionary<string, (int Col, int Row, Vector2 Origin)> _stems =
        new(StringComparer.OrdinalIgnoreCase);

    private Texture2D? _decoAtlas;

    /// <summary>本帧画了多少次 <c>DrawTextureRectRegion</c>（含轮廓），口径与 <see cref="_drawProps"/> 一致。</summary>
    private int _drawDeco;
    private int _lastDeco;

    /// <summary>地图里引用了、但图集/表里找不到的精灵数（只用于那行日志）。</summary>
    private int _decoMissing;

    /// <summary>实际能画的装饰条数（<c>deco[]</c> 总数 − <see cref="_decoMissing"/>）。</summary>
    private int _decoDrawable;

    /// <summary>装饰准入规则取值（config.json 的 <c>decoRules</c>）：跑配额 / 间距 / 出生点净空。</summary>
    private const string DecoRulesAuto = "auto";

    /// <summary>
    /// 装饰准入规则取值：**只做两项安全过滤**（落在地图外/Void、stem 没有贴图），
    /// 其余一条不跑 —— 手画地图"编辑器里放几个，游戏里就画几个"。
    /// </summary>
    private const string DecoRulesOff = "off";

    /// <summary>
    /// 当前生效的装饰准入规则，由 <see cref="Initialize"/> 从 config.json 的 <c>decoRules</c> 设进来
    /// （<c>refblock_scene</c> 默认 off，见 <c>GameRoot.GameConfig.EffectiveDecoRules</c>）。
    /// </summary>
    private string _decoRules = DecoRulesAuto;

    /// <summary>本帧有几个装饰因为盖住玩家被画成半透明（玩家轮廓遮挡淡化）。</summary>
    private int _decoFade;
    private int _lastDecoFade;

    /// <summary>
    /// 装饰淡化：每个 deco 一个 0..1 的当前不透明度，<see cref="_Process"/> 里按 delta 线性推进。
    /// 与 <see cref="_deco"/> 等长、下标一一对应（<see cref="LoadDeco"/> 末尾重建）。
    /// </summary>
    private float[] _decoFadeAlphas = System.Array.Empty<float>();

    /// <summary>玩家格心周围这个半径（格）内的装饰全部淡化。1.5 格 = 玩家一步之内，正好"人走进去就透"。</summary>
    private const float DecoFadeRadius = 1.5f;

    /// <summary>淡化后的不透明度：透到能一眼看见玩家，又留着"那儿有个东西"。</summary>
    private const float DecoFadeAlpha = 0.25f;

    /// <summary>淡化过渡时长（秒）：0.12 s 线性，够快不拖影，够慢不跳变。</summary>
    private const float DecoFadeSeconds = 0.12f;

    private Texture2D[] _playerDown = System.Array.Empty<Texture2D>();
    private Texture2D[] _playerUp = System.Array.Empty<Texture2D>();
    private Texture2D[] _playerSide = System.Array.Empty<Texture2D>();

    private double _animTime;

    /// <summary>
    /// 上一帧的绘制次数，按地面顶面 / 菱形描边 / 侧壁（含草皮盖与压暗）/ 物件 /
    /// 低频光照 / 种类过渡带 / 细节散布分开记。
    /// </summary>
    private int _drawGround;
    private int _drawRim;
    private int _drawWalls;
    private int _drawDressing;
    private int _drawProps;
    private int _drawShade;
    private int _drawBlend;
    private int _drawSpecks;
    private int _drawWarmth;
    private int _drawContact;
    private int _lastGround;
    private int _lastRim;
    private int _lastWalls;
    private int _lastDressing;
    private int _lastProps;
    private int _lastShade;
    private int _lastBlend;
    private int _lastSpecks;
    private int _lastWarmth;
    private int _lastContact;
    private int _reportFrames;

    /// <summary>本帧按「选中的档」计的侧壁绘制数（depth 规则的分布，index = 档位常量）。</summary>
    private readonly int[] _drawTierWalls = new int[WallTierCount];

    /// <summary>本帧按「实际画出来的那张图属于哪一档」计的绘制数 —— 美术缺档时它和上一行不同。</summary>
    private readonly int[] _drawResolvedTier = new int[WallTierCount];

    private readonly int[] _lastTierWalls = new int[WallTierCount];
    private readonly int[] _lastResolvedTier = new int[WallTierCount];

    /// <summary>本帧因为「水格不长壁」而被跳过的边数（水格朝虚空的那几条边），只用于日志。</summary>
    private int _drawWaterWallSkips;

    /// <summary>上一帧跳过的水边数。</summary>
    private int _lastWaterWallSkips;

    // ---------------------------------------------------------------- 侧壁档位

    /// <summary>薄壁：画布 64x52 = 32px 顶面菱形 + 20px 壁。窄缝/悬空薄边用。</summary>
    private const int WallTierThin = 0;

    /// <summary>中壁：画布 64x72 = 32 + 40。旧美术口径（<c>&lt;slug&gt;_wall_se.png</c>），也是回退链的中间档。</summary>
    private const int WallTierMid = 1;

    /// <summary>厚壁：画布 64x104 = 32 + 72。开着的外海/地图边沿用。</summary>
    private const int WallTierThick = 2;

    /// <summary>档数。加第四档时这里 +1，并同步 <see cref="WallTierHeight"/> / <see cref="WallTierName"/> / 两个数组。</summary>
    private const int WallTierCount = 3;

    /// <summary>
    /// 每档的壁高（像素）。画布高 = <see cref="WallTopHeight"/> + 本值；三档画布宽度都是 64。
    /// 必须和 <c>tools/art/gen_iso_tiles.py</c> 的档位表一致（改这里 = 改美术口径）。
    /// </summary>
    private static readonly int[] WallTierHeight = { 20, 40, 72 };

    /// <summary>三档画布最上面那半个菱形的固定高度：永远占前 32px，壁从 y=32 往下长。</summary>
    private const int WallTopHeight = 32;

    /// <summary>档位名（config.json 的 <c>wallTierFixed</c>、文件名、日志都用它）。</summary>
    private static readonly string[] WallTierName = { "thin", "mid", "thick" };

    /// <summary>
    /// 落差深度 → 档位。索引 = <c>depth</c>（1..3；0 表示"没有落差"，不画壁），值 = 档位常量。
    ///
    /// 规则（本任务最终采用；和任务书的默认映射有一处不同，理由见 docs/engine/WALL-TIERS.md §4）：
    /// <code>
    /// depth == 1  → thin   落差点外一格就是陆地（1 格窄缝/悬空薄边），壁只该露出薄薄一条；
    /// depth == 2  → mid    2 格深的裂缝，露出 40px（= 旧口径，也是唯一有美术的档）；
    /// depth >= 3  → thick  3 格以上/出界（外海、地图边沿），整面 72px 崖壁。
    /// </code>
    /// 为什么不是任务书的 <c>1→mid, 2→thick, 3→thick</c>：那样 mid 只在 depth1 出现、
    /// thin 永远选不到（任务书里 thin 的触发条件"落差点再往外一格仍是 Void"恰好等于 depth>=2，
    /// 和同一段的 <c>depth==2 → thick</c> 直接冲突）。上面这条是单调的、三档都用得上，
    /// 而且窄缝里用 thin 正好避开"72px 高壁盖住对面陆地顶面"的过冲。
    /// </summary>
    private static readonly int[] DepthToTier = { -1, WallTierThin, WallTierMid, WallTierThick };

    /// <summary>回退链（请求档 → mid → thin → 都没有就 null），静态表避免每帧分配数组。</summary>
    private static readonly int[] WallChainThin = { WallTierThin, WallTierMid };
    private static readonly int[] WallChainMid = { WallTierMid, WallTierThin };
    private static readonly int[] WallChainThick = { WallTierThick, WallTierMid, WallTierThin };

    /// <summary>config.json 的 <c>wallTierMode</c> 解析结果：false = depth（默认），true = fixed。</summary>
    private bool _wallTierFixedMode;

    /// <summary>fixed 模式下所有边统一用的档；depth 模式下不读它。</summary>
    private int _wallTierFixed = WallTierMid;

    /// <summary>三档贴图的搜索目录，按优先级：配了 <c>wallTierDir</c> 就先找它，再找 res:// 的 cliff 目录。</summary>
    private string[] _wallTierDirs = { CliffDir };

    // ------------------------------------------------------- 体积感（2026-11-17）
    //
    // 「这岛像一块平整托盘」的成因有三条，全部在渲染而不是美术：
    //   1. 整片草地没有逐格明暗 → 读不出菱形格子（无格子 = 无尺度 = 无立体感）；
    //   2. 岛缘的土壁直接顶到草顶面 → 没有「草皮挂在土上」的厚度，也没有底边的重；
    //   3. 所有边都落在 thick(72px) → 一圈等高的墙把岛围成托盘边。
    // 下面三个开关 + 两组常量分别对付这三条；都不新增任何贴图，全部是 DrawColoredPolygon/DrawPolyline。

    /// <summary>
    /// 地面菱形描边的亮边增量（<c>config.json</c> 的 <c>groundRimLight</c>，**默认 0 = 不画**）。
    /// 2026-11-17 第四轮把默认值从 0.02 压到 0：0.02 在 1.5 zoom 的截图上是"满地菱形网格线"的来源，
    /// 一格 2 条线 = 整片读成花布。要回看旧观感就在 config 里设回去（代码不动）。
    /// </summary>
    private float _rimLight;

    /// <summary>地面菱形描边的暗边减量（<c>config.json</c> 的 <c>groundRimDark</c>，**默认 0 = 不画**）。</summary>
    private float _rimDark;

    /// <summary>
    /// 整层地面描边总开关（<c>config.json</c> 的 <c>groundRim</c>，默认 true）。
    /// false ⇒ 亮/暗两个增量都强制归零 ⇒ <see cref="DrawCellRim"/> 早退，一格都不画（省 2 draw call/格）。
    /// 这是给"草像塑料 / 满地菱形网格"这类截图问题的一键开关：C# 不动，只改 json。
    /// </summary>
    private bool _groundRim = true;

    /// <summary>
    /// 逐格亮度扰动的幅度（±2.5%）。低频（2 格块）哈希，见 <see cref="DrawGround"/>。
    /// **低频光照层（<see cref="_groundShade"/>）开着时这一项被强制关掉**：它的周期只有 2 格，
    /// 正是"整片在 2 格周期上重复"的那半个成因，留着会和新的世界坐标场打架。
    /// </summary>
    private const float GroundWobbleAmount = 0.025f;

    // --------------------------- A/C/D：世界坐标低频光照 + 种类过渡带 + 跨格细节散布（2026-11-17 第四轮）
    //
    // 「这个地目前也是一眼难受…你现在做得就很贴图，没有质感」的三条结构性成因（全部在渲染侧，不是美术）：
    //   1. 没有跨格的低频光照：每格的明暗被锁在单格内、且只在 2 格块上重复 ⇒ 整片读成"平铺的花布"；
    //   2. 逐格菱形描边把格子画出来了（1.5 zoom 下满地菱形网格）；
    //   3. 种类边界是硬边菱形台阶（水/沙/草的"切一刀"），参考图是 8–16px 的柔和过渡带。
    // 下面三组参数分别对付这三条，全部是纯绘制（DrawPolygon / DrawColoredPolygon），不新增任何贴图。
    //
    // 为什么不按格算噪声再乘贴图：逐格一个常数 = 每一格一块平色菱形，格子照样读得出来。
    // 这里把噪声取在**菱形四个顶点**（= 相邻格共用的角）上，一格的四个角取到四个不同的世界坐标值，
    // 逐顶点插值后明暗在格内是渐变的、跨格是连续的 —— 一个 draw call/格拿到整片连续的光照场。

    /// <summary>
    /// 世界坐标低频光照的幅度（<c>config.json</c> 的 <c>groundShade</c>，默认 <c>0.18</c> = 亮度 ×(1 ± 0.18)）。
    /// 0 = 整层关掉（不生成场、不画一个 draw call），同时把 <see cref="GroundWobbleAmount"/> 放回来。
    /// </summary>
    private float _groundShade = 0.18f;

    /// <summary>细斑的格周期：4 格一个明暗斑（需求里的"3–5 格一个斑"，即频率 ≈ 1/4 格）。</summary>
    private const float ShadeFineCells = 4f;

    /// <summary>粗斑的格周期：9 格一个超大斑，负责"整片地有起伏"的那一层（低频、几乎看不出重复）。</summary>
    private const float ShadeCoarseCells = 9f;

    /// <summary>细斑权重（需求给的 0.12）。两层的比值决定"小起伏 : 大起伏"。权重和 = 归一化分母。</summary>
    private const float ShadeFineWeight = 0.12f;

    /// <summary>粗斑权重（需求给的 0.08）。</summary>
    private const float ShadeCoarseWeight = 0.08f;
    private const float ShadePatchCells = 20f;
    private const float ShadePatchWeight = 0.22f;

    /// <summary>
    /// 亮向覆盖强度：<c>shade &gt; 1</c> 时叠白色，alpha = (shade − 1) × 这个系数。
    /// 为什么不是 1：白色覆盖的结果是 <c>base + (1−base)·a</c>，要拿到 <c>base·s</c> 就得
    /// <c>a = base(s−1)/(1−base)</c>，地面中位色 base ≈ 0.45 ⇒ 系数 ≈ 0.82；取 0.7 是留一点欠曝，
    /// 免得亮斑在浅色沙地上直接烧成白块。暗向系数 1.0：黑色覆盖正好是 ×(1−a)，与基色无关。
    /// </summary>
    private const float ShadeLightScale = 0.7f;

    /// <summary>暗向覆盖强度（黑色覆盖 alpha 直接等于 1 − shade）。</summary>
    private const float ShadeDarkScale = 1f;

    /// <summary>小于这个幅度就当作没偏移，省掉一次多边形（±0.0001 = 肉眼看不出）。</summary>
    private const float ShadeEpsilon = 0.0001f;

    /// <summary>
    /// 种类过渡带的宽度（<c>config.json</c> 的 <c>groundBlendWidth</c>，默认 12，**屏幕** px，0 = 关）。
    /// 需求给的 10–14px 是"看上去"的宽度，所以和描边同一套换算：本地宽 = 12 ÷ 当前缩放。
    /// </summary>
    private float _groundBlendWidth = 12f;

    /// <summary>过渡带贴在边界上的不透明度（需求给的 0.55），向格内线性渐隐到 0。</summary>
    private const float BlendEdgeAlpha = 0.55f;

    /// <summary>
    /// 过渡带沿边两端各收进来的比例（0.12 = 两端各让出 12% 边长）。
    /// 为什么必须让：相邻两条边的带会在格角叠出一块 alpha 0.55+0.55×0.45 ≈ 0.8 的亮角斑，
    /// 三个 kind 交汇处就成了规则的"星形亮角"，比原来的硬边更抢眼。让开之后角上是硬点（参考图里也是硬点）。
    /// </summary>
    private const float BlendEdgeInset = 0.12f;

    /// <summary>
    /// 细节散布密度（<c>config.json</c> 的 <c>groundSpecks</c>，默认 0.25 = **每 4 格 1 个**，0 = 关）。
    /// 候选点按 <see cref="SpeckStride"/> 格一个铺满全图，命中率 = 密度 × 每格候选数。
    /// </summary>
    private float _groundSpecks = 0.25f;

    /// <summary>
    /// 细节点候选格的小格边长（格）。0.5 ⇒ 每格 4 个候选，位置在小格内再抖动 ——
    /// 位置与"地面格"完全无关，这就是"跨格边界分布、同格内没有固定图案"的做法。
    /// </summary>
    private const float SpeckStride = 0.5f;

    /// <summary>细节点尺寸范围（**屏幕** px，2:1 菱形，绘制时 ÷ 缩放换算成本地像素）。</summary>
    private const float SpeckMinPx = 2f;

    /// <summary>细节点尺寸上限（屏幕 px）。</summary>
    private const float SpeckMaxPx = 4f;

    /// <summary>细节点颜色 = 本格中位色的亮度 ±10%。</summary>
    private const float SpeckTintJitter = 0.1f;

    /// <summary>本帧实际生效的逐格亮度扰动（光照层开着时是 0，见 <see cref="GroundWobbleAmount"/>）。</summary>
    private float _wobble = GroundWobbleAmount;

    // ------------------------------------------------ A2/E：色温场 + 接触阴影（2026-11-18 第五轮）
    //
    // 明暗（groundShade）只给"亮/暗"，没有"暖/冷"；种类边界只有邻居色渐隐，没有"落地"的暗角。
    // 这两项都是**纯绘制**、都不新增贴图、都不新增第三种 draw：
    //   - 色温并进 DrawShadeCell 里已经在画的那 1–2 个多边形（按角给颜色），一格 0 额外 draw；
    //   - 接触阴影与 DrawGroundBlend 共用 BlendEdges 表，同类相邻 0 draw，异类边 +1。

    /// <summary>
    /// 色温场的格周期（需求给的"尺度 7 格"）：7 格一个暖/冷斑。
    /// 与明暗场的 4 格 / 9 格都不同源，所以暖斑和亮斑不会叠成同一个形状。
    /// </summary>
    private const float WarmthCells = 7f;

    /// <summary>色温场的盐：必须与 <see cref="ShadeFineCells"/> 用的 0x51ED / 0x9C0A 都不同 ⇒ 两场互不相关。</summary>
    private const uint WarmthSalt = 0x3D71u;

    /// <summary>暖色（t &gt; 0，需求给的 0.98/0.92/0.74）：偏黄的"日照草尖"。</summary>
    private static readonly Color WarmTint = new(0.98f, 0.92f, 0.74f);

    /// <summary>冷色（t &lt; 0，需求给的 0.72/0.86/0.98）：偏蓝的"阴影草"。</summary>
    private static readonly Color CoolTint = new(0.72f, 0.86f, 0.98f);

    /// <summary>
    /// 色温叠色的幅度（<c>config.json</c> 的 <c>groundWarmth</c>，默认 <b>0.10</b>，0 = 关）。
    /// 角上的 alpha = |t| × 这个值，所以最大只有 0.10 —— 它是"色偏"，不是"色块"。
    /// </summary>
    private float _groundWarmth = 0.10f;

    /// <summary>
    /// 地面全局染色（<c>config.json</c> 的 <c>groundTint</c>，格式 <c>"#rrggbb"</c> 或 <c>"r,g,b"</c>（0..1），
    /// 默认白 = 不染色）。乘在每张地面瓦的 modulate 上 —— 用来一次性把整套地面压暗 / 往黄绿推，
    /// 不必重生成 2538 张 PNG。只影响地面贴图：光照、过渡带、侧壁、装饰都不受它影响。
    /// </summary>
    private Color _groundTint = Colors.White;

    /// <summary>接触阴影的不透明度（<c>config.json</c> 的 <c>groundContactShadow</c>，默认 <b>0.16</b>，0 = 关）。</summary>
    private float _groundContactShadow = 0.16f;

    /// <summary>接触阴影的宽度（**屏幕** px，需求给的 6–10 区间取中值 8），绘制时 ÷ 缩放。</summary>
    private const float ContactShadowScreenPx = 8f;

    /// <summary>接触阴影色 (0.10, 0.09, 0.12)：偏冷的黑，不是纯黑 —— 纯黑会把岸线压成一条硬边。</summary>
    private static readonly Color ContactShadowColor = new(0.10f, 0.09f, 0.12f);

    /// <summary>
    /// 世界坐标低频光照场的**角点缓存**：下标 <c>[i, j]</c> 对应世界格坐标 <c>(i − 0.5, j − 0.5)</c>，
    /// 也就是格 (i, j) 的**上顶点**，同时也是格 (i−1, j)、(i, j−1)、(i−1, j−1) 的共用角。
    /// 一次性在 <see cref="BuildGroundLighting"/> 里算好，绘制期间只查表 —— 一帧一次、绝不在 _Draw 里算噪声。
    /// </summary>
    private float[,] _shadeField = new float[0, 0];

    /// <summary>
    /// 色温场的**角点缓存**，下标含义与 <see cref="_shadeField"/> 完全一致（<c>[i, j]</c> ↔ 世界格坐标
    /// <c>(i − 0.5, j − 0.5)</c> = 格 (i, j) 的上顶点）。值 ∈ [−1, 1]：&gt; 0 偏暖、&lt; 0 偏冷。
    /// 同样是 <see cref="BuildGroundLighting"/> 里 bake 一次，绘制期间只查表。
    /// </summary>
    private float[,] _warmthField = new float[0, 0];

    /// <summary>每帧复用的顶点/颜色数组（169 格 × 每帧几段零分配）。</summary>
    private readonly Vector2[] _shadePts = new Vector2[4];
    private readonly Color[] _shadeColors = new Color[4];
    private readonly Color[] _shadeColorsDark = new Color[4];
    private readonly Color[] _warmthColors = new Color[4];
    private readonly Color[] _contactColors = new Color[4];
    private readonly Vector2[] _blendPts = new Vector2[12];
    private readonly Color[] _blendColors = new Color[12];
    private readonly Vector2[] _contactPts = new Vector2[4];
    private readonly Vector2[] _speckPts = new Vector2[4];
    private readonly float[] _shadeCornerScratch = new float[4];

    /// <summary>
    /// 一条"种类过渡带"的几何：从哪条边（菱形四边之一）往格内铺，以及那条边的两个端点（相对格心的本地像素）。
    /// 四边与 <see cref="WangSides"/> 同序：ne / nw / se / sw。
    /// </summary>
    private readonly record struct BlendEdge(
        int Dx, int Dy,
        float Ax, float Ay,
        float Bx, float By);

    /// <summary>四条边的端点（相对格心）。顺序与 <see cref="WangSides"/> 一致。</summary>
    private static readonly BlendEdge[] BlendEdges =
    {
        new(0, -1, 0f, -HalfH, HalfW, 0f),      // ne：上顶点 → 右顶点
        new(-1, 0, -HalfW, 0f, 0f, -HalfH),     // nw：左顶点 → 上顶点
        new(1, 0, HalfW, 0f, 0f, HalfH),        // se：右顶点 → 下顶点
        new(0, 1, 0f, HalfH, -HalfW, 0f),       // sw：下顶点 → 左顶点
    };

    /// <summary>四条边各自的"朝格心"单位法线（过渡带向内铺的方向）。</summary>
    private static readonly Vector2[] BlendInward = BuildInwardNormals();

    /// <summary>一个细节点（草叶尖 / 土粒 / 石子）：世界坐标（屏幕本地像素）、尺寸（屏幕 px）、颜色（已含光照）。</summary>
    private readonly record struct Speck(Vector2 At, float SizePx, Color Tint);

    /// <summary>本图全部细节点，一次性构建（见 <see cref="BuildSpecks"/>），绘制期间只遍历不计算。</summary>
    private Speck[] _specks = System.Array.Empty<Speck>();

    /// <summary>
    /// 侧壁自动档的上限（<c>config.json</c> 的 <c>wallMaxTier</c>，默认 <c>mid</c>）。
    /// <see cref="TierFor"/> 的最后一步拿它夹住结果，所以外海/地图边沿不再一律 thick(72px)。
    /// </summary>
    private int _wallMaxTier = WallTierMid;

    /// <summary>
    /// 水格要不要长侧壁（<c>config.json</c> 的 <c>waterWallTier</c>，默认 <c>"none"</c> = 不长）。
    /// 水比陆地低一层，给它落土壁 = 岛缘出现一段深蓝缺角；水面到岸的过渡交给水瓦自己的 wang 边缘。
    /// false 时 <see cref="DrawWalls"/> 整格跳过水，并把跳过的边数记进日志（对账用）。
    /// </summary>
    private bool _waterWalls;

    /// <summary>
    /// 水格长壁时最多用到哪一档（<c>waterWallTier</c> 给 <c>"thin"</c> 时是 thin，给 <c>"auto"</c> 时是 thick
    /// = 完全按落差 + <see cref="_wallMaxTier"/> 夹，即改造前的旧行为）。默认 thin（极薄一条）。
    /// </summary>
    private int _waterWallTier = WallTierThin;

    // ------------------------------------------------ 描边自适应缩放（2026-11-17 第三轮）
    //
    // 问题：1px 是**世界**像素。相机 zoom = 0.75 时它只有 0.75 屏幕像素，被光栅化摊成两条半透明边，
    // 菱形格子读不出来（上一轮报告的原话：0.75 缩放下 1px 描边被抗锯齿摊掉）。
    // 做法：按「屏幕上一格菱形有多宽」分两档 —— 窄档把线画成 2 屏幕像素，同时把对比**压**一档。
    // 为什么压而不是抬：2px 的墨量已经是 1px 的两倍；再抬对比就等于把整片岛面画成绗缝被。

    /// <summary>常规档的描边屏幕像素宽（硬边，不开抗锯齿 —— 像素画不要灰色过渡）。</summary>
    private const float RimScreenPx = 1f;

    /// <summary>窄档（低缩放）的描边屏幕像素宽。</summary>
    private const float RimNarrowScreenPx = 2f;

    /// <summary>
    /// 进窄档的阈值：**屏幕上**一格菱形宽（px）小于它就换 2px 线。
    /// 128 × 0.75 = 96，默认 zoom 正好踩在 96 上，所以阈值取 112 而不是 96：
    /// 一是严格小于判断不到正好等于 96 的情况，二是 0.75~0.875 这一带同样窄、同样需要补。
    /// 128（zoom 1.0）及以上走常规档，1px。
    /// </summary>
    private const float RimNarrowTileScreenPx = 112f;

    /// <summary>
    /// 窄档的对比倍率：0.9 = 把 <c>groundRimLight/Dark</c> 相对压一档，换 2px 的线宽。
    /// 为什么不抬：地皮贴图自身的四档变体已有 1.045 / 0.958 的亮度差（≈ ±4.5%），叠上 6%/7% 描边本就偏重；
    /// 线宽翻倍带来的「格子可读」增益远大于再加 2% 亮度。调到 1.2 以上会明显变绗缝被。
    /// </summary>
    private const float RimNarrowContrast = 0.9f;

    /// <summary>
    /// 描边内缩的最小量（px）：线画在格子**里面**。
    /// 为什么不贴着菱形边界画：顶面是按 x+y 从小到大画的一层，后画的邻格会把自己那份菱形**盖在共享边**上，
    /// 贴着边界画的描边会被整条吃掉（只剩亮边），必须内缩。
    /// 实际内缩 = <see cref="RimInsetPad"/> + 线宽的一半 → 线宽怎么变，笔画都整体落在菱形内部
    /// （1px 线 → 内缩 1，和改造前逐像素一致；2px 屏幕线在 0.75 下是 2.67 本地 px → 内缩 1.83）。
    /// </summary>
    private const float RimInsetPad = 0.5f;

    /// <summary>侧壁顶边的草皮条高度（px）：这 6px 用该格顶面中位色盖住土壁。</summary>
    private const int WallCapHeight = 6;

    /// <summary>
    /// 草皮条相对顶面中位色的压暗倍率。为什么不是直接拿中位色原样画：草皮条是**竖面**（朝下），
    /// 和水平顶面同色就会被读成"草地往下多长了一截"，看不出厚度；参考剖面里草皮竖面明显比顶面深一档。
    /// 设 1.0 就退回"原样中位色"。
    /// </summary>
    private const float WallCapShade = 0.82f;

    /// <summary>草皮条下沿那条深色阴影的高度（px）。</summary>
    private const int WallCapShadowHeight = 2;

    /// <summary>草皮阴影色 <c>#01501C</c>（参考剖面里草根压暗的那一档）。</summary>
    private static readonly Color WallCapShadowColor = new(1 / 255f, 80 / 255f, 28 / 255f);

    /// <summary>侧壁最底几 px 压暗：覆盖 18% 黑 == 逐通道 ×0.82。</summary>
    private const int WallFootHeight = 4;

    /// <summary>底边压暗倍率（<c>#D68C3F</c> 的土 ~ <c>#853C1B</c> 的底，差的就是这个量级）。</summary>
    private const float WallFootMultiplier = 0.82f;

    /// <summary>等效于乘 <see cref="WallFootMultiplier"/> 的黑覆盖色。</summary>
    private static readonly Color WallFootOverlayColor = new(0f, 0f, 0f, 1f - WallFootMultiplier);

    /// <summary>最底那 1px 暗线：比 0.82 那一档更重，给剖面收个底。</summary>
    private static readonly Color WallFootLineColor = new(0f, 0f, 0f, 0.5f);

    /// <summary>每种地面顶面的中位色（<see cref="BakeTiles"/> 里每种算一次），描边与草皮条都从这里取色。</summary>
    private readonly Color[] _groundMedian = new Color[GroundKindCount];

    /// <summary>每种地面的亮描边色（中位色 + <see cref="_rimLight"/> × <see cref="_rimContrast"/>）。</summary>
    private readonly Color[] _rimLightColor = new Color[GroundKindCount];

    /// <summary>每种地面的暗描边色（中位色 − <see cref="_rimDark"/> × <see cref="_rimContrast"/>）。</summary>
    private readonly Color[] _rimDarkColor = new Color[GroundKindCount];

    /// <summary>
    /// 当前实际的屏幕放大率（相机 zoom，可能再乘上节点自身的缩放），每帧从 canvas 变换里取。
    /// 1 = 1 世界像素 = 1 屏幕像素。
    /// </summary>
    private float _effZoom = 1f;

    /// <summary>当前描边档：<c>-1</c> 还没定 / <c>0</c> 常规 1px / <c>1</c> 窄档 2px。</summary>
    private int _rimBucket = -1;

    /// <summary>当前描边线宽，单位是**本节点的本地像素**（= 屏幕像素 ÷ <see cref="_effZoom"/>）。</summary>
    private float _rimLineWidth = RimScreenPx;

    /// <summary>当前描边对比倍率（窄档 = <see cref="RimNarrowContrast"/>，常规档 = 1）。</summary>
    private float _rimContrast = 1f;

    /// <summary>每帧复用的小数组，避免 <see cref="DrawGround"/> 里每格分配（169 格 × 2 个短命数组/帧）。</summary>
    private readonly Vector2[] _rimUp = new Vector2[3];
    private readonly Vector2[] _rimDown = new Vector2[3];
    private readonly Vector2[] _quad = new Vector2[4];

    /// <summary>确定性整数哈希（和贴图生成器无关，只用来挑变体）。</summary>
    private static uint Hash(int x, int y, uint salt)
    {
        uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ salt * 0xCB1AB31Fu;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }

    /// <summary>
    /// 绑定世界与玩家，并预先烘焙好所有菱形贴图（只做一次）。
    /// <paramref name="player"/> 可以为 null：土地样片/关卡预览不需要角色。
    /// <paramref name="decoMapPath"/> 给了就把它 <c>deco[]</c> 里的装饰精灵一起接进来（仅 refblock 地图有）；
    /// 为空 = 不画装饰（样片场景照旧）。
    /// <paramref name="decoPlacements"/> 直接给一份**内存里的**装饰表（<c>mapSource=refblock_scene</c> 从
    /// <c>Maps/Refblock.tscn</c> 的 Deco 系列层读出来的），非空时优先于 <paramref name="decoMapPath"/>；
    /// <paramref name="decoSource"/> 只用于日志里交代"这些装饰是哪来的"。
    /// <paramref name="decoRules"/> 是 config.json 的 <c>decoRules</c>（<c>auto</c> / <c>off</c>，
    /// 空 = <c>auto</c>）：<c>off</c> 只保留 Void 与"stem 没贴图"两项安全过滤，手画地图默认走它。
    /// 两条路共用同一套 stem 表 / 准入规则 / 绘制代码，差别只在"装饰从哪读"。
    /// </summary>
    public void Initialize(
        WorldMap map,
        Player? player,
        string? decoMapPath = null,
        List<RefblockMapLoader.DecoPlacement>? decoPlacements = null,
        string? decoSource = null,
        string? decoRules = null)
    {
        _map = map;
        _player = player;
        _decoRules = string.Equals(decoRules, DecoRulesOff, StringComparison.OrdinalIgnoreCase)
            ? DecoRulesOff
            : DecoRulesAuto;
        BakeTiles();
        SetupWaterFx();
        LoadDeco(decoMapPath, decoPlacements, decoSource);

        // 玩家节点本身不参与绘制（由本渲染器按深度画），但位置仍由它算出来，
        // 这样相机跟随、存档取格子都不用改。
        if (_player is not null)
        {
            _player.Visible = false;
        }

        GD.Print($"[iso] renderer ready: {map.Width}x{map.Height}, tile {TileW}x{TileH} (2:1), wang47");
    }

    // ------------------------------------------------------------ 动态水（瓦片级）

    /// <summary>
    /// 瓦片级动态水独立画在 <see cref="WaterFxLayer"/>（<c>ZIndex = -1</c>，压在本渲染器的地面/侧壁之下、
    /// 物件之上）。
    ///
    /// 为什么单开一层：Godot 4 的自定义绘制只能整节点共用一份 <see cref="CanvasItem.Material"/>，
    /// 想让"只有水面动"就必须让水面成为一个独立 canvas item。
    /// 为什么 <c>ZIndex = -1</c>：岛缘的土壁应该在水的**前面**（水在更远、更低的位置），
    /// 放在 -1 正好让壁盖住水，而物件照旧按 x+y 深度画在最上面。
    /// </summary>
    private WaterFxLayer? _waterFx;

    /// <summary>水面分层：0 = 近岸、1 = 过渡、2 = 池心。每层一个 canvas item（材质只能整节点共用）。</summary>
    private readonly WaterFxLayer?[] _waterBands = new WaterFxLayer?[3];

    /// <summary>每层的 <c>deep_amt</c>：0 近岸 / 0.30 过渡 / 0.60 池心（WATER-R3 §2 的 0/0.35/0.7 折中）。</summary>
    private static readonly float[] BandDeepAmount = { 0.0f, 0.30f, 0.60f };

    /// <summary>水面分层：值是 0..2 的带号，不是水格记 -1（BFS 到岸的阶数，&gt;=3 归池心）。</summary>
    private int[]? _shoreBand;

    /// <summary>近岸水瓦（<c>ground/water_shallow</c>，带泡沫/湿边）；空 = 回退到 <c>ground/water</c>。</summary>
    private Texture2D?[] _waterShallowTiles = System.Array.Empty<Texture2D?>();

    /// <summary>池心水瓦（<c>ground/water_deep</c>）。</summary>
    private Texture2D?[] _waterDeepTiles = System.Array.Empty<Texture2D?>();

    /// <summary>
    /// 本帧已画的水格数：三个 band 各自累加（<see cref="DrawWaterTiles"/>），每帧开头清零。
    /// 早先只在 band 0 赋值，band 1/2 随后把 0 覆盖回去 ⇒ 日志恒显示 <c>+ water fx 0 cell(s)</c>。
    /// </summary>
    private int _waterFxCells;

    /// <summary>上一帧画完的水格数（清零前的快照）：日志在父节点 <see cref="_Draw"/> 里打，用这份才不会是 0。</summary>
    private int _lastWaterFxCells;

    /// <summary>焦散网空间周期倍率（<c>config.json</c> 的 <c>waterNetPeriod</c>，默认 1.6）。&gt;1 = 网眼更大、每屏网线更少。</summary>
    private float _waterNetPeriod = 1.6f;

    /// <summary>网线宽度（<c>config.json</c> 的 <c>waterNetWidth</c>，默认 0.045 = 索引值空间的半带宽 0.0225）。</summary>
    private float _waterNetWidth = 0.045f;

    /// <summary>网线亮度上限（<c>config.json</c> 的 <c>waterNetContrast</c>，默认 0.3；1 = 改造前满亮，0 = 没有网）。</summary>
    private float _waterNetContrast = 0.3f;

    /// <summary>
    /// 建水面层：加载 <c>res://Shaders/water/water_tile_flow.gdshader</c> +
    /// <c>res://Assets/tiles/iso/waterfx/water_a.png|water_b.png</c>。
    /// 任一缺失就打印告警并退回"静态水瓦"（不会崩、不会变黑）。
    /// </summary>
    private void SetupWaterFx()
    {
        if (_waterFx is not null)
        {
            return;
        }

        Shader? shader = GD.Load<Shader>("res://Shaders/water/water_tile_flow.gdshader");

        // r3：网周期拉到 12x（1536×768 无缝）。6x 在 1280 宽画面里能看出重复，
        // 缺 12x 就退回 6x 并且 tex_px 一起改（texel 仍严格 = 1 世界像素）。
        Vector2 texPx = new(1536f, 768f);
        Texture2D? waterA = GD.Load<Texture2D>("res://Assets/tiles/iso/waterfx/water_a_12x.png");
        Texture2D? waterB = GD.Load<Texture2D>("res://Assets/tiles/iso/waterfx/water_b_12x.png");
        if (waterA is null || waterB is null)
        {
            texPx = new Vector2(768f, 384f);
            waterA = GD.Load<Texture2D>("res://Assets/tiles/iso/waterfx/water_a_6x.png");
            waterB = GD.Load<Texture2D>("res://Assets/tiles/iso/waterfx/water_b_6x.png");
        }

        if (shader is null || waterA is null || waterB is null)
        {
            GD.PushWarning("[iso] water fx disabled (shader or water_a/water_b missing), static water tiles stay");
            return;
        }

        _waterShallowTiles = LoadWaterWangSet("water_shallow");
        _waterDeepTiles = LoadWaterWangSet("water_deep");

        // 三层：同一张网、同一相位，只差 deep_amt（0 / 0.30 / 0.60），所以层间接缝不会露。
        // 分层而不是逐格换材质，是因为 CanvasItem.Material 只能整节点共用一份。
        for (int band = 0; band < _waterBands.Length; band++)
        {
            var layer = new WaterFxLayer
            {
                Name = $"WaterFxBand{band}",
                OwnerRenderer = this,
                Band = band,
                ZIndex = -1 - band,
                Material = BuildWaterMaterial(shader, waterA, waterB, texPx, BandDeepAmount[band]),
            };
            _waterBands[band] = layer;
            AddChild(layer);
        }

        _waterFx = _waterBands[0];

        GD.Print($"[iso] water fx r3: {(int)texPx.X}x{(int)texPx.Y} net, shallow {_waterShallowTiles.Length}/47, "
            + $"deep {_waterDeepTiles.Length}/47, bands 0/{BandDeepAmount[1]:0.##}/{BandDeepAmount[2]:0.##}");
    }

    /// <summary>
    /// 水材质。参数表来自 `docs/art/water/WATER-R3.md` §2（美术-水实测对齐 REF-05 的那一版），
    /// 两层的唯一差别就是 <paramref name="deepAmount"/>。
    ///
    /// 2026-11-17 调整（截图判定"水面像泳池渔网"）：
    /// <c>net_period / net_width / net_contrast</c> 三个网参数从 <c>config.json</c> 读，网线更细更暗，
    /// 网眼放大 1.35 倍；<c>col_light / col_high</c> 各压一档（原来 #2FD9DF / #8FF2F2 在 zoom 1.5 下
    /// 把整片水读成亮青），本体锚点仍是 REF 的 #02B3C4（S .99 / V .78）。
    /// </summary>
    private ShaderMaterial BuildWaterMaterial(
        Shader shader, Texture2D waterA, Texture2D waterB, Vector2 texPx, float deepAmount)
    {
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("water_a", waterA);
        material.SetShaderParameter("water_b", waterB);
        material.SetShaderParameter("tex_px", texPx);
        material.SetShaderParameter("speed_a", 3.2f);
        material.SetShaderParameter("speed_b", -1.2f);
        material.SetShaderParameter("dir_a", new Vector2(1.0f, 0.18f));
        material.SetShaderParameter("dir_b", new Vector2(-0.85f, 0.42f));
        material.SetShaderParameter("crossfade_period", 36.0f);
        material.SetShaderParameter("glint_amount", 0.0f);   // 0.26 会刷出宽斜亮带 = 塑料感
        material.SetShaderParameter("alpha_mul", 1.0f);
        material.SetShaderParameter("rim_dark", 0.0f);       // >0 会画出菱形渔网
        material.SetShaderParameter("net_warp_px", 0.7f);    // 两次取样位移；>2 开始看出重影
        material.SetShaderParameter("net_period", _waterNetPeriod);
        material.SetShaderParameter("net_width", _waterNetWidth);
        material.SetShaderParameter("net_contrast", _waterNetContrast);
        material.SetShaderParameter("deep_amt", deepAmount);
        material.SetShaderParameter("tile_detail", 0.45f);   // 水瓦 RGB 当细节乘子（岸带全靠它）；0.85 会把浅水瓦的亮青刷满整片 = 偏亮的"泳池青"
        material.SetShaderParameter("col_deep", new Color("#03505C"));
        material.SetShaderParameter("col_base", new Color("#02B3C4"));   // REF 锚点：S .99 / V .78
        material.SetShaderParameter("col_light", new Color("#22C4D0"));  // 原 #2FD9DF，压一档
        material.SetShaderParameter("col_high", new Color("#6EDBE2"));   // 原 #8FF2F2，压一档
        material.SetShaderParameter("tile_ref", new Color("#02B3C4"));
        return material;
    }

    /// <summary>
    /// 读一组水瓦（<c>ground/&lt;dir&gt;/water_wNN.png</c>，47 张 wang 表）。
    /// 缺图返回空数组，调用方回退到 <c>ground/water</c> 那套。
    /// </summary>
    private static Texture2D?[] LoadWaterWangSet(string dir)
    {
        var tiles = new Texture2D?[IsoWangTable.TileCount];
        int found = 0;
        for (int w = 0; w < IsoWangTable.TileCount; w++)
        {
            var tex = GD.Load<Texture2D>($"{GroundDirDefault}{dir}/water_w{w:00}.png");
            tiles[w] = tex;
            if (tex is not null)
            {
                found++;
            }
        }

        if (found != IsoWangTable.TileCount)
        {
            GD.PushWarning($"[iso] water set '{dir}': {found}/{IsoWangTable.TileCount} tiles, falling back to ground/water");
            return found == 0 ? System.Array.Empty<Texture2D?>() : tiles;
        }

        return tiles;
    }

    /// <summary>
    /// 画一层水。
    ///
    /// 分层逻辑：从贴岸的水格（四邻有非水，含越界=岛缘）做 BFS，第 1 圈 = band 0（浅、亮、
    /// 带泡沫）、第 2–3 圈 = band 1（过渡）、更里面 = band 2（池心，压深）。逐格换材质做不到，
    /// 因为 Godot 4 的自定义绘制只能整节点共用一份 <see cref="CanvasItem.Material"/>。
    /// </summary>
    /// <param name="target">要画进去的 canvas item。</param>
    /// <param name="band">0..2，见 <see cref="_shoreBand"/>。</param>
    private void DrawWaterTiles(CanvasItem target, int band)
    {
        if (_map is null)
        {
            return;
        }

        int gi = KindToGroundIndex(TileKind.Water);
        if (gi < 0 || gi >= _ground.Length)
        {
            return;
        }

        EnsureShoreBands();
        int[] bands = _shoreBand!;
        Texture2D?[] set = band == 0 ? _waterShallowTiles : _waterDeepTiles;

        int drawn = 0;
        for (int y = 0; y < _map.Height; y++)
        {
            for (int x = 0; x < _map.Width; x++)
            {
                if (_map.GetTile(x, y) != TileKind.Water || bands[(y * _map.Width) + x] != band)
                {
                    continue;
                }

                int w = IsoWangTable.RawMaskToTile[WangMask(x, y, TileKind.Water)];
                Texture2D? tex = set.Length == IsoWangTable.TileCount ? set[w] : null;
                tex ??= _ground[gi][VariantFor(gi, x, y)][w] ?? _ground[gi][0][0];
                if (tex is null)
                {
                    continue;
                }

                // 水瓦的 alpha 就是菱形：shader 只负责水里面的花纹，四角透明处直接丢弃。
                target.DrawTexture(tex, GridToScreen(x, y) - new Vector2(HalfW, HalfH));
                drawn++;
            }
        }

        // 三个 band 各画一部分水格：**累加**，不是覆盖。
        // 早先写 `if (band == 0) _waterFxCells = drawn;`，band 1/2 不参与 ⇒ 池心多的地图恒为 0。
        _waterFxCells += drawn;
    }

    /// <summary>
    /// 算每格水的"离岸阶数"：岸水 = 0，往里每一格 +1，超过 2 一律记 2（池心）。
    /// 世界生成/覆盖改了水面就置 <c>_shoreBand = null</c> 让它重算。
    /// </summary>
    private void EnsureShoreBands()
    {
        if (_shoreBand is not null || _map is null)
        {
            return;
        }

        int w = _map.Width;
        int h = _map.Height;
        var bands = new int[w * h];
        var queue = new System.Collections.Generic.Queue<(int X, int Y)>();

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bands[(y * w) + x] = -1;
                if (_map.GetTile(x, y) != TileKind.Water)
                {
                    continue;
                }

                if (!IsWaterShore(x, y))
                {
                    continue;
                }

                bands[(y * w) + x] = 0;
                queue.Enqueue((x, y));
            }
        }

        while (queue.Count > 0)
        {
            (int cx, int cy) = queue.Dequeue();
            int next = bands[(cy * w) + cx] + 1;
            if (next > 2)
            {
                next = 2;
            }

            foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || _map.GetTile(nx, ny) != TileKind.Water)
                {
                    continue;
                }

                int idx = (ny * w) + nx;
                if (bands[idx] >= 0)
                {
                    continue;
                }

                bands[idx] = next;
                queue.Enqueue((nx, ny));
            }
        }

        // 没被 BFS 触及的水格（理论上不会有）留在池心
        for (int i = 0; i < bands.Length; i++)
        {
            if (bands[i] < 0 && _map.GetTile(i % w, i / w) == TileKind.Water)
            {
                bands[i] = 2;
            }
        }

        _shoreBand = bands;
    }

    /// <summary>四邻里只要有一个不是水（含越界 = 岛缘），就算贴岸。</summary>
    private bool IsWaterShore(int x, int y)
    {
        return _map is null
            || _map.GetTile(x + 1, y) != TileKind.Water
            || _map.GetTile(x - 1, y) != TileKind.Water
            || _map.GetTile(x, y + 1) != TileKind.Water
            || _map.GetTile(x, y - 1) != TileKind.Water;
    }

    /// <summary>水面层：自己的 canvas item，只做 <see cref="DrawWaterTiles"/> 与每帧重绘。</summary>
    private sealed partial class WaterFxLayer : Node2D
    {
        internal IsoWorldRenderer? OwnerRenderer;

        /// <summary>0 = 近岸、1 = 过渡、2 = 池心。</summary>
        internal int Band;

        public override void _Draw()
        {
            OwnerRenderer?.DrawWaterTiles(this, Band);
        }

        public override void _Process(double delta)
        {
            QueueRedraw();
        }
    }

    // ---------------------------------------------------------------- 烘焙

    /// <summary>地面贴图根目录（<c>&lt;kind&gt;/&lt;kind&gt;_w&lt;nn&gt;.png</c>，64x32 菱形，透明底）。</summary>
    private const string GroundDirDefault = "res://Assets/tiles/iso/ground/";

    /// <summary>
    /// 实际使用的地面贴图根目录。默认就是 <see cref="GroundDirDefault"/>；
    /// config.json 的可选键 <c>groundKindDir</c> 可以改掉它（绝对路径也行），
    /// 用途：在工程外放一套"故意缺几个 kind"的目录，验证缺目录 → 回退草地那条路径。
    /// 常规游戏不该设它。见 docs/engine/TILE-KINDS.md。
    /// </summary>
    private string _groundDir = GroundDirDefault;

    /// <summary>物件贴图目录（脚底锚点 = 图片底部中心，阴影已烘焙进图）。</summary>
    private const string PropDir = "res://Assets/tiles/iso/props/";

    /// <summary>角色帧目录（ansimuz 的 32x32 四向八帧，已从原始包整理过来）。</summary>
    private const string CharsDir = "res://Assets/chars/player/";

    /// <summary>
    /// 12 个地面种类在磁盘上的目录名（<c>res://Assets/tiles/iso/ground/&lt;slug&gt;/</c>），
    /// 顺序必须和 <see cref="KindToGroundIndex"/> 一致 —— 索引 0..11 依次是
    /// grass, soil, tilled, water, sand, path, grass_wild, grass_dry, mud, soil_loose, gravel, stone_floor。
    /// 这份顺序 = <see cref="TileKind"/> 的枚举顺序（Void 之后的 1..12），
    /// <see cref="GroundKindIndexToKind"/> 会把两边对不上当成开发期错误报出来。
    /// </summary>
    private static readonly string[] GroundSlugs =
    {
        "grass", "soil", "tilled", "water", "sand", "path",
        "grass_wild", "grass_dry", "mud", "soil_loose", "gravel", "stone_floor",
    };

    /// <summary>
    /// 每种地面的 wang 变体集后缀（磁盘上 <c>&lt;slug&gt;&lt;suffix&gt;_w&lt;nn&gt;.png</c>）。
    /// 变体只换细节位置、不换色板，所以它们可以逐格/逐片混用而不会出现亮度补丁。
    /// 第一个必须是空串（基础集）：任何缺失都回退到它。见 docs/art/AUTOTILE.md §5b。
    ///
    /// 老 6 条的套数刻意钉住（<see cref="PinnedLegacyGroundSets"/>）：磁盘上 soil/tilled/path 其实
    /// 已经各有 a/b/c 三套，但换上去会移动现有画面的像素，所以按用户口径"零回归"先不动。
    /// 新 6 条的套数在加载时<b>扫目录</b>得到，美术再补 <c>_d</c> 也不用改代码；
    /// 这张表对它们只是"扫不到时的兜底"（例如导出包里只剩 .ctex）。
    /// </summary>
    private static readonly string[][] GroundVariantSuffixes =
    {
        new[] { "", "_a", "_b", "_c", "_d", "_e", "_f" },   // grass (7 layouts)
        new[] { "" },               // soil
        new[] { "" },               // tilled
        new[] { "", "_b", "_c" },   // water
        new[] { "", "_b", "_c" },   // sand
        new[] { "" },               // path
        new[] { "", "_a", "_b", "_c" },   // grass_wild
        new[] { "", "_a", "_b", "_c" },   // grass_dry
        new[] { "", "_a", "_b", "_c" },   // mud
        new[] { "", "_a", "_b", "_c" },   // soil_loose
        new[] { "", "_a", "_b", "_c" },   // gravel
        new[] { "", "_a", "_b", "_c" },   // stone_floor
    };

    /// <summary>
    /// 侧壁贴图目录。三档文件名规则（档在前 / 档在后两种写法都吃）见
    /// <see cref="WallFileCandidates"/>，中档 canonical = 旧的 <c>&lt;slug&gt;_wall_{se,sw}.png</c>。
    /// </summary>
    private const string CliffDir = "res://Assets/tiles/iso/cliff/";

    /// <summary>把数组末尾那一条（没加载到的变体集）摘掉。</summary>
    private static Texture2D?[][] TrimLast(Texture2D?[][] sets, int index)
    {
        var kept = new Texture2D?[sets.Length - 1][];
        for (int i = 0, j = 0; i < sets.Length; i++)
        {
            if (i == index)
            {
                continue;
            }

            kept[j++] = sets[i];
        }

        return kept;
    }

    /// <summary>
    /// 某 kind 要加载哪几套变体集（<c>"" / "_a" / "_b" ...</c>）。
    /// <list type="bullet">
    ///   <item>老 6 种（约定"零回归"）：只用静态表，见 <see cref="PinnedLegacyGroundSets"/>；</item>
    ///   <item>新增 6 种：扫 <c>ground/&lt;slug&gt;/</c> 目录得到，美术补集不用改代码；
    ///   扫不到（导出包里只剩 .ctex 等）就退回静态表，保证离线也能跑。</item>
    /// </list>
    /// </summary>
    private string[] SuffixesFor(int kind, string slug)
    {
        bool pinned = PinnedLegacyGroundSets && kind < LegacyGroundKindCount;
        if (!pinned)
        {
            string[] discovered = DiscoverGroundVariantSuffixes(slug);
            if (discovered.Length > 0)
            {
                return discovered;
            }
        }

        return GroundVariantSuffixes[kind];
    }

    /// <summary>
    /// 扫目录得出变体集后缀：<c>&lt;slug&gt;_wNN.png</c> 是基础集（后缀空串，永远排第一），
    /// <c>&lt;slug&gt;_a_wNN.png</c> 之类的后缀按字典序跟在后面。
    /// 基础集一张都没有时返回空数组（让调用方退回静态表，别把整片地都弄丢）。
    /// </summary>
    private string[] DiscoverGroundVariantSuffixes(string slug)
    {
        string dir = $"{_groundDir}{slug}/";
        string[] names;
        try
        {
            names = DirAccess.GetFilesAt(dir);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[iso] cannot scan {dir}: {e.Message}");
            return System.Array.Empty<string>();
        }

        if (names is null || names.Length == 0)
        {
            return System.Array.Empty<string>();
        }

        var others = new List<string>();
        bool hasBase = false;
        foreach (string name in names)
        {
            if (!TryWangPrefix(name, out string prefix) || !prefix.StartsWith(slug, StringComparison.Ordinal))
            {
                continue;
            }

            string suffix = prefix[slug.Length..];
            if (suffix.Length == 0)
            {
                hasBase = true;
                continue;
            }

            if (suffix[0] == '_' && !others.Contains(suffix))
            {
                others.Add(suffix);
            }
        }

        if (!hasBase)
        {
            return System.Array.Empty<string>();
        }

        others.Sort(StringComparer.Ordinal);
        var result = new string[others.Count + 1];
        result[0] = string.Empty;
        for (int i = 0; i < others.Count; i++)
        {
            result[i + 1] = others[i];
        }

        return result;
    }

    /// <summary>从 <c>&lt;prefix&gt;_wNN.png</c> 里取出 prefix；不是 wang 图就返回 <c>false</c>。只看文件名。</summary>
    /// <remarks>
    /// 2026-11-16 夜修正：原实现用 <c>stem.Length - 3</c> 锚定分隔符，只认一位数（<c>_w0</c>），
    /// 而磁盘上的 wang 图全是两位（<c>_w00.._w46</c>），于是<b>每一张都判负</b>，扫目录恒定返回空数组，
    /// 所有 kind 一直悄悄走静态表（新增 6 种能出 4 套是因为静态表也写了 4 套，日志看不出来）。
    /// 现在改成"从右找最后一个 <c>_</c>，其后必须是 <c>w</c> + 纯数字"。
    /// </remarks>
    private static bool TryWangPrefix(string fileName, out string prefix)
    {
        prefix = string.Empty;
        const string Ext = ".png";
        if (!fileName.EndsWith(Ext, StringComparison.Ordinal) || fileName.Length <= Ext.Length + 4)
        {
            return false;
        }

        string stem = fileName[..^Ext.Length];
        int sep = stem.LastIndexOf('_');
        if (sep <= 0 || sep + 2 >= stem.Length || stem[sep + 1] != 'w')
        {
            return false;
        }

        for (int i = sep + 2; i < stem.Length; i++)
        {
            if (!char.IsAsciiDigit(stem[i]))
            {
                return false;
            }
        }

        prefix = stem[..sep];
        return true;
    }

    /// <summary>
    /// 加载一个地面 kind 的所有变体集，返回 <c>[variant][wang]</c>。
    /// 逐张缺失时的回退顺序：本集 w00 → 程序化图集里同材质的一格。
    /// 变体集（vi&gt;0）一张都没加载到就整条丢弃，绝不留空洞。
    /// </summary>
    private Texture2D?[][] LoadGroundKind(string slug, int kind, Image atlas, string[] suffixes, ref int loaded, ref int missing)
    {
        var sets = new Texture2D?[suffixes.Length][];
        for (int vi = 0; vi < suffixes.Length; vi++)
        {
            sets[vi] = new Texture2D?[IsoWangTable.TileCount];
        }

        for (int vi = 0; vi < suffixes.Length; vi++)
        {
            Texture2D?[] set = sets[vi];
            string prefix = $"{slug}{suffixes[vi]}";

            for (int w = 0; w < IsoWangTable.TileCount; w++)
            {
                string path = $"{_groundDir}{slug}/{prefix}_w{w:00}.png";
                Texture2D? tex = LoadGroundPng(path);
                if (tex is not null)
                {
                    set[w] = tex;
                    loaded++;
                    continue;
                }

                if (vi > 0)
                {
                    // 变体集缺图：整条丢弃，后面统一回退基础集，不算错误。
                    continue;
                }

                // 兜底：老路径（把方格贴图斜切成菱形）。单张缺图不让整屏崩。
                missing++;
                if (w == 0)
                {
                    set[0] = MakeDiamond(atlas, AtlasFallbackColumn(kind));
                    GD.PushWarning($"[iso] missing ground tile {path}, fell back to sheared atlas cell");
                }
            }

            // 任何一张缺失都退回本集的 w00（同材质的纯内部块），至少颜色是对的。
            for (int w = 1; w < IsoWangTable.TileCount; w++)
            {
                set[w] ??= set[0];
            }

            if (vi > 0 && set[0] is null)
            {
                // 这套变体一张都没加载到 → 从可用列表里摘掉。
                sets = TrimLast(sets, vi);
                vi--;
            }
        }

        return sets;
    }

    /// <summary>
    /// 按路径取一张地面贴图。<c>res://</c> / <c>user://</c> 走导入系统（正常路径，语义与旧代码一致）；
    /// 其它绝对路径当"工程外试稿目录"处理，运行期解 PNG（只在 <c>groundKindDir</c> 验证时出现）。
    /// </summary>
    private static Texture2D? LoadGroundPng(string path)
    {
        if (path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal))
        {
            return GD.Load<Texture2D>(path);
        }

        if (!Godot.FileAccess.FileExists(path))
        {
            return null;
        }

        var image = new Image();
        if (image.Load(path) != Error.Ok || image.IsEmpty())
        {
            GD.PushWarning($"[iso][WARN] ground PNG unreadable: {path}");
            return null;
        }

        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// 程序化图集（<see cref="WorldRenderer.BuildAtlasImage"/>）只画了老 6 种地皮，
    /// 所以新 kind 缺图时借草地那一列当兜底——中性、且不会取到图集外的列。
    /// </summary>
    private static int AtlasFallbackColumn(int groundIndex)
        => WorldRenderer.ColumnOf(groundIndex < LegacyGroundKindCount ? GroundKindIndexToKind(groundIndex) : TileKind.Grass, 0);

    /// <summary>
    /// 读 config.json 的可选键 <c>groundKindDir</c>：覆盖地面贴图根目录（绝对路径也行）。
    /// 唯一用途是在工程外摆一套"故意缺几个 kind"的目录，验证「缺目录 → 回退草地 + 告警」这条路径；
    /// 常规游戏不要设。缺文件 / 坏 JSON / 键不存在都退回 <see cref="GroundDirDefault"/>，绝不抛。
    /// </summary>
    private void LoadGroundDirSetting()
    {
        _groundDir = GroundDirDefault;

        if (!Godot.FileAccess.FileExists(GameRoot.ConfigPath))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(GameRoot.ConfigPath));
            if (document.RootElement.TryGetProperty("groundKindDir", out JsonElement dir)
                && dir.ValueKind == JsonValueKind.String
                && dir.GetString() is { Length: > 0 } path)
            {
                _groundDir = path.TrimEnd('/', '\\') + "/";
                GD.Print($"[iso] ground kind override dir: {_groundDir} (default {GroundDirDefault})");
            }
        }
        catch (JsonException ex)
        {
            GD.PushWarning($"[config] {GameRoot.ConfigPath} is not valid JSON ({ex.Message}); ground kinds use {GroundDirDefault}");
        }
    }

    /// <summary>
    /// 读 config.json 的外观键：
    /// <list type="bullet">
    ///   <item><c>groundRim</c>（bool，默认 true）：整层菱形描边开关；</item>
    ///   <item><c>groundRimLight</c>（默认 <b>0</b>）/ <c>groundRimDark</c>（默认 <b>0</b>）：上下边的提亮/压暗增量；</item>
    ///   <item><c>groundShade</c>（默认 0.18）：世界坐标低频光照幅度，0 = 关；</item>
    ///   <item><c>groundBlendWidth</c>（默认 12，屏幕 px）：种类边界过渡带宽度，0 = 关；</item>
    ///   <item><c>groundSpecks</c>（默认 0.25）：细节散布密度（每格几个），0 = 关；</item>
    ///   <item><c>groundWarmth</c>（默认 0.10）：色温场幅度（暖/冷偏移，7 格斑），0 = 关；</item>
    ///   <item><c>groundContactShadow</c>（默认 0.16）：种类边界内侧的接触阴影不透明度，0 = 关；</item>
    ///   <item><c>waterNetPeriod / waterNetWidth / waterNetContrast</c>：焦散网的三参数。</item>
    /// </list>
    /// 描边范围夹在 0..0.5：0.5 以上描边会盖过贴图本身，0 以下会把颜色反号。
    /// 0.06/0.07（改造前的默认）在 zoom 1.5 的截图上就是"满地规则斜线"的来源；0.02/0.03 仍然看得出菱形网格，
    /// 所以 **2026-11-17 第四轮把默认压到 0**（描边整条下线，改由格内低频光照 + 边界过渡带表达体积）。
    /// 坏 JSON / 键不在 / 不是数字 → 保留默认值，绝不抛。
    /// </summary>
    private void LoadLookSettings()
    {
        _rimLight = 0f;
        _rimDark = 0f;
        _groundRim = true;
        _groundShade = 0.18f;
        _groundBlendWidth = 12f;
        _groundSpecks = 0.25f;
        _groundWarmth = 0.10f;
        _groundContactShadow = 0.16f;
        _waterNetPeriod = 1.6f;
        _waterNetWidth = 0.045f;
        _waterNetContrast = 0.3f;

        if (Godot.FileAccess.FileExists(GameRoot.ConfigPath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(GameRoot.ConfigPath));
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("groundRim", out JsonElement rimSwitch)
                    && (rimSwitch.ValueKind == JsonValueKind.True || rimSwitch.ValueKind == JsonValueKind.False))
                {
                    _groundRim = rimSwitch.ValueKind == JsonValueKind.True;
                }

                if (root.TryGetProperty("groundRimLight", out JsonElement light) && light.TryGetSingle(out float lightValue))
                {
                    _rimLight = Mathf.Clamp(lightValue, 0f, 0.5f);
                }

                if (root.TryGetProperty("groundRimDark", out JsonElement dark) && dark.TryGetSingle(out float darkValue))
                {
                    _rimDark = Mathf.Clamp(darkValue, 0f, 0.5f);
                }

                if (root.TryGetProperty("groundShade", out JsonElement shade) && shade.TryGetSingle(out float shadeValue))
                {
                    _groundShade = Mathf.Clamp(shadeValue, 0f, 0.5f);
                }

                if (root.TryGetProperty("groundBlendWidth", out JsonElement blend) && blend.TryGetSingle(out float blendValue))
                {
                    _groundBlendWidth = Mathf.Clamp(blendValue, 0f, 40f);
                }

                if (root.TryGetProperty("groundSpecks", out JsonElement specks) && specks.TryGetSingle(out float specksValue))
                {
                    _groundSpecks = Mathf.Clamp(specksValue, 0f, 1f);
                }

                if (root.TryGetProperty("groundWarmth", out JsonElement warmth) && warmth.TryGetSingle(out float warmthValue))
                {
                    _groundWarmth = Mathf.Clamp(warmthValue, 0f, 0.4f);
                }

                if (root.TryGetProperty("groundContactShadow", out JsonElement contact) && contact.TryGetSingle(out float contactValue))
                {
                    _groundContactShadow = Mathf.Clamp(contactValue, 0f, 0.4f);
                }

                if (root.TryGetProperty("grassTufts", out JsonElement tufts) && tufts.TryGetSingle(out float tuftValue))
                {
                    _grassTuftDensity = Mathf.Clamp(tuftValue, 0f, 1f);
                }

                if (root.TryGetProperty("grassTuftScale", out JsonElement tuftScale) && tuftScale.TryGetSingle(out float tuftScaleValue))
                {
                    _grassTuftScale = Mathf.Clamp(tuftScaleValue, 0.5f, 4f);
                }

                if (root.TryGetProperty("groundTint", out JsonElement tintElement)
                    && tintElement.ValueKind == JsonValueKind.String)
                {
                    _groundTint = ParseTint(tintElement.GetString(), _groundTint);
                }

                if (root.TryGetProperty("waterNetPeriod", out JsonElement period) && period.TryGetSingle(out float periodValue))
                {
                    _waterNetPeriod = Mathf.Clamp(periodValue, 0.5f, 4f);
                }

                if (root.TryGetProperty("waterNetWidth", out JsonElement width) && width.TryGetSingle(out float widthValue))
                {
                    _waterNetWidth = Mathf.Clamp(widthValue, 0.01f, 0.4f);
                }

                if (root.TryGetProperty("waterNetContrast", out JsonElement contrast) && contrast.TryGetSingle(out float contrastValue))
                {
                    _waterNetContrast = Mathf.Clamp(contrastValue, 0f, 1f);
                }
            }
            catch (JsonException ex)
            {
                GD.PushWarning($"[config] {GameRoot.ConfigPath} is not valid JSON ({ex.Message}); look settings use defaults");
            }
        }

        if (!_groundRim)
        {
            _rimLight = 0f;
            _rimDark = 0f;
        }

        GD.Print(!_groundRim
            ? "[iso] ground rim: off (config groundRim=false, 0 draw calls for the rim layer)"
            : $"[iso] ground rim: light +{_rimLight:0.###} dark -{_rimDark:0.###}"
              + (_rimLight <= 0f && _rimDark <= 0f
                  ? " (off by default since 2026-11-17; set groundRimLight/Dark to restore)"
                  : " (2 lines/cell, inset pad 0.5px + half line)"));

        // A / C / D 三项的生效值（和美术对表用：截图前先看这三行）。
        GD.Print(_groundShade > 0f
            ? $"[iso] ground shade: amp {_groundShade:0.###} (3 & 9 cell octaves, fine {ShadeFineWeight:0.##}/coarse {ShadeCoarseWeight:0.##},"
              + " world-corner bilinear lattice, baked once)"
            : "[iso] ground shade: off (config groundShade=0, per-cell wobble +/-2.5% restored)");

        GD.Print(_groundBlendWidth > 0f
            ? $"[iso] ground blend: {_groundBlendWidth:0.#}px inward band (alpha {BlendEdgeAlpha:0.##}->0, neighbour median colour)"
            : "[iso] ground blend: off (config groundBlendWidth=0)");

        GD.Print(_groundSpecks > 0f
            ? $"[iso] ground specks: density {_groundSpecks:0.###}/cell ({SpeckStride:0.##}-cell candidates x {_groundSpecks * SpeckStride * SpeckStride:0.####})"
              + $", {SpeckMinPx:0.#}-{SpeckMaxPx:0.#}px, world-coord hash"
            : "[iso] ground specks: off (config groundSpecks=0)");

        // A2 / E 两项的生效值（第五轮）。色温那行带场统计，接触阴影那行带边数统计 —— 都对账用。
        GD.Print(_groundWarmth > ShadeEpsilon
            ? $"[iso] ground warmth: amp {_groundWarmth:0.00} (7 cell octave)"
              + $" warm ({WarmTint.R:0.##},{WarmTint.G:0.##},{WarmTint.B:0.##}) / cool"
              + $" ({CoolTint.R:0.##},{CoolTint.G:0.##},{CoolTint.B:0.##}) per corner, salt 0x{WarmthSalt:X4}"
            : "[iso] ground warmth: off (config groundWarmth=0)");

        GD.Print(_groundContactShadow > ShadeEpsilon
            ? $"[iso] ground contact: {ContactShadowScreenPx:0}px, alpha {_groundContactShadow:0.00}"
              + $" -> 0 inward, colour ({ContactShadowColor.R:0.##},{ContactShadowColor.G:0.##},{ContactShadowColor.B:0.##}),"
              + " after blend / before shade, shared edge table"
            : "[iso] ground contact: off (config groundContactShadow=0)");

        GD.Print($"[iso] water net: period {_waterNetPeriod:0.##}x width {_waterNetWidth:0.###} "
                 + $"contrast {_waterNetContrast:0.##} (config waterNetPeriod/Width/Contrast)");

        GD.Print(_groundTint.R >= 0.999f && _groundTint.G >= 0.999f && _groundTint.B >= 0.999f
            ? "[iso] ground tint: none (config groundTint unset)"
            : $"[iso] ground tint: ({_groundTint.R:0.###},{_groundTint.G:0.###},{_groundTint.B:0.###})"
              + " multiplied into each ground tile's modulate (lighting/blend/walls/props unaffected)");
    }

    /// <summary>
    /// 解析 <c>groundTint</c>：支持 <c>"#rrggbb"</c> / <c>"rrggbb"</c> / <c>"r,g,b"</c>（0..1）。
    /// 解析失败时保留 <paramref name="fallback"/> 并打警告 —— 配色试错不该因为一个手滑的字符串把渲染停掉。
    /// </summary>
    private static Color ParseTint(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        string raw = text!.Trim();

        if (raw.Contains(','))
        {
            string[] parts = raw.Split(',');
            if (parts.Length == 3
                && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
                && float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
            {
                return new Color(Mathf.Clamp(r, 0f, 2f), Mathf.Clamp(g, 0f, 2f), Mathf.Clamp(b, 0f, 2f));
            }

            GD.PushWarning($"[iso] bad groundTint '{raw}', expected r,g,b in 0..1");
            return fallback;
        }

        string hex = raw.TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int packed))
        {
            return new Color(
                ((packed >> 16) & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f,
                (packed & 0xFF) / 255f);
        }

        GD.PushWarning($"[iso] bad groundTint '{raw}', expected #rrggbb or r,g,b");
        return fallback;
    }

    /// <summary>
    /// 每种地面算一次「顶面中位色」，再派生出亮描边色 / 暗描边色 / 草皮盖色。
    /// 取样用 wang 内部块（index 0 = 四周同材质 = 最接近纯色块的那张），所以描边颜色不会因为
    /// 岛缘那张带暗边的瓦而偏深。缺图（美术没交这个 kind）的 kind 借 grass 的中位色，和贴图的兜底路径一致。
    /// </summary>
    private void ComputeGroundColours()
    {
        Color grass = new(0.54f, 0.80f, 0.09f); // 兜底：连 grass 都没图时用的绿地色

        for (int kind = 0; kind < GroundKindCount; kind++)
        {
            Texture2D? probe = null;
            if (kind < _ground.Length && _ground[kind].Length > 0 && _ground[kind][0].Length > 0)
            {
                probe = _ground[kind][0][0];
            }

            if (probe is null)
            {
                _groundMedian[kind] = kind == 0 ? grass : _groundMedian[0];
                continue;
            }

            _groundMedian[kind] = MedianColor(probe);
            if (kind == 0)
            {
                grass = _groundMedian[0];
            }
        }

        RefreshRimColours();

        // 过渡带（C）的颜色取的就是这里的中位色，所以水/沙两项一起打出来，A/B 时能对得上。
        // 描边归零（第四轮默认）时不再打两个 rim 颜色 —— 那两行是给描边调试看的。
        GD.Print($"[iso] ground median: grass #{_groundMedian[0].ToHtml(false)}"
                  + $" water #{_groundMedian[3].ToHtml(false)} sand #{_groundMedian[4].ToHtml(false)}"
                  + (_rimLight <= 0f && _rimDark <= 0f
                      ? " (rim off)"
                      : $" -> rim #{_rimLightColor[0].ToHtml(false)} / #{_rimDarkColor[0].ToHtml(false)}"));
    }

    /// <summary>
    /// 重新算每个地面的亮/暗描边色 = 中位色 ± (<c>groundRimLight/Dark</c> × <see cref="_rimContrast"/>)。
    /// 只在 <see cref="BakeTiles"/> 与 <see cref="UpdateRimForZoom"/> 换档时调用，绘制期间只查表。
    /// </summary>
    private void RefreshRimColours()
    {
        float light = _rimLight * _rimContrast;
        float dark = _rimDark * _rimContrast;

        for (int kind = 0; kind < GroundKindCount; kind++)
        {
            _rimLightColor[kind] = _groundMedian[kind].Lightened(light);
            _rimDarkColor[kind] = _groundMedian[kind].Darkened(dark);
        }
    }

    /// <summary>
    /// 按当前**屏幕**缩放决定描边档（1px / 2px）与对比倍率，换档时重算描边色并打一行日志。
    ///
    /// 为什么不能只看 <c>Camera2D.Zoom</c>：相机 zoom 是 canvas 变换的一部分，不在节点的
    /// <c>GlobalTransform</c> 里；<see cref="CanvasItem.GetGlobalTransformWithCanvas"/> 才是
    /// 「本地坐标 → 屏幕」的完整变换（相机 zoom + 节点缩放都在里面）。
    /// 拿不到（没有相机 / 变换退化）时按 1 算，于是画面和改造前一样。
    /// </summary>
    private void UpdateRimForZoom()
    {
        float scale = Mathf.Abs(GetGlobalTransformWithCanvas().Scale.X);
        if (scale < 0.0001f || !float.IsFinite(scale))
        {
            scale = 1f;
        }

        _effZoom = scale;

        // 描边默认已归零（第四轮），此时只需要 _effZoom —— 过渡带宽度和细节点尺寸都按它换算成屏幕 px；
        // 不必再算档位、重算描边色、打 tier 日志（那两个增量都是 0，打出来也没信息量）。
        if (_rimLight <= 0f && _rimDark <= 0f)
        {
            return;
        }

        float tileScreenPx = TileW * scale;
        int bucket = tileScreenPx < RimNarrowTileScreenPx ? 1 : 0;
        if (bucket == _rimBucket)
        {
            return;
        }

        _rimBucket = bucket;
        _rimContrast = bucket == 1 ? RimNarrowContrast : 1f;
        _rimLineWidth = (bucket == 1 ? RimNarrowScreenPx : RimScreenPx) / scale;
        RefreshRimColours();

        GD.Print($"[iso] ground rim tier: zoom {scale:0.###}x -> {tileScreenPx:0.#} px/cell -> "
                  + $"{(bucket == 1 ? "narrow" : "normal")} {_rimLineWidth * scale:0.##}px line"
                  + $" (local {_rimLineWidth:0.###}px, contrast x{_rimContrast:0.##}"
                  + $" => light +{_rimLight * _rimContrast:0.####} dark -{_rimDark * _rimContrast:0.####})");
    }

    /// <summary>
    /// 一张菱形瓦的中位色：只统计不透明度 ≥250 的像素（半透明的抗锯齿角会把中位色往黑里拽），
    /// 每个通道各取中位数（不是均值 —— 均值会被草地上的暗点拉低，描边会发灰）。
    /// </summary>
    private static Color MedianColor(Texture2D texture)
    {
        Image? image = texture.GetImage();
        if (image is null || image.IsEmpty())
        {
            return Colors.White;
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        byte[] data = image.GetData();
        int count = data.Length / 4;
        var red = new byte[count];
        var green = new byte[count];
        var blue = new byte[count];
        int used = 0;

        for (int i = 0; i + 3 < data.Length; i += 4)
        {
            if (data[i + 3] < 250)
            {
                continue;
            }

            red[used] = data[i];
            green[used] = data[i + 1];
            blue[used] = data[i + 2];
            used++;
        }

        if (used == 0)
        {
            return Colors.White;
        }

        System.Array.Sort(red, 0, used);
        System.Array.Sort(green, 0, used);
        System.Array.Sort(blue, 0, used);

        int mid = used / 2;
        return new Color(red[mid] / 255f, green[mid] / 255f, blue[mid] / 255f);
    }

    private void BakeTiles()
    {
        LoadGroundDirSetting();
        LoadLookSettings();

        Image atlas = WorldRenderer.BuildAtlasImage();
        int missing = 0;
        int loaded = 0;

        _ground = new Texture2D?[GroundKindCount][][];
        int variantSets = 0;
        int groundKindsMissing = 0;

        // 开发期断言：枚举顺序 ↔ 渲染索引必须一一对应，否则美术的目录会和图集错位。
        for (int index = 0; index < GroundKindCount; index++)
        {
            if (KindToGroundIndex(GroundKindIndexToKind(index)) != index)
            {
                GD.PushError($"[iso] ground kind index {index} ({GroundSlugs[index]}) does not round-trip through TileKind");
            }
        }

        for (int kind = 0; kind < GroundKindCount; kind++)
        {
            string slug = GroundSlugs[kind];
            string dir = $"{_groundDir}{slug}/";

            // 缺目录兜底：美术还没画的 kind 借草地的图集（共享引用，不复制贴图），
            // 刷出来是草地，而不是黑洞。草地自己缺了就只能是空集（后面每格都会走程序化兜底）。
            if (!DirAccess.DirExistsAbsolute(dir))
            {
                groundKindsMissing++;
                _ground[kind] = kind == 0 ? System.Array.Empty<Texture2D?[]>() : _ground[0];
                GD.PushWarning($"[iso] ground kind '{slug}' missing, falling back to grass");
                GD.Print($"[iso] ground kind {slug}: MISSING dir -> fallback grass");
                continue;
            }

            string[] suffixes = SuffixesFor(kind, slug);
            _ground[kind] = LoadGroundKind(slug, kind, atlas, suffixes, ref loaded, ref missing);
            variantSets += _ground[kind].Length;
            GD.Print($"[iso] ground kind {slug}: {_ground[kind].Length} set(s) [{string.Join(" ", suffixes)}]");
        }

        GD.Print($"[iso] ground kinds: {GroundKindCount - groundKindsMissing}/{GroundKindCount} on disk, {groundKindsMissing} fell back to grass");

        // 顶面中位色 -> 描边色 / 草皮条色。必须在 ground 数组填完之后算。
        ComputeGroundColours();

        // 世界坐标低频光照场 + 细节散布表：一次性构建（也在这里决定要不要关掉逐格扰动）。
        // 依赖 _groundMedian（细节点颜色 = 本格中位色 ±10%），所以必须排在 ComputeGroundColours 之后。
        BuildGroundLighting();

        LoadWallTierSettings();

        _wallSeThin = new Texture2D?[GroundKindCount];
        _wallSeMid = new Texture2D?[GroundKindCount];
        _wallSeThick = new Texture2D?[GroundKindCount];
        _wallSwThin = new Texture2D?[GroundKindCount];
        _wallSwMid = new Texture2D?[GroundKindCount];
        _wallSwThick = new Texture2D?[GroundKindCount];

        var loadedPerTier = new int[WallTierCount];
        for (int kind = 0; kind < GroundKindCount; kind++)
        {
            string slug = GroundSlugs[kind];
            _wallSeThin[kind] = LoadWallTier(slug, "se", WallTierThin, loadedPerTier);
            _wallSeMid[kind] = LoadWallTier(slug, "se", WallTierMid, loadedPerTier);
            _wallSeThick[kind] = LoadWallTier(slug, "se", WallTierThick, loadedPerTier);
            _wallSwThin[kind] = LoadWallTier(slug, "sw", WallTierThin, loadedPerTier);
            _wallSwMid[kind] = LoadWallTier(slug, "sw", WallTierMid, loadedPerTier);
            _wallSwThick[kind] = LoadWallTier(slug, "sw", WallTierThick, loadedPerTier);
        }

        // 槽位 = 材质 × 朝向 × 档。某槽自己那档没图、但回退链能捞到图 → 记一个 fallback。
        // 三档都没有（美术还没上手）不算 fallback，那是"这格材质没有壁"，现状就是这样。
        int slots = GroundKindCount * 2 * WallTierCount;
        int fallbackSlots = 0;
        int fallbackKinds = 0;
        for (int kind = 0; kind < GroundKindCount; kind++)
        {
            bool kindTouched = false;
            for (int side = 0; side < 2; side++)
            {
                bool se = side == 0;
                for (int tier = 0; tier < WallTierCount; tier++)
                {
                    if (WallTexture(se, tier, kind) is not null)
                    {
                        continue;
                    }

                    if (ResolveWallTexture(se, kind, tier, out _) is null)
                    {
                        continue;
                    }

                    fallbackSlots++;
                    kindTouched = true;
                }
            }

            if (kindTouched)
            {
                fallbackKinds++;
            }
        }

        string modeText = _wallTierFixedMode ? $"fixed:{WallTierName[_wallTierFixed]}" : "depth";
        string waterText = _waterWalls ? $"<={WallTierName[Mathf.Min(_waterWallTier, _wallMaxTier)]}" : "none";
        GD.Print($"[iso] wall tiers: thin={loadedPerTier[WallTierThin]} mid={loadedPerTier[WallTierMid]} thick={loadedPerTier[WallTierThick]}"
                  + $" (fallbacks {fallbackSlots}/{slots} slots, {fallbackKinds} kinds; mode {modeText}, max {WallTierName[_wallMaxTier]}, water {waterText})");

        _treeTrunk = new Texture2D?[PropShapes];
        _treeCanopy = new Texture2D?[PropShapes];
        _rocks = new Texture2D?[PropShapes];
        for (int s = 0; s < PropShapes; s++)
        {
            char tag = s == 0 ? 'a' : 'b';
            _treeTrunk[s] = LoadProp($"tree_{tag}_trunk", atlas, WorldRenderer.PropColumn(PropKind.Tree));
            _treeCanopy[s] = LoadProp($"tree_{tag}_canopy", atlas, null);
            _rocks[s] = LoadProp($"rock_{tag}", atlas, WorldRenderer.PropColumn(PropKind.Rock));
        }

        _weed = LoadProp("weed", atlas, WorldRenderer.PropColumn(PropKind.Weed));

        _playerDown = LoadFrames("down");
        _playerUp = LoadFrames("up");
        _playerSide = LoadFrames("side");

        int walls = loadedPerTier[WallTierThin] + loadedPerTier[WallTierMid] + loadedPerTier[WallTierThick];
        GD.Print($"[iso] tiles: {loaded} wang PNGs on disk across {variantSets} variant set(s), {walls} wall PNGs, {missing} fallback(s)");
    }

    // ------------------------------------------------------- 侧壁：配置 / 加载 / 选档

    /// <summary>
    /// 读 config.json 的四个侧壁开关：
    /// <list type="bullet">
    ///   <item><c>wallTierMode</c>：<c>"depth"</c>（默认，按落差深度选档）或 <c>"fixed"</c>（所有边统一用一档）；</item>
    ///   <item><c>wallTierFixed</c>：fixed 模式用哪档，<c>"thin"|"mid"|"thick"</c>，默认 mid；</item>
    ///   <item><c>wallMaxTier</c>：自动档的上限，<c>"thin"|"mid"|"thick"</c>，默认 mid。</item>
    ///   <item><c>waterWallTier</c>：水格要不要长壁 —— <c>"none"</c>（默认，不长）/ <c>"thin"</c>（最多一条极薄）
    ///         / <c>"auto"</c>（旧行为：和陆地一样按落差选档）。</item>
    ///   <item><c>wallTierDir</c>：可选，覆盖搜索目录（绝对路径也行）。给美术在工程外放试稿 / 给截图验几何用。</item>
    /// </list>
    /// 文件缺失或 JSON 坏了就用默认值，绝不抛。
    /// </summary>
    private void LoadWallTierSettings()
    {
        _wallTierFixedMode = false;
        _wallTierFixed = WallTierMid;
        _wallMaxTier = WallTierMid;
        _wallTierDirs = new[] { CliffDir };
        _waterWalls = false;
        _waterWallTier = WallTierThin;

        if (!Godot.FileAccess.FileExists(GameRoot.ConfigPath))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(GameRoot.ConfigPath));
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("wallTierMode", out JsonElement mode) && mode.ValueKind == JsonValueKind.String)
            {
                string name = mode.GetString() ?? "depth";
                if (string.Equals(name, "fixed", StringComparison.OrdinalIgnoreCase))
                {
                    _wallTierFixedMode = true;
                }
                else if (!string.Equals(name, "depth", StringComparison.OrdinalIgnoreCase))
                {
                    GD.PushWarning($"[config] unknown wallTierMode '{name}'; expected 'depth' or 'fixed' -> using depth");
                }
            }

            if (root.TryGetProperty("wallTierFixed", out JsonElement fixedTier) && fixedTier.ValueKind == JsonValueKind.String)
            {
                int tier = TierFromName(fixedTier.GetString());
                if (tier < 0)
                {
                    GD.PushWarning($"[config] unknown wallTierFixed '{fixedTier.GetString()}'; expected thin/mid/thick -> using mid");
                }
                else
                {
                    _wallTierFixed = tier;
                }
            }

            if (root.TryGetProperty("wallMaxTier", out JsonElement maxTier) && maxTier.ValueKind == JsonValueKind.String)
            {
                int tier = TierFromName(maxTier.GetString());
                if (tier < 0)
                {
                    GD.PushWarning($"[config] unknown wallMaxTier '{maxTier.GetString()}'; expected thin/mid/thick -> using mid");
                }
                else
                {
                    _wallMaxTier = tier;
                }
            }

            if (root.TryGetProperty("waterWallTier", out JsonElement waterTier) && waterTier.ValueKind == JsonValueKind.String)
            {
                string name = waterTier.GetString() ?? "none";
                if (string.Equals(name, "none", StringComparison.OrdinalIgnoreCase))
                {
                    _waterWalls = false;
                }
                else if (string.Equals(name, "thin", StringComparison.OrdinalIgnoreCase))
                {
                    _waterWalls = true;
                    _waterWallTier = WallTierThin;
                }
                else if (string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    // 旧行为：水格和陆地一样走 TierFor（落差 3 → thick → 被 wallMaxTier 夹到 mid）。
                    _waterWalls = true;
                    _waterWallTier = WallTierThick;
                }
                else
                {
                    GD.PushWarning($"[config] unknown waterWallTier '{name}'; expected none/thin/auto -> water walls off");
                }
            }

            if (root.TryGetProperty("wallTierDir", out JsonElement overrideDir)
                && overrideDir.ValueKind == JsonValueKind.String
                && overrideDir.GetString() is { Length: > 0 } dir)
            {
                _wallTierDirs = new[] { dir.TrimEnd('/', '\\'), CliffDir };
                GD.Print($"[iso] wall tier override dir: {dir} (searched before {CliffDir})");
            }
        }
        catch (JsonException ex)
        {
            GD.PushWarning($"[config] {GameRoot.ConfigPath} is not valid JSON ({ex.Message}); wall tiers use defaults");
        }
    }

    /// <summary>"thin"/"mid"/"thick" → 档位常量；不认识返回 -1。</summary>
    private static int TierFromName(string? name)
    {
        for (int tier = 0; tier < WallTierCount; tier++)
        {
            if (string.Equals(name, WallTierName[tier], StringComparison.OrdinalIgnoreCase))
            {
                return tier;
            }
        }

        return -1;
    }

    /// <summary>
    /// 某档某朝向的候选文件名，按优先级排列。美术的两种写法都吃（档在前 / 档在后）；
    /// 中档的 canonical 是旧名字 <c>&lt;slug&gt;_wall_se.png</c>，再认 <c>_wall_mid_se</c> / <c>_wall_se_mid</c>。
    /// </summary>
    private static string[] WallFileCandidates(string slug, string side, int tier)
    {
        if (tier == WallTierMid)
        {
            return new[]
            {
                $"{slug}_wall_{side}.png",
                $"{slug}_wall_mid_{side}.png",
                $"{slug}_wall_{side}_mid.png",
            };
        }

        string tierName = WallTierName[tier];
        return new[] { $"{slug}_wall_{tierName}_{side}.png", $"{slug}_wall_{side}_{tierName}.png" };
    }

    /// <summary>加载「某材质 × 某朝向 × 某档」；一个候选都没命中返回 null（绘制时走回退链）。</summary>
    private Texture2D? LoadWallTier(string slug, string side, int tier, int[] loadedPerTier)
    {
        foreach (string fileName in WallFileCandidates(slug, side, tier))
        {
            Texture2D? tex = TryLoadWallPng(_wallTierDirs, fileName, out string path);
            if (tex is null)
            {
                continue;
            }

            // 开发期断言：画布高必须 = 32(顶面菱形) + 该档壁高，否则画出来必然错位。
            // 只警告不崩 —— 一张图的尺寸不对不该让整屏黑掉。
            int expected = WallTopHeight + WallTierHeight[tier];
            if (tex.GetHeight() != expected)
            {
                GD.PushWarning($"[iso][WARN] wall tier size mismatch: {path} h={tex.GetHeight()} expected={expected}");
            }

            loadedPerTier[tier]++;
            return tex;
        }

        return null;
    }

    /// <summary>
    /// 在 <paramref name="searchDirs"/> 里按顺序找第一张存在的 PNG。
    /// <c>res://</c>/<c>user://</c> 走资源加载（要 .import）；绝对路径直接读盘，
    /// 所以美术把试稿丢在 <c>E:\AsahiMono\tmp\</c> 里也能被渲染器看到，不必先进 res://。
    /// </summary>
    private static Texture2D? TryLoadWallPng(string[] searchDirs, string fileName, out string foundPath)
    {
        foundPath = "";
        foreach (string dir in searchDirs)
        {
            string path = $"{dir}/{fileName}";
            if (dir.StartsWith("res://", StringComparison.Ordinal) || dir.StartsWith("user://", StringComparison.Ordinal))
            {
                if (!ResourceLoader.Exists(path))
                {
                    continue;
                }

                Texture2D? tex = GD.Load<Texture2D>(path);
                if (tex is null)
                {
                    continue;
                }

                foundPath = path;
                return tex;
            }

            if (!Godot.FileAccess.FileExists(path))
            {
                continue;
            }

            var image = new Image();
            if (image.Load(path) != Error.Ok || image.IsEmpty())
            {
                GD.PushWarning($"[iso][WARN] wall tier PNG unreadable: {path}");
                continue;
            }

            foundPath = path;
            return ImageTexture.CreateFromImage(image);
        }

        return null;
    }

    /// <summary>取某朝向某档某材质的贴图（可能为 null = 该档没这张图，不代表不画）。</summary>
    private Texture2D? WallTexture(bool se, int tier, int kind) => (se, tier) switch
    {
        (true, WallTierThin) => _wallSeThin[kind],
        (true, WallTierThick) => _wallSeThick[kind],
        (true, _) => _wallSeMid[kind],
        (false, WallTierThin) => _wallSwThin[kind],
        (false, WallTierThick) => _wallSwThick[kind],
        _ => _wallSwMid[kind],
    };

    /// <summary>
    /// 回退链：请求档 → mid → thin → null。null 就是不画，等同"这张材质根本没有任何侧壁贴图"的现状。
    /// 回退上来的图不再做尺寸断言（它自己那档已经检查过了），所以不会重复报警。
    /// </summary>
    private Texture2D? ResolveWallTexture(bool se, int kind, int tier, out int resolvedTier)
    {
        int[] chain = tier switch
        {
            WallTierThick => WallChainThick,
            WallTierThin => WallChainThin,
            _ => WallChainMid,
        };

        foreach (int candidate in chain)
        {
            Texture2D? tex = WallTexture(se, candidate, kind);
            if (tex is not null)
            {
                resolvedTier = candidate;
                return tex;
            }
        }

        resolvedTier = tier;
        return null;
    }

    /// <summary>
    /// 该边用哪一档：fixed 模式直接返回配置的档；depth 模式按 <see cref="DepthToTier"/> 查表。
    /// 最后一步统一被 <see cref="wallMaxTier">_wallMaxTier</see> 夹一次 —— 默认 mid，
    /// 于是"外海/地图边沿 = thick(72px)"不再是默认观感（那圈等高的 72px 墙正是"托盘边"的来源）。
    /// 想要旧的满配厚壁就把 <c>config.json</c> 的 <c>wallMaxTier</c> 设成 <c>thick</c>。
    /// </summary>
    private int TierFor(int depth)
    {
        int tier = _wallTierFixedMode
            ? _wallTierFixed
            : (depth <= 0 ? WallTierMid : DepthToTier[Mathf.Min(depth, 3)]);

        return Mathf.Min(tier, _wallMaxTier);
    }

    private static Texture2D? LoadProp(string slug, Image atlas, int? fallbackColumn)
    {
        Texture2D? tex = GD.Load<Texture2D>($"{PropDir}{slug}.png");
        if (tex is not null || fallbackColumn is null)
        {
            return tex;
        }

        GD.PushWarning($"[iso] missing prop {slug}.png, fell back to atlas prop");
        return Upright(atlas, fallbackColumn.Value);
    }

    /// <summary>GroundKind 索引 → TileKind（跳过 Void，因为 Void 永不绘制）。BakeTiles 会校验它和反函数互逆。</summary>
    private static TileKind GroundKindIndexToKind(int index) => index switch
    {
        0 => TileKind.Grass,
        1 => TileKind.Soil,
        2 => TileKind.TilledSoil,
        3 => TileKind.Water,
        4 => TileKind.Sand,
        5 => TileKind.Path,
        6 => TileKind.GrassWild,
        7 => TileKind.GrassDry,
        8 => TileKind.Mud,
        9 => TileKind.SoilLoose,
        10 => TileKind.Gravel,
        11 => TileKind.StoneFloor,
        _ => TileKind.Void,
    };

    /// <summary>
    /// 磁盘图集 slug（<c>atlas_grass_wild.png</c> 去掉 <c>atlas_</c> 前缀后的 <c>grass_wild</c>）
    /// → <see cref="TileKind"/>。走 <see cref="GroundSlugs"/>，**不是** <c>Enum.Parse</c>：
    /// slug <c>tilled</c> 对应的枚举名是 <c>TilledSoil</c>，直接 Enum.Parse 会解析失败。
    ///
    /// 谁用：<c>mapSource=refblock_scene</c> 那条路（<see cref="RefblockSceneLoader"/>）——
    /// 编辑器 <c>Ground</c> 层只存 source id + 图集坐标，kind 必须靠 source 的贴图文件名反查。
    /// 认不出返回 false，调用方自己决定怎么兜（现在是按草地画 + 打警告 + 计数）。
    /// </summary>
    public static bool TryKindFromGroundSlug(string slug, out TileKind kind)
    {
        kind = TileKind.Grass;
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        // 文件名极少见地会带空格/连字符（美术手改过图），统一成下划线再比。
        string needle = slug.Trim().Replace('-', '_').Replace(' ', '_');
        for (int index = 0; index < GroundSlugs.Length; index++)
        {
            if (string.Equals(GroundSlugs[index], needle, StringComparison.OrdinalIgnoreCase))
            {
                kind = GroundKindIndexToKind(index);
                return true;
            }
        }

        return false;
    }

    private static Texture2D Upright(Image atlas, int column)
        => ImageTexture.CreateFromImage(atlas.GetRegion(new Rect2I(column * WorldRenderer.TileSize, 0, WorldRenderer.TileSize, WorldRenderer.TileSize)));

    private static Texture2D[] LoadFrames(string facing)
    {
        // 只取第 1 / 3 帧做两帧循环：够表达"在走"，也不让包体多几十张贴图。
        // 帧文件来自 Assets/chars/player/player_walk_<facing>_<n>.png（ansimuz CC0）。
        var frames = new Texture2D[2];
        frames[0] = GD.Load<Texture2D>($"{CharsDir}player_walk_{facing}_1.png");
        frames[1] = GD.Load<Texture2D>($"{CharsDir}player_walk_{facing}_3.png");
        return frames;
    }

    /// <summary>把 32x32 方格贴图重采样成 64x32 菱形（双线性，避免 45° 条纹）。</summary>
    private static ImageTexture MakeDiamond(Image atlas, int column)
    {
        var dst = Image.CreateEmpty(TileW, TileH, false, Image.Format.Rgba8);
        int ox = column * WorldRenderer.TileSize;
        const int source = WorldRenderer.TileSize;

        for (int py = 0; py < TileH; py++)
        {
            for (int px = 0; px < TileW; px++)
            {
                double dx = px - (TileW - 1) * 0.5;
                double dy = py - (TileH - 1) * 0.5;
                double u = dx / (TileW * 0.5);
                double v = dy / (TileH * 0.5);

                if (System.Math.Abs(u) + System.Math.Abs(v) > 1.0)
                {
                    dst.SetPixel(px, py, Colors.Transparent);
                    continue;
                }

                double fx = (u + v + 1.0) * 0.5 * (source - 1);
                double fy = (v - u + 1.0) * 0.5 * (source - 1);
                dst.SetPixel(px, py, SampleBilinear(atlas, ox, fx, fy, source));
            }
        }

        return ImageTexture.CreateFromImage(dst);
    }

    private static Color SampleBilinear(Image img, int ox, double fx, double fy, int size)
    {
        int x0 = Mathf.Clamp((int)fx, 0, size - 1);
        int y0 = Mathf.Clamp((int)fy, 0, size - 1);
        int x1 = System.Math.Min(x0 + 1, size - 1);
        int y1 = System.Math.Min(y0 + 1, size - 1);
        float tx = (float)(fx - x0);
        float ty = (float)(fy - y0);

        Color c00 = img.GetPixel(ox + x0, y0);
        Color c10 = img.GetPixel(ox + x1, y0);
        Color c01 = img.GetPixel(ox + x0, y1);
        Color c11 = img.GetPixel(ox + x1, y1);

        return c00.Lerp(c10, tx).Lerp(c01.Lerp(c11, tx), ty);
    }

    // ---------------------------------------------------------------- 投影

    /// <summary>网格坐标（可为小数）→ 屏幕坐标。线性投影，方格直线在屏幕上也还是直线。</summary>
    public static Vector2 GridToScreen(float x, float y)
        => new((x - y) * HalfW, (x + y) * HalfH);

    /// <summary>屏幕坐标 → 网格坐标（<see cref="GridToScreen"/> 的逆）。</summary>
    public static Vector2 ScreenToGrid(Vector2 screen)
    {
        // screen.x = (x - y) * HalfW ; screen.y = (x + y) * HalfH
        float a = screen.X / HalfW;
        float b = screen.Y / HalfH;
        return new Vector2((a + b) * 0.5f, (b - a) * 0.5f);
    }

    // ---------------------------------------------------------------- autotile

    /// <summary>
    /// 四条边 → 掩码位。位序与 <c>docs/art/AUTOTILE.md</c>、<c>IsoWangTable</c> 一致：
    /// 邻居 (0,-1) 在屏幕右上，共边是本格的 ne 面，因此占 bit0。
    /// </summary>
    private static readonly (int Dx, int Dy, int Bit)[] WangSides =
    {
        (0, -1, 0),   // ne 边（右上）
        (-1, 0, 1),   // nw 边（左上）
        (1, 0, 2),    // se 边（右下）
        (0, 1, 3),    // sw 边（左下）
    };

    /// <summary>四个角 → 掩码位：只有两侧边都暴露时这个角才可见（生成器已折掉其余情况）。</summary>
    private static readonly (int Dx, int Dy, int Bit)[] WangCorners =
    {
        (-1, -1, 4),  // top    顶点（上）
        (-1, 1, 5),   // left   顶点（左）
        (1, 1, 6),    // bottom 顶点（下）
        (1, -1, 7),   // right  顶点（右）
    };

    /// <summary>
    /// 8 邻接"暴露掩码"：某方向邻居材质不同（或出界 = Void）就把那一位打开。
    /// 出界算暴露，所以地图边缘/岛的四周会自动拿到厚土剖面那圈暗带。
    /// </summary>
    private int WangMask(int x, int y, TileKind self)
    {
        int mask = 0;
        foreach ((int dx, int dy, int bit) in WangSides)
        {
            if (NeighbourTile(x + dx, y + dy) != self)
            {
                mask |= 1 << bit;
            }
        }

        foreach ((int dx, int dy, int bit) in WangCorners)
        {
            if (NeighbourTile(x + dx, y + dy) != self)
            {
                mask |= 1 << bit;
            }
        }

        return mask;
    }

    private TileKind NeighbourTile(int x, int y)
        => _map.InBounds(x, y) ? _map.GetTile(x, y) : TileKind.Void;

    // ---------------------------------------------------------------- 绘制

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        _animTime += delta;
        UpdateDecoFade((float)delta);

        // 水格计数：先快照上一帧（三个 band 的 _Draw 已经跑完），再清零给本帧累加。
        // 日志打的是 _lastWaterFxCells —— 父节点 _Draw 里读 _waterFxCells 只能读到 band 还没画完的一半。
        _lastWaterFxCells = _waterFxCells;
        _waterFxCells = 0;

        QueueRedraw();
    }

    /// <summary>
    /// 逐帧推进装饰淡化：玩家格心 <see cref="DecoFadeRadius"/> 格（1.5）以内的 deco 目标 0.25，
    /// 之外的 1.0，用 <c>MoveToward(delta / <see cref="DecoFadeSeconds"/>)</c> 线性过渡 ——
    /// 0.12 s 走完一整趟，所以走进去是"化开"而不是"闪一下"。
    ///
    /// 判定用**格坐标的平方距离**（<c>dx*dx + dy*dy</c>，1 格 = 1.0），和密度过滤同一套度量；
    /// 早先那版用"是否画在玩家之后 + 矩形是否罩住头/身两点"，只有命中的那一个精灵瞬间跳变，
    /// 结果人站在灌木后面时只有一块变半透、旁边半格的花还是实的，读起来像穿模。
    /// </summary>
    private void UpdateDecoFade(float delta)
    {
        int count = _decoFadeAlphas.Length;
        if (count == 0)
        {
            return;
        }

        float step = DecoFadeSeconds <= 0f ? 1f : Mathf.Clamp(delta / DecoFadeSeconds, 0f, 1f);
        bool hasPlayer = _player is not null;
        Vector2 p = hasPlayer ? _player!.IsoGrid : Vector2.Zero;
        float radius2 = DecoFadeRadius * DecoFadeRadius;

        for (int i = 0; i < count; i++)
        {
            float target = 1f;
            if (hasPlayer && i < _deco.Count)
            {
                RefblockMapLoader.DecoPlacement deco = _deco[i];
                float dx = deco.X - p.X;
                float dy = deco.Y - p.Y;
                if ((dx * dx) + (dy * dy) < radius2)
                {
                    target = DecoFadeAlpha;
                }
            }

            _decoFadeAlphas[i] = Mathf.MoveToward(_decoFadeAlphas[i], target, step);
        }
    }

    /// <inheritdoc />
    public override void _Draw()
    {
        if (_map is null)
        {
            return;
        }

        _drawGround = 0;
        _drawRim = 0;
        _drawShade = 0;
        _drawBlend = 0;
        _drawSpecks = 0;
        _drawWarmth = 0;
        _drawContact = 0;
        _drawWalls = 0;
        _drawDressing = 0;
        _drawProps = 0;
        _drawDeco = 0;
        _decoFade = 0;
        System.Array.Clear(_drawTierWalls);
        System.Array.Clear(_drawResolvedTier);
        _drawWaterWallSkips = 0;

        // 描边线宽/对比跟着实际屏幕缩放走（0.75 下 1px 读不出格子，见文件头那一段）。
        UpdateRimForZoom();

        DrawGround();

        // 物件 + 装饰 + 玩家统一按深度排序：x + y 越大越靠前（越后画）；同深度再按 y。
        // 装饰是浮点格坐标（散点摆放），所以深度用浮点比；物件/玩家是整数格，用浮点比等价于原来的整数比。
        var order = new List<(float Depth, float Tie, int Tag, int A, int B)>();
        for (int y = 0; y < _map.Height; y++)
        {
            for (int x = 0; x < _map.Width; x++)
            {
                if (_map.GetProp(x, y) == PropKind.None)
                {
                    continue;
                }

                order.Add((x + y, y, SortTagProp, x, y));
            }
        }

        for (int i = 0; i < _deco.Count; i++)
        {
            RefblockMapLoader.DecoPlacement deco = _deco[i];

            // 图集 stem 与 loose 散图两条通道都要入排序表：旧实现只放行 _stems，
            // 于是 props/loose/tree_*.png 那 11 个散图树在整张岛上从来没被画出来过。
            if (!_stems.ContainsKey(deco.Stem) && !_looseProps.ContainsKey(deco.Stem))
            {
                continue;
            }

            order.Add((deco.X + deco.Y, deco.Y, SortTagDeco, i, 0));
        }

        Vector2 playerGrid = _player?.IsoGrid ?? new Vector2(-1e6f, -1e6f);
        if (_player is not null)
        {
            // 玩家沿用改造前的整数截断深度，保证与旧截图的遮挡关系逐例一致。
            order.Add(((int)playerGrid.X + (int)playerGrid.Y, (int)playerGrid.Y, SortTagPlayer, 0, 0));
            // 淡化不再在这里判：由 UpdateDecoFade 按"格距 < 1.5"逐帧推进（见那里的注释）。
        }

        order.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.Tie.CompareTo(b.Tie));

        foreach ((float depth, float tie, int tag, int a, int b) in order)
        {
            _ = depth;
            _ = tie;

            switch (tag)
            {
                case SortTagPlayer:
                    DrawPlayer(playerGrid.X, playerGrid.Y);
                    break;

                case SortTagDeco:
                    DrawDeco(a);
                    break;

                default:
                    DrawProp(_map.GetProp(a, b), a, b, GridToScreen(a, b));
                    break;
            }
        }

        _lastGround = _drawGround;
        _lastRim = _drawRim;
        _lastShade = _drawShade;
        _lastBlend = _drawBlend;
        _lastSpecks = _drawSpecks;
        _lastWarmth = _drawWarmth;
        _lastContact = _drawContact;
        _lastWalls = _drawWalls;
        _lastDressing = _drawDressing;
        _lastProps = _drawProps;
        _lastDeco = _drawDeco;
        _lastDecoFade = _decoFade;
        System.Array.Copy(_drawTierWalls, _lastTierWalls, WallTierCount);
        System.Array.Copy(_drawResolvedTier, _lastResolvedTier, WallTierCount);
        _lastWaterWallSkips = _drawWaterWallSkips;
        _reportFrames++;
        // 第 1 帧太早：水面层（WaterFxLayer，ZIndex<0 的子节点）这一帧还没 _Draw，
        // _lastWaterFxCells 必然还是 0 —— 这就是日志恒显示 `water fx 0 cell(s)` 的第二半原因
        // （第一半是 DrawWaterTiles 里三个 band 互相覆盖）。改从第 2 帧起报。
        if (_reportFrames == 2 || _reportFrames % 120 == 0)
        {
            // (thin/mid/thick) = 本帧按 depth 规则选中的档位分布；[resolved] = 实际画出来的图属于哪档。
            // 美术只交了中档时两者不同（选中的 thin/thick 都回退成 mid 画），这正是要盯的数。
            // dressing = 侧壁上的草皮盖 + 草皮阴影 + 底部压暗 + 底部暗线（每条边 4 次自绘，不加贴图）。
            // skipped water = 水格朝虚空、但按「水不长壁」跳过的边数 → walls + skipped water = 全部边数。
            GD.Print($"[iso] draw calls/frame: {_lastGround + _lastRim + _lastWalls + _lastDressing + _lastProps + _lastShade + _lastBlend + _lastSpecks + _lastWarmth + _lastContact}"
                      + $" (ground {_lastGround}, rim {_lastRim}, walls {_lastWalls}, dressing {_lastDressing},"
                      + $" props {_lastProps} incl. deco {_lastDeco}, faded {_lastDecoFade};"
                      + $" shade {_lastShade}, blend {_lastBlend}, specks {_lastSpecks},"
                      + $" warmth {_lastWarmth} (folded into shade), contact {_lastContact})"
                      + $" (thin {_lastTierWalls[WallTierThin]}, mid {_lastTierWalls[WallTierMid]}, thick {_lastTierWalls[WallTierThick]})"
                      + $" [resolved: thin {_lastResolvedTier[WallTierThin]}, mid {_lastResolvedTier[WallTierMid]}, thick {_lastResolvedTier[WallTierThick]}]"
                      + $" [skipped water {_lastWaterWallSkips}]"
                      + (_waterFx is not null ? $" + water fx {_lastWaterFxCells} cell(s)" : string.Empty));
        }
    }

    /// <summary>
    /// 一层地面 = 每格一次 <see cref="DrawTexture"/>。
    ///
    /// 早先的方案是"底层统一画 grass、其它 kind 再用 wang 块盖上去"，实测每格
    /// 2 次绘制、但下层永远被上层完全遮住（wang 块是不透明菱形，整块覆盖），
    /// 于是换成单层：绘制数直接减半，肉眼无差别。等真的有半透明地面
    /// （浅滩/冰面）再把这个 BaseLayer 常量打开。
    /// </summary>
    private static readonly bool DrawGrassBaseLayer = false;

    /// <summary>
    /// 画一格地面。美术可以直接出 128x64（1:1），也可以出 64x32（按 2x 绘制）。
    /// 用目标矩形绘制而不是原尺寸贴图，是为了让 64x32 的美术在整数 2 倍下保持硬边像素块
    /// —— 这正是"星露谷那种看得见像素块"的质感来源。nearest 过滤已在 project.godot 设成 0。
    /// </summary>
    private void DrawGroundTexture(Texture2D? tex, Vector2 topLeft, Color modulate)
    {
        if (tex is null)
        {
            return;
        }

        DrawTextureRect(tex, new Rect2(topLeft, new Vector2(TileW, TileH)), false, modulate);
    }
    private void DrawGround()
    {
        // Pass A：全部顶面 + 种类过渡带 + 接触阴影 + 低频光照（色温并进光照）。按 x+y 升序 = 从远到近，远的先画、近的后盖。
        // 每格的顺序是「贴图 → 过渡带（C）→ 接触阴影（E）→ 低频光照 + 色温（A/A2）」：
        // 过渡带必须压在光照下面，否则它是一块"不吃光"的同色带，在暗斑里会亮成一条线；
        // 接触阴影同理，必须压在光照下面，才会跟着亮斑一起被提亮 —— 画在光照之上就成了一根死黑描边。
        for (int y = 0; y < _map.Height; y++)
        {
            for (int x = 0; x < _map.Width; x++)
            {
                TileKind tile = _map.GetTile(x, y);
                if (tile == TileKind.Void)
                {
                    continue;
                }

                Vector2 center = GridToScreen(x, y);
                Vector2 topLeft = center - new Vector2(HalfW, HalfH);
                int gi = KindToGroundIndex(tile);

                // 水面交给 WaterFxLayer（动态水）画；它用同一套水瓦的菱形 alpha，位置逐像素对齐。
                // 注意这里只跳过**贴图**：过渡带与低频光照照画 —— 岸线才柔、水面才吃同一片明暗，
                // 否则水面读成"一块没有光的地"，和陆地不是一个世界。
                bool dynamicWater = tile == TileKind.Water && _waterFx is not null;
                if (!dynamicWater)
                {
                    if (DrawGrassBaseLayer)
                    {
                        DrawGroundTexture(_ground[0][0][0], topLeft, _groundTint);
                        _drawGround++;
                    }

                    int index = IsoWangTable.RawMaskToTile[WangMask(x, y, tile)];
                    Texture2D? tex = _ground[gi][VariantFor(gi, x, y)][index];
                    if (tex is not null)
                    {
                        // 逐格亮度扰动 ±2.5%，键在 2×2 格的低频块上（(x>>1, y>>1) 的哈希）。
                        // 低频光照层（A）开着时 _wobble = 0：光照层已经提供跨格渐变，
                        // 这层 2 格周期的扰动只会把"2 格重复"补回来。
                        uint wob = Hash(x >> 1, y >> 1, 0x5A17u);
                        float k = 1f + (((wob & 0xFFFFu) / 32767.5f) - 1f) * _wobble;

                        // groundTint 乘进同一次 modulate：一次 draw 同时得到抖动与全局染色。
                        DrawGroundTexture(tex, topLeft, new Color(k * _groundTint.R, k * _groundTint.G, k * _groundTint.B));
                        _drawGround++;
                    }
                }

                DrawGroundBlend(x, y, gi, center);
                DrawContactShadow(x, y, gi, center);
                DrawShadeCell(x, y, center);
                DrawCellRim(gi, center);
            }
        }

        // Pass A2：世界坐标细节散布。压在全部顶面与光照之上、侧壁之前 —— 点按世界坐标撒会骑在格边界上，
        // 跟着格画就会被后画的邻格盖掉半个。
        DrawSpecks();

        // Pass A3：草簇散布（世界坐标、与格子无关）。和 specks 同层，压在顶面与光照之上、侧壁之前。
        DrawGrassTufts();

        // Pass B：全部侧壁，统一压在顶面之后（见 docs/art/AUTOTILE.md §5c 第 5 条）。
        DrawWalls();
    }

    /// <summary>
    /// 一格地面的菱形描边：上两条边提亮（中位色 + <see cref="_rimLight"/>×对比倍率），下两条边压暗（− <see cref="_rimDark"/>×对比倍率）。
    ///
    /// 两条边各一条 <see cref="DrawPolyline"/> = 每格 +2 次绘制，描边本身不贴图、不占内存。
    /// 线宽 <see cref="_rimLineWidth"/> 与内缩都随屏幕缩放变（见 <see cref="UpdateRimForZoom"/>）：
    /// 顶点始终内缩「半个线宽 + <see cref="RimInsetPad"/>」画在格子**里面**，所以线变粗也不会跑到菱形外
    /// 被邻格盖掉。顶面是按 x+y 从小到大铺的一层，后画的邻格会把自己那份菱形盖在共享边上，
    /// 贴着边界画的线会被整条吃掉。
    /// </summary>
    private void DrawCellRim(int groundIndex, Vector2 center)
    {
        if (_rimLight <= 0f && _rimDark <= 0f)
        {
            return;
        }

        float inset = RimInsetPad + _rimLineWidth * 0.5f;
        float hw = HalfW - inset;
        float hh = HalfH - inset;

        // 上两条边：左顶点 -> 顶顶点 -> 右顶点（光从上方来）。
        _rimUp[0] = center + new Vector2(-hw, 0f);
        _rimUp[1] = center + new Vector2(0f, -hh);
        _rimUp[2] = center + new Vector2(hw, 0f);

        // 下两条边：右顶点 -> 底顶点 -> 左顶点。
        _rimDown[0] = _rimUp[2];
        _rimDown[1] = center + new Vector2(0f, hh);
        _rimDown[2] = _rimUp[0];

        if (_rimLight > 0f)
        {
            DrawPolyline(_rimUp, _rimLightColor[groundIndex], _rimLineWidth, false);
            _drawRim++;
        }

        if (_rimDark > 0f)
        {
            DrawPolyline(_rimDown, _rimDarkColor[groundIndex], _rimLineWidth, false);
            _drawRim++;
        }
    }

    // ============================== A/C/D：地面光照 / 种类过渡带 / 细节散布 ==============================

    /// <summary>四条边的"朝格心"单位法线：取边向量旋转 90°，再按"指向格心"的符号修正。</summary>
    private static Vector2[] BuildInwardNormals()
    {
        var normals = new Vector2[BlendEdges.Length];
        for (int i = 0; i < BlendEdges.Length; i++)
        {
            BlendEdge edge = BlendEdges[i];
            var a = new Vector2(edge.Ax, edge.Ay);
            var b = new Vector2(edge.Bx, edge.By);
            Vector2 along = b - a;
            Vector2 normal = new Vector2(-along.Y, along.X).Normalized();
            if (normal.Dot(-(a + b) * 0.5f) < 0f)
            {
                normal = -normal;
            }

            normals[i] = normal;
        }

        return normals;
    }

    /// <summary>
    /// 格点上的确定性噪声值 ∈ [−1, 1]：<c>x*73856093 ^ y*19349663</c> 风格的大质数哈希 + 三轮混淆。
    /// 同 seed 同坐标永远同值（不用 Random、不看帧号），所以同一张图截几次都一样。
    /// </summary>
    private static float LatticeValue(int x, int y, uint salt)
    {
        unchecked
        {
            uint h = ((uint)x * 73856093u) ^ ((uint)y * 19349663u) ^ (salt * 83492791u);
            h ^= h >> 13;
            h *= 0x5BD1E995u;
            h ^= h >> 15;
            return ((h & 0xFFFFFFu) / 8388607.5f) - 1f;
        }
    }

    /// <summary>
    /// 双线性 + smoothstep 的值噪声：<paramref name="wx"/>/<paramref name="wy"/> 是世界格坐标，
    /// <paramref name="cellsPerLattice"/> = 每几个格一个格点（= 斑的尺度）。返回 ∈ [−1, 1]。
    /// </summary>
    private static float ValueNoise(float wx, float wy, float cellsPerLattice, uint salt)
    {
        float fx = wx / cellsPerLattice;
        float fy = wy / cellsPerLattice;
        int x0 = Mathf.FloorToInt(fx);
        int y0 = Mathf.FloorToInt(fy);
        float tx = fx - x0;
        float ty = fy - y0;

        // smoothstep 而不是线性：格点上的一阶导连续，斑块之间没有"折痕"。
        tx = tx * tx * (3f - (2f * tx));
        ty = ty * ty * (3f - (2f * ty));

        float top = Mathf.Lerp(LatticeValue(x0, y0, salt), LatticeValue(x0 + 1, y0, salt), tx);
        float bottom = Mathf.Lerp(LatticeValue(x0, y0 + 1, salt), LatticeValue(x0 + 1, y0 + 1, salt), tx);
        return Mathf.Lerp(top, bottom, ty);
    }

    /// <summary>
    /// 世界坐标的低频明暗系数：<c>1 ± _groundShade</c>。
    /// 两层叠加 —— 4 格斑（权重 0.12，就是需求说的"3–5 格一个斑"）+ 9 格超大斑（权重 0.08）——
    /// 再按权重和归一化，所以峰值不会因为叠层而超出幅度；粗层负责"整片地有起伏"，细层负责"看得出斑"。
    /// </summary>
    private float ShadeAt(float wx, float wy)
    {
        float fine = ValueNoise(wx, wy, ShadeFineCells, 0x51EDu);
        float coarse = ValueNoise(wx, wy, ShadeCoarseCells, 0x9C0Au);
        // 第三层：20 格波长的"大坡"。前两层最长 9 格，铺开仍像均匀纹理；
        // 参考图里那种"一大片偏暗、一大片偏亮"的起伏必须靠这一层，否则单块瓦片
        // 无论怎么画都会被读成平涂（美术那边也测过：把低频烘进瓦片会按格重复）。
        float patch = ValueNoise(wx, wy, ShadePatchCells, 0x2B7Fu);
        float mix = ((ShadeFineWeight * fine) + (ShadeCoarseWeight * coarse) + (ShadePatchWeight * patch))
                    / (ShadeFineWeight + ShadeCoarseWeight + ShadePatchWeight);
        return 1f + (_groundShade * mix);
    }

    /// <summary>
    /// 在角点场上双线性取样（世界格坐标 → 明暗系数）。给"已经离开地面绘制趟"的东西用（细节点上色）。
    /// </summary>
    private float SampleShade(float gx, float gy)
    {
        if (_shadeField.Length == 0)
        {
            return 1f;
        }

        // 角点 (i, j) ↔ 世界坐标 (i − 0.5, j − 0.5)，所以先平移半格再取整。
        float u = gx + 0.5f;
        float v = gy + 0.5f;
        int i0 = Mathf.FloorToInt(u);
        int j0 = Mathf.FloorToInt(v);
        float tx = u - i0;
        float ty = v - j0;
        int w = _shadeField.GetLength(0);
        int h = _shadeField.GetLength(1);
        int i1 = Mathf.Clamp(i0 + 1, 0, w - 1);
        int j1 = Mathf.Clamp(j0 + 1, 0, h - 1);
        i0 = Mathf.Clamp(i0, 0, w - 1);
        j0 = Mathf.Clamp(j0, 0, h - 1);

        float top = Mathf.Lerp(_shadeField[i0, j0], _shadeField[i1, j0], tx);
        float bottom = Mathf.Lerp(_shadeField[i0, j1], _shadeField[i1, j1], tx);
        return Mathf.Lerp(top, bottom, ty);
    }

    /// <summary>
    /// 一次性构建「地面光照场 + 细节点表」（<see cref="BakeTiles"/> 里调一次，之后每帧只查表）。
    /// 顺手决定逐格亮度扰动 <see cref="_wobble"/> 取 0 还是 0.025：光照层开着时它必须关
    /// —— 它只有 2 格周期，正是"整片在 2 格上重复"的那半个成因。
    /// </summary>
    private void BuildGroundLighting()
    {
        _wobble = _groundShade > ShadeEpsilon ? 0f : GroundWobbleAmount;

        if (_map is null)
        {
            _shadeField = new float[0, 0];
            _warmthField = new float[0, 0];
            _specks = System.Array.Empty<Speck>();
            return;
        }

        BuildShadeField();
        BuildWarmthField();
        BuildSpecks();
        BuildGrassTufts();
    }

    /// <summary>
    /// 按「格角」建**色温场**（需求 A）：<c>_warmthField[i, j]</c> = 世界坐标 <c>(i − 0.5, j − 0.5)</c> 处的
    /// 色温值 t ∈ [−1, 1]（&gt; 0 暖、&lt; 0 冷）。单层 7 格值噪声、不用第二层叠 —— 色温是"大面积氛围"，
    /// 叠细层只会退化成"格子在泛黄"，而明暗那边才是需要"看得出斑"的地方。
    /// 场结构与 <see cref="BuildShadeField"/> 同构（同样尺寸的角点表、同样一次 bake），
    /// 所以绘制时两场用同一套下标，一格内两次查表、零计算。
    /// </summary>
    private void BuildWarmthField()
    {
        if (_map is null || _groundWarmth <= ShadeEpsilon)
        {
            _warmthField = new float[0, 0];
            GD.Print("[iso] ground warmth field: skipped (amp 0)");
            return;
        }

        int width = _map.Width + 1;
        int height = _map.Height + 1;
        _warmthField = new float[width, height];

        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                _warmthField[i, j] = ValueNoise(i - 0.5f, j - 0.5f, WarmthCells, WarmthSalt);
            }
        }

        // 量化报告：|t| 的分布决定"看得见但不偏色"的余量（需求：spread ≈ ±1）。
        int n = width * height;
        var sorted = new float[n];
        int k = 0;
        float absSum = 0f;
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                float t = _warmthField[i, j];
                sorted[k++] = t;
                absSum += Mathf.Abs(t);
            }
        }

        float meanAbs = absSum / n;
        GD.Print($"[iso] ground warmth field: {width}x{height} corners, t mean |t| {meanAbs:0.###}"
                 + $" (peak alpha {_groundWarmth:0.###} => mean {(meanAbs * _groundWarmth):0.####})");
    }

    /// <summary>
    /// 按「格角」建光照场：<c>_shadeField[i, j]</c> = 世界坐标 <c>(i − 0.5, j − 0.5)</c> 处的明暗系数，
    /// 也就是格 (i, j) 的**上顶点**。选角不选格心是因为角是相邻三格共用的：
    /// 一格的四个角拿到四个不同的值，逐顶点插值后格内是连续渐变、跨格也连续
    /// —— 换成"一格一个值"就还是每格一块平色菱形，格子照样看得见。
    /// </summary>
    private void BuildShadeField()
    {
        int width = _map!.Width + 1;
        int height = _map.Height + 1;

        if (_groundShade <= ShadeEpsilon)
        {
            _shadeField = new float[0, 0];
            GD.Print("[iso] ground shade field: skipped (amp 0)");
            return;
        }

        _shadeField = new float[width, height];
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                _shadeField[i, j] = ShadeAt(i - 0.5f, j - 0.5f);
            }
        }

        // 量化报告：参考图的大尺度明度差 p95 − p05 ≈ 0.236，这里打自己的 spread 好和它比。
        int n = width * height;
        var sorted = new float[n];
        int k = 0;
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                sorted[k++] = _shadeField[i, j];
            }
        }

        System.Array.Sort(sorted);
        float p05 = sorted[Mathf.Clamp(Mathf.RoundToInt(0.05f * (n - 1)), 0, n - 1)];
        float p95 = sorted[Mathf.Clamp(Mathf.RoundToInt(0.95f * (n - 1)), 0, n - 1)];
        GD.Print($"[iso] ground shade field: {width}x{height} corners, shade p05 {p05:0.###} p95 {p95:0.###}"
                 + $" => spread {(p95 - p05):0.###} (reference image 0.236)");
    }

    /// <summary>
    /// 世界坐标细节散布（需求 D）：候选点在**世界坐标**上按 <see cref="SpeckStride"/> 格铺满全图，
    /// 与小格位置抖动一起决定位置 —— 和"地面格"完全无关，所以点会自然骑在格边界上、同格内没有固定图案。
    /// 颜色 = 本格中位色 ×(1 ± 10%)，并把当地光照系数烘进去（光照场是静态的，一次乘好）。
    /// </summary>
    private void BuildSpecks()
    {
        _specks = System.Array.Empty<Speck>();
        if (_map is null || _groundSpecks <= 0f)
        {
            GD.Print("[iso] ground specks: none (density 0)");
            return;
        }

        // 每格候选数 = 1 / stride²；命中率要让"平均每格 _groundSpecks 个"。
        float chance = Mathf.Clamp(_groundSpecks * SpeckStride * SpeckStride, 0f, 1f);
        int stepsX = Mathf.CeilToInt(_map.Width / SpeckStride);
        int stepsY = Mathf.CeilToInt(_map.Height / SpeckStride);
        var found = new System.Collections.Generic.List<Speck>(_map.Width * _map.Height / 2);

        for (int sy = 0; sy < stepsY; sy++)
        {
            for (int sx = 0; sx < stepsX; sx++)
            {
                uint h = Hash(sx, sy, 0x5EEDu);
                if (((h & 0xFFFFu) / 65535f) >= chance)
                {
                    continue;
                }

                float jx = ((h >> 16) & 0xFFu) / 255f;
                float jy = ((h >> 24) & 0xFFu) / 255f;
                float gx = (sx + jx) * SpeckStride;
                float gy = (sy + jy) * SpeckStride;
                int cx = Mathf.FloorToInt(gx);
                int cy = Mathf.FloorToInt(gy);
                if (!_map.InBounds(cx, cy))
                {
                    continue;
                }

                TileKind tile = _map.GetTile(cx, cy);
                if (tile == TileKind.Void || tile == TileKind.Water)
                {
                    continue;   // 水格/虚空不撒：水自带动态水层，撒上去只像脏点
                }

                uint tintHash = Hash(sx, sy, 0x7A11u);
                float jitter = 1f + ((((tintHash >> 8) & 0xFFu) / 255f * 2f) - 1f) * SpeckTintJitter;
                Color baseColor = _groundMedian[KindToGroundIndex(tile)];
                float shade = SampleShade(gx, gy);
                Color tint = new(
                    Mathf.Clamp(baseColor.R * jitter * shade, 0f, 1f),
                    Mathf.Clamp(baseColor.G * jitter * shade, 0f, 1f),
                    Mathf.Clamp(baseColor.B * jitter * shade, 0f, 1f));

                float sizePx = SpeckMinPx + ((((tintHash >> 20) & 0xFFu) / 255f) * (SpeckMaxPx - SpeckMinPx));
                found.Add(new Speck(GridToScreen(gx, gy), sizePx, tint));
            }
        }

        if (found.Count == 0)
        {
            return;
        }

        _specks = found.ToArray();
        float perCell = (float)_specks.Length / (_map.Width * _map.Height);
        GD.Print($"[iso] ground specks: {_specks.Length} point(s) over {_map.Width}x{_map.Height} cell(s)"
                 + $" ({perCell:0.###}/cell, candidate stride {SpeckStride:0.##} cell)");
    }

    /// <summary>
    /// 一格的低频光照：四个角从 <see cref="_shadeField"/> 查表，整格一次 <see cref="DrawColoredPolygon"/>。
    /// 亮斑叠白、暗斑叠黑（都是半透明），所以沙/草/水各自的中位色都被同一层光照统一 —— 这正是
    /// "不同地面种类看起来在同一片光下"的关键；如果改成按格乘贴图色，一格一个常数又回到平色菱形。
    /// </summary>
    /// <summary>
    /// 一格的低频光照：四个角从 <see cref="_shadeField"/> 查表，整格一次 <see cref="DrawColoredPolygon"/>。
    /// 亮斑叠白、暗斑叠黑（都是半透明），所以沙/草/水各自的中位色都被同一层光照统一 —— 这正是
    /// "不同地面种类看起来在同一片光下"的关键；如果改成按格乘贴图色，一格一个常数又回到平色菱形。
    ///
    /// 第五轮把**色温**并进同一组角颜色（<see cref="_warmthField"/>）：暖角叠 <see cref="WarmTint"/>、
    /// 冷角叠 <see cref="CoolTint"/>，alpha = |t| × <see cref="_groundWarmth"/>。做法是 alpha-over 合成到
    /// **已经在画的那个多边形里的那个角**上（亮角并进白色那层、暗角并进黑色那层），所以一格仍然是
    /// 0–2 次 draw、**没有第三个 draw call**。合成后：
    /// <list type="bullet">
    ///   <item>亮 + 暖 = 暖白（0.99, 0.98, 0.94）⇒ 日照草地偏黄绿；</item>
    ///   <item>暗 + 冷 = 冷黑（0.17, 0.20, 0.24）⇒ 树影里的草偏蓝绿。</item>
    /// </list>
    /// 两场都是同尺寸的角点表、同一套下标，所以一格内只是两次查表。
    /// </summary>
    private void DrawShadeCell(int x, int y, Vector2 center)
    {
        int sw = _shadeField.GetLength(0);
        int sh = _shadeField.GetLength(1);
        bool shadeOn = sw != 0 && sh != 0;

        int ww = _warmthField.GetLength(0);
        int wh = _warmthField.GetLength(1);
        bool warmthOn = ww != 0 && wh != 0 && _groundWarmth > ShadeEpsilon;

        if (!shadeOn && !warmthOn)
        {
            return;
        }

        _shadePts[0] = center + new Vector2(0f, -HalfH);
        _shadePts[1] = center + new Vector2(HalfW, 0f);
        _shadePts[2] = center + new Vector2(0f, HalfH);
        _shadePts[3] = center + new Vector2(-HalfW, 0f);

        // 两个多边形：亮角叠白、暗角叠黑，颜色按角给，所以同一格里"一半偏亮一半偏暗"是连续的。
        // 一格里同时有亮角和暗角是正常情况（噪声场穿过 1.0 等值线），两个都要画。
        bool light = false;
        bool dark = false;
        for (int c = 0; c < 4; c++)
        {
            // 角序 = 上 / 右 / 下 / 左，角点 (i, j) 在世界坐标 (i − 0.5, j − 0.5)。
            int ci = c == 1 || c == 2 ? x + 1 : x;
            int cj = c >= 2 ? y + 1 : y;

            float shade = shadeOn ? _shadeField[Mathf.Clamp(ci, 0, sw - 1), Mathf.Clamp(cj, 0, sh - 1)] : 1f;
            float t = warmthOn ? _warmthField[Mathf.Clamp(ci, 0, ww - 1), Mathf.Clamp(cj, 0, wh - 1)] : 0f;
            _shadeCornerScratch[c] = shade;

            float lightAlpha = shade > 1f ? Mathf.Clamp((shade - 1f) * ShadeLightScale, 0f, 1f) : 0f;
            float darkAlpha = shade < 1f ? Mathf.Clamp((1f - shade) * ShadeDarkScale, 0f, 1f) : 0f;
            _shadeColors[c] = new Color(1f, 1f, 1f, lightAlpha);
            _shadeColorsDark[c] = new Color(0f, 0f, 0f, darkAlpha);
            light |= lightAlpha > ShadeEpsilon;
            dark |= darkAlpha > ShadeEpsilon;

            // 色温按角先单独记下（合成要等"哪个多边形会被画"确定之后）。
            if (warmthOn && Mathf.Abs(t) > ShadeEpsilon)
            {
                Color tint = t > 0f ? WarmTint : CoolTint;
                _warmthColors[c] = new Color(tint.R, tint.G, tint.B, Mathf.Abs(t) * _groundWarmth);
            }
            else
            {
                _warmthColors[c] = new Color(0f, 0f, 0f, 0f);
            }
        }

        // 第二遍：把色温 alpha-over 进该角所属的那层多边形。亮的角跟着亮层、暗的角跟着暗层，
        // 既不新增 draw，也不让"暖"和"暗"互相抵消（各自独立半透明叠加才是对的）。
        if (warmthOn)
        {
            for (int c = 0; c < 4; c++)
            {
                Color tint = _warmthColors[c];
                if (tint.A <= ShadeEpsilon)
                {
                    continue;
                }

                float shade = _shadeCornerScratch[c];
                if (shade > 1f)
                {
                    _shadeColors[c] = OverGroundLayer(_shadeColors[c], tint);
                }
                else if (shade < 1f)
                {
                    _shadeColorsDark[c] = OverGroundLayer(_shadeColorsDark[c], tint);
                }
                else if (dark)
                {
                    _shadeColorsDark[c] = OverGroundLayer(_shadeColorsDark[c], tint);
                }
                else if (light)
                {
                    _shadeColors[c] = OverGroundLayer(_shadeColors[c], tint);
                }
            }
        }

        if (light)
        {
            DrawPolygon(_shadePts, _shadeColors);
            _drawShade++;
        }

        if (dark)
        {
            DrawPolygon(_shadePts, _shadeColorsDark);
            _drawShade++;
        }

        // 极端情况：一格四角明暗全等于 1.0（两个多边形都不画）但有色温 —— 这时才需要单独一次。
        // 正常场上（shade 场连续、等于 1.0 的测度为零）走不到这里，所以不是"每格第 3 个 draw"。
        if (warmthOn && !light && !dark)
        {
            DrawPolygon(_shadePts, _warmthColors);
            _drawWarmth++;
        }
    }

    /// <summary>
    /// 两个半透明覆盖层按 alpha-over 合成成一个角颜色：<paramref name="under"/> 在下（明暗层），
    /// <paramref name="over"/> 在上（色温层）。结果 alpha = a_o + a_u(1 − a_o)，rgb 按各自面积加权。
    /// 用合成而不是"把 alpha 相加"：相加会把"暗 0.20 + 冷 0.06"变成 alpha 0.26 的纯冷色，
    /// 把阴影提亮了；合成得到的 (0.17,0.20,0.24, 0.25) 才是"更暗、偏蓝"。
    /// </summary>
    private static Color OverGroundLayer(Color under, Color over)
    {
        float alpha = over.A + (under.A * (1f - over.A));
        if (alpha <= ShadeEpsilon)
        {
            return new Color(over.R, over.G, over.B, 0f);
        }

        float underWeight = under.A * (1f - over.A);
        return new Color(
            ((over.R * over.A) + (under.R * underWeight)) / alpha,
            ((over.G * over.A) + (under.G * underWeight)) / alpha,
            ((over.B * over.A) + (under.B * underWeight)) / alpha,
            alpha);
    }

    /// <summary>
    /// 接触阴影（需求 E）：四条边各自检查邻居，**异类**（含出界 = <see cref="TileKind.Void"/>，即地图外）
    /// 的那条边内侧铺一条宽 <see cref="ContactShadowScreenPx"/> 屏幕 px 的软阴影，
    /// alpha 从 <see cref="_groundContactShadow"/> 线性收到 0，颜色 <see cref="ContactShadowColor"/>（偏冷黑）。
    ///
    /// 与 <see cref="DrawGroundBlend"/> 共用同一套边几何表（<see cref="BlendEdges"/> / <see cref="BlendInward"/>）
    /// 与同一个沿边收进量 —— 两条带的端点因此逐像素对齐，不会在格角错开半个像素。
    /// 四邻同类时 0 draw call；一条异类边 = 1 次 <see cref="DrawPolygon"/>。
    ///
    /// 顺序：**过渡带之后、光照之前**（见 <see cref="DrawGround"/>）。压在光照下面，接触阴影就跟水/土
    /// 吃同一片明暗，亮斑会把暗角一起提亮、暗斑会更沉 —— 这样它读起来是"地面的一部分"，
    /// 而不是一圈浮在画面上的黑边。若画在光照之上，阴影在亮斑里会变成一条死黑的描边。
    /// </summary>
    private void DrawContactShadow(int x, int y, int groundIndex, Vector2 center)
    {
        if (_groundContactShadow <= ShadeEpsilon)
        {
            return;
        }

        float width = ContactShadowScreenPx / Mathf.Max(_effZoom, 0.0001f);

        for (int e = 0; e < BlendEdges.Length; e++)
        {
            BlendEdge edge = BlendEdges[e];
            TileKind neighbour = NeighbourTile(x + edge.Dx, y + edge.Dy);

            // 出界（Void）= 地图外：那边是侧壁/岛缘，正是最需要"落地暗角"的地方，照画。
            if (neighbour != TileKind.Void && KindToGroundIndex(neighbour) == groundIndex)
            {
                continue;   // 同类相邻：零痕迹、零 draw
            }

            var a = new Vector2(edge.Ax, edge.Ay);
            var b = new Vector2(edge.Bx, edge.By);
            Vector2 inward = BlendInward[e];

            // 与过渡带同样的端点收进：不收的话相邻两条边的阴影会在格角叠成 alpha ≈ 0.29 的黑角斑。
            Vector2 aIn = a + ((b - a) * BlendEdgeInset);
            Vector2 bIn = b + ((a - b) * BlendEdgeInset);

            _contactPts[0] = center + aIn;
            _contactPts[1] = center + bIn;
            _contactPts[2] = center + bIn + (inward * width);
            _contactPts[3] = center + aIn + (inward * width);

            _contactColors[0] = new Color(ContactShadowColor.R, ContactShadowColor.G, ContactShadowColor.B, _groundContactShadow);
            _contactColors[1] = new Color(ContactShadowColor.R, ContactShadowColor.G, ContactShadowColor.B, _groundContactShadow);
            _contactColors[2] = new Color(ContactShadowColor.R, ContactShadowColor.G, ContactShadowColor.B, 0f);
            _contactColors[3] = new Color(ContactShadowColor.R, ContactShadowColor.G, ContactShadowColor.B, 0f);

            DrawPolygon(_contactPts, _contactColors);
            _drawContact++;
        }
    }

    /// <summary>
    /// 种类过渡带（需求 C）：四条边各自检查邻居，邻居种类不同就在这条边的**内侧**铺一条
    /// 「邻居中位色、<see cref="BlendEdgeAlpha"/> 起、向格内渐隐到 0」的梯形带，宽度 <see cref="_groundBlendWidth"/> 屏幕 px。
    /// 目的：水→沙→草看起来是"渗过来"，不是菱形台阶一刀切。
    /// 同类相邻完全不画（0 draw call），所以 169 格里只在真正的边线上有开销。
    /// </summary>
    private void DrawGroundBlend(int x, int y, int groundIndex, Vector2 center)
    {
        if (_groundBlendWidth <= 0.5f)
        {
            return;
        }

        float width = _groundBlendWidth / Mathf.Max(_effZoom, 0.0001f);

        for (int e = 0; e < BlendEdges.Length; e++)
        {
            BlendEdge edge = BlendEdges[e];
            TileKind neighbour = NeighbourTile(x + edge.Dx, y + edge.Dy);
            if (neighbour == TileKind.Void)
            {
                continue;   // 虚空那边是侧壁，不铺带
            }

            int neighbourIndex = KindToGroundIndex(neighbour);
            if (neighbourIndex == groundIndex)
            {
                continue;   // 同类相邻：完全无痕
            }

            Color tint = _groundMedian[neighbourIndex];
            var a = new Vector2(edge.Ax, edge.Ay);
            var b = new Vector2(edge.Bx, edge.By);
            Vector2 inward = BlendInward[e];

            // 沿边两端各收进 BlendEdgeInset，避免相邻两条边的带在格角叠成亮角斑。
            Vector2 aIn = a + ((b - a) * BlendEdgeInset);
            Vector2 bIn = b + ((a - b) * BlendEdgeInset);

            // 内边界按 5 段抖动：直的菱形渐隐带在实拍里读成"用尺子画的斜线"，
            // 参考图里材质交界是"啃出来"的。用世界坐标哈希给每段不同的进深，
            // 段与段之间自然形成台阶，肉眼即碎边。
            const int Segments = 5;
            int n = Segments + 1;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Segments;
                Vector2 onEdge = aIn + ((bIn - aIn) * t);
                float jitter = 0.55f + (0.9f * ((WorldRenderer.VariantFor((x * 7) + i, (y * 13) + e) % 7) / 6f));
                Vector2 inner = onEdge + (inward * width * jitter);
                _blendPts[i] = center + onEdge;
                _blendPts[(2 * n) - 1 - i] = center + inner;
            }

            for (int i = 0; i < n; i++)
            {
                _blendColors[i] = new Color(tint.R, tint.G, tint.B, BlendEdgeAlpha);
            }

            for (int i = n; i < (2 * n); i++)
            {
                _blendColors[i] = new Color(tint.R, tint.G, tint.B, 0f);
            }

            // 数组满额传：Segments=5 ⇒ n=6 ⇒ 12 个顶点，正好用满预分配缓冲。\n            DrawPolygon(_blendPts, _blendColors);
            _drawBlend++;
        }
    }

    /// <summary>
    /// 细节散布的绘制趟：画在**全部顶面与光照之后**、侧壁之前。
    /// 顺序有讲究 —— 点按世界坐标撒，会骑在格边界上；如果跟着格画，后画的邻格会把它盖掉半个。
    /// 尺寸是屏幕 px，换算时 ÷ <see cref="_effZoom"/>，所以 1.5 zoom 下也是 2–4 px。
    /// </summary>
    private void DrawSpecks()
    {
        if (_specks.Length == 0)
        {
            return;
        }

        float invZoom = 1f / Mathf.Max(_effZoom, 0.0001f);
        foreach (Speck speck in _specks)
        {
            float halfW = speck.SizePx * 0.5f * invZoom;
            float halfH = halfW * 0.5f;   // 2:1 菱形，与瓦片的等距投影一致

            _speckPts[0] = speck.At + new Vector2(0f, -halfH);
            _speckPts[1] = speck.At + new Vector2(halfW, 0f);
            _speckPts[2] = speck.At + new Vector2(0f, halfH);
            _speckPts[3] = speck.At + new Vector2(-halfW, 0f);
            DrawColoredPolygon(_speckPts, speck.Tint);
            _drawSpecks++;
        }
    }

    /// <summary>
    /// 只给"朝屏幕下方的两条边且邻居是虚空"的格子画像参考图那样的土剖面。
    ///
    /// 几何：一条边的挤出是 64 px 宽的平行四边形，向下长 <c>WallTierHeight[档]</c> 像素
    /// （thin 20 / mid 40 / thick 72），三档画布的上半菱形都固定占前 <see cref="WallTopHeight"/> px，
    /// 所以锚点公式与档位无关，只换贴图高度：
    /// - se 边（右下）：贴图以**格中心**为锚点放在 <c>center</c>，画布内顶边 <c>y = 32 - 0.5x</c>；
    /// - sw 边（左下）：以**格中心 − (64,0)** 为锚点，画布内顶边 <c>y = 0.5x</c>。
    /// 两者都只往虚空那格伸，所以不会盖住自己的顶面；统一在全部顶面之后绘制，
    /// 就保证了「上层地形的壁压住下层地形的顶面」。
    /// 贴图按原始像素画（<see cref="DrawTexture"/>），绝不用 <c>DrawTextureRect</c> 缩放 ——
    /// 缩放既会糊，也会把"档位高度差"这个要检的东西抹掉。
    ///
    /// <see cref="TileKind.Water"/> 默认整格跳过（不长壁）：水的层级比陆地低，给它落一段土壁 =
    /// 岛缘出现深蓝缺角；水面到岸的过渡由水瓦自己的 wang 边缘表达。要水也有极薄一条时
    /// 在 <c>config.json</c> 里给 <c>waterWallTier</c> 设 <c>"thin"</c>（<c>"auto"</c> = 旧行为）。
    /// </summary>
    private void DrawWalls()
    {
        for (int y = 0; y < _map.Height; y++)
        {
            for (int x = 0; x < _map.Width; x++)
            {
                TileKind tile = _map.GetTile(x, y);
                if (tile == TileKind.Void)
                {
                    continue;
                }

                // 水是比陆地**更低的一层**，默认不长土壁（config.json 的 waterWallTier = "none"）。
                // 以前水格也走 TierFor() → 按落差 3 选档，岛缘的水就落一段 40px 的深蓝壁，
                // 读起来像岛缺了个角（截图右侧那条）。水面到岸的过渡交给水瓦自己的 wang 边缘。
                // 要 A/B 就把 waterWallTier 设成 "thin"（最多一条极薄、颜色随水色）或 "auto"（旧行为）；
                // 跳过时把边数记进日志，好和"陆地侧壁没变少"对账。
                int? forcedTier = null;
                if (tile == TileKind.Water)
                {
                    if (!_waterWalls)
                    {
                        if (NeighbourTile(x + 1, y) == TileKind.Void)
                        {
                            _drawWaterWallSkips++;
                        }

                        if (NeighbourTile(x, y + 1) == TileKind.Void)
                        {
                            _drawWaterWallSkips++;
                        }

                        continue;
                    }

                    forcedTier = Mathf.Min(_waterWallTier, _wallMaxTier);
                }

                int gi = KindToGroundIndex(tile);

                // se 边：往外是 +x。只有邻居是虚空才有壁，落差深度从这条边往外数。
                if (NeighbourTile(x + 1, y) == TileKind.Void)
                {
                    DrawWallEdge(gi, true, OutwardVoidDepth(x, y, 1, 0), GridToScreen(x, y), forcedTier);
                }

                // sw 边：往外是 +y。
                if (NeighbourTile(x, y + 1) == TileKind.Void)
                {
                    DrawWallEdge(gi, false, OutwardVoidDepth(x, y, 0, 1), GridToScreen(x, y) - new Vector2(HalfW, 0), forcedTier);
                }
            }
        }
    }

    /// <summary>
    /// 画一条边的壁：按 <paramref name="depth"/> 选档 → 走回退链拿图 → 原尺寸画在 <paramref name="topLeft"/>。
    /// 回退链拿不到图（该材质三档全无）就整条不画，等同改造前的行为。
    /// <paramref name="forcedTier"/> 非 null 时跳过 <see cref="TierFor"/> 直接用该档（水格走这条：
    /// 水不参与陆地那套落差选档）。
    /// </summary>
    private void DrawWallEdge(int groundIndex, bool se, int depth, Vector2 topLeft, int? forcedTier = null)
    {
        int tier = forcedTier ?? TierFor(depth);
        Texture2D? tex = ResolveWallTexture(se, groundIndex, tier, out int resolvedTier);
        if (tex is null)
        {
            return;
        }

        DrawTexture(tex, topLeft);
        _drawWalls++;
        _drawTierWalls[tier]++;
        _drawResolvedTier[resolvedTier]++;

        DrawWallDressing(groundIndex, se, topLeft, WallTierHeight[resolvedTier]);
    }

    /// <summary>
    /// 一条壁上的四段"自绘装饰"，沿壁的顶边往下叠（不新增贴图，纯 <see cref="DrawColoredPolygon"/>）：
    /// <list type="number">
    ///   <item>顶边 0..<see cref="WallCapHeight"/>px：草皮盖，颜色 = 该格顶面中位色 × <see cref="WallCapShade"/>
    ///         → 岛缘长出"草挂在土上"的竖面（和水平顶面同色会被读成草地多长了一截，看不出厚度）；</item>
    ///   <item>接着 <see cref="WallCapShadowHeight"/>px 的 <see cref="WallCapShadowColor"/>（<c>#01501C</c>）→ 草根压暗，
    ///         参考剖面里草皮和土的接缝就是这一档深绿；</item>
    ///   <item>最底 <see cref="WallFootHeight"/>px：18% 黑（= 乘 0.82，土体 <c>#D68C3F</c> 压成 <c>#B07334</c> 那个档）；</item>
    ///   <item>再 1px 的 50% 黑收边 → 岛的下沿读得出一条硬边，而不是糊进背景。</item>
    /// </list>
    /// 四段都在**壁的贴图范围内**（<paramref name="wallHeight"/> = 实际画出来的那档的高度，
    /// 不是选中的档 —— 回退链可能把 thick 的画成了 mid 的图，按选中的档算会比图还长）。
    /// </summary>
    private void DrawWallDressing(int groundIndex, bool se, Vector2 topLeft, int wallHeight)
    {
        // 壁的顶边就是该格菱形的下半沿：se 是 (0,32)→(64,0)，sw 是 (0,0)→(64,32)。
        Vector2 a = se ? topLeft + new Vector2(0f, WallTopHeight) : topLeft;
        Vector2 b = a + new Vector2(HalfW, se ? -HalfH : HalfH);

        // 贴图比装饰还矮（理论上不会：最矮 thin 也有 20px）就整条跳过，别画出图外去。
        int minimum = WallCapHeight + WallCapShadowHeight;
        if (wallHeight < minimum)
        {
            return;
        }

        DrawWallBand(a, b, 0f, WallCapHeight, _groundMedian[groundIndex].Darkened(1f - WallCapShade));
        DrawWallBand(a, b, WallCapHeight, WallCapHeight + WallCapShadowHeight, WallCapShadowColor);

        int footTop = wallHeight - WallFootHeight;
        if (footTop > WallCapHeight + WallCapShadowHeight)
        {
            DrawWallBand(a, b, footTop, wallHeight, WallFootOverlayColor);
            DrawWallBand(a, b, wallHeight - 1, wallHeight, WallFootLineColor);
        }
    }

    /// <summary>
    /// 顶边 <paramref name="a"/>→<paramref name="b"/> 之下、纵向 <paramref name="from"/>..<paramref name="to"/> px 的一条带。
    /// 顶边本身是斜的，所以这条带是平行四边形；每画一条 = 1 次 draw call。
    /// </summary>
    private void DrawWallBand(Vector2 a, Vector2 b, float from, float to, Color color)
    {
        var down = new Vector2(0f, 1f);
        _quad[0] = a + (down * from);
        _quad[1] = b + (down * from);
        _quad[2] = b + (down * to);
        _quad[3] = a + (down * to);
        DrawColoredPolygon(_quad, color);
        _drawDressing++;
    }

    /// <summary>
    /// 从 (x,y) 沿 (dx,dy) 往外数连续 <see cref="TileKind.Void"/> 的格数，cap 到 3（三档正好够）。
    /// 出界算 Void（见 <see cref="NeighbourTile"/>），所以地图边沿/外海拿到的是 3 → thick；
    /// 1 格窄缝拿到 1 → thin。调用点已经确认第一格是 Void，所以返回值 ≥1。
    /// </summary>
    private int OutwardVoidDepth(int x, int y, int dx, int dy)
    {
        int depth = 0;
        while (depth < 3 && NeighbourTile(x + dx * (depth + 1), y + dy * (depth + 1)) == TileKind.Void)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>
    /// 载入草簇贴图并按世界坐标撒点。缺失贴图时整层静默关闭（美术未交付也不影响跑）。
    /// </summary>
    private void BuildGrassTufts()
    {
        _tuftSpots.Clear();
        _grassTufts = System.Array.Empty<Texture2D?>();
        if (_map is null || _grassTuftDensity <= 0f)
        {
            return;
        }

        var loaded = new System.Collections.Generic.List<Texture2D?>(GrassTuftPaths.Length);
        // 目录扫描：美术只要把 grass_tuft_* / grass_clump_* 丢进 scatter 就会自动参与撒点，
        // 不必改代码再编译（这两类前缀之外的文件一律忽略，避免误捡 thumbs 之类）。
        var discovered = new System.Collections.Generic.List<string>();
        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        using (DirAccess? dir = DirAccess.Open(ScatterDir))
        {
            if (dir is not null)
            {
                foreach (string file in dir.GetFiles())
                {
                    string baseName = file.EndsWith(".import", System.StringComparison.Ordinal)
                        ? file[..^7]
                        : file;
                    if (!baseName.EndsWith(".png", System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (baseName.StartsWith("grass_tuft", System.StringComparison.Ordinal)
                        || baseName.StartsWith("grass_clump", System.StringComparison.Ordinal))
                    {
                        if (seen.Add(baseName))
                        {
                            discovered.Add(baseName);
                        }
                    }
                }
            }
        }

        discovered.Sort(System.StringComparer.Ordinal);
        foreach (string name in discovered)
        {
            loaded.Add(GD.Load<Texture2D>(ScatterDir + name));
        }

        if (loaded.Count == 0)
        {
            foreach (string path in GrassTuftPaths)
            {
                loaded.Add(ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null);
            }
        }

        if (loaded.TrueForAll(x => x is null))
        {
            GD.Print("[iso] grass tufts: no scatter sprites on disk (layer off)");
            return;
        }

        _grassTufts = loaded.ToArray();

        for (int y = 0; y < _map.Height; y++)
        {
            for (int x = 0; x < _map.Width; x++)
            {
                if (!IsTuftGround(_map.GetTile(x, y)))
                {
                    continue;
                }

                // 世界坐标哈希决定"这里长不长草"，与格子边界无关 → 有的地方一丛没有、有的地方两丛挤着。
                uint h = Hash(x, y, 0x7F31u);
                if ((h % 1000u) / 1000f >= _grassTuftDensity)
                {
                    continue;
                }

                float jx = ((h >> 10) & 0xFF) / 255f - 0.5f;
                float jy = ((h >> 18) & 0xFF) / 255f - 0.5f;
                int variant = (int)((h >> 26) % (uint)_grassTufts.Length);
                _tuftSpots.Add((x + 0.5f + (jx * 0.55f), y + 0.5f + (jy * 0.45f), variant));
            }
        }

        GD.Print($"[iso] grass tufts: {_tuftSpots.Count} spot(s), density {_grassTuftDensity:0.##}, sprites {_grassTufts.Length}");
    }

    /// <summary>草簇只长在草地上（土/田/沙/水/路都不长）。</summary>
    private static bool IsTuftGround(TileKind kind) =>
        kind == TileKind.Grass || kind == TileKind.GrassWild || kind == TileKind.GrassDry;

    /// <summary>把草簇画出来（脚底锚点：底行中点在格坐标上）。</summary>
    private void DrawGrassTufts()
    {
        if (_grassTufts.Length == 0)
        {
            return;
        }

        foreach ((float gx, float gy, int variant) in _tuftSpots)
        {
            Texture2D? tex = _grassTufts[variant % _grassTufts.Length];
            if (tex is null)
            {
                continue;
            }

            // 草簇贴图按 GrassTuftScale 倍整数放大画（16×16 原大只占 1/8 格，屏幕上太小），
            // 乘光照系数让它跟着明暗走。
            Vector2 foot = GridToScreen(gx, gy);
            float shade = SampleShade(gx, gy);
            float scale = _grassTuftScale;
            var size = new Vector2(tex.GetWidth() * scale, tex.GetHeight() * scale);
            var pos = new Vector2(foot.X - (size.X * 0.5f), foot.Y - size.Y + (6f * scale));
            DrawTextureRect(tex, new Rect2(pos, size), false, new Color(shade, shade, shade, 1f));
            _drawSpecks++;
        }
    }

    /// <summary>
    /// 该格用第几套变体。变体只换细节位置、不换色板，所以怎么选都不会出补丁；
    /// 目的是打散"整片同款"的重复感。
    /// </summary>
    private int VariantFor(int groundIndex, int x, int y)
    {
        // 防御：美术部门可能正在重写某个 kind 的 PNG（先删后写），这一帧该 kind 就是空的。
        // 没有这层保护会在 _Draw 里抛 NullReferenceException，整个渲染器停摆（实拍图全空）。
        var sets = _ground[groundIndex];
        if (sets is null || sets.Length == 0)
        {
            return 0;
        }

        int count = sets.Length;
        if (count <= 1)
        {
            return 0;
        }

        // grass: per cell (land-r3). 老写法按 3x3 格块取变体，空旷草地上相邻 9 格是同一张
        // PNG —— 铺开就是"正好一格周期（实际 3 格 = 3×192px）的整齐重复"，这是用户抱怨的
        // 主要来源。改成逐格后（PNG 一张没换），美术侧低通后 lag r 从 0.607/0.698 掉到
        // 0.135/0.009。7 套布局只差笔触位置、亮度分布一致，所以逐格跳变不会成"麻点"。
        // water: 仍按 3x3 格块 —— 水面逐格换变体会变成一块块疏密不一的水晶噪点。
        // sand / 其它: 逐格，两块沙交界的锯齿才不会重合。
        bool coarse = groundIndex == 3; // water only; grass is per-cell (land-r3)
        uint h = coarse
            ? Hash(x / 3, y / 3, (uint)groundIndex)
            : Hash(x, y, (uint)groundIndex);
        return (int)(h % (uint)count);
    }

    /// <summary>
    /// <see cref="TileKind"/> → 地面图集索引（= <see cref="GroundSlugs"/> 下标）。
    /// 老代码这里默认落到"路"，新增 kind 会静默画成路面；现在默认落草地，
    /// 未知值（比如更新版本的存档）画成草，至少不是一块突兀的土路。
    /// </summary>
    private static int KindToGroundIndex(TileKind kind) => kind switch
    {
        TileKind.Grass => 0,
        TileKind.Soil => 1,
        TileKind.TilledSoil => 2,
        TileKind.Water => 3,
        TileKind.Sand => 4,
        TileKind.Path => 5,
        TileKind.GrassWild => 6,
        TileKind.GrassDry => 7,
        TileKind.Mud => 8,
        TileKind.SoilLoose => 9,
        TileKind.Gravel => 10,
        TileKind.StoneFloor => 11,
        _ => 0,
    };

    /// <summary>造型选择：确定性哈希，同一格永远同一个造型，且邻居不同。</summary>
    private static int ShapeFor(int x, int y) => (x * 7 + y * 13) & (PropShapes - 1);

    private void DrawProp(PropKind prop, int x, int y, Vector2 center)
    {
        int shape = ShapeFor(x, y);

        switch (prop)
        {
            case PropKind.Tree:
            {
                Texture2D? trunk = _treeTrunk[shape];
                Texture2D? canopy = _treeCanopy[shape];
                if (trunk is null)
                {
                    return;
                }

                // 树干脚底锚点 = 菱形中心往下 2px（贴图自带椭圆阴影）
                Vector2 trunkTopLeft = center + new Vector2(-trunk.GetWidth() * 0.5f, -trunk.GetHeight() + 2);
                DrawTexture(trunk, trunkTopLeft);
                _drawProps++;

                if (canopy is null)
                {
                    return;
                }

                // 树冠锚点 = 树冠底部中心，落在树干顶部往下 4px（盖住接缝）
                float canopyBottom = trunkTopLeft.Y + 4f;
                DrawTexture(canopy, new Vector2(center.X - canopy.GetWidth() * 0.5f,
                                                canopyBottom - canopy.GetHeight()));
                _drawProps++;
                return;
            }

            case PropKind.Rock:
            {
                Texture2D? rock = _rocks[shape];
                if (rock is null)
                {
                    return;
                }

                DrawTexture(rock, center + new Vector2(-rock.GetWidth() * 0.5f, -rock.GetHeight() + 3));
                _drawProps++;
                return;
            }

            default:
            {
                if (_weed is null)
                {
                    return;
                }

                DrawTexture(_weed, center + new Vector2(-_weed.GetWidth() * 0.5f, -_weed.GetHeight() + 3));
                _drawProps++;
                return;
            }
        }
    }

    // ------------------------------------------------------------ 装饰精灵（deco[]）

    /// <summary>深度排序表里的三种载荷：玩家 / 瓦片物件 / 装饰精灵。</summary>
    private const int SortTagPlayer = 0;
    private const int SortTagProp = 1;
    private const int SortTagDeco = 2;

    private const string DecoAtlasPath = "res://Assets/tiles/iso/props/atlas_deco.png";

    /// <summary>
    /// 装饰图集 TileSet（stem 元数据的唯一来源）。
    /// <c>public</c> 是因为 <see cref="RefblockSceneLoader"/> 要按<b>同一个文件</b>解析出
    /// "图集格 → stem" 的反向表；路径写死两处就会漂。
    /// </summary>
    public const string DecoTilesetPath = "res://Assets/tiles/iso/props/deco_tileset.tres";

    /// <summary>
    /// 散图道具目录（每张一个文件、自带像素尺寸）。图集固定 128×128 一格，
    /// 装不下"4–6 格高的树"，所以高物件走这里：
    /// <c>res://Assets/tiles/iso/props/loose/&lt;stem&gt;.png</c>，stem 直接写进地图 deco[]。
    /// </summary>
    private const string LoosePropDir = "res://Assets/tiles/iso/props/loose/";

    /// <summary>散图道具：stem → 纹理（只装 deco[] 里实际用到的，缺的算 missing）。</summary>
    private readonly Dictionary<string, Texture2D> _looseProps = new();

    /// <summary>
    /// 装饰图集一格的边长（px）。<c>atlas_deco.png</c> 是 1024×1408 = 8×11 格、每格 128×128；
    /// 解析 .tres 时若读到 <c>texture_region_size</c> 就覆盖这个默认值。
    /// </summary>
    private const int DecoCell = 128;

    /// <summary>图集格边长（可能被 .tres 的 <c>texture_region_size</c> 覆盖）。</summary>
    private int _decoCell = DecoCell;

    /// <summary>
    /// 精灵**脚底**相对格心的落点（px）。依据 <c>Assets/tiles/iso/props/deco/README.md</c> §1
    /// “锚点 = 底边中心，放置 = <c>screen = tile_center + (dx - w/2, dy - h)</c>，屏幕偏移 <c>(0, 6)</c>”，
    /// 即底边中点踩在格心往下 6px（等距格子的站立面）。
    ///
    /// 实测校正（2026-11-17）：图集 88 格里 84 张有效，**每张的 alpha 底行都正好落在格内 y=128、
    /// 水平中心 ≈ x=64**，和图集格完全对齐，所以不需要按 <c>texture_origin</c> 再挪 ——
    /// 那个 <c>Vector2i(0, 58)</c> 在 84 格里完全一样（连 128 宽的桥栏杆也一样），它是编辑器
    /// TileSet 的整格偏移，不是单个精灵的脚底信息。绘制时只用 (0, 6) 这一条实测规则。
    /// </summary>
    private static readonly Vector2 DecoFootOffset = new(0f, 6f);

    /// <summary>
    /// <see cref="DecoFootOffset"/> 的只读出口。<see cref="RefblockSceneLoader"/> 要把编辑器里
    /// <c>Sprite2D.position</c>（脚底中点，world px）反算回网格坐标，必须减**同一个**偏移；
    /// 两处各写一个 <c>(0, 6)</c> 迟早会漂开。
    /// </summary>
    public static Vector2 DecoFootOffsetPx => DecoFootOffset;

    /// <summary>轮廓色 <c>#0F170D</c> 量级：深墨绿。纯黑在草地上像抠图，深绿才像"影子"。</summary>
    private static readonly Color DecoOutlineColor = new(0.06f, 0.09f, 0.05f, 1f);

    /// <summary>轮廓的四个方向（本地 px，会被相机 zoom 放大；1 世界像素 = 贴图 1 像素）。</summary>
    private static readonly Vector2[] DecoOutlineOffsets =
    {
        new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
    };

    /// <summary>小于这个缩放就不描边（草簇这种 1.0 的物件，1px 轮廓只会脏）。</summary>
    private const float DecoOutlineMinScale = 1.05f;

    /// <summary>匹配不到关键字时的缩放。</summary>
    private const float DecoScaleDefault = 1.0f;

    /// <summary>
    /// stem → 屏幕缩放倍率的**精确表**（84 个 stem，逐个登记，不做关键字匹配）。
    ///
    /// 单位约定（整份渲染文档都用这个）：**1 格 = 64 px**（等距格子的竖向步长 = <see cref="TileH"/>；
    /// 横向一格 128 px = 2 个竖向步）。判据是玩家：<c>player_walk_*.png</c> 32×32 × 绘制倍率 1.5 = 48 px
    /// ≈ 0.75 格，所以"人高 1 格"= 64 px，而 4–5 格高的树 = 288 px（散图通道的树正是 128×288）。
    ///
    /// 目标尺寸 = 上限，<c>scale = min(1, 上限 / 包围盒边长)</c>，向下取到 2 位小数：
    /// <code>
    /// 类别                       上限        折算 px   轴
    /// stone_large                1.00 格宽    64       宽
    /// stone_mid                  0.70 格宽    44.8     宽
    /// stone_small                0.45 格宽    28.8     宽
    /// bush_*                     1.50 格高    96       高
    /// flower_* / daisy_*         0.90 格高    57.6     高
    /// grass_tuft_* / reed_*      0.80 格高    51.2     高
    /// lily_* / lotus_* / mushroom_*   0.80 格长边 51.2  长边
    /// tree_stump                 0.90 格高    57.6     高
    /// lamp_* / sign*             1.40 格高    89.6     高
    /// barrel / crate / bench     1.00 格长边  64       长边
    /// bridge_* / fence_* / log_* / stone_step* / plank / step   固定 1.00（地面拼接件，缩了会错位）
    /// </code>
    ///
    /// 为什么从"关键字前缀"换成"逐 stem 表"：关键字匹配让 <c>tree_stump</c> 命中 "tree"（3.0）、
    /// <c>bush_flowering</c> 命中 "bush"（1.9 = 186 px = 2.9 格高）、<c>stone_large</c> 命中 "stone"
    /// （1.2 = 125 px ≈ 2 格宽）—— 玩家只有 0.75 格高，整片地形就被这些被放大的 128 px 图集格吞掉了。
    /// 表里的数字由 <c>tmp/gen_scale.py</c> 用 PIL 量 alpha 包围盒重算，注释即实测包围盒 + 结果尺寸（格）。
    /// 改法：改 <c>tmp/gen_scale.py</c> 的上限表再重跑，别手改这里的数字。
    ///
    /// 散图通道（<c>props/loose/</c> 的树，128×288）**不查这张表**：按自身像素、scale = 1.0 画。
    /// 未登记的 stem ⇒ <see cref="DecoScaleDefault"/>（1.0），宁可小也不放大。
    ///
    /// 完整表格（含未用到的 stem）另存一份在人读的 <c>docs/engine/DECO-SCALE.md</c>。
    /// </summary>
    private static readonly Dictionary<string, float> DecoScaleByStem = new()
    {
        ["barrel"] = 1.00f, //  50x 61 px [ 0, 0] 箱桶凳 长边 ≤1.00 格 -> 0.78 × 0.95
        ["bridge_plank_b"] = 1.00f, // 126x 76 px [ 0, 1] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["bush_green_a"] = 0.97f, // 106x 98 px [ 0, 2] 灌木 高 ≤1.50 格 -> 1.61 × 1.49
        ["fence_post"] = 1.00f, //  28x 54 px [ 0, 3] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["flower_pink_hydrangea_b"] = 0.60f, // 101x 96 px [ 0, 4] 花 高 ≤0.90 格 -> 0.95 × 0.90
        ["flower_yellow_cluster_a"] = 0.88f, //  49x 65 px [ 0, 5] 花 高 ≤0.90 格 -> 0.67 × 0.89
        ["lamp_post"] = 0.87f, //  39x102 px [ 0, 6] 路灯/告示牌 高 ≤1.40 格 -> 0.53 × 1.39
        ["log_fallen_b"] = 0.55f, // 107x 63 px [ 0, 7] 横躺木堆 宽 <=1.00 格 -> 0.92 x 0.54
        ["reed_cattail_a"] = 0.53f, //  65x 96 px [ 0, 8] 草/芦苇 高 ≤0.80 格 -> 0.54 × 0.80
        ["stone_mid"] = 0.58f, //  77x 62 px [ 0, 9] 中石 宽 ≤0.70 格 -> 0.70 × 0.56
        ["stone_step_b"] = 1.00f, //  70x 33 px [ 0,10] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["barrel_a"] = 1.00f, //  49x 61 px [ 1, 0] 箱桶凳 长边 ≤1.00 格 -> 0.77 × 0.95
        ["bridge_rail"] = 1.00f, // 126x102 px [ 1, 1] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["bush_green_b"] = 0.97f, // 112x 98 px [ 1, 2] 灌木 高 ≤1.50 格 -> 1.70 × 1.49
        ["fence_post_a"] = 1.00f, //  30x 54 px [ 1, 3] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["flower_purple_spike"] = 0.60f, //  52x 96 px [ 1, 4] 花 高 ≤0.90 格 -> 0.49 × 0.90
        ["flower_yellow_cluster_b"] = 0.88f, //  55x 65 px [ 1, 5] 花 高 ≤0.90 格 -> 0.76 × 0.89
        ["lamp_post_a"] = 0.87f, //  39x102 px [ 1, 6] 路灯/告示牌 高 ≤1.40 格 -> 0.53 × 1.39
        ["lotus_pink"] = 0.66f, //  77x 63 px [ 1, 7] 水生/蘑菇 长边 ≤0.80 格 -> 0.79 × 0.65
        ["reed_cattail_b"] = 0.53f, //  65x 96 px [ 1, 8] 草/芦苇 高 ≤0.80 格 -> 0.54 × 0.80
        ["stone_mid_a"] = 0.56f, //  79x 63 px [ 1, 9] 中石 宽 ≤0.70 格 -> 0.69 × 0.55
        ["tree_stump"] = 0.88f, //  74x 65 px [ 1,10] 树桩 高 ≤0.90 格 -> 1.02 × 0.89
        ["barrel_b"] = 1.00f, //  48x 62 px [ 2, 0] 箱桶凳 长边 ≤1.00 格 -> 0.75 × 0.97
        ["bridge_rail_a"] = 1.00f, // 126x100 px [ 2, 1] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["crate"] = 1.00f, //  50x 52 px [ 2, 2] 箱桶凳 长边 ≤1.00 格 -> 0.78 × 0.81
        ["fence_post_b"] = 1.00f, //  24x 54 px [ 2, 3] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["flower_purple_spike_a"] = 0.60f, //  50x 96 px [ 2, 4] 花 高 ≤0.90 格 -> 0.47 × 0.90
        ["grass_tuft_a"] = 1.00f, //  47x 49 px [ 2, 5] 草/芦苇 高 ≤0.80 格 -> 0.73 × 0.77
        ["lamp_post_b"] = 0.87f, //  39x102 px [ 2, 6] 路灯/告示牌 高 ≤1.40 格 -> 0.53 × 1.39
        ["lotus_pink_a"] = 0.64f, //  80x 64 px [ 2, 7] 水生/蘑菇 长边 ≤0.80 格 -> 0.80 × 0.64
        ["sign"] = 1.00f, //  47x 71 px [ 2, 8] 路灯/告示牌 高 ≤1.40 格 -> 0.73 × 1.11
        ["stone_mid_b"] = 0.59f, //  75x 63 px [ 2, 9] 中石 宽 ≤0.70 格 -> 0.69 × 0.58
        ["tree_stump_a"] = 0.88f, //  75x 65 px [ 2,10] 树桩 高 ≤0.90 格 -> 1.03 × 0.89
        ["bench"] = 1.00f, //  64x 42 px [ 3, 0] 箱桶凳 长边 ≤1.00 格 -> 1.00 × 0.66
        ["bridge_rail_b"] = 1.00f, // 126x 86 px [ 3, 1] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["crate_a"] = 1.00f, //  50x 52 px [ 3, 2] 箱桶凳 长边 ≤1.00 格 -> 0.78 × 0.81
        ["fence_rail"] = 1.00f, // 126x 90 px [ 3, 3] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["flower_purple_spike_b"] = 0.60f, //  52x 96 px [ 3, 4] 花 高 ≤0.90 格 -> 0.49 × 0.90
        ["grass_tuft_a_a"] = 1.00f, //  47x 49 px [ 3, 5] 草/芦苇 高 ≤0.80 格 -> 0.73 × 0.77
        ["lily_pad"] = 0.98f, //  52x 34 px [ 3, 6] 水生/蘑菇 长边 ≤0.80 格 -> 0.80 × 0.52
        ["lotus_pink_b"] = 0.63f, //  81x 64 px [ 3, 7] 水生/蘑菇 长边 ≤0.80 格 -> 0.80 × 0.63
        ["sign_a"] = 1.00f, //  48x 71 px [ 3, 8] 路灯/告示牌 高 ≤1.40 格 -> 0.75 × 1.11
        ["stone_small"] = 0.50f, //  57x 49 px [ 3, 9] 小石 宽 ≤0.45 格 -> 0.45 × 0.38
        ["tree_stump_b"] = 0.88f, //  74x 65 px [ 3,10] 树桩 高 ≤0.90 格 -> 1.02 × 0.89
        ["bench_a"] = 0.98f, //  65x 42 px [ 4, 0] 箱桶凳 长边 ≤1.00 格 -> 1.00 × 0.64
        ["bush_flowering"] = 1.00f, // 103x 96 px [ 4, 1] 灌木 高 ≤1.50 格 -> 1.61 × 1.50
        ["crate_b"] = 1.00f, //  50x 52 px [ 4, 2] 箱桶凳 长边 ≤1.00 格 -> 0.78 × 0.81
        ["fence_rail_a"] = 1.00f, // 126x 98 px [ 4, 3] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["flower_white_cluster"] = 0.88f, //  57x 65 px [ 4, 4] 花 高 ≤0.90 格 -> 0.78 × 0.89
        ["grass_tuft_a_b"] = 1.00f, //  51x 50 px [ 4, 5] 草/芦苇 高 ≤0.80 格 -> 0.80 × 0.78
        ["lily_pad_a"] = 0.98f, //  52x 34 px [ 4, 6] 水生/蘑菇 长边 ≤0.80 格 -> 0.80 × 0.52
        ["mushroom_red"] = 1.00f, //  50x 50 px [ 4, 7] 水生/蘑菇 长边 ≤0.80 格 -> 0.78 × 0.78
        ["sign_b"] = 1.00f, //  47x 70 px [ 4, 8] 路灯/告示牌 高 ≤1.40 格 -> 0.73 × 1.09
        ["stone_small_a"] = 0.48f, //  60x 49 px [ 4, 9] 小石 宽 ≤0.45 格 -> 0.45 × 0.37
        ["bench_b"] = 0.98f, //  65x 42 px [ 5, 0] 箱桶凳 长边 ≤1.00 格 -> 1.00 × 0.64
        ["bush_flowering_a"] = 1.00f, // 106x 96 px [ 5, 1] 灌木 高 ≤1.50 格 -> 1.66 × 1.50
        ["daisy_single"] = 1.00f, //  40x 49 px [ 5, 2] 花 高 ≤0.90 格 -> 0.62 × 0.77
        ["fence_rail_b"] = 1.00f, // 126x 90 px [ 5, 3] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["flower_white_cluster_a"] = 0.87f, //  57x 66 px [ 5, 4] 花 高 ≤0.90 格 -> 0.77 × 0.90
        ["grass_tuft_b"] = 1.00f, //  78x 49 px [ 5, 5] 草/芦苇 高 ≤0.80 格 -> 1.22 × 0.77
        ["lily_pad_b"] = 0.98f, //  52x 34 px [ 5, 6] 水生/蘑菇 长边 ≤0.80 格 -> 0.80 × 0.52
        ["mushroom_red_a"] = 1.00f, //  50x 48 px [ 5, 7] 水生/蘑菇 长边 ≤0.80 格 -> 0.78 × 0.75
        ["stone_large"] = 0.61f, // 104x 98 px [ 5, 8] 大石 宽 ≤1.00 格 -> 0.99 × 0.93
        ["stone_small_b"] = 0.47f, //  61x 49 px [ 5, 9] 小石 宽 ≤0.45 格 -> 0.45 × 0.36
        ["bridge_plank"] = 1.00f, // 126x 76 px [ 6, 0] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["bush_flowering_b"] = 1.00f, // 101x 95 px [ 6, 1] 灌木 高 ≤1.50 格 -> 1.58 × 1.48
        ["daisy_single_a"] = 1.00f, //  39x 49 px [ 6, 2] 花 高 ≤0.90 格 -> 0.61 × 0.77
        ["flower_pink_hydrangea"] = 0.60f, // 104x 96 px [ 6, 3] 花 高 ≤0.90 格 -> 0.97 × 0.90
        ["flower_white_cluster_b"] = 0.87f, //  56x 66 px [ 6, 4] 花 高 ≤0.90 格 -> 0.76 × 0.90
        ["grass_tuft_b_a"] = 1.00f, //  84x 50 px [ 6, 5] 草/芦苇 高 ≤0.80 格 -> 1.31 × 0.78
        ["log_fallen"] = 0.55f, // 111x 63 px [ 6, 6] 横躺木堆 宽 <=1.00 格 -> 0.95 x 0.54
        ["mushroom_red_b"] = 1.00f, //  49x 50 px [ 6, 7] 水生/蘑菇 长边 ≤0.80 格 -> 0.77 × 0.78
        ["stone_large_a"] = 0.60f, // 106x 98 px [ 6, 8] 大石 宽 ≤1.00 格 -> 0.99 × 0.92
        ["stone_step"] = 1.00f, //  69x 33 px [ 6, 9] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["bridge_plank_a"] = 1.00f, // 126x 76 px [ 7, 0] 地面拼接件 固定 1.00（不缩） -> 1.00
        ["bush_green"] = 0.97f, // 105x 98 px [ 7, 1] 灌木 高 ≤1.50 格 -> 1.59 × 1.49
        ["daisy_single_b"] = 1.00f, //  39x 50 px [ 7, 2] 花 高 ≤0.90 格 -> 0.61 × 0.78
        ["flower_pink_hydrangea_a"] = 0.60f, // 107x 96 px [ 7, 3] 花 高 ≤0.90 格 -> 1.00 × 0.90
        ["flower_yellow_cluster"] = 0.87f, //  53x 66 px [ 7, 4] 花 高 ≤0.90 格 -> 0.72 × 0.90
        ["grass_tuft_b_b"] = 1.00f, //  85x 49 px [ 7, 5] 草/芦苇 高 ≤0.80 格 -> 1.33 × 0.77
        ["log_fallen_a"] = 0.55f, // 115x 63 px [ 7, 6] 横躺木堆 宽 <=1.00 格 -> 0.99 x 0.54
        ["reed_cattail"] = 0.53f, //  62x 96 px [ 7, 7] 草/芦苇 高 ≤0.80 格 -> 0.51 × 0.80
        ["stone_large_b"] = 0.57f, // 111x 98 px [ 7, 8] 大石 宽 ≤1.00 格 -> 0.99 × 0.87
        ["stone_step_a"] = 1.00f, //  70x 33 px [ 7, 9] 地面拼接件 固定 1.00（不缩） -> 1.00
    };

    private static readonly System.Text.RegularExpressions.Regex DecoRegionLine = new(
        @"texture_region_size\s*=\s*Vector2i\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DecoOriginLine = new(
        @"texture_origin\s*=\s*Vector2i\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DecoStemLine = new(
        @"metadata/stem\s*=\s*""([^""]+)""",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DecoTileKeyLine = new(
        @"^(\d+):(\d+)/\d+\s*=\s*",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 读 <c>deco[]</c> + stem 表，打印 <c>[iso] deco: N sprites … (missing M)</c>。
    /// 装饰来源有两个，二选一：
    /// <list type="bullet">
    ///   <item><paramref name="inMemory"/> 非空 → 直接用（编辑器场景读出来的，<c>mapSource=refblock_scene</c>）；</item>
    ///   <item>否则读 <paramref name="mapPath"/> 的 json <c>deco[]</c>（<c>mapSource=refblock</c>，老路径）。</item>
    /// </list>
    /// 之后所有处理（stem 查表、散图兜底、准入规则、淡化数组）**只有一套**。
    /// </summary>
    private void LoadDeco(string? mapPath, List<RefblockMapLoader.DecoPlacement>? inMemory, string? source)
    {
        _deco.Clear();
        _stems.Clear();
        _decoMissing = 0;
        _decoDrawable = 0;

        bool hasInMemory = inMemory is { Count: > 0 };
        if (!hasInMemory && string.IsNullOrEmpty(mapPath))
        {
            GD.Print("[iso] deco: 0 sprites (no deco map; --projection iso with a generated world)");
            return;
        }

        string label = hasInMemory
            ? (string.IsNullOrEmpty(source) ? "in-memory deco list" : source)
            : mapPath!;

        LoadStemTable();
        _decoAtlas = GD.Load<Texture2D>(DecoAtlasPath);
        if (_decoAtlas is null)
        {
            GD.PushWarning($"[iso] deco atlas {DecoAtlasPath} could not be loaded; decorations skipped");
            GD.Print($"[iso] deco: 0 sprites from {label} (missing 0)");
            return;
        }

        if (hasInMemory)
        {
            _deco.AddRange(inMemory!);
        }
        else if (!RefblockMapLoader.TryLoadDeco(mapPath!, _deco))
        {
            GD.Print($"[iso] deco: 0 sprites from {label} (missing 0)");
            return;
        }

        var unknown = new List<string>();
        int drawable = 0;
        _looseProps.Clear();
        int ruleInput = _deco.Count;

        // off：只做两项安全过滤（Void/地图外、stem 没贴图），过滤完的条数就是最终要画的条数。
        // auto：原样进 ApplyCompositionRules()，配额/间距/出生点净空一条不少。
        if (string.Equals(_decoRules, DecoRulesOff, StringComparison.Ordinal))
        {
            DropBySafetyFilters();
        }

        foreach (RefblockMapLoader.DecoPlacement placement in _deco)
        {
            if (_stems.ContainsKey(placement.Stem))
            {
                drawable++;
                continue;
            }

            if (_looseProps.ContainsKey(placement.Stem) || TryLoadLooseProp(placement.Stem))
            {
                drawable++;
                continue;
            }

            _decoMissing++;
            if (!unknown.Contains(placement.Stem))
            {
                unknown.Add(placement.Stem);
            }
        }

        _decoDrawable = drawable;
        GD.Print($"[iso] deco: {_decoDrawable} sprites from {label} (missing {_decoMissing})");
        if (unknown.Count > 0)
        {
            GD.PushWarning($"[iso] deco stems not in {DecoTilesetPath}: {string.Join(", ", unknown)}");
        }

        // 密度 / 间距 / 出生点留白：**只在这里过滤一次**，refblock.json 一个字节都不动。
        // off —— 手画地图（refblock_scene）：编辑器里放几个就画几个，上面的安全过滤已经跑完了。
        // auto —— 生成/老地图：配额 + 间距 + 出生点净空全跑（ApplyCompositionRules 自己打日志）。
        if (string.Equals(_decoRules, DecoRulesOff, StringComparison.Ordinal))
        {
            _decoDrawable = _deco.Count;
            GD.Print($"[iso] deco rules: off (kept {_deco.Count}/{ruleInput})");
        }
        else
        {
            ApplyCompositionRules();
        }

        _decoFadeAlphas = new float[_deco.Count];
        for (int i = 0; i < _decoFadeAlphas.Length; i++)
        {
            _decoFadeAlphas[i] = 1f;
        }
    }

    /// <summary>
    /// <c>decoRules = off</c> 时**仅有的**两项安全过滤（其余配额/间距/出生点净空一条都不跑）：
    /// <list type="number">
    ///   <item>脚点落在地图外或 <see cref="TileKind.Void"/> 上 → 丢（画出来是一张悬空贴纸）；</item>
    ///   <item>stem 既不在 <c>deco_tileset.tres</c> 也不在 <c>props/loose/&lt;stem&gt;.png</c> → 丢（没有贴图可画）。</item>
    /// </list>
    /// 这两条是"会不会画错"，不是"好不好看"，所以 <c>off</c> 也留着。
    /// </summary>
    private void DropBySafetyFilters()
    {
        int before = _deco.Count;
        int droppedVoid = 0;
        int droppedStem = 0;
        var kept = new List<RefblockMapLoader.DecoPlacement>(before);
        foreach (RefblockMapLoader.DecoPlacement placement in _deco)
        {
            if (!IsOnDrawnGround(placement.X, placement.Y))
            {
                droppedVoid++;
                continue;
            }

            if (!_stems.ContainsKey(placement.Stem)
                && !_looseProps.ContainsKey(placement.Stem)
                && !TryLoadLooseProp(placement.Stem))
            {
                droppedStem++;
                continue;
            }

            kept.Add(placement);
        }

        _deco.Clear();
        _deco.AddRange(kept);
        if (droppedVoid > 0 || droppedStem > 0)
        {
            GD.Print($"[iso] deco off-mode safety: -{droppedVoid} out-of-map/Void, -{droppedStem} stem without texture ({before} -> {_deco.Count})");
        }
    }

    /// <summary>
    /// 脚点（格坐标，可能是半整数）踩到的这一格，是不是"画得出来的地面"。
    /// 取 <c>FloorToInt</c> 而不是四舍五入：脚点落在格角时归到左/上那格，取整只会把岛缘的装饰多留一格，不会误杀。
    /// </summary>
    private bool IsOnDrawnGround(float gx, float gy)
    {
        if (_map is null)
        {
            return true;
        }

        int cx = Mathf.FloorToInt(gx);
        int cy = Mathf.FloorToInt(gy);
        return _map.InBounds(cx, cy) && _map.GetTile(cx, cy) != TileKind.Void;
    }

    /// <summary>出生点这个半径（格）内一律不放装饰：玩家一出门就得能看见自己。</summary>
    private const float DecoSpawnClearRadius = 3f;

    /// <summary>任意两个装饰的格心最小距离（格，比较用平方）。0.85 格 ≈ 54 屏幕像素。</summary>
    private const float DecoMinSpacing = 0.85f;

    /// <summary>
    /// 装饰准入规则（渲染期，一次）：密度配额 + 最小间距 + 出生点留白 + 栈桥落水判定。
    ///
    /// 顺序有讲究，四个计数器都要打出来（对账用）：
    /// <list type="number">
    ///   <item><b>出生点留白</b>：距 <see cref="RefblockMapLoader.FindSpawn"/> 的格心 &lt;
    ///   <see cref="DecoSpawnClearRadius"/>（3 格）→ 丢；</item>
    ///   <item><b>栈桥落水</b>：<c>bridge_*</c> 的 8 邻域里没有 Water / Path → 丢
    ///   （地图里真有桥横穿草地，渲染期不画，不改 json）；</item>
    ///   <item><b>配额</b>：bush ≤8、stone ≤12、flower ≤24、grass_tuft ≤20、tree_stump ≤3、
    ///   其余每个 stem 族 ≤4；超了就按"离岛心更近优先"留下；</item>
    ///   <item><b>最小间距</b>：与已接受的任一个格心距离 &lt; 0.85 格 → 丢（同样中心优先），
    ///   这样丛生的草丛不会糊成一坨。</item>
    /// </list>
    ///
    /// 176 个装饰铺在 169 格上（每格 1.07 个）是"太挤"的全部原因；规则跑完这一版 refblock 是
    /// 176 → 43（规则写死在 docs/engine/COMPOSITION.md）。
    /// </summary>
    private void ApplyCompositionRules()
    {
        int before = _deco.Count;
        if (before == 0 || _map is null)
        {
            GD.Print($"[iso] deco rules: auto (filtered {before} -> {before})");
            return;
        }

        (int X, int Y) spawnCell = RefblockMapLoader.FindSpawn(_map);
        var spawn = new Vector2(spawnCell.X, spawnCell.Y);
        float spawnR2 = DecoSpawnClearRadius * DecoSpawnClearRadius;
        var center = new Vector2(_map.Width * 0.5f, _map.Height * 0.5f);

        // ① 出生点留白 ② 栈桥只在临水/临路时画
        var candidates = new List<(int Index, float Centre2, RefblockMapLoader.DecoPlacement Deco)>(before);
        int droppedSpawn = 0;
        int droppedBridge = 0;
        for (int i = 0; i < before; i++)
        {
            RefblockMapLoader.DecoPlacement deco = _deco[i];
            float dx = deco.X - spawn.X;
            float dy = deco.Y - spawn.Y;
            if ((dx * dx) + (dy * dy) < spawnR2)
            {
                droppedSpawn++;
                continue;
            }

            if (deco.Stem.StartsWith("bridge", StringComparison.Ordinal) && !HasWaterOrPathNeighbour(deco.X, deco.Y))
            {
                droppedBridge++;
                continue;
            }

            float cx = deco.X - center.X;
            float cy = deco.Y - center.Y;
            candidates.Add((i, (cx * cx) + (cy * cy), deco));
        }

        // 中心优先：冲突时保留"更靠岛中心"的那一个（配额与间距共用这一条）
        candidates.Sort((a, b) => a.Centre2 != b.Centre2 ? a.Centre2.CompareTo(b.Centre2) : a.Index.CompareTo(b.Index));

        // ③ 同类配额
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var byQuota = new List<RefblockMapLoader.DecoPlacement>(candidates.Count);
        int droppedQuota = 0;
        foreach ((int _, float _, RefblockMapLoader.DecoPlacement deco) in candidates)
        {
            string key = DecoQuotaKey(deco.Stem, out int cap);
            used.TryGetValue(key, out int count);
            if (count >= cap)
            {
                droppedQuota++;
                continue;
            }

            used[key] = count + 1;
            byQuota.Add(deco);
        }

        // ④ 最小间距（格心平方距离 < 0.85² 视为重叠）
        float minD2 = DecoMinSpacing * DecoMinSpacing;
        var accepted = new List<RefblockMapLoader.DecoPlacement>(byQuota.Count);
        int droppedOverlap = 0;
        foreach (RefblockMapLoader.DecoPlacement deco in byQuota)
        {
            bool clash = false;
            foreach (RefblockMapLoader.DecoPlacement kept in accepted)
            {
                float dx = deco.X - kept.X;
                float dy = deco.Y - kept.Y;
                if ((dx * dx) + (dy * dy) < minD2)
                {
                    clash = true;
                    break;
                }
            }

            if (clash)
            {
                droppedOverlap++;
                continue;
            }

            accepted.Add(deco);
        }

        _deco.Clear();
        _deco.AddRange(accepted);
        _decoDrawable = _deco.Count;

        GD.Print($"[iso] deco rules: auto (filtered {before} -> {_deco.Count})");
        GD.Print($"[iso] deco skip detail: {droppedSpawn} within {DecoSpawnClearRadius:0.#} cell(s) of spawn "
                 + $"({spawnCell.X},{spawnCell.Y}), {droppedBridge} bridge(s) with no water/path neighbour, "
                 + $"{droppedQuota} over quota, {droppedOverlap} closer than {DecoMinSpacing:0.##} cell");

        var quotaText = new List<string>();
        foreach (string key in new[] { "bush", "stone", "flower", "grass_tuft", "tree_stump" })
        {
            used.TryGetValue(key, out int count);
            quotaText.Add($"{key} {count}/{DecoQuotaCap(key)}");
        }

        int perClass = 0;
        foreach (KeyValuePair<string, int> pair in used)
        {
            if (pair.Key is not ("bush" or "stone" or "flower" or "grass_tuft" or "tree_stump"))
            {
                perClass += pair.Value;
            }
        }

        GD.Print($"[iso] deco quota: {string.Join(", ", quotaText)}, other {perClass} kept (<=4 per stem family)");
    }

    /// <summary>
    /// 配额键与上限：<c>bush_*</c> 合成一类 8 个，<c>stone_*</c> 12、<c>flower_*</c> 24、
    /// <c>grass_tuft_*</c> 20、<c>tree_stump</c> 3，其余按"去掉 _a/_b/_c 变体后缀的 stem"各 4。
    /// </summary>
    private static string DecoQuotaKey(string stem, out int cap)
    {
        foreach (string family in new[] { "bush", "stone", "flower", "grass_tuft" })
        {
            if (stem.StartsWith(family, StringComparison.Ordinal))
            {
                cap = DecoQuotaCap(family);
                return family;
            }
        }

        if (stem.StartsWith("tree_stump", StringComparison.Ordinal))
        {
            cap = DecoQuotaCap("tree_stump");
            return "tree_stump";
        }

        cap = DecoQuotaCap("other");
        return DecoStemFamily(stem);
    }

    /// <summary>配额表的数字只在这一个地方（日志也读它，避免日志和实际规则两处漂移）。</summary>
    private static int DecoQuotaCap(string family) => family switch
    {
        "bush" => 8,
        "stone" => 12,
        "flower" => 24,
        "grass_tuft" => 20,
        "tree_stump" => 3,
        _ => 4,
    };

    /// <summary>去掉 <c>_a / _b / _c</c> 变体后缀：<c>lily_pad_b</c> 与 <c>lily_pad</c> 算同一族。</summary>
    private static string DecoStemFamily(string stem)
    {
        if (stem.Length > 2 && stem[^2] == '_' && (stem[^1] == 'a' || stem[^1] == 'b' || stem[^1] == 'c'))
        {
            return stem[..^2];
        }

        return stem;
    }

    /// <summary>8 邻域里有没有 Water / Path：栈桥只有搭在水上或路边才画。</summary>
    private bool HasWaterOrPathNeighbour(float gx, float gy)
    {
        int cx = Mathf.RoundToInt(gx);
        int cy = Mathf.RoundToInt(gy);
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int nx = cx + dx;
                int ny = cy + dy;
                if (!_map!.InBounds(nx, ny))
                {
                    continue;
                }

                TileKind kind = _map.GetTile(nx, ny);
                if (kind == TileKind.Water || kind == TileKind.Path)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 尝试按 stem 加载散图道具 <c>props/loose/&lt;stem&gt;.png</c>。成功登记进
    /// <see cref="_looseProps"/> 并返回 true；不存在则返回 false（调用方计入 missing）。
    /// </summary>
    private bool TryLoadLooseProp(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        string path = LoosePropDir + stem + ".png";
        if (!Godot.FileAccess.FileExists(path))
        {
            return false;
        }

        var tex = GD.Load<Texture2D>(path);
        if (tex is null)
        {
            GD.PushWarning($"[iso] loose prop {path} exists but could not be loaded as Texture2D");
            return false;
        }

        _looseProps[stem] = tex;
        return true;
    }

    /// <summary>
    /// 解析 <c>deco_tileset.tres</c> 的文本，建 stem → (列, 行, texture_origin) 表。
    /// **不依赖编辑器 <see cref="TileSet"/>**：运行时只读这个文本文件，三条一组，
    /// <code>
    /// 0:0/0 = 0
    /// 0:0/0/texture_origin = Vector2i(0, 58)
    /// 0:0/0/metadata/stem = "barrel"
    /// </code>
    /// 读不到文本（比如导出把 .tres 转成了二进制）就退回 <see cref="TileSet"/> 资源，
    /// 那里只有 <c>metadata/stem</c> 和格坐标，origin 用默认 (0,58) —— 反正 84 格全是这个值。
    /// </summary>
    private void LoadStemTable()
    {
        Dictionary<string, (int Col, int Row, Vector2 Origin)> stems =
            ParseDecoStemTable(out int cellPx, out bool viaTileSetResource);
        _decoCell = cellPx;
        _stems.Clear();
        foreach ((string stem, (int Col, int Row, Vector2 Origin) entry) in stems)
        {
            _stems[stem] = entry;
        }

        if (_stems.Count == 0)
        {
            return;
        }

        GD.Print(viaTileSetResource
            ? $"[iso] deco atlas: {_stems.Count} stem(s) via TileSet resource (cell {_decoCell}x{_decoCell})"
            : $"[iso] deco atlas: {_stems.Count} stem(s), cell {_decoCell}x{_decoCell} from {DecoTilesetPath}");
    }

    /// <summary>
    /// <see cref="LoadStemTable"/> 的纯函数版：解析 <see cref="DecoTilesetPath"/>，返回
    /// <c>stem → (列, 行, texture_origin)</c>。<paramref name="cellPx"/> 给出一格的边长。
    ///
    /// 为什么会是 <c>public static</c>：<c>mapSource=refblock_scene</c> 那条路拿到的装饰是
    /// <b>图集坐标</b>（编辑器 TileMapLayer 存的），必须反向查成 stem。反向表和正向表
    /// 必须是同一份解析结果，所以这里抽成纯函数，两个方向共用，绝不写第二套 .tres 解析。
    /// 解析不出来时退回 <see cref="TileSet"/> 资源（导出成二进制 .res 的情况），
    /// <paramref name="viaTileSetResource"/> 会告诉调用方走的是哪条。
    /// </summary>
    public static Dictionary<string, (int Col, int Row, Vector2 Origin)> ParseDecoStemTable(
        out int cellPx, out bool viaTileSetResource)
    {
        var stems = new Dictionary<string, (int Col, int Row, Vector2 Origin)>(StringComparer.OrdinalIgnoreCase);
        cellPx = DecoCell;
        viaTileSetResource = false;

        if (!Godot.FileAccess.FileExists(DecoTilesetPath))
        {
            GD.PushWarning($"[iso] {DecoTilesetPath} not found; decorations cannot resolve stems");
            return stems;
        }

        string text = Godot.FileAccess.GetFileAsString(DecoTilesetPath);
        int col = -1;
        int row = -1;
        Vector2 origin = new(0f, 58f);

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            System.Text.RegularExpressions.Match match = DecoRegionLine.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int cellW))
            {
                cellPx = cellW;
                continue;
            }

            match = DecoOriginLine.Match(line);
            if (match.Success
                && int.TryParse(match.Groups[1].Value, out int ox)
                && int.TryParse(match.Groups[2].Value, out int oy))
            {
                origin = new Vector2(ox, oy);
                continue;
            }

            match = DecoStemLine.Match(line);
            if (match.Success)
            {
                if (col >= 0 && row >= 0)
                {
                    stems[match.Groups[1].Value] = (col, row, origin);
                }

                continue;
            }

            match = DecoTileKeyLine.Match(line);
            if (match.Success
                && int.TryParse(match.Groups[1].Value, out int keyCol)
                && int.TryParse(match.Groups[2].Value, out int keyRow))
            {
                col = keyCol;
                row = keyRow;
                origin = new Vector2(0f, 58f);
            }
        }

        if (stems.Count > 0)
        {
            return stems;
        }

        // 文本解析不出来（导出把 text resources 转成 binary .res 时会这样）→ 退回资源 API。
        TileSet? tileSet = GD.Load<TileSet>(DecoTilesetPath);
        if (tileSet is null)
        {
            GD.PushWarning($"[iso] {DecoTilesetPath} unreadable both as text and as TileSet");
            return stems;
        }

        for (int i = 0; i < tileSet.GetSourceCount(); i++)
        {
            if (tileSet.GetSource(tileSet.GetSourceId(i)) is not TileSetAtlasSource atlas)
            {
                continue;
            }

            for (int t = 0; t < atlas.GetTilesCount(); t++)
            {
                Vector2I coords = atlas.GetTileId(t);
                TileData? data = atlas.GetTileData(coords, 0);
                if (data is null || !data.HasMeta("stem"))
                {
                    continue;
                }

                Variant stem = data.GetMeta("stem");
                if (stem.VariantType == Variant.Type.String)
                {
                    stems[stem.AsString()] = (coords.X, coords.Y, new Vector2(0f, 58f));
                }
            }
        }

        viaTileSetResource = stems.Count > 0;
        return stems;
    }

    /// <summary>
    /// stem → 缩放倍率：查 <see cref="DecoScaleByStem"/>，未登记的 stem 回落到
    /// <see cref="DecoScaleDefault"/>（1.0，宁可小不放大）。找不到就是找不到，绝不做关键字猜测。
    /// </summary>
    private static float DecoScaleFor(string stem)
        => DecoScaleByStem.TryGetValue(stem, out float scale) ? scale : DecoScaleDefault;

    /// <summary>
    /// 画一条装饰：脚底钉在 <see cref="GridToScreen"/> + <see cref="DecoFootOffset"/>，
    /// 按 <see cref="DecoScaleFor"/> 放大（脚底不动 → 自然"往上长"）。
    /// 先画 4 个方向的深色偏移（1 世界像素），再画本体 —— 这就是那圈 1px 轮廓，
    /// 不用 shader：同一张图集、只换位置和 modulate。
    /// </summary>
    private void DrawDeco(int index)
    {
        if (_decoAtlas is null)
        {
            return;
        }

        RefblockMapLoader.DecoPlacement placement = _deco[index];
        bool inAtlas = _stems.TryGetValue(placement.Stem, out (int Col, int Row, Vector2 Origin) tile);
        bool loose = !inAtlas && _looseProps.TryGetValue(placement.Stem, out Texture2D? looseTex);
        if (!inAtlas && !loose)
        {
            return;
        }

        // Loose props are authored at their final in-world size (a 128x288 tree is 4.5 tiles
        // tall), so they bypass the atlas scale table; atlas deco is a 128x128 cell and needs
        // the per-keyword upscale to read as bushes / stones.
        float scale = loose ? 1.0f : DecoScaleFor(placement.Stem);
        float cell = _decoCell;
        // Atlas deco is one square cell; loose props (trees) carry their own pixel size,
        // which is how a 4-6 tile tall tree is possible inside a 128 px atlas cell world.
        Texture2D tex = inAtlas ? _decoAtlas! : _looseProps[placement.Stem];
        var native = inAtlas
            ? new Vector2(cell, cell)
            : new Vector2(tex.GetWidth(), tex.GetHeight());
        var size = new Vector2(native.X * scale, native.Y * scale);
        Vector2 foot = GridToScreen(placement.X, placement.Y) + DecoFootOffset;
        Vector2 topLeft = foot - new Vector2(size.X * 0.5f, size.Y);
        var src = inAtlas
            ? new Rect2(tile.Col * cell, tile.Row * cell, cell, cell)
            : new Rect2(0f, 0f, native.X, native.Y);

        // 玩家 1.5 格内的装饰淡化到 0.25（值由 UpdateDecoFade 逐帧线性推进，0.12 s 一趟）。
        // 淡化时不描边：那圈 1px 深色轮廓会把 25% 不透明的本体读成一个"黑框亮点"。
        float alpha = index < _decoFadeAlphas.Length ? _decoFadeAlphas[index] : 1f;
        if (alpha < 0.999f)
        {
            DrawTextureRectRegion(tex, new Rect2(topLeft, size), src, new Color(1f, 1f, 1f, alpha));
            _drawDeco++;
            _drawProps++;
            _decoFade++;
            return;
        }

        // Loose props always keep their outline: their scale is pinned to 1.0, so gating on
        // DecoOutlineMinScale used to skip the outline for every tree and let the canopy melt
        // into the grass.
        if (loose || scale >= DecoOutlineMinScale)
        {
            foreach (Vector2 offset in DecoOutlineOffsets)
            {
                DrawTextureRectRegion(tex, new Rect2(topLeft + offset, size), src, DecoOutlineColor);
                _drawDeco++;
                _drawProps++;
            }
        }

        DrawTextureRectRegion(tex, new Rect2(topLeft, size), src, Colors.White);
        _drawDeco++;
        _drawProps++;
    }

    private void DrawPlayer(float gx, float gy)
    {
        if (_player is null)
        {
            return;
        }

        Vector2 center = GridToScreen(gx, gy);
        int facing = _player.IsoFacing;
        Texture2D[] frames = facing switch
        {
            0 => _playerDown,
            2 => _playerUp,
            _ => _playerSide,
        };

        bool moving = _player.IsoMoving;
        int frame = moving ? (int)(_animTime * 6.0) % frames.Length : 0;
        Texture2D tex = frames[frame];
        // 玩家身高基准：Faune 帧是 32x32，scale 2.25 => 72 px = 1.125 格（1 格 = 64 px）。
        // 之前 1.5（48 px = 0.75 格）时 树/人 = 6.7 倍，参考图约 4 倍，人物显得像个豆子。
        const float scale = 2.25f;
        bool flip = facing == 3;

        DrawShadow(center + new Vector2(0, -2), 10f, 5f);
        DrawSetTransform(center + new Vector2(0, -2), 0f, new Vector2(flip ? -scale : scale, scale));
        DrawTexture(tex, new Vector2(-tex.GetWidth() * 0.5f, -tex.GetHeight() + 2));
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    private void DrawShadow(Vector2 center, float rx, float ry)
    {
        const int segments = 14;
        var pts = new Vector2[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = Mathf.Tau * i / segments;
            pts[i] = center + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
        }

        DrawColoredPolygon(pts, new Color(0f, 0f, 0f, 0.22f));
    }
}
