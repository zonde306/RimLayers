# 袭击、手榴弹落层与二三层交火验收

## 已修复：投掷兵选中射位后站着不投

真实袭击留下了 5 名长期处于 `Wait_Combat` 的投掷兵。对同一份战场存档重新检查，5 人都满足：抛物线可达、视线被楼板挡住、自动索敌没有目标、角色不处于忙碌状态。

原因是两个原版入口使用不同条件：`JobGiver_AIFightEnemy` 根据武器 `CanHitTarget` 决定原地等待；`JobDriver_Wait.CheckForAutoAttack` 重新索敌时要求 `NeedLOSToAll`。三维曲线能越过平台，并不意味着该位置能看见平台上的实体。射位搜索因此选择了不能自动开火的位置。

修复将**敌人战斗 AI 的跨层曲射射位**同时约束为可见、弹道可达。当前格和候选格使用一致条件；找不到时交回原版其他行为。玩家手动指定投掷位置、普通直射、同层规则不增加这个前提。

生产改动：`Source/RimLayers/Combat/AIFiringVisibility.cs`，以及 `FiringPositions.cs` 的候选筛选入口。

使用原失败存档而非重新抽取袭击：修复后 2400 ticks 内，原先 5 人全部离开无效射位，真实发射了 2 枚手榴弹，未再进入该等待循环。恢复后的保存中原目标炮台已不在地图实体列表内。随后新生成的隔离平台袭击也通过，普通直射 AI 回归通过。

## 二层与三层之间没有依赖地面层的坐标偏差

这里的层号均为界面显示的 1、2、3；原始 JSON 的 `LayerId` 为 0、1、2。

- 二层向三层直射：射程为原武器的 90%；三层沙袋阵地的身体暴露为 3/9。实弹分别击中了真实沙袋和 Pawn，接触与伤害均发生在三层。
- 三层向二层直射：射程为 110%；枪口可以越过自己的沙袋，实弹击中二层 Pawn。过陡俯射仍被自己的掩体挡住。
- 二层站在完整三层楼板下方时，提示解与原生发射入口都拒绝穿板射击。
- 移除二三层之间的步行连接后，上述合法远程射击仍可进行。

该专项使用原生武器发射和伤害入口；关闭武器野射只用于稳定检查身体采样/掩体碰撞。真实袭击不修改攻击者装备、精度、散布、伤害或 Job。

## 手榴弹的落点、引信和爆炸使用同一实际层

`combat-grenade` 调用真实 `Verb_LaunchProjectile.TryCastShot`，让原生 `Projectile_Explosive` 推进飞行和引信，核对存储格、物理高度、爆炸原点与伤害：

- 一层→二层、一层→三层、二层→一层、三层→一层。
- **二层→三层、三层→二层**，以及三层同层露天投掷。
- 完整楼板阻挡时拒绝发射；飞行后插入的水平阻挡面在实际下降交点截住手榴弹，爆炸留在接触层，而不是原指定的地面层。
- 恢复原版 `forcedMissRadius=1.9` 做 20 次平台边缘投掷：既有落在三层平台的，也有落到一层地面的；每一枚的爆炸原点均匹配实际落点。
- 在三层落地、引信尚未结束时保存并完全重启。引信继续，三层物体受到原版爆炸伤害，完整楼板下的二层物体不受伤，没有重复接触或引爆。

精确落层组仅关闭强制散布和野射；原版格内落点偏移仍存在。动态楼板测试用发射快照解析求解 `y(t)=6` 的下降交点，避免把没放在弹道上的楼板误报成穿透。

## 真实袭击观察

测试殖民地为三层平台、两名持栓动步枪的守军、一座原生供电迷你炮台。攻击方由原版 `RaidEnemy`、`ImmediateAttack`、`EdgeWalkIn` 生成，规模 1000 点；攻击、寻路、Lord、倒地和撤退均由游戏运行，未给攻击者下达战斗 Job。

- Core 枪械混合袭击：14 名攻击者，包括手枪、冲锋手枪、步枪、狙击枪、霰弹枪和投掷武器。记录到 51 发向上射击，7 人到达三层，最终撤离。
- 全 DLC 近战袭击：15 名近战攻击者均经真实楼梯到达三层，出现原版近战伤害、伤亡和撤退。
- 全 DLC 投掷兵及伴随动物袭击：观察到破片、燃烧、EMP 装备，上楼绑走倒地殖民者并从地面离图。
- 断开二三层楼梯后，攻击者没有非法进入三层；修复后新袭击记录到 16 次向上投掷，守军失能后攻击方撤离。

这些是立即攻击袭击；围攻建炮阵地、破墙/工兵、空降、第三方战斗框架和大规模性能不属于本轮实测。

## 构建与游戏证据

RimWorld 1.7.4982 rev1280。Release 构建零警告、零错误。下表有效运行均为零失败，测试记录 `errors=[]`。每个目录包含结果 JSON，袭击另有逐 120 ticks 的 `raid-trace.json`，保存与截图也位于同目录。

| 验证 | 结果数量 | 独立测试目录 |
|---|---:|---|
| Core 枪械混合袭击 | 13 | [202510](../Tests/Artifacts/v1-combat-raid-guns-20261020-202510) |
| 全 DLC 投掷兵及绑架撤退 | 12 | [200453](../Tests/Artifacts/v1-combat-raid-20261020-200453) |
| 全 DLC 近战上楼袭击 | 11 | [201332](../Tests/Artifacts/v1-combat-raid-melee-20261020-201332) |
| 二层↔三层实弹、掩体、楼板 | 25 | [201746](../Tests/Artifacts/v1-combat-upper-20261020-201746) |
| 原卡住存档恢复，修复后 | 5 | [203104](../Tests/Artifacts/v1-combat-raid-audit-20261020-203104) |
| 普通直射 AI 回归，修复后 | 17 | [203307](../Tests/Artifacts/v1-combat-ai-20261020-203307) |
| 新隔离平台袭击，修复后 | 13 | [203336](../Tests/Artifacts/v1-combat-raid-isolated-20261020-203336) |
| 手榴弹全方向及落地重载，修复后 | 125 / 6 | [203656](../Tests/Artifacts/v1-combat-grenade-20261020-203656) |

已查看实际袭击、二三层交火、重载后平台手榴弹及 AI 恢复截图。手榴弹生成阶段的同步探针会留下密集爆炸光效；核对落地外观使用重载阶段的 `load-grenade-landed.png`。

## 复测入口

在游戏根目录运行，脚本会创建独立测试资料并启动自己的游戏进程：

```powershell
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario combat-raid-guns
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario combat-raid-melee -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario combat-raid-isolated -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario combat-upper -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario combat-grenade -AllDlc
```

`combat-raid-guns` 用原版编组预览选择包含枪械兵种的种子；不替换袭击生成器。`combat-raid` 可用 `-RaidSeed` 指定编组种子。重放本次缺陷使用 `combat-raid-audit -AllDlc -RaidAuditSave <200630 目录中的 Saves/RimLayers_V1RaidEnd.rws>`；脚本复制到新的测试目录，原文件保持原样。重放断言针对这份含 5 名卡住投掷兵的具体保存。
