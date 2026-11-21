# `props/deco/` —— 等距装饰物件库

> 由 `tools/art/gen_deco.py` 生成并校验；重跑 `python tools/art/gen_deco.py all` 可整体重建。
> 色板：地形族见 `docs/art/PALETTE-BOLD.md`，花/果扩展族见 `docs/art/PALETTE-DECO.md`。

## 1. 通用约定

| 项 | 约定 |
| --- | --- |
| 锚点 | **底边中心**：图片最底一行必有像素，那一行中点是物件与格点的接触点 |
| 放置 | `screen = tile_center + (dx - w/2, dy - h)`，`dy` 就是格点；屏幕偏移 `(0, 6)` |
| 格子 | 等距 2:1，**TileW=128 TileH=64**（地砖 ×2 后的尺寸，与 `<其他>/ground/` 同步） |
| alpha | **二值**：只有 0 / 255，没有任何半透明像素（无灰边、无白边） |
| 描边 | 1 px，色值取该物件 `outline` 族的 `d3`，不画黑边 |
| 接触阴影 | 底部 2–3 px **实心暖色**阴影（族的 d3 推两档，最底 1 px 更暗）；alpha 全 255 |
| 色数 | **8–10 档本体**（≤13 含阴影与描边；2026-11-16 由 ≤6 提高） |
| 过滤 | Godot 导入需 **Nearest**、无 mipmap、无压缩（`filter=false`） |

## 2. 物件清单

