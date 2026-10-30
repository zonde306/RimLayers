# 已有目的地图运行时扩容

2026-10-19；RimWorld 1.7.4982 rev1280 / Waylith / .NET 10。状态：隔离扩容与读档已通过，已接入合法多层飞船出发准备；完整航行证据见下表。仍随 Odyssey 开发候选交付，程序集版本保持 0.1.4。

## 确认出发后、扣油之前准备目的地

仅拥有玩家官方逆重核心、有效原版基底、且来源为 RimLayers 地图的飞船进入此流程。原版目的地验证、燃料、航程、驾驶台和起降规则继续生效。

1. `LayerShipDestination.Report` 只读检查；选址、悬停和待确认弹窗都不扩容。
2. `SettlementProximityGoodwillUtility.CheckConfirmSettle` 的确认动作先调用 `TryPrepare`。已有但未扩层的地表或太空目的地图在此转换，成功后才执行原版动作中的毁树、扣油和起飞。取消确认不转换。
3. `InitiateTakeoff` 保留幂等复核，覆盖开发工具等直接调用入口。扩容失败时终止出发并记录错误；普通驾驶台流程尚未扣油、飞船仍在原图。
4. 原版到达现有地图后选择一层和真实落点。落地时再按目的地自己的 stride 解码随船楼层；不携带来源坐标带偏移。

普通 NPC / 太空地图不会因此普遍预留楼层；口袋、地下地图不支持此转换。已有太空地图的选址保留原版限制：船体至少接触一格平台/陆地，不能直接停在完全断开的空域。已管理地图已有足够楼层时不重复扩容；缺少层数时仍可继续追加。首次准备需要重建网格，有一次性停顿。

共享扩容事务也用于第一座楼梯完工时按需创建上层，见 [楼梯入口验收](STAIR-BOOTSTRAP-VALIDATION.md)。这不要求普通楼梯拥有飞船核心；飞船收集与迁移仍保留合法核心限制。

## 同一个 Map，只追加坐标行

实现位于 `Generation/LiveMapExpansion.cs` 与 `ExpansionBuffers.cs`。宽度与原始坐标保持不变，调用 `EnsureLayerCount(map, requiredCount)`，仅在尾部追加缺少楼层的坐标行。楼梯/单层电梯每次增加一层，飞船提前准备载荷实际需要的层数。MapID、原始 Thing/Pawn 实例、容器、Job/Driver、预订与管理器均保留；不执行 `Map.ConstructComponents`，不重新 Spawn 原实体。

- 原有一维地形、屋顶、气体、资源、分区、蓝图、指定任务和实体网格按前缀复制；布尔区域更新高度，原生雾、光、雪沙和躲避网格显式分配并复制。
- 仓储 `HaulDestinationManager.groupGrid` 是 `[x,y,z]` 三维数组，逐坐标复制，不能当作一维前缀。
- 河流向量按 `(x * height + z) * 2` 重映射；旧纹理只在提交后释放。
- 保留同一 GlowGrid 的光源池和句柄，只替换它的每格缓冲。扩容后原生自定义光源仍可注销。
- 完成旧寻路任务后暂停移动路径，不结束 Job。新建寻路、洪水填充、完整区域/连接图和绘制缓存，恢复房间温度/真空，再恢复 Pawn 的原路径目标与参数。
- 临时区域图独立于原图，包括 RegionLinkDatabase。洪水填充的未访问值必须为 `-1`；不能把新行简单初始化为零。绘制 section 初始化早于光照触发的脏网格通知。
- 所有旧缓冲保留到校验和故障注入点之后。可捕获的转换异常恢复原字段、尺寸、布局、原区域图与路径，释放新分配；成功提交才释放旧分配。

只改变内存中的地图；扩容不会自动保存。建议游戏中首次使用后另存新槽。保存后由原版保存扩展网格与实体，RimLayers 仅保存布局和自有事实；读档重建派生服务。

## 历史首次转换验收

下表为先前一次性三层版本；本轮两次追加、派生房间队列回滚、天空缓存和阶段计时见 [新验收](DECKS-AND-ELEVATORS.md)。

