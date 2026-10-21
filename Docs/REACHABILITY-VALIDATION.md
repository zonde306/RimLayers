# RimLayers 0.1.3：寻找食物时的长卡顿修复

**完整重启游戏后生效，无需新档。** 本轮将 `CanReach` 的布尔连通性查询与完整路径生成分开；正常走楼梯、连续工作和实际路线成本比较仍使用原有完整寻路。

## 现场原因已定位

2026-10-18，玩家反馈暂停切层不卡、不切层也会卡。0.1.2 的两次非侵入主线程采样均命中：

```text
JobGiver_GetFood → FoodUtility.SpawnedFoodSearchInnerScan
→ Reachability.CanReach → RimLayers.LayerReachPatch
→ LayerRouter.Find / EndpointSearch.Find / LayerRouter.Leg
→ PathFinder.FindPathNow → PathFinderJob.Execute
```

当前玩家日志记录约32–61秒的主线程停顿。原始栈与只读阶段结论保留在 [现场记录](../Tests/Artifacts/stall-20261018-live/FINDINGS.md)。瓶颈是每个食物候选的布尔查询都执行完整跨层Dijkstra和格级路径，不是切层或薄雾。

## 实现保留哪些规则

- 新增 `Navigation/LayerReachability.cs`：层内调用原生 CanReach，复用其区域/缓存及目标解析；跨层按全部合法、有方向的楼梯边搜索连通性。只记录访问过的落脚端点，不生成 PawnPath，不按最近几个楼梯裁剪。
- 同层原生可达即可成功；原生不可达仍搜索经其他层绕回的路线。内部原生查询使用独立作用域防止再次进入跨层扩展，不改变 Pawn、Job 或模拟位置。
- 原始 TraverseParms 和 PathEndMode 传到每段；目标仍是真实 Thing。门、禁止、围栏、危险、Touch/ClosestTouch/交互格及特殊原生模式由原版和连接器资格共同处理。
- 不新增跨查询缓存；每次读取当前合法楼梯，层内缓存继续由原生维护。门、楼面和楼梯变化后不能复用过期跨层布尔结果。
- `Navigation/PathPatches.cs` 只替换 CanReach 的接入；`LayerRouter`、实际寻路、自定义路径网格、路径成本、跨层提交复核、工作优先级及预订保持原有实现。

**接口含义**：CanReach 回答原生通行规则下的连通性；真正需要完整路径或实际成本的调用方仍必须寻路。没有把工作成本比较替换为直线距离，也没有暂停 Pawn Tick 或吞掉异常来提速。

## 同一输入的原生食物扫描对照

基准：**1.7.4981 rev136 / Waylith / .NET 10 / Harmony 3**，Assembly-CSharp MVID `f1e6382600a84191bbc6d8a184fd6b57`。

固定250×250地面、三层，实际存储191250格。130个真实食物候选包含128个地面食物、一个可达三层食物及一个无楼梯连接的高层孤岛食物。每组在**同一个进程、同一地图、同一Pawn和同一候选顺序**下，对原生 SpawnedFoodSearchInnerScan 分别启用旧完整路径布尔实现与新连通实现；先预热代码，再记录查询经过时间与真正 PathFinderJob.Execute 次数。

| 组合 | 旧食物扫描 | 新食物扫描 | 完整格级路径任务：旧→新 |
|---|---:|---:|---:|
| Core，[214032](../Tests/Artifacts/v1-reachability-20261018-214032/generate-reach-metrics.json) | 636.6508 ms | 0.2827 ms | 1675→0 |
| 全五DLC，[213506](../Tests/Artifacts/v1-reachability-20261018-213506/generate-reach-metrics.json) | 822.1789 ms | 0.2949 ms | 1675→0 |