| 文件 | 尺寸 | 高度档 | 用途 | 建议散布密度 | 来源 | 色数 | 阴影 | QA |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `barrel.png` | 50x61 | 60 | 木桶（装水/酒/腌菜） | 0.02 / tile | AI | 12 | 有 74 px | OK |
| `barrel_a.png` | 49x61 | 60 | 木桶（装水/酒/腌菜） | 0.02 / tile | AI | 12 | 有 70 px | OK |
| `barrel_b.png` | 48x62 | 60 | 木桶（装水/酒/腌菜） | 0.02 / tile | AI | 11 | 有 70 px | OK |
| `bench.png` | 64x42 | 40 | 木长凳（可坐） | 0.02 / tile | AI | 11 | 有 112 px | OK |
| `bench_a.png` | 65x42 | 40 | 木长凳（可坐） | 0.02 / tile | AI | 13 | 有 110 px | OK |
| `bench_b.png` | 65x42 | 40 | 木长凳（可坐） | 0.02 / tile | AI | 13 | 有 114 px | OK |
| `bridge_plank.png` | 128x80 | 80 | 木桥桥面（1 格菱形板，沿 -y 每格一块） | per bridge tile | 程序化 | 5 | - | OK |
| `bridge_plank_a.png` | 128x80 | 80 | 木桥桥面（1 格菱形板，沿 -y 每格一块） | per bridge tile | 程序化 | 5 | - | OK |
| `bridge_plank_b.png` | 128x80 | 80 | 木桥桥面（1 格菱形板，沿 -y 每格一块） | per bridge tile | 程序化 | 5 | - | OK |
| `bridge_rail.png` | 128x128 | 128 | 木桥栏杆（柱 + 扶手） | per bridge | 程序化 | 4 | - | OK |
| `bridge_rail_a.png` | 128x128 | 128 | 木桥栏杆（柱 + 扶手） | per bridge | 程序化 | 4 | - | OK |
| `bridge_rail_b.png` | 128x128 | 128 | 木桥栏杆（柱 + 扶手） | per bridge | 程序化 | 4 | - | OK |
| `bush_flowering.png` | 103x96 | 94 | 带白花的灌木 | 0.06 / tile | AI | 12 | 有 215 px | OK |
| `bush_flowering_a.png` | 106x96 | 94 | 带白花的灌木 | 0.06 / tile | AI | 12 | 有 221 px | OK |
| `bush_flowering_b.png` | 101x95 | 94 | 带白花的灌木 | 0.06 / tile | AI | 12 | 有 212 px | OK |
| `bush_green.png` | 105x98 | 96 | 圆团灌木 | 0.10 / tile | AI | 9 | 有 227 px | OK |
| `bush_green_a.png` | 106x98 | 96 | 圆团灌木 | 0.10 / tile | AI | 9 | 有 249 px | OK |
| `bush_green_b.png` | 112x98 | 96 | 圆团灌木 | 0.10 / tile | AI | 9 | 有 228 px | OK |
| `crate.png` | 50x52 | 50 | 木箱（仓储/运输） | 0.02 / tile | AI | 9 | 有 90 px | OK |
| `crate_a.png` | 50x52 | 50 | 木箱（仓储/运输） | 0.02 / tile | AI | 10 | 有 94 px | OK |
| `crate_b.png` | 50x52 | 50 | 木箱（仓储/运输） | 0.02 / tile | AI | 10 | 有 92 px | OK |
| `daisy_single.png` | 40x49 | 48 | 单株小雏菊 | 0.30 / tile | AI | 12 | 有 48 px | OK |
| `daisy_single_a.png` | 39x49 | 48 | 单株小雏菊 | 0.30 / tile | AI | 12 | 有 36 px | OK |
| `daisy_single_b.png` | 39x50 | 48 | 单株小雏菊 | 0.30 / tile | AI | 11 | 有 40 px | OK |
| `fence_post.png` | 28x54 | 52 | 围栏立柱（沿 iso 边每 1 格一根） | 0.15 / tile edge | 程序化 | 6 | 有 44 px | OK |
| `fence_post_a.png` | 30x54 | 52 | 围栏立柱（沿 iso 边每 1 格一根） | 0.15 / tile edge | 程序化 | 6 | 有 48 px | OK |
| `fence_post_b.png` | 24x54 | 52 | 围栏立柱（沿 iso 边每 1 格一根） | 0.15 / tile edge | 程序化 | 6 | 有 38 px | OK |
| `fence_rail.png` | 128x128 | 128 | 围栏双横杆（沿 iso +x 方向，横翻得到另一方向） | per fence span | 程序化 | 5 | - | OK |
| `fence_rail_a.png` | 128x128 | 128 | 围栏双横杆（沿 iso +x 方向，横翻得到另一方向） | per fence span | 程序化 | 5 | - | OK |
| `fence_rail_b.png` | 128x128 | 128 | 围栏双横杆（沿 iso +x 方向，横翻得到另一方向） | per fence span | 程序化 | 5 | - | OK |
| `flower_pink_hydrangea.png` | 104x96 | 94 | 粉色绣球团 | 0.06 / tile | AI | 12 | 有 231 px | OK |
| `flower_pink_hydrangea_a.png` | 107x96 | 94 | 粉色绣球团 | 0.06 / tile | AI | 12 | 有 157 px | OK |
| `flower_pink_hydrangea_b.png` | 101x96 | 94 | 粉色绣球团 | 0.06 / tile | AI | 11 | 有 221 px | OK |
| `flower_purple_spike.png` | 52x96 | 94 | 直立紫色羽扇豆穗 | 0.08 / tile | AI | 12 | 有 138 px | OK |
| `flower_purple_spike_a.png` | 50x96 | 94 | 直立紫色羽扇豆穗 | 0.08 / tile | AI | 12 | 有 117 px | OK |
| `flower_purple_spike_b.png` | 52x96 | 94 | 直立紫色羽扇豆穗 | 0.08 / tile | AI | 12 | 有 132 px | OK |
| `flower_white_cluster.png` | 57x65 | 64 | 草地/路边白花丛，最常用的点缀 | 0.20 / tile | AI | 12 | 有 84 px | OK |
| `flower_white_cluster_a.png` | 57x66 | 64 | 草地/路边白花丛，最常用的点缀 | 0.20 / tile | AI | 12 | 有 96 px | OK |
| `flower_white_cluster_b.png` | 56x66 | 64 | 草地/路边白花丛，最常用的点缀 | 0.20 / tile | AI | 12 | 有 80 px | OK |
| `flower_yellow_cluster.png` | 53x66 | 64 | 黄花丛，和白花丛交错 | 0.15 / tile | AI | 10 | 有 86 px | OK |
| `flower_yellow_cluster_a.png` | 49x65 | 64 | 黄花丛，和白花丛交错 | 0.15 / tile | AI | 11 | 有 74 px | OK |
| `flower_yellow_cluster_b.png` | 55x65 | 64 | 黄花丛，和白花丛交错 | 0.15 / tile | AI | 11 | 有 82 px | OK |
| `grass_tuft_a.png` | 47x49 | 48 | 草簇（尖细） | 0.45 / tile | AI | 10 | 有 42 px | OK |
| `grass_tuft_a_a.png` | 47x49 | 48 | 草簇（尖细） | 0.45 / tile | AI | 9 | 有 46 px | OK |
| `grass_tuft_a_b.png` | 51x50 | 48 | 草簇（尖细） | 0.45 / tile | AI | 8 | 有 48 px | OK |
| `grass_tuft_b.png` | 78x49 | 48 | 草簇（低阔） | 0.35 / tile | AI | 9 | 有 124 px | OK |
| `grass_tuft_b_a.png` | 84x50 | 48 | 草簇（低阔） | 0.35 / tile | AI | 9 | 有 134 px | OK |
| `grass_tuft_b_b.png` | 85x49 | 48 | 草簇（低阔） | 0.35 / tile | AI | 9 | 有 136 px | OK |
| `lamp_post.png` | 39x102 | 100 | 路灯（夜晚照明） | 0.03 / tile | AI | 12 | 有 64 px | OK |
| `lamp_post_a.png` | 39x102 | 100 | 路灯（夜晚照明） | 0.03 / tile | AI | 12 | 有 64 px | OK |
| `lamp_post_b.png` | 39x102 | 100 | 路灯（夜晚照明） | 0.03 / tile | AI | 12 | 有 64 px | OK |
| `lily_pad.png` | 52x34 | 32 | 睡莲叶（带缺口） | 0.12 / tile (on water) | 程序化 | 6 | 有 68 px | OK |
| `lily_pad_a.png` | 52x34 | 32 | 睡莲叶（带缺口） | 0.12 / tile (on water) | 程序化 | 6 | 有 68 px | OK |
| `lily_pad_b.png` | 52x34 | 32 | 睡莲叶（带缺口） | 0.12 / tile (on water) | 程序化 | 6 | 有 68 px | OK |
| `log_fallen.png` | 111x63 | 62 | 倒木 | 0.03 / tile | AI | 13 | 有 160 px | OK |
| `log_fallen_a.png` | 115x63 | 62 | 倒木 | 0.03 / tile | AI | 12 | 有 190 px | OK |
| `log_fallen_b.png` | 107x63 | 62 | 倒木 | 0.03 / tile | AI | 13 | 有 172 px | OK |
| `lotus_pink.png` | 77x63 | 62 | 荷花（粉） | 0.03 / tile (on water) | AI | 12 | 有 88 px | OK |
| `lotus_pink_a.png` | 80x64 | 62 | 荷花（粉） | 0.03 / tile (on water) | AI | 13 | 有 96 px | OK |
| `lotus_pink_b.png` | 81x64 | 62 | 荷花（粉） | 0.03 / tile (on water) | AI | 8 | 有 98 px | OK |
| `mushroom_red.png` | 50x50 | 48 | 蘑菇（红） | 0.05 / tile | AI | 13 | 有 42 px | OK |
| `mushroom_red_a.png` | 50x48 | 48 | 蘑菇（红） | 0.05 / tile | AI | 11 | 有 50 px | OK |
| `mushroom_red_b.png` | 49x50 | 48 | 蘑菇（红） | 0.05 / tile | AI | 12 | 有 44 px | OK |
| `reed_cattail.png` | 62x96 | 94 | 水边芦苇/香蒲 | 0.10 / tile (only on shore) | AI | 12 | 有 126 px | OK |
| `reed_cattail_a.png` | 65x96 | 94 | 水边芦苇/香蒲 | 0.10 / tile (only on shore) | AI | 12 | 有 151 px | OK |
| `reed_cattail_b.png` | 65x96 | 94 | 水边芦苇/香蒲 | 0.10 / tile (only on shore) | AI | 12 | 有 95 px | OK |
| `sign.png` | 47x71 | 68 | 木牌（路标/商店招牌） | 0.01 / tile | AI | 11 | 有 31 px | OK |
| `sign_a.png` | 48x71 | 68 | 木牌（路标/商店招牌） | 0.01 / tile | AI | 12 | 有 28 px | OK |
| `sign_b.png` | 47x70 | 68 | 木牌（路标/商店招牌） | 0.01 / tile | AI | 10 | 有 28 px | OK |
| `stone_large.png` | 104x98 | 96 | 石块（大） | 0.02 / tile | AI | 11 | 有 255 px | OK |
| `stone_large_a.png` | 106x98 | 96 | 石块（大） | 0.02 / tile | AI | 11 | 有 237 px | OK |
| `stone_large_b.png` | 111x98 | 96 | 石块（大） | 0.02 / tile | AI | 12 | 有 267 px | OK |
| `stone_mid.png` | 77x62 | 62 | 石块（中） | 0.08 / tile | AI | 12 | 有 124 px | OK |
| `stone_mid_a.png` | 79x63 | 62 | 石块（中） | 0.08 / tile | AI | 12 | 有 138 px | OK |
| `stone_mid_b.png` | 75x63 | 62 | 石块（中） | 0.08 / tile | AI | 11 | 有 124 px | OK |
| `stone_small.png` | 57x49 | 48 | 碎石（小） | 0.25 / tile | AI | 10 | 有 80 px | OK |
| `stone_small_a.png` | 60x49 | 48 | 碎石（小） | 0.25 / tile | AI | 10 | 有 86 px | OK |
| `stone_small_b.png` | 61x49 | 48 | 碎石（小） | 0.25 / tile | AI | 10 | 有 90 px | OK |
| `stone_step.png` | 69x33 | 32 | 石阶（连接不同高度的路） | 0.05 / tile | AI | 12 | 有 126 px | OK |
| `stone_step_a.png` | 70x33 | 32 | 石阶（连接不同高度的路） | 0.05 / tile | AI | 11 | 有 128 px | OK |
| `stone_step_b.png` | 70x33 | 32 | 石阶（连接不同高度的路） | 0.05 / tile | AI | 11 | 有 126 px | OK |
| `tree_stump.png` | 74x65 | 64 | 树桩（带年轮） | 0.03 / tile | AI | 12 | 有 100 px | OK |
| `tree_stump_a.png` | 75x65 | 64 | 树桩（带年轮） | 0.03 / tile | AI | 13 | 有 82 px | OK |
| `tree_stump_b.png` | 74x65 | 64 | 树桩（带年轮） | 0.03 / tile | AI | 13 | 有 86 px | OK |