全部使用 `Tests/Artifacts/<run>` 的独立配置和存档，未修改玩家原档。下列成功结果的 `failures=0` 且 `errors=[]`。

| 场景 | 结果 | 原始证据 |
|---|---|---|
| 全 DLC，现有 120×137 河流地图；故障回滚、实体/网格/活动任务/预订、两次跨层移动 | 19 项通过 | [生成](../Tests/Artifacts/v1-expansion-20261019-140546/generate-expansion-results.json) |
| 同一河流地图新进程读档；原实体、材料、Bills、区域、资源、楼层幂等 | 7 项通过 | [读档](../Tests/Artifacts/v1-expansion-20261019-140546/load-expansion-results.json) |
| 全 DLC，现有 250×267 原版太空地图；同样执行真实故障回滚与扩容 | 17 项通过 | [生成](../Tests/Artifacts/v1-expansion-20261019-140758/generate-expansion-results.json) |
| 同一扩容太空地图新进程读档及跨层可达性 | 7 项通过 | [读档](../Tests/Artifacts/v1-expansion-20261019-140758/load-expansion-results.json) |
| 多层飞船前往现有太空地图；飞行保存、实际选址、落地保存、返航 | 22 / 22 / 11 项通过 | [起飞](../Tests/Artifacts/v1-gravship-20261019-141241/generate-gravship-results.json)、[落地](../Tests/Artifacts/v1-gravship-20261019-141241/load-gravship-results.json)、[返航](../Tests/Artifacts/v1-gravship-20261019-141241/landed-load-gravship-results.json) |
| 多层飞船前往现有地表地图；实际原版确认动作、只读选址、待确认不扩容、确认后扣油前准备、读档与返航 | 26 / 22 / 11 项通过 | [起飞](../Tests/Artifacts/v1-gravship-20261019-141547/generate-gravship-results.json)、[落地](../Tests/Artifacts/v1-gravship-20261019-141547/load-gravship-results.json)、[返航](../Tests/Artifacts/v1-gravship-20261019-141547/landed-load-gravship-results.json) |

[最终太空画面重放](../Tests/Artifacts/gravship-space-pier-20261019-142255/load-gravship-results.json)：复用 141241 的同一飞行保存，以小型合法接地点落地，22 项通过、errors 为空；已检查 [实际轨道画面](../Tests/Artifacts/gravship-space-pier-20261019-142255/landed-layer-ship.png)，没有地表雾覆盖轨道背景。截图左上的占地拒绝提示来自夹具故意阻挡上层后验证拒绝的步骤。

太空航行验证了真实核心、上层 Pawn/物品/墙灯、容器衣物、工作台 Bills、区域及楼梯，原有目的地物品/资源/MapID 不丢失；密闭上层舱室在原版恢复提交点的温度和真空数值与装船记录一致。之后正常模拟造成的温度变化不当作搬运损坏。太空显示沿用原版轨道背景，不绘制地表薄雾。

扩容调用的原始耗时与字段清单分别在各自的 `expansion-buffers.json`。250×267 太空样本为 1288.1 ms；这是一次扩容调用耗时，不是帧率或持续 TPS。长期大规模任务仍需针对性验证。

## 第三方自定义原生网格（VVRace）

原实现遇到未登记的 `NativeArray` 逐格缓冲会抛 `NotSupportedException` 并整体回滚，因此任何在 `MapComponent` 里持有原生逐格数组的 mod 都会让楼梯扩容失败。VVRace（`gguake.race.vivi`）的 `ManaMapComponent._manaReserveGrid` / `_manaGrid` / `_tmpGrid` 就是这种情况。

登记入口是 `Compatibility/LayerCellBufferCompatibility`：

- `RegisterNativeCellBuffer(type, modName, params string[] fields)` 声明哪些 `NativeArray<T>` 字段按 `CellIndices` 寻址（`index = z * width + x`）。扩容只追加坐标行，「地面部分是前缀」成立，所以复制前缀、新增行清零即可。
- 索引约定无法靠反射判断：`(index = x * height + z)` 的 x-major 缓冲（如原生河流流场）不是前缀，必须由属主自行重映射。所以未登记的缓冲仍然安全拒绝，不猜测。
- `RegisterBeforeResize(type, hook)` 在字段被替换前于主线程、事务内回调一次。VVRace 用它结束仍在读写旧分配的扩散 Job 并把预留魔素折算进主网格，保证移动无损。

