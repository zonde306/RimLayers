# RimLayers 0.1.2：叠层显示与定位修复

2026-10-18；RimWorld **1.7.4981 rev136 / Waylith / .NET 10 / Harmony 3**。主程序集 MVID `f1e6382600a84191bbc6d8a184fd6b57`。本页记录用户反馈的五项修复；[0.1.1](USABILITY-VALIDATION.md) 保留此前可用性与旧档迁移证据。

## 重启游戏即可使用

1. **结构楼板与木地板正常显示**：当前楼面始终盖住下层背景，不再被下层地形材质盖掉。
2. **头像首次跨层定位正确**：先切目标层再执行原生居中，停止旧层的平移与惯性；同层仍保留原版平滑移动。
3. **动态透明薄雾**：使用内置生图工具生成的 RGBA 雾纹理，错位、旋转并缓慢漂移；只覆盖下层背景，不给当前楼面整屏加灰。暂停时仍有轻微流动，不影响天气和 AI。素材、参数和完整提示词见 [ThinMist](../Textures/Atmosphere/README.md)。
4. **双向跨层瞄准正确**：身体面向、原生武器偏移和枪口角度使用逻辑位置；Nanami 自定义渲染器直接传固定角度的情况也在共用武器入口修正。
5. **下层尸体可见**：复用真实 Corpse / InnerPawn 的原生倒卧绘制；被高层楼面遮住或仍在迷雾中的尸体不显示。

本轮没有变更存档格式，不需要新开存档。DLL 已更新，须完整退出并重启游戏；Def 热重载不能载入新补丁。

## 原因与实现入口

| 问题 | 修正 |
|---|---|
| 地板被背景覆盖 | `TerrainDef.PostLoad` 设置 `2000 + renderPrecedence`，只降低背景高度不足以改变所有材质顺序。`BackgroundPass` 给背景材质副本独立队列，并压平背景原生高度；原材质和地形网格不变 |
| 首次居中错误 | 原来在原生跳转后切层，相机已按旧层裁剪。`LayerView` 在 `CameraJumper.JumpLocalInternal` 前切层；跨层用 Cut 并取消旧 CameraPanner，同层不改原生 Pan |
| 灰色滤镜 | `DriftingMist` 用真实透明纹理，按可见范围提交漂移雾片，位于对应下层背景之后、当前楼面之前；不再画全图灰色矩形 |
| 武器与身体朝向 | `LogicalAiming` 修正原生装备向量与 `Pawn_RotationTracker.Face`；共用 DrawEquipmentAiming 通过装备 holder 找回射手，仅修正有效跨层 Stance_Busy。原生 NeverAimWeapon / Portrait 范围、同层与闲置角度、Verb.AimAngleOverride 保留 |
| 尸体及附带武器漏绘 | `LowerFloorBackdrop` 从 DynamicDrawManager.DrawThings 取得真实动态实体，不再只列 Pawn。输入保持真实坐标，在受限背景 pass 的最终网格提交时投影一次，包含躺卧身体和装备；专用弹丸/飞行物仍走原有显示 |

`Graphics.DrawMeshCore` 与 DrawPropertyBlock 的十参数 DrawMesh 是两个独立延迟提交端。只补 GenDraw 会漏掉原生武器和自定义 PostDraw；同时补包装方法和最终提交端又会重复投影。本版只在这两个独立提交端接入背景作用域，显式更新 renderQueue，不修改共享材质，不拦截即时 DrawMeshNow 肖像/图集绘制。

下层呈现继续尊重本层及中间层楼面、原生迷雾和隐藏 Pawn。展示不改变实际位置、Job、目标身份、LOS 或伤害流程。

## 原生游戏与新进程读档

所有测试使用 `Tests/Artifacts` 独立数据目录，不使用玩家存档或启用列表。Release 构建 0 警告、0 错误；JSON 记录实际 activeMods 与程序集基准。

| 组合 | 原始目录 | 生成 / 新进程读档 |
|---|---|---|
| Core + Harmony + RimLayers | [204856](../Tests/Artifacts/v1-presentation-20261018-204856) | **16 / 9，零失败** |
| Core + 全五 DLC + Harmony + RimLayers | [204702](../Tests/Artifacts/v1-presentation-20261018-204702) | **16 / 9，零失败** |
| 全 DLC 真实射击回归 | [205233](../Tests/Artifacts/v1-combat-shot-20261018-205233) | **14 / 9，零失败**；真实跨层弹丸、飞行中保存和一次伤害提交 |
| 全 DLC + HAR + Nanami | [205000](../Tests/Artifacts/v1-presentation-20261018-205000) | **16 / 9 项功能检查通过**；两段的“无运行错误”检查均失败，见下一节 |

