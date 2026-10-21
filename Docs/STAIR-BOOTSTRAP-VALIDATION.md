# 楼梯完工时按需创建上层

2026-10-19；RimWorld 1.7.4982 rev1280 / Waylith / .NET 10。随当前开发候选提供，程序集版本仍为 0.1.4。

## 飞船开局未扩层，也能先造楼梯

在落脚点正下方完成**人工屋顶**，再放置楼梯蓝图。地图没有上层不再阻止楼梯建造；地面楼梯完工时只创建二层，二层楼梯完工才创建三层，并建立上层最小平台和通路。再通过出口扩建楼板。

- 放蓝图、送材料、开始施工以及取消蓝图都不会提前扩层。
- 已有目标层时不重复扩容；缺少上一层时只追加一层，最多三层。单层电梯使用同一完工保护。
- 完工前失去屋顶会阻止提交，保留原框架和材料。结构楼板、人工屋顶支撑、顶层和出口阻挡规则仍有效。
- 开发模式直接生成、安装或读档中已经完成的楼梯，若地图仍未扩层，会请求下一次安全地图 tick 补建。暂停时需先继续运行一次模拟。
- Pocket / 地下地图继续拒绝扩层。新生成地图与未扩层旧档不再预建楼层；新版实现与证据见 [多层飞船与电梯](DECKS-AND-ELEVATORS.md)。

这是一条通用的楼梯按需创建入口，不要求普通建筑使用逆重核心；**随船迁移**仍只对拥有合法官方逆重核心与有效原版基底的飞船生效。

## 完工提交前扩容，失败不消耗材料

`PlaceWorker_LayerSupport` 在未管理地图上针对楼梯检查地图是否可扩及人工屋顶；其他需要楼层的建筑不会因此普遍放行。`ConstructionSupport.Validate` 在工作候选和施工 tick 中复用这一规则。

`Frame.CompleteConstruction` 的前置补丁在原版销毁资源、发送建成信号及替换 Frame **之前**运行 `StairLayerBootstrap.Prepare`，复用 [已验证的扩容事务](LIVE-EXPANSION-VALIDATION.md)。扩容成功后才交回原版完工流程；楼梯 SpawnSetup 建立结构落脚点并注册连接器。失败时保留 Frame、原材料及原地图。

绕过 Frame 的成品生成/安装/加载，在 SpawnSetup 中仅设置地图级派生请求，不在原生半初始化网格上扩容。`LayerMapComponent.MapComponentTick` 消费请求并连接已有楼梯。该请求不用单独保存：加载成品楼梯时重新提出。旧单层地图不改写加载 XML，已完成的楼梯会请求安全 tick 追加所需层与出口；已有多层存档不因此重置被拆除的结构。

## 历史首版三层创建证据

下表保留原一次性三层实现的结果；当前逐层创建的证据集中在 [新验收](DECKS-AND-ELEVATORS.md)。

| 场景 | 结果 | 原始结果 |
|---|---|---|
| 全 DLC，未扩层太空地图与有效逆重核心；原版建造完工、材料保护、实际上楼、直接生成成品补层 | 18 项通过 | [生成](../Tests/Artifacts/v1-stairs-bootstrap-20261019-150332/generate-stairs-bootstrap-results.json) |
| 同一太空地图，扩层后保存与新进程读档 | 5 项通过 | [读档](../Tests/Artifacts/v1-stairs-bootstrap-20261019-150332/load-stairs-bootstrap-results.json) |
| Core 地表；原版完工、重复楼梯幂等、成品安全 tick 补层、在待补层请求执行前保存 | 19 项通过 | [生成](../Tests/Artifacts/v1-stairs-bootstrap-20261019-150815/generate-stairs-bootstrap-results.json) |
| 同一 Core 保存；成品楼梯重新提出未持久化请求、创建上层和出口，原有上楼 Pawn/物品/通路保留 | 6 项通过 | [读档](../Tests/Artifacts/v1-stairs-bootstrap-20261019-150815/load-stairs-bootstrap-results.json) |
| 已有多层地图施工回归：整块旋转占地、最终 tick 拆顶、材料/预订、真实施工及坍塌 | 31 项通过 | [生成](../Tests/Artifacts/v1-structure-20261019-151024/generate-structure-results.json) |

以上成功结果均 `errors=[]`。已查看太空上层与 Core 重载后的实际画面，楼层切换和下降出口可见。

## 回归发现与修复

`v1-stairs-bootstrap-20261019-150530` 的 Core 成品夹具没有驻留玩家 Pawn，NPC 聚落按原版回收规则消失，等待补层超时。保留失败结果；补入真实驻留 Pawn 并断言 `ShouldRemoveMapNow=false` 后，150815 的生成/读档通过，没有为测试禁用原版地图回收。

共享施工补丁回归的 `v1-structure-20261019-151024` 生成通过，但读档坍塌断言有一项失败：`SpawnSafety.Recover` 在 Map.FinalizeInit 先把位于待坍塌楼板的 Pawn 移到同层邻格，随后坍塌已找不到原 Pawn。修正为：当 Pawn 位于仍存在的结构楼板且该格在保存的待坍塌队列中，保留坍塌流程对跌落与携带物的处理权；普通非法出生/读档位置仍按原规则恢复。原失败存档不重生成、不放宽落地层和物品唯一性断言，使用同一检查点重放：[stair-structure-load-20261019-151343](../Tests/Artifacts/stair-structure-load-20261019-151343/load-structure-results.json) 的 15 项全部通过，`errors=[]`。

## 复现

```powershell
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario stairs-bootstrap -AllDlc -Space -TimeoutSeconds 480
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario stairs-bootstrap -TimeoutSeconds 480
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario structure -AllDlc -TimeoutSeconds 480
```

测试仅使用 `Tests/Artifacts` 的独立数据；没有自动保存玩家游戏。替换 DLL 后须重启游戏。
