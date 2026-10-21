# RimLayers 0.1.0：第一版验收

2026-10-18。结论：**0.1.0 第一版已完成所列范围的实现与验收**。本页把 [PLAN](PLAN.md) 的要求对应到实际实现与有效证据；失败原因和修复过程保留在 [WORK-LOG](WORK-LOG.md)。

本页冻结记录 0.1.0 的范围和证据；**当前 0.1.1 的旧档扩层、下层背景、规划及快捷键见 [更新验收](USABILITY-VALIDATION.md)**，不再沿用下文“仅新地图”的历史限制。

## 使用范围

- RimWorld **1.7.4981 rev136**，Waylith / .NET 10，Harmony 3；根 Assembly-CSharp MVID `f1e6382600a84191bbc6d8a184fd6b57`。
- 新生成的玩家地表殖民地固定三层、一张真实 Map；NPC / Pocket / 地下 / 太空地图不自动扩容。已有普通地图不迁移。
- 默认地面与上层、人工屋顶支撑、普通二维切层；近战限同层，射程用逻辑平面距离，弹道和爆炸使用真实层高与遮挡。人工灯光首版限同层；日光按逻辑天空列计算。
- 首版建议地面不超过 **250×250**。二层仅有独立测试用例，不是产品层数设置；层数、步长和坐标带不可在存档内热改。更大地图、更多楼层、多年殖民地及第三方组合尚未验证。
- IsoRim / ProjectZ / Baal 是研究参考，不是运行依赖；保留实际采用代码的 MIT 归属。CE、三维显示、天然岩顶、地下与旧图迁移仍是原计划的后续适配项。

## 按原始要求验收

表内目录位于 `Tests/Artifacts`。生成 / 读档分别是两个原生游戏进程；数字为该场景实际断言数，不代表其他场景或全部原版玩法自动兼容。Core 与 DLC 结果均另含 Harmony / RimLayers。