场景实际建立三层结构楼面及 5×9 木地板饰面；真实射手站在三层楼面边缘 (64,58)，真实目标位于地面 (74,58)，双方使用原生 Bolt-action rifle、Wait_Combat 和长 Warmup。先检查两把原生 Verb 均能命中目标，保留原生瞄准失效/中断规则。双方实际持续调用原生绘制，观察到高→低 **90°**、低→高 **270°**；另以自定义 worker 使用的固定 70° 入口验证修正，并核对禁止瞄准范围仍保留原角度。

相机覆盖高低两个目标从全部三层出发的首次原生跳转，跨层按默认 Pan 请求，且事先启动旧平移；调用真实 CameraDriver.Update 后仍正确居中。渲染阶段相机固定局部 (65.5,60.5)，其后继续真实相机更新，未屏蔽相机或 Pawn Tick。

两具原生 Kill 产生的 Cow 尸体分别位于开放列与楼面下。验证开放列实际提交 Corpse、覆盖列不可暴露；重新启动后保持相同 Pawn/Corpse ID、死亡事实和木地板/结构底层。下层武器最终提交矩阵还检查其投影坐标、高度及背景队列，防止“角度对但装备仍画在存储带”的漏检。

### 已查看实际画面

- [全 DLC：上层结构/木地板、两层互相瞄准、下层尸体](../Tests/Artifacts/v1-presentation-20261018-204702/generate-presentation-a.png)
- [同镜头稍后：雾片漂移，楼面仍保持清晰](../Tests/Artifacts/v1-presentation-20261018-204702/generate-presentation-b.png)
- [同一存档重启后的真实显示](../Tests/Artifacts/v1-presentation-20261018-204702/load-presentation-a.png)
- [Core 的动态薄雾与楼面遮盖](../Tests/Artifacts/v1-presentation-20261018-204856/generate-presentation-b.png)
- [实际 Nanami 自定义 worker 跨层瞄准](../Tests/Artifacts/v1-presentation-20261018-205000/generate-presentation-a.png)、[重启后](../Tests/Artifacts/v1-presentation-20261018-205000/load-presentation-a.png)

图像确认为楼面完整盖住背景，木纹清晰；低层持枪 Pawn 与倒卧尸体可见，枪口分别朝向对方。两张固定镜头画面中的雾有不均匀丝状透明度及位置变化，而非整屏固定灰色。原生光照/天气随正常模拟变化，不将两帧所有像素差异都归为薄雾动画。

## Nanami：瞄准修复通过，已有外部错误仍保留

只读用户指定 `3822131067/Source/Rendering.cs`：`PawnRenderNodeWorker_Startrail.PostDraw` 根据东西朝向直接传 **70° / 290°**，绕过原生装备角度计算。修复位于 RimLayers 的共用入口，不修改工坊代码、不新增编译或运行依赖。

205000 使用实际自动加入的 Nanami 作为三层射手，观测其真实 PostDraw 调用，并完成双向角度、背景武器、相机、楼面、尸体与保存恢复检查；功能项生成 16 / 读档 9 通过。测试只替换自身场景中的武器为标准步枪，便于与原生对照，未使用玩家角色。

该组合仍报告年龄生成、生命阶段、需求与关系空引用，因此没有整体“零错误通过”。相同类别问题在此前 [不启用 RimLayers 的独立对照](../Tests/Artifacts/nanami-control-20261018-193124/control-result.json) 已复现；保留本次完整错误和失败 JSON，未加忽略列表。需由 Nanami / HAR 组合的维护者修复；不影响 Core / 全官方 DLC 两组的本轮验收。

## 失败探针与验证边界

- 203122 的计数通过，但截图相机漂走，不能作为楼面实际画面证据。定向读取原生 CameraDriver 后确认隐藏测试进程仍会读取屏幕边缘鼠标；仅将本测试 profile 的 edgeScreenScroll 关闭，并加强下一次真实相机更新后的坐标检查，未修改玩家偏好。
- 204419 的装备网格已经投影，但角度为 143° / 217°：夹具射手 (62,58) 的弹道被自身楼板阻挡，原生 Warmup 正常退为携枪姿态。后续移动到合法边缘 (64,58)，明确检查双方 CanHitTargetFrom，保留原生 Tick 和中断规则，204702 / 204856 才作为最终瞄准证据。没有通过冻结 Pawn 来强保瞄准状态。
- 203642 是中间 Core 的 12 / 6 项通过，尚未包含最终提交端与自定义 worker 检查；不替代最终结果。

全套第三方特效、CE、三维显示和长期大规模背景性能仍未验证；0.1.0 方法计时不能当作本版新增绘制的 FPS。飞行物显示继续沿用已有专用入口，不把所有动态对象盲目叠画。

```powershell
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario presentation
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario presentation -AllDlc
```

`-Nanami -Har -AllDlc` 为明确第三方探针；如发生上述外部错误，脚本会如实失败，不自动忽略。