## 3. 变体说明

同一 base name 的 `_a/_b/_c` 是**同一物件的三种姿态**，散布时随机取，避免重复感；
花丛/草簇/碎石/石块是密铺主力，必须至少 2 个变体（本条已满足）。

## 4. 拼段说明（围栏 / 木桥）

这两组是**程序化**画的（AI 出不来稳定的直线），保证可拼：

| 文件 | 拼法 |
| --- | --- |
| `fence_post.png` | 沿 iso 边每 1 格立在格点；底边中心即格点 |
| `fence_rail.png` | 双横杆，沿 **iso +x**（屏幕右下）跨越 1 格；`.flip_h` 得到 iso +y 方向；左端 x=0 与右端 x=127 可无缝接下一段（128 宽 = 2× 旧 64） |
| `bridge_plank.png` | 桥面板，沿 iso +x 平铺，上下相邻各错 1 格宽 |
| `bridge_rail.png` | 桥栏杆（柱 + 扶手），与 `bridge_plank` 同 x 平铺，放在桥面两侧 |

> 这三件**不加接触阴影**：拼接件的 canvas 一旦向下长出行，x 平铺的对齐行就会错位。
> 它们靠 1 px d3 描边 + 板缝暗线接地。

## 5. 2026-11-16 新增的 6 类道具

| 物件 | 目标尺寸（×2 后） | 用途 |
| --- | --- | --- |
| `barrel` | 52×60 | 木桶：门口/码头堆货 |
| `crate` | 56×50 | 木箱：可堆叠的储物 |
| `sign` | 44×68 | 木牌：路标 / 商店招牌 |
| `lamp_post` | 36×100 | 路灯：夜里点灯，参照图里的灯柱 |
| `bench` | 72×40 | 木长凳：广场/码头休息点 |
| `stone_step` | 64×32 | 石阶：踩出来的石板路，可连成小径 |

## 6. 密度参数建议

`density` 列是「每格出现概率」。实际散布建议：

- 花草类用 **Poisson 采样 + 最小间距 12 px**，不要均匀随机（均匀会出现条带）。
- 岸边 2 格内 `reed_cattail` 权重 × 3；水面 `lily_pad` 只在离岸 ≥ 1 格的浅水。
- 同一格最多叠 1 个「大件」（灌木/石块/树桩），小件（草簇/雏菊/碎石）最多 3 个。