| 要求 | 当前结果与关键证据 |
|---|---|
| 单 Map 三层、间隔隔离、显示与保存 | 当前简中 [M1](../Tests/Artifacts/m1-20261018-181433)：41 / 43，全部五 DLC；布局纯检查 862803 项。生成仅地面内容、上层空域；切层不移动实体，多图与河流/道路、相机、拾取和存档恢复通过 |
| 连续 1→2→3、同层绕行、必须反向跨层、不截断楼梯候选 | [Core maze](../Tests/Artifacts/v1-navigation-maze-20261018-135042) / [全 DLC maze](../Tests/Artifacts/v1-navigation-maze-20261018-135439)：各 13 / 6。真实 1→2→3→2→1、第四个连接器才有路，原生 Job / Driver 与最终目标保持 |
| 拆梯、堵出口、目标移动、自定义路径参数 | [navigation-rules](../Tests/Artifacts/v1-navigation-rules-20261018-140334)：11；动态拆梯及目标移动见工作/导航记录。[Core 落脚门](../Tests/Artifacts/v1-navigation-door-20261018-175037) / [全 DLC](../Tests/Artifacts/v1-navigation-door-20261018-174215)：各 15，闭门、禁止、途中变敌对、备用路线、真实开门与原始 canBash / TraverseParms 保留 |
| 工作台、多次取料、原生预订和产物入库 | [Core work-save](../Tests/Artifacts/v1-work-save-20261018-144307)：17 / 15；真实 DoBill 跨三层反复取材、实际产物和入库，途中保存保持原生账单、队列和身份；未引入跨图 Travel Job 或第二套预订 |
| 容器材料、堆栈拆并、多 Pawn 与需求/命令抢占 | [最小 Odyssey 容器](../Tests/Artifacts/v1-work-container-20261018-135759) / [全 DLC](../Tests/Artifacts/v1-work-container-20261018-135849)：各 16 / 10；[搬运](../Tests/Artifacts/v1-work-haul-20261018-140114)：15；[饥饿/征召/取消](../Tests/Artifacts/v1-work-hunger-20261018-140138)：11。取消后的旧 Job 不复活，新合法工作仍可重新预订 |
| 蓝图供料、家具与容量 | [Core home](../Tests/Artifacts/v1-home-20261018-174044) / [全 DLC home](../Tests/Artifacts/v1-home-20261018-174247)：各 17 / 6。原生蓝图→供料→Frame→Stool 精确消耗两层 12/13 木材；三名真实 Play_Horseshoes 保留任务经二层到达并获得娱乐，第四人被原生容量 3 拒绝 |
| 严格屋顶、旋转多格、最终提交前保护 | [structure](../Tests/Artifacts/v1-structure-20261018-144222)：31 / 15；部分缺顶、实际最后施工 tick 拆顶、Frame/4 钢保留和预订释放、恢复支撑后邻格扩建、正常拆层与装修底板保留。全 DLC 的蓝图/供料及天空结构另有上、下行证据 |
| 支撑失效与天空落物、真实 holder | [Core skyfall](../Tests/Artifacts/v1-skyfall-20261018-145703) / [全 DLC](../Tests/Artifacts/v1-skyfall-20261018-145758)：各 32 / 22；[Core 原生穿梭机](../Tests/Artifacts/v1-skyfall-shuttle-20261018-145347) / [全 DLC](../Tests/Artifacts/v1-skyfall-shuttle-20261018-145413)：各 13。完整旋转占地、最高遮挡、失败保留内容物、部分卸货、不重播撞击；连锁坍塌、退款、携带实体及队列读档见 structure |
| 跨层目标、射位、敌人 AI、楼板挡弹、无步行路线交火 | [射位 AI](../Tests/Artifacts/v1-combat-ai-20261018-113044)：16；[Core 实际射击](../Tests/Artifacts/v1-combat-shot-20261018-164539)：14 / 9；[Core 友伤/体型](../Tests/Artifacts/v1-combat-policy-20261018-153540) / [全 DLC](../Tests/Artifacts/v1-combat-policy-20261018-153756)：各 28；[Core 自动炮塔](../Tests/Artifacts/v1-combat-turret-20261018-153724) / [全 DLC](../Tests/Artifacts/v1-combat-turret-20261018-153832)：各 9。真实 Thing 身份与原生伤害流程，未复制跨层靶标 |
| 护盾、飞行/引爆/爆炸传播中保存 | [最小 Royalty 拦截](../Tests/Artifacts/v1-combat-shield-save-20261018-152716) / [全 DLC 放行](../Tests/Artifacts/v1-combat-shield-pass-20261018-152801)：各 13 / 10；[原生 100 tick 手雷](../Tests/Artifacts/v1-combat-grenade-flee-20261018-151218)：11 / 9；[爆炸传播](../Tests/Artifacts/v1-combat-explosion-20261018-152521)：15 / 10。冻结发射/覆盖事实、原生计时与命中去重，读档不重播伤害/拦截/开始阶段 |
| 电网、设施、空气与热 | [Core utilities](../Tests/Artifacts/v1-utilities-20261018-124119)：26 / 8；早期实际全 DLC [utilities](../Tests/Artifacts/v1-utilities-20261018-115831)：25 / 8。三层原生电网、环/分网、预算与设施同层容量；[全 DLC air](../Tests/Artifacts/v1-air-20261018-121310)：18 / 5，授权气道、门与井口守恒交换；[Core heat](../Tests/Artifacts/v1-heat-20261018-132512)：20 / 6，独立 Room、容量/交换与原生温度读档精度 |
| 天空、天气、游戏光照、火焰/惊扰/危险预警 | [Core sky](../Tests/Artifacts/v1-sky-20261018-154209)：19 / 9；[全 DLC sky](../Tests/Artifacts/v1-sky-20261018-154312)：20 / 9。日光/实际照明、雨雪和真实 SoakingWet；[Core hazards-timed](../Tests/Artifacts/v1-hazards-timed-20261018-134536) / [全 DLC](../Tests/Artifacts/v1-hazards-timed-20261018-134632)：各 34 / 11，分别授权、真实原生通知和 Sprint 逃离 |
| 牵绳、机体、Lord | [Core 双动物](../Tests/Artifacts/v1-roping-20261018-171013) / [全 DLC](../Tests/Artifacts/v1-roping-20261018-171430)：各 30 / 24；[Core Lord](../Tests/Artifacts/v1-lord-20261018-171122) / [全 DLC](../Tests/Artifacts/v1-lord-20261018-171540)：各 13 / 13；[最小 Biotech 机体](../Tests/Artifacts/v1-mechanitor-20261018-165050) / [全 DLC](../Tests/Artifacts/v1-mechanitor-20261018-165140)：各 17 / 15。真实双绳、控制组和成员唯一；正常过层不记损失，死亡仍原生处理；远层圈经过同镜头画面核对 |
| 世界出口、NPC/逃跑/偷窃/家园商队 | [Core 最终出口](../Tests/Artifacts/v1-world-exit-20261018-181553)：37 / 25；[全 DLC 最终出口](../Tests/Artifacts/v1-world-exit-20261018-181730)：37 / 25。真实三层步行至地面逻辑北界，普通离图保留库存并通知原生 Lord；原生家园商队全流程、Flee、实际 Steal 同一 Driver 下楼后一次提交；二层保存重启保持原生出口任务 |
| 地图规模与有限持续运行 | 120/250 地面、2/3 层、20/60 名测试 NPC，各进程真实运行 12000 tick；四次全程往返、独立读档、实体唯一性、隔离区、计时与内存见下表。多年持续游戏未验证 |

