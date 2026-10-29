# 拒绝说明窗口（错误收集）

玩家被 RimLayers 拒绝时，过去只有一行红字（`RejectInput`），最关键的两种情况甚至完全静默：楼梯的 `StairLayerBootstrap.Prepare` 校验不过直接 `return false`，建造完成时 `ConstructionCommitPatch` 只是 `EndJobWith(JobCondition.Incompletable)` 把工作取消。玩家看到的是「点了没反应」，反馈帖里只能写「不能建上层」。

现在每个拒绝点都会走 `RimLayers.Diagnostics.RejectionReporter`，弹一个原生风格的窗口，并把可复制的报告塞进剪贴板。

## 窗口内容

- 标题 + 一行摘要（动作 + 定义，例如「创建楼层：楼梯」）。
- **原因**：直接就是原来那条 `RL_xxx` 译文，一字不改，所以老玩家熟悉的话术仍然对得上。
- **相关 MOD**（只在真的能归因时出现）：红底条，`MOD 名 [packageId]` + 小字 `Namespace.Type.field`。
- 上下文行：地图、格坐标、层高度、`defName`。
- 异常全文（折叠在滚动区里的深色块）：扩层失败的真实原因只存在于异常里，因为 `LiveMapExpansion` 已经把地图回滚了。
- 底部按钮：**复制错误信息** / **打开日志**（`LudeonTK.EditWindow_Log`）/ **关闭**。

## 复制出来的东西

```
=== RimLayers rejection report ===
Mod: RimLayers 0.1.5 (rimlayers.core)
Game: RimWorld 1.7.4979 rev109

Action: 创建楼层
Reason: 每个占地格的紧邻下层都需要已建成的人工屋顶。
Definition: 楼梯 (RL_Stairs)
Map: 山丘
Cell: 114,94
Layer elevation: 1
Involved mod: VVRace [vexal.vvrace]
Involved type: VVRace.CompVVRaceCellCache
Involved member: cachedCells
Detail: ...
--- Exception ---
System.NotSupportedException: Unmapped native per-cell buffer: ...
--- Active mods (37) ---
  - Core [ludeon.rimworld]
  ...
```

**完整启用列表**是刻意放进去的：这是维护者定位冲突最需要、而玩家最不可能手打的东西。清单来自 `LoadedModManager.RunningModsListForReading`，顺序就是实际加载顺序。

## MOD 名怎么来的

`ModOrigin` 三级回退：

1. `LayerCellBufferCompatibility.DeclaringMod(type)` —— 已经注册过 `RegisterNativeCellBuffer` 的第三方组件，直接拿到注册时声明的名字（目前只有 `VVRace`）。
2. 用 `LoadedModManager.RunningModsListForReading` 里每个 `mod.assemblies.loadedAssemblies` 反查 `Type.Assembly`。这是通用的「类型属于哪个 MOD」判定，覆盖所有带 DLL 的 MOD。
3. 拿不到就只显示类型全名，不硬猜。

类型名从异常文本里解析：`LiveMapExpansion` 抛的 `Unmapped native per-cell buffer: {FullName}.{field}` 是唯一一条自带第三方身份的异常，`RejectionReporter.Blame` 专门认这个前缀。这条也是实际最常见的第三方冲突：别的 MOD 给 `MapComponent` 加了 `NativeArray` 之类的每格缓冲，扩层时我们不知道该怎么扩它，于是整次扩层被拒绝并回滚。

## 不去打扰玩家的地方

- **放置幽灵每帧校验**：`Designator_Place.SelectedUpdate` 每帧调 `CanPlaceBlueprintAt`，所以 `PlacementBoundaryPatch` 只在 `Event.current` 是左键 `mouseDown` 时弹窗（`RejectionReporter.PlayerClickInProgress`）。
- **飞船选点逐格校验**：`LayerShipDestinationValidation` 的 validator 每格都会返回报告，那里**故意不弹窗**，只保留原来的红字。
- **后台路径**：`StairLayerBootstrap.Process`（地图组件初始化时扫描楼梯）调用 `Prepare(..., report:false)`，静默。
- **自动化测试**：带 `rimlayersv1test` / `rimlayerstest` / `quicktest` 命令行参数时整体关闭窗口。`Diagnostics/V1ShaftExpansion.cs:43` 断言 `!shaftSource.StartExtension(-1)`，窗口会卡住测试。
- **同一拒绝不刷屏**：窗口按 `Action|DefName|Reason|CulpritType|CulpritField` 去重，同名窗口已在屏幕上就不再弹；另有 250 tick 冷却（约 4 秒）。

红字消息没有删：`StartExtension`、`TryPrepare`、`LayerShipConfirmLanding` 仍然发原来的 `RejectInput` 消息，窗口是额外的一层，所以习惯看消息栏的玩家行为不变。

## 设置

设置 → RimLayers → 显示 → **操作被拒绝时显示说明窗口**（默认开），对应 `RimLayersSettings.RejectionDialogs`，存盘字段 `rejectionDialogs`。

## 失败不会自动重试

第一版有一个真实事故：玩家完成一个向下楼梯的框架，扩层在生成虫巢时被第三方 MOD 打断，扩层回滚，`Prepare` 返回 false。原版的工作分配器下一次扫描又把同一个框架派给殖民者（框架仍然 `CanConstruct`），于是每轮都重跑一次完整扩层 + 完整回滚——`EnsureElevation` 会重建区域、寻路、地图绘制器和气候，一轮就很重——玩家看到的就是"一直提示创建失败，游戏卡死"。

现在的处理：

