# RimLayers 发布美术

2026-10-20 · 内置 `image_gen.imagegen` 生图工具 · 9 张 PNG，均已逐张检查文字、材料数值与画面。

## 文件与用途

| 成品 | 用途 | 实际尺寸 |
|---|---|---|
| [Preview.png](../About/Preview.png) | mod 封面与 README 头图 | 1254 × 1254 |
| [向上楼梯](Images/Release/01-stairs-up.png) | 建筑介绍卡 | 1536 × 1024 |
| [向下楼梯](Images/Release/02-stairs-down.png) | 建筑介绍卡 | 1536 × 1024 |
| [单层电梯](Images/Release/03-single-lift.png) | 建筑介绍卡 | 1536 × 1024 |
| [向下单层电梯](Images/Release/04-single-lift-down.png) | 建筑介绍卡 | 1536 × 1024 |
| [垂直电梯站](Images/Release/05-vertical-lift.png) | 建筑介绍卡 | 1536 × 1024 |
| [垂直电力端子](Images/Release/06-power-riser.png) | 建筑介绍卡 | 1536 × 1024 |
| [结构楼板](Images/Release/07-structural-floor.png) | 建筑介绍卡 | 1536 × 1024 |
| [飞船结构楼板](Images/Release/08-ship-structural-floor.png) | 建筑介绍卡 | 1536 × 1024 |

封面与卡片采用同一套深石墨底色、暖白标题、琥珀指引与青色设备强调，画面为手绘等距剖面。全部卡片已嵌入 [README](../README.md)；封面使用常规 `About/Preview.png` 位置。图片存放在 About / Docs 下，不加入游戏纹理加载目录。

## 使用与更新

- 这些图片是概念示意，游戏采用俯视切层显示。封面和各卡片均已在图内标注，README 也明确说明显示方式。
- 卡片覆盖 6 种 ThingDef 建筑：向上楼梯、向下楼梯、单层电梯、向下单层电梯、垂直电梯站、垂直电力端子；另含结构楼板、飞船结构楼板两种 TerrainDef。
- 名称依据简中 DefInjected；材料、研究与耗电依据 `Defs/ThingDefs_Buildings/`、`Defs/TerrainDefs/RimLayers_Structure.xml`、`RimLayers_ShipFloor.xml`。通行与支撑说明同时核对地下、电梯及飞船共存指南。
- 上下行建筑卡片使用当前 mod 自有楼梯 / 电梯图标作为设计参考；电力端子按现有开关型定义作示意，没有导出原版素材。其余场景为新生成画面。
- [imagegen-prompts.json](Release/imagegen-prompts.json) 保存每张成品的完整最终提示词、参考图片角色顺序、模式及尺寸。参考路径均相对 mod 根目录；实际调用的参考图来自同一文件的生成原件。未使用 CLI 或额外 API 密钥。
- 更新建筑名称、配方、研究、耗电或通行规则时，一并更新对应卡片与 README。重生成时保留原文件，先检查新图的中文与数值，再替换交付版。

原封面生成提示要求 1536 方图，工具实际返回 1254 方图，采用返回原件；8 张卡片均为 1536 × 1024。未做裁切、重绘或后期图像编辑。

