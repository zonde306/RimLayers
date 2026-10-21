# 跨层安全温区：实现与验收

日期：2026-10-21。游戏：**1.7.4984 rev1242**，根 Assembly-CSharp MVID `484efdc5765b463ca9cc54a6131e38f0`。

## 当前行为

本层原版搜索找不到合适温区时，Pawn 可以沿合法楼梯／电梯寻找其他层的温区。成人继续由原版思维树生成 `GotoSafeTemperature`；Biotech 婴儿安全位置查询复用同一扩展，并继续由原版 `BringBabyToSafety` 抱起、移动、放下。

- 保留原版本层成功结果。跨层候选按实际移动者的路径可达性和成本选择，温度范围与允许区域使用受保护 Pawn。
- 禁止连接器、阻塞落脚点、没有允许区域内的温区、未牵引圈养动物的楼梯限制仍生效。电梯使用既有通行规则，停电时的应急通行也不改变。
- 默认自主避温仍要求 **Serious 温度伤害**；不是一离开舒适范围就强制停止工作。婴儿原版冷却、床位优先级和搬运资格保持。
- 婴儿找到异层 Region 后，局部放置格搜索不再被原始存储坐标的 4–15 格近距限制挡住。该调整仅在 `SafePlaceForBaby` 的异层调用作用域内生效，保留原版放置过滤并检查楼面、婴儿允许区域和搬运者可达。
- 没有新增 Job、持久状态或存档格式；无需迁移。安装更新后的 DLL 后重启游戏。

## 实现入口

- `Source/RimLayers/Work/LayerTemperatureSearch.cs`：共享温区搜索的 null-result Postfix；局部 Region 邻接加 MovementEdges；真实路径验证；婴儿放置作用域。
- `Source/RimLayers/Diagnostics/V1Temperature.cs`：专用 temperature 场景。
- `Source/RimLayers/Diagnostics/V1Integration.cs`、`Tests/Run-V1.ps1`：场景分派、保存／读档入口、Biotech 默认配置与 `-TemperatureCore`。
- 原版成员与基准证据见游戏根 `docs/multilayer-services.md` 和 `docs/sources.md`。

搜索处理最多 9999 个 Region。每区使用原版选出的允许格比较路径成本，不承诺所有格子上的全局最短路线。它不修改全局 Region 图，也不合并温度 Room。

## 已完成的真实游戏测试

两轮 Release 构建均为 0 警告、0 错误；四个游戏进程退出码均为 0。每轮均先生成场景，再关闭游戏、用独立进程加载半程存档。

| 运行配置 | 生成 | 重启读档 | 目录（Tests/Artifacts/ 下） |
|---|---:|---:|---|
| Core + Biotech，楼梯 | 24/24 | 7/7 | `v1-temperature-20261021-190522` |
| Core，无 DLC，单层电梯应急通行 | 16/16 | 6/6 | `v1-temperature-20261021-190630` |

每个目录保存 `generate-temperature-results.json`、`load-temperature-results.json`、stdout/stderr、截图、`temperature-baseline.json` 以及隔离存档。结果内的实际 activeMods 与预期一致，主程序集 MVID 一致。

### 成人：真实 AI 选任务与跨两层移动

三层分别建立独立封闭房间。高温阶段固定第 1、2 层 65°C，第 3 层 21°C；低温阶段固定第 2、3 层 −40°C，第 1 层 21°C。夹具注入 severity 0.4 的原版 Heatstroke／Hypothermia，满足 Serious 条件，然后让原版思维树选任务，未直接下发成人避温 Job。

实际观察到 `GotoSafeTemperature`，成人上楼到舒适温区、再下楼到舒适温区。Core 电梯场景在中间层保存，重启后 Job ID 不变并继续完成两阶段。测试同时验证原版本层结果优先、禁止连接器、落点被墙阻塞、允许区域、未牵引圈养动物与全层无温区时的拒绝行为。

### 婴儿：无床温区、原对象与持有者续存

Biotech 场景创建不能自行步行的婴儿、确认没有可用床，使用原版 `BabyNeedsMovingForTemperatureReasons` 和 `SafePlaceForBaby` 找到第 3 层；查询采用搬运者而非婴儿的移动能力。禁止唯一上层连接器时拒绝迁移。

测试通过原版 `MakeBringBabyToSafetyJob` 创建并下发任务，验证实际抱起婴儿、跨两层、放到舒适房间。中间层抱行时保存；重启后原婴儿仍由同一 Pawn 的 carryTracker 持有且未 Spawn，Job ID 保持；最终放下后原实体恰好一份。这里验证原版安全搬运任务的执行链，未另测自动育儿工作分配优先级。

已查看两个读档终态截图：婴儿落地在 Floor 3，成人通过电梯回到 Floor 1 的 21°C 室内。夹具每 tick 固定室温以隔离 AI／路由问题；自然热交换属于既有 heat 测试。

### 首轮失败与修正

`v1-temperature-20261021-185945` 为首轮失败记录，23 项中 5 项失败；保留原文件。原因及处理：

- 征召 Pawn 按原版规则忽略 forbidden，导致成人／婴儿两项禁止连接器断言前提不成立；改用未征召的查询者。
- 只有不舒适、没有 Serious 温伤时，成人选择了 Clean 而非自主避温，导致两项 AI 断言失败；修正夹具注入真实温伤，不改变正式 AI 的触发条件。
- 同期 cargo Def 产生 14 条配置错误，导致运行错误检查失败；由 cargo 任务负责人修正 XML，后续两轮没有该错误。

最终场景采集的运行错误数组均为空。完整启动日志仍有本机缺失 Bandicam／Social Club Vulkan layer、三个其他已安装 Mod 的重复 packageId，stderr 有 Fossilize 过滤配置错误；退出阶段另有 Waylith persistent allocation 警告。这些记录保留在日志中，不能把场景内零错误表述为整个进程日志无告警。

## 复现

在游戏根目录运行；runner 自动使用各自的隔离测试数据目录，不修改玩家启用列表或存档。

```powershell
./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario temperature -TimeoutSeconds 240
./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario temperature -TemperatureCore -LiftWork -TimeoutSeconds 240
```

本次实现和验收聚焦温区搜索、自主避温和婴儿避温搬运；拘捕、普通救援上床与动物牵引沿用既有实现，本轮未扩展这些流程。

