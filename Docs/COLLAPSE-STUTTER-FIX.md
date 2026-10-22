# 坍塌后周期性卡顿修复

2026-10-22；RimWorld **1.7.4985 rev1269 / Waylith / .NET 10**；RimLayers 0.1.5。

## 原因：失去储存区后，搬运检查为被拒绝的容器反复寻路

在 250×250、20 名正常工作的殖民者、Core＋全部五个 DLC＋Harmony＋RimLayers 的独立场景中，拆一楼屋顶后，二楼和三楼连续失去楼板。坍塌、材料退还和实体恢复队列已经清空，仍会周期性出现秒级 tick；重铺一楼屋顶没有消除它。

原补丁把 `StoreUtility.TryFindBestBetterNonSlotGroupStorageFor` 内廉价的 `DistanceToSquared` 换成完整跨层路径成本。原版随后才检查容器是否接受物品、阵营、禁用、预订和可达性。因此，每个本来会被拒绝的容器也先寻路一次；上层储存区消失后，搬运工作验证会针对大量物品重复这轮无效评分。

同 tick 第 128 次完整寻路的实际栈：

```text
StorageContainerDistancePatch.Distance
StoreUtility.TryFindBestBetterNonSlotGroupStorageFor
HaulAIUtility.HaulToStorageJob
WorkGiver_Haul / WorkGiver_HaulGeneral
LayerCandidates.Closest（调用原版工作验证委托）
```

## 修复：先检查资格，再计算路径成本

生产修改仅在 [StorageQueries.cs](../Source/RimLayers/Work/StorageQueries.cs)。原来的距离 transpiler／临时上下文改为局部 Prefix：

- 先保留原版接受条件、阵营、禁用、预订、入库途中容量和原生可达性检查，再为合格容器生成路径。
- 保持原有储存优先级、平方路径成本、整数截断后的 float 比较、等分时后候选可替换前候选的规则。
- 保持吊篮实际可操作端口作为源位置及目的端口的等待成本。
- 原生可达性决定候选资格；同步路径评分暂不可用时沿用旧实现的 int.MaxValue 分数，不把有效候选误判为不可达。
- 普通未分层地图、无搬运者查询继续走原版；不新增持久状态或跨 tick 缓存。

没有改动坍塌规则、物品恢复算法或存档格式。二楼失效仍会使依赖它的三楼失效；连锁坍塌、真实材料退还和原生 Pawn 工作继续执行。已有存档更新 DLL 后重启即可使用，无需清理队列或重建屋顶。

## 原始复现与同一检查点回放

完整原始复现：[v1-collapse-perf-20261022-124931](../Tests/Artifacts/v1-collapse-perf-20261022-124931)。该轮 11 项业务／运行检查通过，`errors=[]`；它验证行为与记录性能，不表示旧版性能合格。保存了坍塌后的 `Saves/RimLayers_CollapsePerf.rws`。

根因修复首次有效回放：[v1-collapse-perf-20261022-130814](../Tests/Artifacts/v1-collapse-perf-20261022-130814)，直接读取上述检查点。以下为每阶段约 1800 个真实 `DoSingleTick` 的 CPU 耗时；修复前为原场景，修复后为其坍塌检查点续跑，不是重新造一个更轻的地图。

| 候选／阶段 | 平均 tick（ms） | P99（ms） | 最大（ms） |
|---|---:|---:|---:|
| 原版补丁：坍塌后 | 48.667 | 2010.783 | 7100.024 |
| 原版补丁：重铺屋顶后 | 51.026 | 2052.538 | 4426.284 |
| 修复：原检查点续跑 | 1.754 | 13.922 | 118.078 |
| 修复：重铺屋顶后 | 1.661 | 5.324 | 290.327 |

数据包含阶段切换的瞬时成本；新增回归门槛另排除最初 120 tick，要求稳定期 P99 小于 100 ms。坍塌发生时的单次破坏／重建开销与稳定期持续卡顿分别记录。原始序列和各子系统耗时保存在 `collapse-performance.json`。

## 连锁塌方与评分契约回归

[新场景源码](../Source/RimLayers/Diagnostics/V1CollapsePerf.cs) 在二、三楼布置建筑，三楼布置含真实材料的墙框架；拆顶当刻在两层分别放入原始银堆（12、13）。检查：

- 拆一楼屋顶立即排入两层的 128 个失效格。
- 两层建筑／框架被处理，楼板与对应屋顶清除；每个目标格恰好提交一次，队列最终清空。
- 两个原始银堆仍是同一对象，数量保留并落到地面。
- 重铺屋顶不凭空恢复已塌楼板，也不遗留反复处理的恢复工作。
- 钢、木、银分别对照旧的完整路径评分顺序；被拒绝的容器结果不变，真实寻路次数显著减少。
- 有效书柜对照旧评分结果，覆盖实际路径成本、储存优先级和异阵营拒绝。

