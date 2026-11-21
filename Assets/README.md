# Assets 目录说明

> 本文件是美术资产的**唯一入口**。新增素材前先读一遍，别再把东西丢在 `Assets/` 根上。
> 目录结构最后一次整理：2026-11-16。

## 1. 目录结构与用途

```
Assets/
  tiles/iso/                我们自己的等距（2:1 菱形）贴图 —— 唯一的运行时地面/物件来源
    ground/<kind>/          六种地面：grass soil tilled water sand path
      <kind>_<v>.png        每格 64x32 菱形，透明底，v = 1/2/3 三个变体
      edge_<kind>_<dir>.png 过渡覆盖层，dir = ne nw se sw，同尺寸透明底
    props/                  物件：tree.png rock.png weed.png（脚底锚点 = 图片底部中心）
  chars/                    角色帧（贴图，不含 .tres）
    player/                 ansimuz 的 32x32 四向八帧：player_walk_<down|up|side>_<1..8>.png
  ui/                       图标、按钮、字体（目前空，占位）
  maps/                     地图数据（.json / .tmx，目前空，占位）
  _input/                   原始 CC0 素材包，**原样保留，不要改里面的文件**
    <pack>/                 一个包一个目录，连 LICENSE.txt 一起放
  README.md                 本文件
```

### 为什么原始包要放在 `_input/` 而不是 `input/`

下划线前缀是三点好处：在按名字排序的资源面板里排在最前 / 最底层；一眼能看出"这是未加工原料，不是成品"；
`_input` 不会被当成成品目录被代码误引用（`res://Assets/_input/...` 长得很显眼，作者会意识到自己绕过了成品区）。

**规则：游戏代码只允许从 `tiles/ chars/ ui/ maps/` 读资源。`_input/` 只给美术取材用。**
唯一的例外是两个 DevPreview 技术样片（`DevPreview/IsoSample.cs`）—— 它们本来就是用来对比原始包的。

## 2. 命名规范

| 类型 | 规范 | 例子 |
| --- | --- | --- |
| 地面贴图 | `<kind>_<variant>.png`，**64x32** | `grass_2.png`、`tilled_1.png` |
| 过渡覆盖层 | `edge_<kind>_<dir>.png`，**64x32** | `edge_water_se.png` |
| 物件贴图 | `<prop>.png`，尺寸自定，**脚底锚点 = 图片底部中心** | `tree.png`、`rock.png` |
| 角色帧 | `<char>_<anim>_<dir>_<frame>.png`，帧从 1 起 | `player_walk_down_1.png` |
| 地图数据 | `<map>.json`（字段见 `Game.Core/Save`） | `farm-default.json` |

硬性约束：

1. **地面/过渡贴图必须是 64x32、透明底**。它们会被按 `中心 - (32,16)` 摆到菱形格上，尺寸差一像素就会露缝。
2. **菱形四角正好落在 (32,0) (64,16) (32,32) (0,16)**，相邻格共享的边界像素也要覆盖（我们留了约 0.3px 的重叠），
   否则缩放到非整数倍时会看到 1px 的亮线。
3. 贴图必须**自身可无缝平铺**：64x32 菱形正好是等距格点阵的一个周期，相邻格在 (u,v) 上偏移 ±1，
   所以贴图里的任何纹理只能是 `cos(π·ka·(u+v))·cos(π·kb·(u-v))` 这种整数谐波叠加 —— 生成脚本
   `tools/art/gen_iso_tiles.py` 已按这条规则写死，手工画图时也要遵守（拿 3x3 拼一下看有没有接缝）。
4. 物件贴图**把柔和阴影烘焙进图里**，渲染器不再另画影子。锚点在底部中心：渲染时图片底边贴在菱形中心。
5. 别在文件名里留空格和中文（Godot 能处理，但命令行和 `git mv` 会很难受）。

## 3. 新素材该放哪

- 自己画的等距地面/物件 → `tiles/iso/ground/<kind>/` 或 `tiles/iso/props/`，用上面的命名，然后
  `python tools/art/gen_iso_tiles.py --check` 之外再跑一次 `--headless --import`。
- 下载来的第三方包 → 整个包丢进 `_input/<作者-包名>/`，**连 LICENSE 一起**，再在 `docs/art/CREDITS.md`
  补一行（源 URL / 作者 / 许可证 / 下载日期）。**没有 LICENSE 的包不许进仓库。**
- 从 `_input/` 里挑出来要用的帧 → 复制（不要移动）到 `chars/` 或 `tiles/iso/` 并重命名，原始包保持完整。
- 图标/字体 → `ui/`；地图数据 → `maps/`。

## 4. 来源包一览（全部 CC0 1.0，详见 `docs/art/CREDITS.md`）

| 目录 | 作者 | 一句话 | 是否已接入引擎 |
| --- | --- | --- | --- |
| `_input/ansimuz-legend-of-faune/` | Luis Zuno (@ansimuz) | 32x32 四向 8 帧走路角色 + 分层树 + 海面动画帧 | ✅ 角色帧已复制到 `chars/player/` 并被 `IsoWorldRenderer` 使用 |
| `_input/lemmi-32x32-water-land/` | Lemmi | 最完整的 32x32 地面 wang 过渡（草/沙/泥/水） | ⚠️ 仅 `DevPreview/IsoSample.cs` 技术样片在用；正式渲染已换成自绘贴图 |
| `_input/mage-city-arcanos/` | Hyptosis | 村庄建筑/装饰/多格树，行不对齐 32 需手工切 | ❌ 未接入 |
| `_input/aeren108-outdoor-32/` | aeren108 | 极简明亮草/泥/水 3x3 过渡 | ❌ 未接入 |
| `_input/buch-outdoor-32/` | Buch | 深色洞穴向石板 + 草地 | ❌ 未接入（`sheet_13.png` 与 `sheet_12.png` 内容重复） |
| `_input/denzi-cc0-32/` | DENZI | 地牢地板/怪物/武器/道具图标 | ❌ 未接入 |
| `_input/dcss-32x32/` | DCSS 社区（Chris Hamons 维护） | 3000+ 地牢瓦片；透明色烘焙成洋红，用前要处理 | ❌ 未接入 |
| `_input/scratchio-farming-set/` | ScratchIO | 作物分阶段图 / 农具 / 收获物（非网格） | ❌ 未接入，将来作物系统用 |

## 5. 我们自己产出的贴图

`tiles/iso/` 下的 45 张 PNG 全部由 `tools/art/gen_iso_tiles.py` 生成（纯 Pillow，可重复运行，
输出确定性，无随机种子漂移）。色板取自 `tmp/styles/B-soft.png`，记录在 `docs/art/PALETTE-B.md`。
**改风格就改脚本重跑，不要手改 PNG** —— 手改的图下次生成就没了。
