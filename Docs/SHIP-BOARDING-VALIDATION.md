# 多层飞船发射：乘员不必集中到核心甲板

2026-10-19；RimWorld 1.7.4982 rev1280 / Waylith / .NET 10，RimLayers 0.1.4开发候选。

## 使用

重启游戏后，同一艘合法飞船其他甲板上的普通乘员可参与发射，不需要先全部走到逆重核心所在层。驾驶员和副驾驶仍须到原版控制台岗位；选择离船的访客仍须真正离开这艘船，船外乘员仍会等待登船。

若乘员尚在船外楼层，需有真实可走的楼梯/电梯路线。无路、出口阻塞或船体不合法不会被当作已登船。

## 原因与修复

已有 `ValidSubstructureAt` 和 `IsOnboardGravship_NewTemp` 的适配并未覆盖全部原版调用者。发射仪式 `StageEndTrigger_RoleArrivedAndSpectatorsOnSubstructure.ArrivedCheck` 直接三次读取 `engine.ValidSubstructure.Contains`，分别检查普通乘员、待登船列表和待离船列表；该集合仍应保持为引擎甲板二维集合。登/离船 Driver 的完成检查也直接读取该集合，造成上层已登船 Pawn 被误当成失败并移出待登船列表。

`Ships/ShipBoardingPatches.cs` 仅在这两个原版成员中，把四处集合查询改用现有 `ValidSubstructureAt`。驾驶员到岗标签、原版每60tick检查、成员列表、访客离船及消息/失败处理保持原逻辑；不把整个仪式结果直接改为通过，也不改全局二维基架getter。

原版 `TryFindSpotOnGravship` / `TryFindSpotOffGravship` 只遍历层内 Region 图。现在保留成功结果；失败时，仅对托管地图中的合法玩家船体枚举真实楼面并检查跨层可达性。登船先找室内，再允许露天甲板；离船只选择该船体之外的真实可达楼面。不增加运输Job、不传送Pawn，也不修改原版预订。

## 游戏验收

`Tests/Run-V1.ps1 -Scenario ship-structure` 加入 `Diagnostics/V1ShipBoarding.cs`。使用真实原版仪式触发器、角色分配及实际 GotoShip Driver；测试到真实60tick边界才检查到齐，不使用总是返回真的替身。

- 全五DLC、引擎在二层：`v1-ship-structure-20261019-182314`，55项通过，`errors=[]`。
- 仅Core+Odyssey（不启用Ideology）、引擎在三层：`v1-ship-structure-20261019-182513`，55项通过，`errors=[]`。
- 逐层验证一/二/三层普通乘员均可通过到齐检查。
- 驾驶员未到岗仍阻止结束；船外乘员、仍在上层船内的待离船访客仍阻止结束；访客真正离船后才通过。
- 实际上层 GotoShip Driver 完成后仍保留该待登船乘员，未发出错误登船失败。
- 独立上层平台通过楼梯到船体；跨层离船出口可达；拆掉唯一楼梯后拒绝登船，不伪造路线。
- 同场景原有船体、承载、设施、施工和单甲板迁移检查继续通过。

```powershell
# 全DLC / 二层核心
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-structure -AllDlc -EngineLayer 1 -TimeoutSeconds 480
# 仅Core+Odyssey / 三层核心
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-structure -EngineLayer 2 -TimeoutSeconds 480
```

首次构建的诊断夹具使用错误的DefOf命名空间，编译失败1项；已修正，未改游戏或原版数据。构建和游戏测试均在独立测试目录运行，不改玩家存档或启用列表。