登记必须发生在其他 mod 程序集加载之后。`Mod` 构造函数里 `AccessTools.TypeByName` 看不到其它 mod 的类型，因此适配器由 `[StaticConstructorOnStartup]` 触发，而不是构造时。

VVRace 自身按 `map.Size` 遍历整张存储区（含 RimLayers 的保留隔离行），因此适配器另外补了三处：扩散后清零隔离行（各层边界等同于原生地图边缘）、悬浮层只看当前查看层、拒绝向隔离行写入魔素。

验证：`pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario expansion -AllDlc -River -VVRace`。`vvrace-*` 五项断言覆盖缓冲跟随扩容、预留折算清空、主网格前缀保持（含真实地表魔素 370）、新增层为空、隔离行无魔素；读档阶段 6 项通过且 `errors` 为空。该场景仍保留一个与本次改动无关的既有失败（`every-original-spawned-thing-keeps-identity-map-and-stack`）：在未改动的原始代码上同样失败。

## 第三方自定义原生网格（Smart Combat）

Smart Combat（`lordfelix.SmartCombat`，Immersive Warfare）的 `SmartCombat.OrdinaryRoadMap` 持有一个
**懒分配**的 `NativeArray<ushort> offsets`（离路附加代价，长度 == `NumGridCells`）。它只在殖民者开始普通工作
时才由 `PrepareSafeBatch` 建出来，所以同一张地图上「刚开局能扩层、玩一会儿就扩不了」：

```text
[RimLayers] Stair-triggered map expansion failed on map 0:
System.NotSupportedException: Unmapped native per-cell buffer: SmartCombat.OrdinaryRoadMap.offsets.
Expansion was not applied.
```

适配方式是 `RegisterBeforeResize` + 丢弃，而不是 `RegisterNativeCellBuffer` + 迁移前缀：
`PrepareSafeBatch` 自己就有 `offsets.Length != numGridCells → DisposeGrid()` 的退化路径，下一次批量会按新尺寸
全量重建并调 `RecycleGridJobData`；而前缀迁移会留下旧地图的代价、并让与 `roadCells` 配对维护的 `roadCount` 失配。
细节与断言清单见 [SMARTCOMBAT-COMPATIBILITY.md](SMARTCOMBAT-COMPATIBILITY.md)。

验证：`pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario smartcombat`。

## 已修复的失败记录

- `v1-expansion-20261019-140017`：洪水填充新增行的零初值导致区域碎裂和日志洪泛；结束的仅是本任务启动的测试 PID，保留日志。
- `140248`：绘制 section 尚未初始化便计算光照，触发空引用；旧区域对象的浅拷贝也不能作为可靠回滚图。改为独立派生图及正确绘制顺序。
- `140423`：原版仓储三维数组未扩展，上层 Pawn 的美观需求查询越界。补齐三维逐坐标复制后，河流与太空扩容/重载均通过。
- `v1-gravship-20261019-141016`：夹具错误地创建无世界图标的远古派系聚落，并把尚未提交的原版降落标记当成已 Spawn。改用真实可显示派系，等待到达回调，再调用原版落点设计器提交；没有为夹具放宽生产落地规则。

- `gravship-space-visual-20261019-142017`：重放时去掉了整片夹具平台，但原版要求接触至少一格非 Space 地形，选址断言正确失败（不是多层扩容失败）。补回 3×3 合法接地点后，同一检查点的 `142255` 重放 22 项通过；没有放宽原版接地限制。

## 复现入口

```powershell
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario expansion -AllDlc -River
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario expansion -AllDlc -Space -MapSize 250 -TimeoutSeconds 480
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario gravship -NativeShip -ExistingMap -AllDlc -TimeoutSeconds 480
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario gravship -NativeShip -ExistingMap -Space -AllDlc -TimeoutSeconds 480
```

游戏关闭后才能替换 DLL；上述脚本只启动和结束自己的隐藏测试进程。
