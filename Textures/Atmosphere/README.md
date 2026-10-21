# ThinMist：透明薄雾素材

- 文件：[ThinMist.png](ThinMist.png)，内容路径 `Atmosphere/ThinMist`。
- 来源：2026-10-18，按用户要求使用内置 `image_gen` 生成；`transparent_background=true`。未调用外部 CLI/API，未复制其他 mod 素材。
- 规格：1254×1254 RGBA PNG，保留生成结果的原始 Alpha，未后期修改。Alpha 范围 0–253，均值约 51.34/255；边缘最高 2/255。
- 使用：`Presentation/DriftingMist.cs` 以低透明度材质、错位旋转和缓慢平移叠加；绘制队列位于对应下层背景之后、当前楼面之前。雾不参与模拟或保存。
- 生成输出原文件：`C:/Users/zonre/.codex/generated_images/01a14e9d-f33d-7e03-a45d-b2ab6c34b419/exec-5b8357cd-b521-48d8-b3df-086ae49b7c70.png`。运行只依赖本目录副本。

## 最终生成提示词

```text
Use case: stylized-concept. Asset type: reusable RGBA thin mist sprite for a top-down 2D strategy game's multi-floor view, to be animated as overlapping drifting patches. Generate one square atmospheric fog texture, pale cool ivory white with a faint blue-gray tint. True transparent background and genuinely varying low alpha: mostly transparent empty space, several delicate elongated wisps curling through the center, fine layered vapor with irregular soft feathered edges, a few denser silky filaments, asymmetrical natural wind-swept forms. Wisps fade completely to zero opacity before ALL image edges so patches can overlap seamlessly. Subtle painterly game-art realism, quiet thin morning mist, NOT thick smoke or puffy cartoon clouds. No ground, scenery, colored background, checkerboard pattern, border, frame, text, icons, or light rays. This is an isolated compositing texture, not a rendered landscape; preserve soft internal translucency.
```