- `LayerMapComponent.BlockedStairs`（`HashSet<IntVec3>`，会存盘）记下扩层失败的楼梯格。首次失败也记得住，因为记账走 `LayerMapComponent.Raw(map)`——`For(map)` 对还没扩层的地图故意返回 null，用它会静默丢记录。
- `StairLayerBootstrap.Prepare` 入口先查 `IsBlocked`，已记录的格子直接返回 false，不会隐式重试。
- `ConstructionSupport.Validate` 对已记录的格子返回 `RL_StairExpansionBlocked`，于是 `GenConstruct.CanConstruct` 变 false，原生 `JobDriver_ConstructFinishFrame` 的 `build.FailOn(()=>!GenConstruct.CanConstruct(Frame,pawn))` 立刻结束任务，`WorkGiver_ConstructFinishFrames.JobOnThing` 也不再派活——循环断开。
- `StairLayerBootstrap.Process` 扫描时跳过已记录的格子。
- 玩家想再试：选中框架或楼梯会看到 **重试创建楼层** 按钮（`Structure/BlockedStairGizmos.cs`），点它会清掉记录并重新走一次真实扩层，成功给 `RL_StairRetryOk` 消息。拆掉楼梯也会清记录。
- 虫巢生成单独兜底：`UndergroundService.SpawnCaveHives` 逐个 hive try/catch。虫巢生成是整条扩层链里唯一会执行任意第三方代码的地方，一个 MOD 崩掉不该让玩家丢掉整层，所以只跳过那一个 hive 并弹窗说明（`RL_HiveSpawnSkipped`），扩层继续。

`Tests/Run-V1.ps1 -Scenario blocked-stair` 用一个 Harmony 前缀往 `LiveMapExpansion.EnsureElevation` 注入失败，断言：失败后框架与材料完整、地图没扩层、格子被记录、只弹一个窗口、工作分配器与 `CanConstruct` 都拒绝该框架、10 个 tick 内没有任何重试、重试按钮存在且可用、点完重试真的建成楼层并清掉记录。

## 接入的拒绝点

| 位置 | 动作 | 何时 |
| --- | --- | --- |
| `Core/IsolationPatches.cs` `PlacementBoundaryPatch` | `Build` | 蓝图放置被拒（仅真实点击） |
| `Structure/ConstructionPatches.cs` `ConstructionCommitPatch` | `Build` | 施工完成时承托校验失败 |
| `Structure/StairLayerBootstrap.cs` `Prepare(...,report:true)` | `Stairs` | 楼梯要扩层但被拒 / 扩层抛异常 |
| `Structure/LiftShaftExpansion.cs` `StartExtension` | `LiftShaft` | 电梯井扩建被拒 / 抛异常 |
| `Ships/ShipLandingPatches.cs` `LayerShipConfirmLanding` | `ShipLanding` | 降落确认被拒 / 扩层抛异常 |
| `Ships/ShipLandingPatches.cs` `LayerShipDestination.TryPrepare` | `ShipTakeoff` | 出发前目的地图被拒 |
| `Underground/UndergroundService.cs` `SpawnCaveHives` | `Expansion` | 单个虫巢生成被第三方 MOD 打断（跳过该虫巢，扩层继续） |

## 已知限制

- `LayerShipPlacement` 用 `throw new InvalidOperationException("[RimLayers] Unsafe layered ship placement: ...")` 拒绝，异常会被原版接住并显示为普通错误，那里没有用户动作边界可挂窗口。
- 报告里不含 `Player.log` 内容，只有异常 `ToString()`。「打开日志」按钮直接开原生日志窗口。
- 窗口高度在构造时按内容量一次算定（`MeasureHeight`），内容少的规则拒绝是小窗、长堆栈截断在滚动区。改绘制内容时**必须同步改 `MeasureHeight`**，否则会出现大面积留白或末行被裁。
- `Text.CalcHeight` 的折行行为读全局 `Text.WordWrap`，其它窗口或 MOD 会改它；`DoWindowContents` 与 `MeasureHeight` 都显式设成 `true` 并在 `finally` 还原。

## 实机验证状态

`Tests/Run-V1.ps1 -Scenario rejection-dialog` —— **22 断言 0 失败**（`Tests/Artifacts/v1-rejection-dialog-*/`）。这个场景跑的是真实拒绝路径和真实窗口，不是替身：

1. 生成地图上放一台 `RL_LiftVertical`，调 `StartExtension(1)`，无屋顶所以必然被拒 → 断言窗口出现、`Action=LiftShaft`、原因已翻译（不含 `RL_`）、`DefName`/`LayerElevation` 正确、规则拒绝不冤枉任何 MOD。
2. 调 `Dialog_Rejection.CopyToClipboard()`（按钮点的同一个方法）→ 断言剪贴板里有 `=== RimLayers rejection report ===`，并把报告写成 `rejection-rule-refusal.txt`。
3. 构造真实的 `Unmapped native per-cell buffer: <type>.<field>` 异常走 `RejectionReporter.Reject(..., exception, ...)` → 断言类型/成员被正确切分、未知类型回退成类型名而不是乱认 MOD、窗口高度够放绘制内容，报告写成 `rejection-third-party.txt`。
4. 冷却期内重复同一条拒绝 → 断言窗口数不超过 1 且被冷却吞掉，日志里仍有 `[RimLayers] Rejected`。

两张截图（`rejection-rule-refusal.png`、`rejection-third-party.png`）就是实际观感：底部三个按钮不重叠、没有原生 Close 按钮、异常块完整两行显示。

尚未验证：第三方 MOD 真名回退（需要装带 DLL 的第三方 MOD 才会走到 `assemblies.loadedAssemblies` 反查），以及 `PlacementBoundaryPatch` 的真实鼠标点击路径（测试直接调 `StartExtension`，没有走 `Designator`）。