初次完整连锁回归 [131052](../Tests/Artifacts/v1-collapse-perf-20261022-131052) 为 23 项通过、`errors=[]`。钢／木／银三种拒绝查询分别从 114 次原生路径 Job 降至 0 次，结果仍为无合法容器。连锁坍塌后及重铺后的稳定期 P99 分别为 3.630／3.603 ms。


### 最终候选

- [132335 完整连锁坍塌](../Tests/Artifacts/v1-collapse-perf-20261022-132335)：27 项通过，`errors=[]`。两层共 128 格各提交一次，两个原始银堆身份及 12、13 数量不变。全阶段 P99：塌后 **5.545 ms**、重铺后 **15.090 ms**；排除阶段初始 120 tick 后分别为 **5.048 ms、11.081 ms**。
- [132431 原坏档重放](../Tests/Artifacts/v1-collapse-perf-20261022-132431)：22 项通过，`errors=[]`。直接复制原始 `124931` 的塌后检查点；全阶段 P99 为 **13.308 ms、5.984 ms**，稳定期为 **7.392 ms、5.942 ms**。钢、木、银的拒绝查询由各 36 次原生路径 Job 降至 0 次，选择结果不变。
- 两项最终测试均验证有效书柜的路径成本、优先级、异阵营过滤，以及“原生可达、同步评分暂不可用”时保留原有候选选择的契约。
- [132516 吊篮回归](../Tests/Artifacts/v1-cargo-20261022-132516)：生成 22 / 重载 20 项通过，`errors=[]`；实际入篮、出篮、处理途中保存、原 Job 和物品身份均保留。
- 飞船生成阶段复用 [132942](../Tests/Artifacts/v1-gravship-20261022-132942/generate-gravship-results.json) 的有效 62 项证据；修正旧夹具后从其飞行存档在新进程回放 [133338](../Tests/Artifacts/v1-gravship-replay-20261022-133338)：落地 35 / 落地重载及返航 15 项通过，`errors=[]`。覆盖已有地图落地、三层载荷、原 Pawn/物品/吊篮身份、落地重启及第二次原生飞行。

最终 Release 构建为 0 警告、0 错误。本轮游戏验证使用 Core、全部 5 个官方 DLC、Harmony、RimLayers 的隔离组合；未验证玩家完整 227 模组组合。性能数字是模拟 tick CPU 耗时，不是 UI 帧耗时。

## 复现入口

在游戏根目录运行；脚本只使用自己的测试配置与存档副本。

```powershell
./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario collapse-perf -AllDlc -MapSize 250 -PawnCount 20 -TimeoutSeconds 300

./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario collapse-perf -AllDlc -CollapseReplaySave 'G:/SteamLibrary/steamapps/common/RimWorld/Mods/RimLayers/Tests/Artifacts/v1-collapse-perf-20261022-124931/Saves/RimLayers_CollapsePerf.rws' -TimeoutSeconds 300
```

`CollapseReplaySave` 复制指定检查点到新 run 目录，原件不改动。`collapse-perf` 另外按日志 Command 核对本轮归属后归档 Player.log。

## 保留的试验与失败

- `124245`：燃料炉夹具误传 Steel；`124508`：储存区夹具覆盖原生 Column／Stairs。这两轮保留性能线索，但因场景错误不作为通过证据。
- `125808`：选物路径下界剪枝保持选择结果，却没有解决整场持续停顿。该生产改动已撤回，`LayerWorkQueries.cs` 保持原实现。
- `130326`：采集到搬运容器评分的直接调用栈后主动结束诊断进程，不作为完整运行通过证据。
- `131430`：全部业务检查通过，但测试对不可禁用的原版 BookcaseSmall 调用 SetForbidden，运行错误检查失败；改为真实异阵营拒绝反例，未放宽生产规则。
- 编译阶段修正了测试文件的 Verse／Verse.AI 命名空间及扩展方法 using；生产修复 Release 构建成功。

- `131808`：吊篮首次目标查询返回空，未计为通过；增加失败诊断。后续检查发现重写不能把“评分路径暂不可用”改为拒绝候选，已恢复旧哨兵分数，并在最终 27/22 项测试中加入强制该分支的断言；最终吊篮 22/20 通过。
- `132618`：飞船生成 62 项通过，重载有 3 项失败。旧夹具用散落 Steel 阻挡落地，但当前规则允许物品；改为真正上层载荷位置的实体墙，保留建筑碰撞拒绝要求。
- `132942`：飞船生成 62 项通过，重载剩自然地板断言失败。夹具只有山体楼面记录、地形仍为 RL_OpenAir；补成真实花岗岩自然地面并在测试后恢复。`133338` 重放 35/15 全过。本轮只修测试夹具，没有改飞船落地生产规则。