表中有效结果均零失败、无测试观测期 Log.Error。`Log.Error` 观测在测试初始化之后；本机启动仍有既有 Vulkan 层文件路径与未启用包重复 packageId 提示，未修改其他项目去清除。原生退出时的分配提示也没有被算作“不存在”。

## 规模的数值和采样条件

硬件：Ryzen 7 5700X，8 核 / 16 逻辑处理器；物理内存 34281680896 bytes（约 31.93 GiB）；Windows 10.0.19045。实际画面 1920×1080。每组是独立新地图，尺寸间未固定同一 seed，不能将差值解释为严格因果 A/B。

每组用一个原生 Lord 管理 20/60 名中立 AncientSoldier，在每对相邻层各两处楼梯的夹具上完成四次整程，再由原生防御 AI 正常运行至 12000 tick（约 4.8 游戏小时）。测试人数不是地图全部 Pawn 数，且不是 60 名复杂账单殖民者的吞吐承诺。Pawn/Lord Tick 未被屏蔽；路径有两次排除的预热后，逐测试成员测一次完整地面→顶层查询。

前 120 tick 排除，共 11880 次 DoSingleTick 方法经过时间；夹具控制和保存 IO 不计入该方法，但系统调度和 GC 会影响结果。绘制分别观测原生 MapInterface、MapDrawer、DynamicDrawManager，通常各 792 个样本；它们是 CPU 侧提交经过时间，**不能换算为 GPU 耗时、FPS 或端到端帧率**。进程内存包含引擎、资产和缓存；不强制 GC。

以下为新生成进程的代表值，单位 ms；内存为结束时 working set / private，单位 GiB。

| 目录后缀 / 组合 | 地面 / 层 / 测试 NPC | 生成 / 保存 | 路径均值 / p95 | tick 均值 / p95 / p99 | WS / private |
|---|---|---:|---:|---:|---:|
| [172328 / Core](../Tests/Artifacts/v1-scale-20261018-172328) | 120×120 / 3 / 20 | 1981 / 457 | 1.809 / 2.260 | 0.463 / 1.026 / 6.160 | 1.748 / 2.478 |
| [172528 / Core](../Tests/Artifacts/v1-scale-20261018-172528) | 120×120 / 2 / 20 | 2384 / 392 | 1.377 / 1.811 | 0.308 / 0.734 / 3.248 | 1.713 / 2.432 |
| [172646 / Core](../Tests/Artifacts/v1-scale-20261018-172646) | 250×250 / 3 / 20 | 4521 / 599 | 2.257 / 2.789 | 1.087 / 3.536 / 15.840 | 2.309 / 3.113 |
| [174753 / Core](../Tests/Artifacts/v1-scale-20261018-174753) | 250×250 / 3 / 60 | 4447 / 816 | 2.105 / 2.344 | 1.079 / 3.198 / 11.420 | 2.313 / 3.157 |
| [172821 / 全 DLC](../Tests/Artifacts/v1-scale-20261018-172821) | 250×250 / 3 / 60 | 5502 / 693 | 2.186 / 2.490 | 1.862 / 5.395 / 27.030 | 2.727 / 4.168 |

