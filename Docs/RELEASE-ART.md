# RimLayers 发布美术

2026-10-20 首批，2026-10-21 补充物品吊篮，2026-10-22 补充分层环境、深层生态与载具兼容 · 内置 `image_gen.imagegen` 生图工具 · 共 13 张 PNG，均已检查文字、数值与画面。

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
| [物品吊篮](Images/Release/09-cargo-hoists.png) | 存取流程及手动／电动、相邻／竖井介绍卡 | 1536 × 1024 |
| [分层环境](Images/Release/10-layered-environment.png) | 高层暴露、岩温缓冲、通风污染与耀斑屏蔽 | 1536 × 1024 |
| [深层矿物与生态](Images/Release/11-deep-geology-ecology.png) | 深度预算、矿种倾向、熔岩与虫巢生命周期 | 1536 × 1024 |
| [载具兼容](Images/Release/12-vehicle-compatibility.png) | 整车坡道、9×9 载具升降机与连续跨层飞行 | 1536 × 1024 |

封面与卡片采用同一套深石墨底色、暖白标题、琥珀指引与青色设备强调，画面为手绘等距剖面。中文卡片已嵌入 [中文 README](../README.zh-CN.md)，英文卡片已嵌入 [English README](../README.md)；封面使用常规 `About/Preview.png` 位置。图片存放在 About / Docs 下，不加入游戏纹理加载目录。

## 使用与更新

- 这些图片是概念示意，游戏采用俯视切层显示。封面和各卡片均已在图内标注，README 也明确说明显示方式。
- 环境与生态两张卡以已实现的环境查询、默认生成参数和虫巢生命周期为依据；B1／B5／B9 为取样深度，不画成相邻三层。提示词直接描述既有美术语言，新生成时未输入参考图片；原有成品保持不变。
- 载具卡以钢铁地平线 4.9.2 的已验收能力为依据：坡道模块并排加宽、单车轿厢、断电续行、连续飞行及指定层返航。画面不表示原版 Vehicle Framework 1.7 已受支持。生成前查看既有环境卡与电梯卡，生图仅使用文字描述风格，没有输入参考图片。
- 原 8 张卡片覆盖 6 种通行与供电建筑及 2 种楼板；新增吊篮卡介绍 7 种可建构件组成的手动／电动、相邻／竖井组合与共同存取流程。
- 名称依据简中 DefInjected；材料、研究与耗电依据 `Defs/ThingDefs_Buildings/`、`Defs/TerrainDefs/RimLayers_Structure.xml`、`RimLayers_ShipFloor.xml`。通行与支撑说明同时核对地下、电梯及飞船共存指南。
- 上下行建筑卡片使用当前 mod 自有楼梯 / 电梯图标作为设计参考；电力端子按现有开关型定义作示意，没有导出原版素材。吊篮卡以既有电梯卡为风格参考、已验证的游戏截图为木架／电机与停靠状态参考；其余场景为新生成画面。
- [imagegen-prompts.json](Release/imagegen-prompts.json) 保存每张成品的完整最终提示词、参考图片角色顺序、模式及尺寸。参考路径均相对 mod 根目录；吊篮的实机参考图位于对应独立测试产物目录。未使用 CLI 或额外 API 密钥。
- 更新建筑名称、配方、研究、耗电或通行规则时，一并更新对应卡片与 README。重生成时保留原文件，先检查新图的中文与数值，再替换交付版。

原封面生成提示要求 1536 方图，工具实际返回 1254 方图，采用返回原件；12 张卡片均为 1536 × 1024。未做裁切、重绘或后期图像编辑。


## English edition · 2026-10-22

The English [README](../README.md) uses a separately generated set of 13 localized illustrations: [cover](Images/Release/en/00-cover.png) and all 12 feature cards in `Images/Release/en/`. The original Chinese artwork, including `About/Preview.png`, remains unchanged and is used by [README.zh-CN.md](../README.zh-CN.md).

Generated with the built-in `image_gen.imagegen` tool in text-localization edit mode, using each Chinese original as its edit target. The English set retains the original artwork, visual hierarchy and gameplay values. All 13 outputs were visually inspected for English text, costs, units, clipping and diagram consistency. The cover is 1254 × 1254; the cards are 1536 × 1024. No CLI, extra API key or post-generation image editing was used.

Complete prompts, source-image roles and output paths: [imagegen-prompts.en.json](Release/imagegen-prompts.en.json).