两组均逐一比较130个候选的新旧布尔结果，完全一致；原生扫描选中同一个真实三层食物。时间只对应这一轮**食物候选扫描**，不是整帧、FPS或玩家完整165模组组合的Tick用时。不同新地图不作为同seed控制实验；新旧比较仅在各自同一输入内成立。

原生查询照常使用其区域缓存，不测缓存清空后的冷启动；改动的主要证据是消除了这些布尔查询里的完整路径任务，而非用单一计时推广任意地图的性能。

## 功能和重启证据

运行只使用独立 Tests/Artifacts 配置及存档；未修改玩家启用列表或存档。候选比较完整序列为 `generate-reach-candidates.json`，原始计时为 `generate-reach-metrics.json`。

| 场景 | 组合 / 目录 | 生成 / 新进程读档 | 结果 |
|---|---|---|---|
| 食物扫描、断路、真实三层进食 | Core [214032](../Tests/Artifacts/v1-reachability-20261018-214032) | 16 / 5 | 通过 |
| 同上 | 全DLC [213506](../Tests/Artifacts/v1-reachability-20261018-213506) | 16 / 5 | 通过 |
| 同层借其他楼层反向绕行 | Core [214137](../Tests/Artifacts/v1-navigation-maze-20261018-214137) | 13 / 6 | 通过；真实0→1→2→1→0，原Job/Driver，倒向跨层途中重启 |
| 动态门权限与实际开门 | 全DLC [214232](../Tests/Artifacts/v1-navigation-door-20261018-214232) | 15 / — | 通过；途中门变敌对后重规划、原生开门 |
| 自定义网格、禁用、堵出口 | Core [214302](../Tests/Artifacts/v1-navigation-rules-20261018-214302) | 11 / — | 通过 |
| 多次取料、原工作与保存恢复 | 全DLC [214327](../Tests/Artifacts/v1-work-save-20261018-214327) | 17 / 15 | 通过 |
| 牵绳、围栏及重启恢复 | Core [214428](../Tests/Artifacts/v1-roping-20261018-214428) | 30 / 24 | 通过；真实双动物经二层进入三层畜栏 |
| 原生战斗AI选目标及射位 | 全DLC [214524](../Tests/Artifacts/v1-combat-ai-20261018-214524) | 17 / — | 通过 |

食物场景另验证：唯一上层楼梯禁用、落脚点堵墙、NoPassClosedDoors 与 PassDoors、移除障碍后的即时恢复、隔离区拒绝。实际Pawn经二层到三层完成原生Ingest；读档保留原Job ID与真实食物身份，加载后连通查询不运行完整路径任务。

已查看 [Core重启后的三层进食画面](../Tests/Artifacts/v1-reachability-20261018-214032/load-reach-ingest.png)，Pawn实际到达三层，楼面、下行标记和既有下层显示仍在。

## 失败探针保留

- 213007 的禁用楼梯检查失败：测试Pawn处于征召状态，原生 IsForbidden 按规则忽略禁用。检查时改用未征召Pawn，生产通行规则未改。该次生成不计完整通过。
- 213222 首次生成的新旧布尔数组比较失败，当时未写逐项反例；其食物选择、计时及其他检查通过，但该次生成不计通过。新增逐项记录后，复用**同一保存**执行加载审计，130个结果一致，17项检查零失败，原失败JSON保留。未重生成覆盖失败输入，也未把差异归因于未经证实的具体原因；后续213506及214032的新建/重启和逐项结果单独列出。

## 使用与范围

```powershell
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario reachability
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario reachability -AllDlc
```

本场景固定250×250三层，不使用MapSize参数改变其数据。旧完整路径实现只由该诊断场景临时接回，普通游戏不会运行对照代码。存档格式未变，不保存路径/Region/新连通图。

玩家原165模组组合尚需重启后确认实际长停顿是否消失；现场另有Nanami年龄/关系、JoyToleranceSet越界错误，本轮未改这些外部问题。新能力没有修改薄雾、关闭原生AI或跳过需求与工作。