三层 250×250 的存储实际为 250×765 = 191250 格。Core 60 人结束时实际共有 97 个地图 Pawn / 34814 个 Thing；全 DLC 60 人共有 136 个 Pawn / 35857 个 Thing。二层仅在新地图分配前应用，未热改任何存档。

加载进程也完整执行相同 12000 tick。Core 250 / 3 / 60：加载 3180 ms，路径平均 2.018 / p95 2.179，tick 平均 1.082 / p95 2.996 / p99 10.973，结束 WS / private 2.239 / 3.027 GiB。全 DLC 最终出口补丁回归复用同一存档：加载 3084 ms，路径平均 2.247 / p95 2.353，tick 平均 1.698 / p95 4.483 / p99 26.523，结束 WS / private 3.185 / 4.574 GiB；12 项零失败、实际 140 个 Pawn，测试队伍四趟全部完成。

绘制代表值（Core 250 / 3 / 60 加载）：MapInterface 平均 0.0086 / p95 0.0135 ms；MapDrawer 0.2478 / 0.3811 ms；DynamicDrawManager 0.8131 / 3.9543 ms。全 DLC 最终加载的三项绘制平均 / p95 分别为 0.0143 / 0.0230、0.2849 / 0.3952、0.7598 / 3.8904 ms。完整均值/分位/最大值、全部样本、分配量和 GC 次数保存在每组 `*-scale-metrics.json`，硬件在 `scale-machine.json`。

172821 初次加载未产生终态结果，旧记录保存在 `load-incomplete-*`。明确独立测试 `Prefs.runInBackground=True` 后，复用同一有效检查点完成加载；最后边界补丁也复用该检查点，不重复生成替换输入。之前有效加载指标保存在 `load-before-world-exit-*`。这反映隐藏进程需要明确后台更新条件，不据未完成记录推断死锁。

## 画面和失败证据

实际检查的画面包括当前简中 M1 地面/空域、完全打开的楼梯落脚门、真实 Stool/多人 Horseshoes、上层电网连线与照明、飞行/命中/爆炸、双动物畜栏和敌方队伍，以及机体的原生同层/远层同镜头指挥圈。图片分别随场景保存，不用“提交了绘制调用”替代像素结果。

已修复的实际缺陷包括落脚门权限丢失、飞行大 delta / 护盾读档提交边界、SelectedPawns 重入枚举，以及出口到达检查漏过加载生命周期。测试前提错误（如家园普通 Goto 不会创建商队、商队提交前已移除 Pawn 地图身份）与生产缺陷分开记录。早期失败目录保留，不能把其失败抹成最终通过。WorldExit 当前直接替换已核实的原生执行委托几何，不包裹或另建 Toil，也不全局拦截 Pawn.ExitMap。

## 重现与后续适配

从游戏根目录运行：

```powershell
dotnet build Mods/RimLayers/Source/RimLayers/RimLayers.csproj -c Release
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-M1.ps1 -River -Language ChineseSimplified
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario world-exit -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario home
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario scale -MapSize 250 -PawnCount 60 -AllDlc
```

脚本只使用自己的 `Tests/Artifacts/<run>` 配置、存档与隐藏子进程。V1 不传原生 `-mod`，以免它自动加入全部官方 DLC；结果记录实际 activeMods / MVID。M1 仍按显式实测的全部五 DLC 组合报告。正在运行的游戏会锁定 DLL，等待退出后再构建；修改程序集/Harmony 后必须重启，不能靠 Def 热重载验收。

天然岩顶、地下层、旧图迁移、三维显示、CE 和具体第三方框架保留为独立后续阶段；未测试的官方玩法组合也不能由本页推导通过。首版不模拟建筑重量，不承诺任意复杂原版/DLC Job 及其他 mod 自动支持。请先在新地图、明确依赖组合内使用；含楼层的存档持续需要 RimLayers。
