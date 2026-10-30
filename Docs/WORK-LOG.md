# RimLayers 第一版实施记录

## 当前目标与范围

**0.1.0 第一版已完成 [PLAN](PLAN.md) 的 D0 与 M1–M5 所列范围实现及验收。** 当前矩阵、已知限制、规模方法和有效证据见 [V1-VALIDATION](V1-VALIDATION.md)。本日志保留各阶段当时的状态和失败，不将历史“下一项”当作当前待办。基准为 RimWorld 1.7.4981 rev136 / .NET 10。

## 测试组合口径更正

2026-10-18 核对实际启动日志发现：原版 `-mod` 会强制加入所有已安装且版本兼容的官方 DLC，不是严格启用列表。下方早期 V1 记录实际包含五个 DLC，不能当作 Core-only 证据。新版 Run-V1 在自己的 Tests/Artifacts 配置里明确 activeMods / knownExpansions，启动时不传 `-mod`；结果增加 activeMods 与程序集 MVID，并断言目标 DLC 数量。原版规则原已记录在工作区 docs/debugging.md，后续按实际结果的组合归档。

## 已有证据

- M1 历史验收见 [M1-VALIDATION](M1-VALIDATION.md)，使用当时的 1.7.4980；不把旧结果当作当前版本全量复验。
- 路由原型：Tests/Artifacts/v1-navigation-20261018-095948，生成 11 项、重启读档 4 项断言通过。真实 Pawn 经过 1→2→3，途中拆除所选楼梯后保留同一个 Goto Job 重规划；跨层步中保存后在新进程恢复最终目标。这次回归已采用实例与 loadID 双重任务身份检查；后续变化按影响范围复验。
- 连续工作与仓储：Tests/Artifacts/v1-work-20261018-100130，该记录对应候选的真实游戏 15 项断言通过。原版 JobGiver_Work 从一层扫描并选中三层工作台，随后原版 DoBill 在三层间两次取料，保留同一 Job，消耗 10 土豆、生产 1 简餐；随后原版搬运将该真实产品放入另一层的指定仓库。同一测试通过跨层床与食物查询。
- 完整端点搜索纯算法测试包含第 4 个楼梯才可达、非单调楼层、成本比较、零成本环和断路，8 个用例通过。它们不代替真实地图的非单调路径验收。

## 结构阶段的当前证据

Tests/Artifacts/v1-structure-20261018-100359：生成 30 项、重启读档 15 项断言通过。覆盖旋转后的完整占地、提交入口材料保护、实际原版施工最后一 tick 拆顶后保留 Frame/4 钢材并释放预订、恢复屋顶后完成扩建、直接移除屋顶触发全列失效、携带物/落物唯一身份、Frame 实有材料退款与楼板残料一次提交，以及待处理队列在新进程读档后继续。新增正常拆除验收：有物品占用则不拆除；空顶层楼板拆除后恢复空域，按原版拆除比例将材料放在有效邻格；移除木地板装修仍保留结构底板。天空落物完整规则尚未验收。

早期夹具把后放 Frame 挤走的物品误当作落物，已修正放置顺序并增加坍塌前状态断言。清理已删除的遗迹还可能留下原版待落顶事件；测试夹具只移除被清空区域的旧事件，不禁止后续真实落顶。原版事故中断任务可将携带物的原实例放在落点；检查唯一身份与所有权，而不强求中断后的同一携带容器。

## 已核实且影响实现的接口

- PathRequest.ValidateInt 的 ByPawn 分支用 Pawn 的实际位置做可达性预检，不使用请求 Start。路由器的原版层内子请求需窄范围改为真实请求起点；不伪造 Pawn/Map，不放宽原版通行参数。
- Pawn_PathFollower.ResetToCurrentPosition 可丢弃旧路径并重发相同目的地请求，不需结束当前业务 Job。
- 原版 Frame.CompleteConstruction 在创建建筑或写地形之前销毁资源和 Frame；校验必须在方法入口之前，并阻止调用者继续以成功推进。
- RoofGrid.RemoveRoofUnsafe 仅清除数组项，不触发 SetRoof 事件；须独立通知结构失效。
- TerrainGrid.SetTerrain 的原生寻路成本刷新早于结构事实的 postfix 更新；新增结构事实后还需更新实际 PathGrid 成本。
- 原版多楼层室外格可能共享室外 Room；导航仅要求 Region 与逻辑边界正确，不能把所有室外 Room 身份不同当作验收。
- 原版 PathGridJob.CostForCell 对 customizer 网格作附加成本；跨层边已计入落点的同类自定义成本；物理爬梯时长不额外加上仅用于规划偏好的自定义成本。

- 原版 Job 对象池可能把同一对象复用为 GotoWander；验收不能仅比较 ReferenceEquals，现同时捕获并比较 Job.loadID。
- 原版读档后首帧可能在 UpdatePlay postfix 前推进 tick；队列恢复测试在检查保存事实前冻结自己的测试进程 tick，然后再由夹具推进。

## 当前验收结论

完整矩阵已收口，详见第一版验收文档。天然岩顶、地下层、旧图迁移、三维显示、CE、具体第三方框架和多年运行仍未验证，保持原定后续范围。所有游戏测试使用 Tests/Artifacts 内独立配置；未修改玩家配置、启用列表或存档，原生引擎 Player.log 仍可能共用日志目录。

## 战斗实现与接入证据

当前代码在 Source/RimLayers/Combat，由 LayerMapComponent.Combat 持有服务。选目标与射位查询保留原版阵营、威胁、验证器、预订和危险规则，候选范围使用逻辑平面距离；当前能射击与可步行到目标分开。原版 AIFightEnemy 的追击/守卫策略不被强行改成追击。武器仍通过原版 Verb / ShotReport / Launch，露出占地格选择保留当前武器射程与 LOS 契约。

LayerFlight 保存发射点、目标点、弧线、进度、已检查实体及提交事实，CWT 只作派生索引。碰撞按三维首次命中排序，范围盾是所属楼层内高 3 的圆柱；个人盾进入原版伤害链。原版散布只掷一次，投射物实际显示/格位置跟随物理飞行层；原生二维弧线绘制对受管弹丸关闭，避免额外北向偏移。手雷保留其原版弧高参数，头顶弹道采用现有原型弧线规则。

LayerBlast 保存物理爆心，以及原版 ExposeData 未写出的 postExplosionGasAmount / postExplosionGasRadiusOverride；原版 Explosion 继续唯一拥有待传播格、开始 tick、受伤去重与忽略名单。覆盖在引爆时冻结，损坏本次遮挡不会让同一波追溯穿透。原版 DamageWorker 负责实际伤害、护甲、火、地形和日志；距离/时间、排除半径、气体、颜色/融雪及 Vaporize 的 2.9 截断使用统一物理距离。爆炸气体释放不代替 M5 的持续空气交换。

### 历史实际游戏证据（Core + 全部五个官方 DLC + Harmony + RimLayers）

| 场景 | 证据目录（相对 mod 根） | 结果与覆盖 |
|---|---|---|
| 实际射击与飞行存档 | Tests/Artifacts/v1-combat-shot-20261018-112457 | 生成 13 / 新进程读档 9 通过；真实 AttackStatic 与原版步枪、飞行后射手换层、旧枪口不变、真实目标伤害只提交一次 |
| 射位与敌人 AI | Tests/Artifacts/v1-combat-ai-20261018-113044 | 16 通过；原版 Goto 移到合法上层射位，实际射击；无目标步行路线仍可交火；区域预算与调用者验证器保留 |
| 护盾与近战 | Tests/Artifacts/v1-combat-shields-20261018-111431 | 19 通过；错层范围盾不拦截、实际经过层拦截、先撞墙不扣后方盾、个人盾能量 1→0.406、EMP 原版失效与跨层近战提交拦截 |
| 动态弹道与多格目标 | Tests/Artifacts/v1-combat-ballistics-20261018-112931 | 14 通过；目标锚点被挡但露出格可命中、飞行后新增有效楼板挡弹、目标换层不追踪命中 |
| 爆炸传播中存档 | Tests/Artifacts/v1-combat-explosion-20261018-113541 | 生成 14 / 新进程读档 10 通过；开放边缘波及上层、楼板阻挡、三维衰减、已受伤实体移到待传播格不再受伤、气体参数读档保留、override/邻格拒绝隔离区 |
| 原版手雷延迟引爆 | Tests/Artifacts/v1-combat-delay-20261018-112352 | 生成 9 / 新进程读档 8 通过；落地已提交而尚未爆炸时保存、原版倒计时继续、实际爆心高度不变、爆炸只伤害一次 |
| 爆炸派生效果 | Tests/Artifacts/v1-combat-blast-effects-20261018-113514 | 9 通过；Vaporize 截断、跨层气体释放、独立气体半径与中心排除、楼板阻气 |

以上结果对应各目录的 *-results.json 和 stdout/stderr。最新 Release 构建 0 警告、0 错误。已查看 112457 的飞行/命中截图和 112352 的延迟爆炸截图：弹丸出现在中间层，靶墙在地面可见，跨层选择线/括号不横穿坐标带。截图现在等待相机更新后再采集，早期空画面截图不作为视觉通过证据。

早期失败记录保留：110404 的反向盾射击被上一靶墙包围，修正夹具后通过；112047 的去重移动终点不在待传播格，改为同波另一侧合法格后通过；112733 的露出格紧贴遮挡边缘，原版 0.3 格散布可再次撞墙，调整射手横向站位后仍保持锚点被挡；113328 的近天花板爆心实际在屋顶下，被正确阻挡，改为开放井口边缘测试 Vaporize 截断。没有放宽碰撞或关闭结构规则来让这些场景通过。

已在当前 MVID 定向读完：
- Verse.Verb.TryFindShootLineFromTo：06003A7C。近战/短程入口先走 Touch，远程再走范围与 LOS；需要保留两种契约。
- Verse.Verb_LaunchProjectile.TryCastShot：06003AB6。发射前建立 ShootLine，计算强制散布、ShotReport、掩体与 hitFlags，再调用原版 Projectile.Launch。准确度、散布与实际投射必须一起使用逻辑几何。
- ShotReport.HitReportFor：060039E8。distance、掩体、烟雾和天气有各自入口，不能只改一处射程。
- Projectile.Launch 完整重载：06002A27。destination 从真实 usedTarget.Cell 加一次随机偏移，ticksToImpact 来自水平距离；原版保存这组字段，但没有楼层发射高度。
- Projectile.TickInterval：06002A29。先取旧 ExactPosition，再减 delta，做整段自由拦截，最后更新 Position / ImpactSomething；不是只扫终点。
- CheckForFreeInterceptBetween / CheckForFreeIntercept：06002A2A / 06002A2B。范围盾先于普通格内拦截；裸用目标层二维投影会错过实际飞行层，必须和最早墙/楼板碰撞排序。
- ImpactSomething：06002A30。仍有 usedTarget、姿态、命中 flags 与屋顶分支；需要在提交前复核目标实际楼层，不追随飞行期间换层的引用直接造成伤害。

补充核实：Explosion.StartExplosion / Tick / ExposeData（0600285D / 0600285E / 06002863）保留传播与去重；GetCellAffectTick / AffectCell / AddCellsNeighbors（06002864 / 06002865 / 06002868）各有空间入口。Projectile_Explosive.Explode（06002A38）在 Destroy 后发起爆炸，须在入口捕获已保存的命中点；ArcHeightFactor（17000878）仅计算原版二维弧线系数。Vaporize 的 ExplosionAffectCell / ExplosionDamageThing（06001D80 / 06001D81）另有 2.9 距离截断，不能只改基类。

下一责任仍在本任务：完成 M5 其余公共系统/特殊 Pawn/规模整合，再完成 M2/M3/M4 剩余场景与规模整合。爆炸总热量分配的后续接入见末节；危险爆炸通知及其 Pawn 躲避的后续接入见末节，剩余组合仍须结合 AI 收口，不能将局部爆炸验证当作 M5 已完成。

## 结构事故默认规则

立即撤销受影响整列的有效楼面和寻路资格，每 tick 最多处理 32 个排队格。普通建筑沿原版 KillFinalize 清理与残骸流程；未完工 Frame 按 Cancel 返回其真实材料；结构/装修地面保留四分之一材料（向下取整）。Pawn 保留实例与库存，并按每下降一层 8 点 Blunt 伤害；任务按事故正常中断，其原版携带物清理可以在落点放下同一实例。不引入重量或冲击下一层的模拟。无合法落点时保留队列和实体，不能删除来消除故障。


正常拆楼板要求先清空本格 Pawn/建筑/蓝图/物品，移除本格屋顶并从最高层向下拆；必须保留合法邻格。施工过程中条件改变则结束尝试，不能只跳过 RemoveTopLayer 后虚报成功。装修层仍使用原版剥离流程，不能剥出假地面。


## M5：垂直电网、设施与气体

代码位于 Source/RimLayers/Environment，由 LayerMapComponent 持有 PowerLinks / AirLinks。RL_PowerRiser 使用原版 CompPowerTransmitter、Building_PowerSwitch / CompFlickable 和一个注册组件；相邻层同局部坐标的有效启用端子建立垂直边。原版 BFS 一次遍历整个连通分量，远端端子也进入原版注销/重建阶段；切换、拆毁、失撑和读档不留下旧网络或复制能源。普通用电器仍只选本层 6 格范围的输电点，设施的数量/朝向/LOS 保留原版规则。

结构事件只处理相关列；生成阶段原版临时地面尺寸下不读尚未开放的上层。派生端子索引须早于 Map.FinalizeInit 的原版初始建网，不能等 MapComponent.FinalizeInit 才提供，也不能为重建索引额外注销已通电的用电器。CompPowerTrader.ResetPowerVars 会把 Flickable 重置为开，因此自有支撑事务保留玩家开关状态，且不重置无关地面发电机。

气体浓度仍由原版 GasGrid 唯一保存。复用 M1 的 GasCanMoveTo 隔离入口，补齐两种 indexed SetDirect 的隔离过滤；更新活跃列索引。每 30 tick 对开放井口或独立授权的楼梯气道交换一次，门状态实时检查，禁止 Pawn 使用楼梯不关闭通气。等体积竖列快照对各通道逐边扣加；保留 BlindSmoke 不扩散及原版 DLC 掩码，并按原生 Gas 标记刷新绘制。原版耗散、水平扩散与排放另算；热交换与天空增量见后节。

### 当前运行证据

- Tests/Artifacts/v1-utilities-20261018-115831：当时实际全 DLC，生成 25 / 新进程读档 8 通过。三层供电、双连接环、单路断开仍通、双路断开分网、开关/拆毁/替换、失撑立即撤销输电、恢复支撑、原版负载和电池预算、设施同层/最多两个/0.12 增益、保存状态与重启唯一网络。100 tick 电池损失 0.5905151 Wd，原版预算含 5 W 自放电预期 0.5916666 Wd，浮点容差 0.003 Wd。已检查端子/本层连线/上层灶台实际画面。
- Tests/Artifacts/v1-air-20261018-121228：真实 Core + Harmony + RimLayers，生成 18 / 新进程读档 5 通过。
- Tests/Artifacts/v1-air-20261018-121310：明确全 DLC 组合，生成 18 / 新进程读档 5 通过。覆盖开放井口三层转移、竖向浓度守恒、实心板阻气、关闭门隔气/开门通气、禁止标记与通气分离、两种写入接口拒绝隔离区、原生水平扩散边界、原版气体保存与活跃索引恢复、实际地图 tick 调用交换。
- Tests/GasExchangeTests：3 个固定场景和 100000 个随机链通过，检查四通道逐通道守恒、0..255 边界、不改变 BlindSmoke、反转层枚举顺序结果一致。源码直接链接正式 GasExchange，不另写测试用交换算法。

新版严格 Core 基线矩阵已完成，以下目录的实际 activeMods 和 MVID 已核对；除护盾仅额外启用 Royalty 外均为 Core + Harmony + RimLayers。所有生成/读档结果零失败、无运行错误；这些是新增热交换之前候选的基线，后续按变化影响补测。

| 场景 | Tests/Artifacts 下目录 | 生成 / 读档断言 |
|---|---|---|
| navigation | v1-navigation-20261018-121554 | 12 / 4 |
| work | v1-work-20261018-121638 | 16 / — |
| structure | v1-structure-20261018-121702 | 31 / 15 |
| combat-shot | v1-combat-shot-20261018-121746 | 14 / 9 |
| combat-ai | v1-combat-ai-20261018-121845 | 17 / — |
| combat-shields（仅 Royalty） | v1-combat-shields-20261018-121909 | 20 / — |
| combat-ballistics | v1-combat-ballistics-20261018-121932 | 15 / — |
| combat-explosion | v1-combat-explosion-20261018-121954 | 15 / 10 |
| combat-delay | v1-combat-delay-20261018-122035 | 10 / 8 |
| combat-blast-effects | v1-combat-blast-effects-20261018-122121 | 10 / — |
| utilities | v1-utilities-20261018-122143 | 26 / 8 |
| air | v1-air-20261018-122226 | 18 / 5 |

已查看 122226 的气体实际画面：只显示第二层井口和气体，不把其他楼层画到同一位置；当前截图为晨间低光，不能用于白天明暗验收。

早期 115027 暴露生成期上层越界与用电器重启采样过早，已修正；115150 暴露读档索引额外断开负载，已修正原版初始建网时序。120530 的 DLC 断言错误和楼梯缺少原版 Forbiddable 组件已修正。121006 验证了即使预写独立配置，传 `-mod` 仍会强制追加 DLC，因此新版脚本移除此参数。

## M5：独立房间热交换与逻辑天空

Environment/HeatLinks 接在原版 MapTemperatureTick 的 120 tick 温度阶段之后。共享面合并成 Room 对，读取一次温度快照、双向提交等量相反热量；屋顶导热 0.006 / 格 / 阶段，开口与授权楼梯对流 0.084。容量使用原版 CellCount，有限节点总混合份额上限 0.25，室外为无限热库。原版屋顶/无顶向室外损耗仅留在最顶层，避免下层重复散热；墙体与深层项继续原版规则。温度只存原版房间/地图数据，不序列化派生 Room 引用或热网络。

此前单靠不可行走地形，隔离区仍会得到 ImpassableFreeAirExchange Region。现把受管隔离区预期类型设为 None，并过滤原版墙体换热的隔离采样格；上层空域仍有合法空气 Region。SkyExposure 按真实逻辑列派生，不改变 RoofGrid；房间形状与结构变化使缓存失效。天气、气体耗散与照明采用各自入口的天空语义；原版游戏条件、源汇、灯光及特殊雨水判定保留。最顶层原有厚屋顶定义不被绘制适配替换成薄顶。

- Tests/HeatExchangeTests：单位/室外热库与 100000 个随机图通过，覆盖逐边等量反向、有限节点温度不越界、反转边枚举一致。使用正式 HeatExchange.cs。
- Tests/Artifacts/v1-heat-20261018-123042：Core 生成 18 / 新进程读档 5 通过。三层封闭 9 格房间彼此独立；实际楼板传热守恒、开门通道、空域对流、上层屋顶隔远层天空、删除最后遮挡整列失效、原版房间温度保存及定时换热继续。
- Tests/Artifacts/v1-sky-20261018-123504：Core 生成 15 通过。真实 GroundGlow 在盖棚下为 0、无遮挡上层为 1；实际照明网格 covered alpha=100 / exposed alpha=0；层内灯光有效且不能跨间隔带，积雪、雨洗污物、气体耗散和灭火共用遮挡，天气噪声按逻辑地理位置采样。已看实际下层遮阴及上层露台画面；重启和全 DLC 积沙结果见下表。

天空 Core / 全 DLC 的生成与读档，以及受 Region/房间/天空变化影响的业务回归均已完成。每项 0 失败、无运行错误，Release 构建 0 警告/错误。以下是加入爆炸热量分布前的环境候选；爆炸热量单独补测。

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 组合（另含 Harmony / RimLayers） |
|---|---|---|
| v1-sky-20261018-123703 | 15 / 4 | Core |
| v1-sky-20261018-123747 | 16 / 4 | 全部五个 DLC |
| v1-heat-20261018-123840 | 18 / 5 | Core |
| v1-navigation-20261018-123924 | 12 / 4 | Core |
| v1-work-20261018-124010 | 16 / — | Core |
| v1-structure-20261018-124035 | 31 / 15 | Core |
| v1-utilities-20261018-124119 | 26 / 8 | Core |
| v1-air-20261018-124202 | 18 / 5 | Core |
| v1-combat-ai-20261018-124246 | 17 / — | Core |

早期 122923 只有“定时阶段上层最终温度高于初值”失败：原版墙体向冷环境散热可超过竖向流入，不能用最终净升温判定有没有调用。保留直接成对换热方向/守恒检查，定时调度改为观察实际调用次数和连通房间对；没有修改传热系数或关闭原版散热来满足夹具。

后续灾害/警戒接入见末节；下一责任转回 M2 的真实非单调路线、容器、竞争/抢占与工作中读档，再完成天空落物、M4 剩余组合、特殊 Pawn 与规模验收。

### 爆炸热量与原版温度保存精度

DamageWorker.ExplosionStart 原版把每覆盖格热量 × 覆盖格数全部注入爆心。现在只在已有 LayerBlast 的受管爆炸中，把同一总热量分配到实际覆盖格，再由原版 GenTemperature.PushHeat 作用于相应房间；不额外调用原版开始阶段，Vaporize 的派生实现仍调用该基类一次。使用同一冻结覆盖集合，不为注热额外穿墙、穿板。室外拒绝热量仍服从原版热库语义。

Tests/Artifacts/v1-heat-20261018-124824：Core 生成 20 / 新进程读档 6 通过。真实原版 Explosion.StartExplosion 的显式三层覆盖让三个室内房间分别升温，预期总热量 15，实际 14.9999943（浮点误差）；不是给每层再注入一整份 15。显式覆盖夹具只验证开始阶段热预算，不替代已有爆炸几何/伤害传播测试。

124456 的读档失败促使核对原版 TemperatureVacuumSaveLoad：温度先乘 16，转 int 向零截断后存 ushort，加载再除 16。因此 20.55556°C 原生读档为 20.5°C，合法误差小于 1/16°C；这是原版存档契约，不另外保存第二份高精度温度去覆盖它。夹具现在严格比较原版量化结果，同时确认各层注入的热量仍保留。顶层损耗也保留原版 coverage<0.001 的阈值和极端温差调整。

爆炸热量接入后的受影响回归已终态通过：Core + Harmony + RimLayers，无运行错误；Release 构建 0 警告/错误。

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 |
|---|---|
| v1-combat-explosion-20261018-124908 | 15 / 10 |
| v1-combat-blast-effects-20261018-124952 | 10 / — |
| v1-combat-delay-20261018-125013 | 10 / 8 |


后续灾害/警戒入口已读：Fire.TrySpread（0600B166）仅水平近处点火或生成 Spark；FireUtility.ChanceToStartFireIn（0600B16A）自身不检查逻辑层。GenClamor.DoClamor 的委托重载（060037C9）使用开门 Region BFS、15 区预算和存储平面距离/听力。GenExplosion.NotifyNearbyPawnsOfDangerousExplosive（06003819）还使用同房间与二维 LOS，保留智力、阵营、伤害类型及 instigator 过滤；下一阶段要同时完成通知后的实际躲避，不能仅让通知跨层。

## M5：火焰、惊扰与实际爆炸躲避

LayerMapComponent 新持有 FireLinks / Acoustics / DangerWarnings，拓扑分别授权 fire / sound。VolumeLinks 统一气体、对流、火焰与惊扰的开口/门资格，各媒介仍使用自己的许可。RL_Stairs 的英文与简中说明明确开放通道的风险，禁止 Pawn 使用不封闭通道。原版 Fire / GasGrid / Pawn / Job / CompExplosive 唯一保存业务事实，新增服务没有第二份实体或倒计时。

- 火焰在每次原版 TrySpread 后检查上下通道，向上/下概率分别乘 0.2 / 0.05；开放井口可把热羽流带到邻格有效楼面。原版可燃性、instigator、静态火唯一性与调度保留，隔离格/悬空格不点静态火。
- 惊扰追加跨层声路搜索，直/斜/竖向代价为 1 / √2 / 3，保留实际听力，每个远层 Pawn 一次回调。同层仍由原版处理；没有修改实际音频混音。封闭门不会阻止其他合法开口或室外绕路，门控验收使用封闭房间。
- 跨层危险通知保留原版智力、阵营、伤害类型与 instigator，物理距离及遮挡不使用存储带间距。Native TryGiveJob 的跨层分支找实际可达安全格，发原版 Sprint / Goto；没有 Travel Job 或临时复制 Pawn。
- 原版 Pawn_MindState.Notify 会立即 CheckForJobOverride；即使躲避节点因 playerForced 返回空，其他节点仍可把强制 Wait 改成 Wait_Wander。受管地图现在在强制命令期间只记录 knownExploder，不重选整棵树，命令结束后仍由原版仲裁。征召不触发自动躲避。
- 读档后从已有引信与已落地延迟弹丸派生重建警告，等待地图实体与弹道索引就绪后再通知；不再次 StartWick，不重启原版计时。

### 最终真实运行证据

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 实际组合（另含 Harmony / RimLayers） |
|---|---|---|
| v1-hazards-20261018-133519 | 32 / 10 | Core |
| v1-hazards-20261018-133605 | 32 / 10 | 全部五个 DLC |

`hazards` 为可控撤离/恢复试验：保存时把引信设为 600 tick，抵达安全距离后设为 1 tick 引爆。它验证任务与实际伤害，但不单独证明原生引信期限内能逃离。另加 `hazards-timed`，从 StartWick 到引爆不改原版随机 70–150 tick 计时：

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 原生引信预算与实际经过 tick | 组合 |
|---|---|---|---|
| v1-hazards-timed-20261018-134536 | 34 / 11 | 128 / 128（生成、读档一致） | Core |
| v1-hazards-timed-20261018-134632 | 34 / 11 | 100 / 100（生成、读档一致） | 全部五个 DLC |

四次运行均观察到真实 Sprint、引爆前实际移动、旧位置属于爆炸覆盖、实际引爆后无伤以及后续 200 tick 未着火。零失败、无运行错误；没有延长这组计时或关闭原版爆炸伤害。

覆盖封闭门/实心板挡火、双向楼梯火焰、独立 fire/sound 权限、井口邻格热羽流、三层声路固定成本 4 / 8、听力回调与去重、禁止标记不封闭通道、阵营过滤、征召和强制命令、真实原版警告引发 Sprint、Pawn 实际跑出危险范围、真正引爆后的无伤与后续 200 tick 安全、原版火焰真实地图调度，以及引信/警告的读档恢复。Core 和全 DLC 均零失败、无运行错误；Release 0 警告/错误。

### 弹丸撞击侧及火花调用方修复

131508 复现：从下方飞入完整楼板的手雷停在物理高度 3，却被存储到上层（z=188）。原来把障碍所属层直接用于命中层，会使警告与爆炸移到板的另一面。现在实心接触沿来向回退 0.0001，按来向空气体积投影存储，保留完整物理高度；最高屋顶之外的物理爆心不再夹回室内。32 项场景实际观察到 point.y=2.9999、storage.z=52，警告被板阻挡，真实引爆覆盖不包含板上的 Pawn。

Native Spark.Impact（0600B34D）使用 Position 而非 hitThing 点火；改变接触侧后，单改弹丸位置会让火花点燃相邻空气而非木墙。已在该唯一点火调用处保持实际被击建筑的接触边缘格，不使用多格建筑锚点替代命中格。真实跨层落火花点燃木墙、不会在相邻空气格点火；多格灶台调用方也点燃接触边而不是锚点。物理点和原版伤害提交次数不变。

Tests/CombatGeometryTests：19 项纯几何通过，新增板下/板上接触、最高屋顶外侧、墙面外侧。下表是接触侧修正后的受影响回归；最后火花点火专用修复只作用于 Spark，另由上面的最终灾害场景验证。

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 |
|---|---|
| v1-air-20261018-132428 | 18 / 5 |
| v1-heat-20261018-132512 | 20 / 6 |
| v1-sky-20261018-132556 | 15 / 4 |
| v1-combat-shot-20261018-132639 | 14 / 9 |
| v1-combat-ballistics-20261018-132723 | 15 / — |
| v1-combat-shields-20261018-132746 | 20 / — |
| v1-combat-explosion-20261018-132808 | 15 / 10 |
| v1-combat-delay-20261018-132852 | 10 / 8 |

以上除 shields 仅额外启用 Royalty，均为严格 Core 组合。已查看逃离后上层选择/危险提示画面，未发现跨存储带选择线或混层显示；后续 tick 的健康/未着火状态也有断言，不仅依赖截图。

早期夹具问题保留原始结果：130316 把 Pawn/物品放在门口，门被原版保持打开，随后又同格放楼梯替换了门；130448 的声路可从室外绕过门；130853 的真实仲裁失败推动了强制命令保护。化合燃料半径按平方根堆叠扩张（不是线性），使用原版 customExplosiveRadius 构造能实际覆盖上层的试验。132019 的固定“两个角色必听到”断言未考虑随机角色听力，存档中的第三名殖民者有 HearingLoss；现分别核对固定几何成本与实际听力回调，不移除健康状况或放宽声音权限。

下一责任：M2 真实非单调/第四楼梯路线、容器取料、竞争/抢占与工作中读档；之后收口天空落物、特殊体型/炮塔/友伤/护盾保存组合、延迟弹丸警告实际逃离、天气心情专项、特殊 Pawn 与规模。第一版仍未完成。


## M2：真实非单调路线、连续工作保存与原版仲裁

本次新增验收使用当前 1.7.4981 rev136 / MVID f1e6382600a84191bbc6d8a184fd6b57；生产导航/工作算法保持原有契约，只移除了测试参数下对预期断路逐次打印完整栈的日志洪泛。原版层内不可达是端点搜索的正常输入，不应产生每次几十行的诊断栈。成本缓存仍仅在一次完整搜索内拥有原版 PawnPath，finally 释放，不保存跨请求旧路径；跨层提交前按当前连接器与支撑事实复核。

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 实际组合（另含 Harmony / RimLayers） |
|---|---|---|
| v1-navigation-maze-20261018-135042 | 13 / 6 | Core |
| v1-navigation-maze-20261018-135439 | 13 / 6 | 全部五个 DLC |
| v1-work-save-20261018-135350 | 17 / 15 | Core |
| v1-work-container-20261018-135759 | 16 / 10 | Core + Odyssey |
| v1-work-container-20261018-135849 | 16 / 10 | 全部五个 DLC |
| v1-work-haul-20261018-140114 | 15 / — | Core |
| v1-work-hunger-20261018-140138 | 11 / — | Core |
| v1-navigation-rules-20261018-140334 | 11 / — | Core |

以上零失败、无运行错误。最终 Release 构建 0 警告/错误。最早 maze / work-save 的测试源码可空告警已修正，未改动其运行判断或玩法逻辑。

- **真实非单调迷宫**：地面整宽阻墙、二层隔墙及三个被围死的近楼梯，只有更远的第四楼梯能接通。规划与 Pawn 实际经过 1→2→3→2→1，目标仍在原来第一层；反向 3→2 跨层步中保存，新进程继续同一 Job.loadID 和最终格。每个进程内 Driver 也保持同一实例。已查看最终地面隔墙/楼梯/Pawn 画面，未见混层显示。
- **路径规则**：原版 IPathGridCustomizer 全图 ushort 网格对原来近楼梯落点附加 20000 成本，实际路线改选另一楼梯；禁止 Pawn 使用也能排除相应连接器。跨层步前在真实出口放墙，保留原楼梯，Pawn 不进入堵塞格，保留 Job/Driver 从另一出口抵达。
- **携料读档**：原版 DoBill 首批五个土豆在跨层步中保存，另五个仍在材料队列；重启核对携带物唯一所有权、Job.loadID/Driver 类型/工作台、队列与数量、材料及工作台原版预订。随后完成第二次取料，合计消耗十个、生产一个简餐，继续产品入库及床/食物查询。
- **真正容器，不把货架当背包**：原版 IHaulSource 的当前实现为书柜、成人/儿童衣架；本例用 Odyssey 衣架持有未 Spawn 的布大衣，第三层原版 Campfire/BurnApparel 消耗一件。取用关闭、持有物禁止、逻辑半径 6/12、另一个 Pawn 的预订竞争分别验证。真实从衣架取出、同一 Driver 跨三层、携带中重启、最终消耗与预订释放均通过。Core 项目不因这个验收新增 Odyssey 运行依赖；此场景单独明确加载 Odyssey。
- **移动目标与拆并**：搬运出发后把仍被预订的真实银堆从第三层移到第二层，原版目标引用跟随该实体。75 中拆出指定 10，源堆保留 65，送入第三层已有 5 的堆，合成 15；携带分堆被原版销毁一次，总数保持。全程同一 Job/Driver。
- **征召与取消**：第二次运输在跨层步中征召，源堆预订即时释放，后续 80 tick 不恢复旧任务；再次出发后原版 StopAll 取消，后续 120 tick 旧任务不复活、不再扣货，完成过的 15 和剩余 65 不变。
- **正常需求抢占**：由原版候选派发的非 playerForced DoBill 携料跨层时使饥饿到 0，调用原版 CheckForJobOverride，真实 Ingest 替换工作并释放工作台预订；随后实际进食至 0.9，未把尚未完成的 Bill 记为完成。观察到抢占后才关闭该测试 Pawn 的烹饪工作，以区分正常新派工与旧任务复活。

135649 的早期衣架试验错误地对原版没有 CompForbiddable 的衣架调用 SetForbidden，已保留失败结果。改为衣物自己的原版禁止组件验证持有物权限；衣架是否可取用由其原生 SetAllowHauling 开关表达，没有给参考/原版 Def 添加组件来配合夹具。

M2 的核心功能、动态路径与原版仲裁已有上述运行覆盖；缓存/规模与特殊 Pawn 仍须进入最终整合。下一责任转到 M3 天空落物整列命中，再处理 M4 剩余组合、天气心情专项、特殊 Pawn 与规模。第一版仍未完成。


### M3 接入前调查记录（实现与运行结果见后节）

当前程序集已重核：Skyfaller.Tick（0600B3AA）在剩余 15 tick 调 HitRoof（0600B3AB），0 tick 调 Impact（0600B3AD）；反向离图使用另一分支。Skyfaller_FlyingPawn 是同图飞行 Pawn，不能把它当成从天空进入的物体整体重定向。DropPodIncoming、PassengerShuttleIncoming、LavaRockIncoming 有 Impact 派生副作用；ShuttleIncoming、DropPodIncoming 又有 SpawnThings 派生路径。不能只补 base.Impact 而忽略调用前后行为。

原版 DropCellFinder.CanPhysicallyDropInto（0600C8FE）仅检查当前存储格，尚无完整逻辑列。GenSpawn.Spawn 完整重载（060038B0）在设置 Position/SpawnSetup 之前已按原 loc 擦除占地旧物；如要改变天空落点，应在这些副作用前确定，而不是 SpawnSetup 后换层。进入/破顶/实际撞击时还需按当时结构复核，保存不能重启动画计时。

更重要的是内容物失败路径：Skyfaller.SpawnThings（0600B3AC）忽略 GenPlace.TryPlaceThing 的失败返回；Impact 随后 ClearAndDestroyContents，Destroy（0600B3A6）再清一次。只增加有效楼面放置 validator 会把放不下的真实物品销毁。ActiveTransporter.PodOpen（0600B35D，1.7 已无 ActiveDropPod 类型）也有同类尾部清空，且可能在放置前 DeSpawn 自己；其 Tick（0600B35B）按原生 age/openDelay 开舱。实现必须先确定无安全放置位置时的持有/恢复事务，并覆盖开舱后路径，不能以改坐标作为天空落物已完成的证据。

Skyfaller.ExposeData（0600B3A3）已保存 innerContainer、倒计时、hasHitRoof、hasImpacted、hasLeftMap；优先复用这些原生阶段事实，避免第二份内容物或重复爆炸。上述为接入前确认的接口和失败路径；后续实现与运行结果见下一节。


## M3：天空整列命中、落地事务与原容器恢复

实现位于 Structure/SkyfallService.cs、SkyfallPatches.cs，由 LayerMapComponent 持有。适用来袭 Skyfaller；反向离图和同图 Skyfaller_FlyingPawn 不进入此路由。薄屋顶保留原生破顶行为，结构楼面是下落中止面，不增加重量或穿板模拟。

- 来袭前在 GenSpawn 的擦除旧占地之前确定逻辑列和统一撞击层；每次实际 Tick 再复核结构，删除或新增楼面不重置倒计时。DropCellFinder 的物理格检查和 RandomDropSpot 两个随机回退同时检查更高层楼面/屋顶，非站立随机模式也不能选隔离带。
- 所有内容物仍由原版 Skyfaller/ActiveTransporter 的 holder 保存；本 mod 没有第二份实体、物品队列或引爆阶段。原版 hasImpacted/hasHitRoof/age/倒计时保存后继续。撞击后的每次重试只放置剩余内容物，不重放 Explosion、到达通知、粒子或信件。
- 本层安全 Near 放置禁止擦除已有建筑/有材料 Frame。污物/植物可按原生规则清理；真实物品先完整规划可行走楼面上的挪位，全部成功才移动，不调用失败后销毁旧物品的原生兜底。显式 Wipe 直落配置继续使用原版调用，但先保护完整占地、实墙、原货物和容器自身。
- ActiveDropPod 的 Def 实际 category=Item。部分卸货时，舱壳和已卸物可能同占一个格；恢复同一舱壳时只跳过这一次 GenSpawn 的物品挤出分支，否则会把刚卸出的货物再挤走/删除。全局 Current.ProgramState 未改动；普通物品和原版仓储容量不走这个恢复例外。等待状态可原生保存，30 tick 重试，清障/扩建后恢复，并给出完成提示。
- 穿梭机的破坏性蓝图清理在确认可放置之后执行；失败等待期间不反复取消后来新建的蓝图。实有材料 Frame 不作为 Near 的最坏落点覆盖；正常取消返回的钢材在最终落地中也保留。

### 已完成的真实游戏运行

全部基于 1.7.4981 rev136 / MVID f1e6382600a84191bbc6d8a184fd6b57；生成与读档是不同游戏进程。每个结果的 activeMods 已核对。

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 组合（另含 Harmony / RimLayers） |
|---|---|---|
| v1-skyfall-20261018-144038 | 30 / 20 | Core |
| v1-skyfall-20261018-144124 | 30 / 20 | 全部五个 DLC |
| v1-skyfall-20261018-145209 | 30 / 20 | Core；分类放置规则接入后的回归 |
| v1-skyfall-shuttle-20261018-145347 | 13 / — | Core |
| v1-skyfall-shuttle-20261018-145413 | 13 / — | 全部五个 DLC |
| v1-structure-20261018-144222 | 31 / 15 | Core |
| v1-work-save-20261018-144307 | 17 / 15 | Core |
| v1-combat-explosion-20261018-144355 | 15 / 10 | Core |

以上均零失败、无运行错误，Release 0 警告/错误。逐对象撞击收据增强验收也已终态通过，见下表。

最终增强验收按每个真实来袭物的 thingIDNumber 独立观测 Impact，避免“本应发生的两个新撞击”掩盖旧物体重播。只在专用测试模式安装观测，不增加生产存档事实。

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 实际组合 |
|---|---|---|
| v1-skyfall-20261018-145703 | 32 / 22 | Core + Harmony + RimLayers |
| v1-skyfall-20261018-145758 | 32 / 22 | Core + 五个 DLC + Harmony + RimLayers |

两组均零失败、无运行错误；新进程中三个已提交来袭物各自的 Impact 观测计数保持 0，生成进程中各自始终只有 1 次，正常仍在途的两个 Pod 则按原计时完成首次撞击。

skyfall 覆盖：屋顶覆盖的低层不是合法随机/物理落点；空域上方更高屋顶；真实 Pod 破顶、最高楼面、在途中删/加楼面而计时保持 40→39→38；缺少完整楼面时保持多格内容物；满格直落保留原 55 银和来袭舱；默认/提前移除舱壳两种开舱方式；双物品部分卸货保留已卸和未卸的各一实例；实墙阻塞时把舱壳及乘客留在来袭物中；原生保存这些阶段后恢复、扩建/清墙后卸载同一物品及活 Pawn。

shuttle 使用原版 ShuttleCrashing + ShuttleCrashed，真实 5×3/15 格占地。先撞单格平台，原实体留在来袭物内；爆炸/弹片结束后创建新蓝图，等待期间蓝图不被重复取消；扩建足够楼面后再放置已供 5 钢的 Frame，穿梭机仍等待且不擦除 Frame。原版取消 Frame 返回 5 钢后，穿梭机完成落地并执行原生蓝图清理，退款钢材未丢失，撞击计数始终只增加一次。两个随机模式各采样 64 次，128 个返回格均通过完整逻辑落点校验。

已查看恢复后的楼面、货物/原建筑和穿梭机实际画面：只显示第三层，完整船体落在同一楼面，退款 5 钢可见；提示使用原版消息并显示卸载完成。没有为放置失败铺假地面或把内容物移到另一坐标带。

### 反例驱动的修正

- 142510 的满格夹具先直接 Spawn 舱壳，原版因舱壳本身是 Item，在卸货测试前就会挤出/销毁银堆。改为真正的 MakeDropPodAt→来袭→舱壳落地链路，既验证生产入口，也避免夹具先破坏被测数据。
- 143823 生成通过，但实墙与已落地舱壳的共格状态在读档时丢失：原版加载 Wall 时会按物品类别擦除舱壳，继而销毁乘客。生产路径现在连舱壳也不能落入实墙；它与乘客继续由来袭 Skyfaller 持有。相同阻塞、保存、清墙与释放场景随后通过，没有移除乘客保存检查。
- 144526 蓝图夹具把 Steel 当 Blueprint 的 Stuff，原版报错。改用 GenConstruct.PlaceBlueprintForBuild，材料通过 stuffToUse 初始化，而不是 ThingMaker 的 Stuff。
- 144800 足额 15 格楼面、原版 CanSpawnAt=true，但自有 Ready=false。原先把所有 SpawningWipes 都视为不安全，会把普通污物清理和可完整挪位的物品一起拒绝；现在分别处理清理、挪位和建筑/Frame 保护，仍不接受无楼面或擦除真实建筑作为 Near 兜底。

下一责任：M4 友伤、特殊体型/炮塔、护盾保存边界及延迟弹丸警告后实际逃离；随后完成天气心情专项、特殊 Pawn 和规模性能。第一版仍未完成。


## M4：原生手雷撤离、空域屋顶与护盾返回边界

基准保持 1.7.4981 rev136 / MVID f1e6382600a84191bbc6d8a184fd6b57。构建为 Release、0 警告/错误。以下均为独立游戏进程，结果的 activeMods 与当前程序集 MVID 已逐项核对。

### 已修复的边界

- LayerShotScene.Roof 原先要求本层 HasSurface，漏掉跨在空气上的真实人工屋顶。现直接读 RoofGrid；实际向上射击在屋顶下表面终止，墙目标保持原耐久，Impact 仅一次。屋顶、楼面仍是不同结构事实。
- 护盾原先在调用 CheckIntercept 前先写 ShieldCommitted=true。友方等原生 false 返回边界可能把尚未确认的拦截保存为成功。现只在匹配当前弹丸/拦截器的原生返回后记录最终结果；命中点提前固定，但不提前承诺拦截。
- Sweep 原先在处理该段任何接触之前保存整段 high。现每次处理接触前仅推进至该接触，在整段确实完成后才写 high。这样原生大 delta 时钟已经到达终点，读档也不会跳过护盾后的墙。
- DrawPos 使用已消费的接触进度。实际渲染还先由原生 DynamicDrawManager 使用 Position/OccupiedDrawRect 裁剪；只在该入口为受管跨层弹丸使用物理显示格及平移后的框，不为显示改变实体 Position。其余 Thing 和普通弹丸保留原生裁剪。

### 手雷使用未修改的原生预算

combat-grenade-flee 使用真实 Proj_GrenadeFrag，Def 半径 1.9、延迟 100 tick，未改半径、倒计时或共享 Def。投掷物从二层边缘落向一层目标，在一层墙侧的物理高度 2.996343 接触；相邻洞口使二层 Pawn 的原位置仍在真实爆炸覆盖内。

原生 Impact 设置 100 后，同次派生 TickInterval 再减 1，因此保存剩余 99。生成与新进程读档都在原始 99 tick 后实际引爆，警告重建不重启引信。Pawn 由真实 Goto/Sprint 移动，爆炸冻结覆盖包含旧格，而已离开的 Pawn 的 TakeDamage 调用为 0、伤势不变且存活；生成 Impact 一次，读档旧 Impact 零次。

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 组合（另含 Harmony / RimLayers） |
|---|---|---|
| v1-combat-roof-20261018-151148 | 6 / — | Core |
| v1-combat-roof-20261018-152334 | 6 / — | 全部五个 DLC |
| v1-combat-grenade-flee-20261018-151218 | 11 / 9 | Core |
| v1-combat-grenade-flee-20261018-152403 | 11 / 9 | 全部五个 DLC |
| v1-combat-shield-save-20261018-152115 | 13 / 9 | Core + Royalty；护盾收据与续飞 |
| v1-combat-shield-pass-20261018-152204 | 13 / 9 | Core + Royalty；护盾放行与后方墙 |
| v1-combat-shields-20261018-152307 | 20 / — | 全部五个 DLC；个人盾/范围盾/EMP/近战 |
| v1-combat-ballistics-20261018-152458 | 15 / — | Core；多格目标、动态楼板和换层 |
| v1-combat-explosion-20261018-152521 | 15 / 10 | Core；传播中重启 |
| v1-combat-delay-20261018-152609 | 10 / 8 | Core；落地等待中重启 |

以上终态均零失败、Log.Error 观测为空。原生运行时退出仍可能输出其分配清理警告，不将其记为模组行为通过的依据。

护盾夹具在同一次大 delta 回调内、原生 CheckIntercept 返回后的 Postfix 保存。敌对护盾原生耐久 100→82 后保存；读档不重新检查护盾、不再扣耐久，只完成一次 Impact。友方护盾保持 100、保存的收据为 false、消费进度 0.3611323 而时钟进度为 1；读档继续后段，真实墙耐久 300→282，TakeDamage 恰一次，后方最终目标未受伤。没有模拟扣盾、重置弹丸时钟或用原生 Impact 计数抵消重播。

已查看屋顶测试的三层显示、撤离后的二层 Pawn/洞口及护盾边界实际画面。最初边界截图暴露非 Pawn 按旧 Position 裁剪的问题，随后加入上述原生裁剪接入与首次恢复 tick 前的 DrawAt 观测；最终结果见后续补证。

### 最终接触点渲染补证

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 实际组合 |
|---|---|---|
| v1-combat-shield-save-20261018-152716 | 13 / 10 | Core + Royalty + Harmony + RimLayers |
| v1-combat-shield-pass-20261018-152801 | 13 / 10 | Core + 全部五个 DLC + Harmony + RimLayers |

两组终态均零失败、无 Log.Error。读档后暂停模拟、保持 3 帧截图，在首次恢复 tick 前真实 Projectile.DrawAt 均被调用 4 次；新截图可以看到弹丸停在二层护盾接触边界，未跳到时钟终点，其他楼层不在画面内。之后继续实际模拟，拦截保持耐久 82 且不重查，放行保持 100 且后方墙只受伤一次。新增裁剪代码没有变更物理游标、原生时钟或伤害事实。最终两张画面已人工查看。



## M4：友伤、特殊体型与真实自动炮塔

本批次不改战斗规则或共享武器 Def，补齐已有单 Map 弹道接入的真实运行证据。基准仍为 1.7.4981 rev136 / MVID f1e6382600a84191bbc6d8a184fd6b57；结果的实际 activeMods 已核对。

| Tests/Artifacts 下目录 | 断言 | 实际组合（另含 Harmony / RimLayers） |
|---|---|---|
| v1-combat-policy-20261018-153540 | 28 | Core |
| v1-combat-policy-20261018-153756 | 28 | 全部五个 DLC |
| v1-combat-turret-20261018-153724 | 9 | Core |
| v1-combat-turret-20261018-153832 | 9 | 全部五个 DLC |

均终态零失败、无 Log.Error。友伤夹具逐一验证 preventFriendlyFire、难度因子 0、排除 NonTargetPawns：中间层的真实友方牦牛在各 12 发中 TakeDamage 均为 0，弹丸继续命中最终墙。允许友伤时第一发命中一次；敌对 Pawn 不因友伤保护或难度因子 0 而免疫。同局部格但在弹道下方一层的 Pawn 在 12 发中不受伤。每颗实际弹丸都提交一次 Impact，没有靠重复命中抵消漏检。

松鼠、牦牛、镰刀机体的发射时瞄准高度分别为 0.45 / 2.25 / 1.35；每个真实目标均进入原生 TakeDamage 一次。明确指定的友方目标仍可命中，preventFriendlyFire 不撤销这条原生显式目标语义。倒地人类使用原生 MakeDowned 后的低姿态，瞄准高度 0.3，近距离实际命中一次。首轮 153321 的松鼠断言把命中后的死亡姿态用于重算原发射高度，已改为发射前记录期待值，未改变弹道或死亡结果。

炮塔使用原版 Turret_MiniTurret、真实 Battery、PowerConduit 和炮管耐久。移除上层步行连接后，原生 TryFindNewTarget 仍选择低层真实敌人；实际 Map tick 驱动蓄力、自动开火、跨层飞行及原生伤害。Core / 全 DLC 首次伤害分别产生 24 / 12 伤势，电池正常供能，炮管预算分别 60→54 / 60→56。没有手动发射代替炮塔，也未把 PowerOn 强制设为 true。目标仅为固定射击夹具暂停自身 AI，枪械精度和弹丸 Def 保持原版。

153610 早期炮塔测试实际击杀后仍试图选中已销毁的 Pawn，出现 Cannot select destroyed thing。截图路径现在选择真实尸体或仍在场的 Pawn；修正后两组完整通过。已查看三层炮塔/电池、实际飞行层与一层受伤目标画面。

## M5：雨天心情与机体指挥

### 雨天记忆沿用原生生命周期

现有 sky 场景追加真实 MindStateTickInterval(120) 天气检查：设为 Core Rain 并完成原生过渡，头顶没有本地 RoofGrid 屋顶但被更高人工屋顶遮挡时，不生成 SoakingWet；暴露露台生成原生记忆，回到遮蔽区不抹掉尚未自然过期的旧记忆。读档保留记忆，再次对遮蔽/暴露位置执行原生检查仍符合规则。没有用 TryGainMemory 人工伪造通过。

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 实际组合（另含 Harmony / RimLayers） |
|---|---|---|
| v1-sky-20261018-154209 | 19 / 9 | Core |
| v1-sky-20261018-154312 | 20 / 9 | 全部五个 DLC |

两组终态零失败、无 Log.Error；全 DLC 的额外一项是 Odyssey 沙层。原有日光/照明网格、雨水清污、积雪/气体/灭火的验收继续通过。已查看雨天的逻辑遮蔽与实际地图显示；天气全屏背景继续使用原版表现。

### 机体控制不再使用坐标带距离

生产接入为 Coordination/MechanitorRangePatch.cs 与 Presentation/MechanitorRadiusPatch.cs。CanCommandTo 保留原生地图边界检查和严格 <620.01 的阈值，只把距离调用改为逻辑 x/z；隔离区明确拒绝，非受管地图仍走原距离。原版 InMechanitorCommandRange 的 overseer / MapHeld 条件未改。范围绘制保留原生 24.9 半径、选中已征召机体的条件与原生谓词，只把绘制中心投影至查看层。没有另存控制关系或创建第二个控制组。

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 实际组合 |
|---|---|---|
| v1-mechanitor-20261018-154617 | 15 / 13 | Core + Biotech + Harmony + RimLayers |
| v1-mechanitor-20261018-154720 | 15 / 13 | Core + 全部五个 DLC + Harmony + RimLayers |
| v1-mechanitor-20261018-155048 | 15 / 13 | Core + Biotech；追加截图帧调用原生范围绘制 |

以上均终态零失败、无 Log.Error。测试真正植入 MechlinkImplant，使原版动态创建 mechanitor tracker；真实镰刀机体通过原生 Overseer 关系加入控制组。距离 24 接受、25 拒绝，同层与跨层一致；隔离带和 Invalid 拒绝。原版范围调用者接受跨层命令，绘制调用中心确实投影到查看层。机体实际 1→2→3，全程同一原版 Goto / Driver；二层途中保存，新进程保留原 Job ID、最终目标、操控者和恰一个控制组成员，继续到三层。

机体及原生控制组/Gizmo 的三层实际画面已查看。默认小平台上的控制圈边界落在未探索空域，不能靠移除原生雾来“让圈可见”；最后的渲染专项使用有支撑、已探索且穿过边界的露台，结果在下节补记。




### 机体显示专项：远层指挥圈通过同镜头原生对照

当前绘制入口为 LayerMapComponent.MapComponentDraw → Presentation.RemoteMechanitorRadius.Draw。未启用 Biotech 时直接退出；同查看层的操控者保留原生 Pawn.DrawAt 调用，只为其他层的真实操控者补一次调用。原生选中条件、24.9 半径、CanCommandTo 谓词和网格圈样式均保留。操控者不被复制、移动或强行绘制。

| Tests/Artifacts 下目录 | 生成 / 新进程读档断言 | 范围 |
|---|---|---|
| v1-mechanitor-20261018-165050 | 17 / 15 | 最小 Biotech；远层/原生同层、圈心在屏内/屏外的同镜头对照 |
| v1-mechanitor-20261018-165140 | 17 / 15 | 全部五个 DLC；相同功能、提交与画面输入 |

两组终态零失败、无测试期间 Log.Error。实际检查 `load-mechanitor-range.png`：操控者仍在地面，查看二层，镜头局部 (54,54)、size 28，完整远层圈清晰可见，东侧合法露台上的边界也可见。原生同层地面与原生同层二层使用相同镜头。第三层到达图也可见远层圈。

**更正此前负面判断**：163928 / 164018 的近景把圈心放在屏外，不能与圈心在屏内的原生全景相比。新增 `*-mechanitor-native-offscreen.png` 与 `*-mechanitor-remote-offscreen.png` 均使用局部 (77,54)、size 8；两者都不显示圈边，远层行为与原生一致。当前保留原生裁剪行为，不另造屏外边界显示规则。此前“已确认远层圈缺失”结论撤回，不再据此调查全局实例化、命令排序或全部高层绘制。

几何提交另有独立证据：查看第三层时中心 (54,0,326)、二层时 (54,0,190)，每层 1941 格与 196 条边。`remote-command-radius-submits-boundary-in-native-overlay-stage` 只记录提交，不替代上述画面检查。

早期 160029 暴露 SelectedPawns 重入枚举：原生 AnySelectedDraftedMechs 会重建同一可复用列表，现先对操控者 Distinct().ToArray() 快照。该异常修正后的运行均无此错误。OnGUI、DynamicDrawManager 后置和线框圆试验已撤销，没有保留备用绘制或全局 GenDraw/Graphics 修改。

相关接口：MapInterface.MapInterfaceUpdate（0600C546）→ MapComponentUtility.MapComponentOnDraw（0600148B）→ MapComponent.MapComponentDraw（06001483）；Pawn.DrawAt（06002300）为原生调用方。Core 回归 `v1-combat-shot-20261018-164539` 生成 14 / 读档 9，实际射击、飞行保存与一次伤害保持。

Log.Error 观测从专用测试配置初始化后开始；启动期既有 Vulkan 路径及未启用安装包重复 packageId 提示未通过修改其他 mod 清理。牵绳与 Lord 的后续结果见下一节；规模性能及 PLAN 最终逐项审计仍由本任务继续完成，第一版保持未完成。


## M5 增量：原生牵绳、畜栏与 Lord（2026-10-18）

基准保持 1.7.4981 rev136 / 根 Assembly-CSharp MVID `f1e6382600a84191bbc6d8a184fd6b57`，net10.0 / Harmony 3。Release 构建 0 警告、0 错误。

### 沿用原生绳索和队伍，而不是另建旅行任务

- 两只真实 Muffalo，由原生 TakeToPen 工作候选选择真实三层畜栏，使用 RopeToPen 与 FollowRoper。领队实际经过二层、双方曾分处不同层，仍保留同一领队 Job/Driver；二层中途保存后用新进程恢复原生两个绳索引用，再实际送达三层围护内并释放预订。
- TryEnterNextPathCell 先移动到跨层落脚点，再执行 8 格拉绳检查。原生 60 tick 等待让原生跟随者赶上；当前不替换为平面距离，不新增绳路服务，也不重建假绳或跨图预订。
- 连接器对未被真实牵引、且按原版 fenceBlocked 通行的 Roamer 保留围栏门语义；已牵引者、人的预先牵引查询及 PassDoors 查询可走合法路线。送达后动物不能自行沿楼梯离开畜栏。
- PenMarkerState.HasOutsideAccess 原本只检查原生门。补充合法楼梯是否跨越本畜栏 Region 边界的检查，仅修正其出入口提示，不合并 Region 或把畜栏改成未围护。拆掉唯一入口后提示也恢复为无外部通道。
- ClosestSuitablePen / PenIsCloser 的“最近”比较改为实际预期牵引路线成本，保留原版允许类型、禁用、围护、预订、营养平衡模式与工作优先级。Core 的合法高层 / 地面备选路线成本为 420 / 836，全部 DLC 为 420 / 960；原生候选都选择了更短的高层路线，而不是被存储带距离误导。
- 真实分层队伍分别经历征召、取消、拆除唯一上层楼梯。原生逻辑断绳、结束旧工作并释放对应预订；取消后的新正常工作允许重新预订，不能把新的有效 Job 判作旧任务复活。

### Lord 成员身份保留，真实损失仍按原版处理

三个真实敌方 AncientSoldier 加入同一个原生 LordJob_Travel，由各自完整原生 AI 实际前往三层；不手工派发各段 Goto。二层途中保存/新进程加载保留同一个 Lord ID、精确成员集合、Travel Toil 与最终职责目标。

送达后三人受原生职责管理；一次原生 TakeDamage 的 1 点钝伤输入触发 Travel→DefendPoint 转移。随后对同一 Lord 使用持久的原生 LordJob_DefendPoint 改派至一层，三人实际经二层返回。正常过层不增减成员、不复制 Lord、不累计战损。最后通过真实 Pawn.Kill 验证原生移除成员且暴力损失只增加一次。当前无需 Lord 生产补丁；测试没有屏蔽 Pawn/Lord 的普通 Tick。

### 原始运行证据

| Tests/Artifacts 下目录 | 组合 | 生成 / 新进程读档 | 结果 |
|---|---|---|---|
| v1-roping-20261018-171013 | Core | 30 / 24 | 零失败；真实工作候选、双动物、两条畜栏路线、围护与三类收尾 |
| v1-roping-20261018-171430 | 全部五个 DLC | 30 / 24 | 同上 |
| v1-lord-20261018-171122 | Core | 13 / 13 | 生成 984 tick 到三层、1098 tick 返回；重启后 683 / 1327 tick |
| v1-lord-20261018-171540 | 全部五个 DLC | 13 / 13 | 生成 1057 / 1118 tick；重启后 743 / 1368 tick |

各结果 JSON 均核对实际 activeMods 与当前 MVID，无测试期间 Log.Error。已检查 Core / 全 DLC 的到达截图：畜栏中有实际动物，无错误的“无外部通道”提示；Lord 返回画面与真实存活/死亡实体相符。这里的 tick 数为该 AI 场景的经过时间，不是性能基准或所有种类队伍的覆盖声明。启动仍有本机既有 Vulkan 层路径和两个重复 packageId 提示。

### 验收中纠正的测试前提

- 165741 在原生放下最后一根绳的瞬间检查预订，早于下一 tick 的 Job 清理。170136 确认正常清理延迟为 1 tick；后续在原生 Job 结束后检查，未为测试额外释放预订。
- 170833 把取消 180 tick 后新工作持有的预订误当成泄漏。现于 StopAll 的实际取消边界检查释放，后续检查旧 Job ID 不复活；新原生工作可合法启动。
- 170540 的 Lord 探针仅修改 Travel 临时防御点，原生图在无伤害 1200 tick 后恢复原行程，导致探针一直等待过时目标。改为原生持久 DefendPoint Job，保留同一 Lord 和正常仲裁，不修改原生转移条件。
- 171122 的初次加载探针漏配检查点，误读 Navigation 文件，尚未加载实际 Lord 存档。错误原始日志保存在同目录 `load-misrouted-*.log`；只停止了已核对路径/启动时间的本任务子进程 PID 31448。补上显式场景→文件选择及缺文件立即报告后，复用同一份有效生成存档，只重跑加载并取得上表 13 项零失败结果；没有重复生成来替换此前的有效输入。全部 DLC 使用修正后的完整两进程脚本。

生产改动为 Navigation/ConnectorGraph.cs 与 Coordination/PenAccessPatch.cs；新增真实探针 Diagnostics/V1Roping.cs、V1Lord.cs，并完成 V1Integration / Run-V1 的场景和保存调用方。没有为楼层深拷贝 Pawn、Job、绳索或 Lord。

### 继续完成第一版

当前负责人仍为本任务。下一项是规模和最终逐项验收：在不同地面尺寸及 Pawn 数的真实三层新图上测生成、路径、tick、绘制和内存，明确采样方法；再把 PLAN 的完整矩阵与当前代码、有效运行证据逐项对应。现有 120×120 / 少量 Pawn 证据不能直接推导大图或层数上限。天然岩顶、地下、旧图迁移、三维显示、CE 与未指定第三方框架仍按原计划留待后续适配。第一版未标完成。


## 第一版收口：规模、门权限、真实供料与世界出口（2026-10-18）

### 楼梯落脚门必须按进入方向检查

`navigation-door-173824` 复现真实缺陷：闭门/禁用落脚门未被连接器过滤，已安装路径途中把落脚门变为敌对门会转成 AttackMelee，丢失 Goto。修正 ConnectorGraph：按方向分别检查目的端 Region.Allows，离开已占用的禁用落脚点仍允许；路由元数据保存调用者原始 TraverseParms，跨层提交复核不再重建默认参数。NoPassClosedDoors、禁止、敌对非破门、显式 canBash、PassDoors 均保留原版规则。

有效结果：Core `v1-navigation-door-20261018-174016`、全部五 DLC `174215` 各 15 项零失败；最终完全打开画面 Core `175037` 仍为 15 项零失败。实际途中改门后走备用楼梯，保持原 Goto/Driver、不损坏门；回程真实打开并穿过友方门。已查看 `navigation-door-open.png`，门达到 TicksTillFullyOpened=0 后采集，不以半开画面代替。

### 蓝图供料和多人家具使用沿用完整原生流程

Core `v1-home-20261018-174044`、全部五 DLC `174247` 各生成 17 / 读档 6，零失败、无测试期间 Log.Error。原生 JobGiver_Work 选择真实三层 Stool 蓝图，由一、二层的 12/13 木材真实供料，经 Blueprint→Frame→成品，精确消耗两堆、仅一个成品；不是预装材料的 Frame。缺紧邻下层屋顶时，蓝图入口与供料入口均拒绝。

三名真实 Pawn 的原生 Play_Horseshoes 选择同一三层杆、经二层抵达并实际获得娱乐；第四人被原生容量 3 拒绝。二层途中保存/新进程恢复同一 Job ID、真实家具和参与者，继续正常玩耍。已查看到达画面。

### 有界规模测试，不把方法计时写成 FPS

新增 Diagnostics/V1Scale.cs 与 Run-V1 的 MapSize/PawnCount/Layers 参数。Layers=2 仅用于测试新分配地图的比较；普通产品仍固定三层，不支持存档热改布局。每次由真实中立 AncientSoldier 与一个原生 Lord 连续四趟地面/顶层往返，再执行原生防御 AI，共 12000 tick（约 4.8 游戏小时）；没有屏蔽 Pawn Tick。前 120 tick 不计时，11880 次 DoSingleTick 方法经过时间排除夹具、控制和保存 IO；包含系统调度影响，不是处理器周期测量。

完整地面→顶层路径先预热 2 次，再按 20/60 名测试成员各查询一次。三项绘制计时分别是原生 MapInterface、MapDrawer、DynamicDrawManager 的 CPU 侧提交，不是 GPU 用时、FPS 或完整帧。内存含游戏资产/引擎/缓存，不强制 GC。原始逐样本系列、方法、规模、完整进程内存和硬件分别保存在 `*-scale-metrics.json` 与 `scale-machine.json`。

| Tests/Artifacts 目录 | 输入 | 生成 / 新进程读档 |
|---|---|---|
| v1-scale-20261018-172328 | Core，120×120，3 层，20 名测试 NPC | 12 / 11，零失败 |
| v1-scale-20261018-172528 | Core，120×120，2 层，20 名测试 NPC | 12 / 11，零失败 |
| v1-scale-20261018-172646 | Core，250×250，3 层，20 名测试 NPC | 12 / 11，零失败 |
| v1-scale-20261018-174753 | Core，250×250，3 层，60 名测试 NPC | 13 / 12，零失败 |
| v1-scale-20261018-172821 | 全部五 DLC，250×250，3 层，60 名测试 NPC | 12 / 12，零失败；加载恢复过程如下 |

本机 Ryzen 7 5700X，16 逻辑处理器，物理内存 34281680896 bytes，Windows 10.0.19045；实际画面 1920×1080。地图大小之间不是同 seed 控制实验，结果用作代表性开销。三层 250×250 实际存储 250×765 = 191250 格。60 人 Core 加载后路径平均 2.018 ms / p95 2.179 ms，tick 平均 1.082 ms / p95 2.996 ms；全 DLC 同规模加载路径平均 2.594 ms / p95 3.102 ms，tick 平均 2.041 ms / p95 5.828 ms。完整数表在最终验收文档收录。

172821 初次加载没有产生终态 JSON，进程于 17:34:07 结束，旧 stdout/stderr 保存在 load-incomplete-*；未将它算为通过，也未凭最后日志推断死锁。查明专用 Prefs 默认 runInBackground=False，而原生 PrefsData.Apply 会把该值写入 Application.runInBackground。显式启用本测试目录的 runInBackground=True，并加入阶段进度记录后，复用同一份有效生成存档，加载完整跑完并得到 12 项零失败结果。Run-V1 / Run-M1 均只在自己的测试配置写该值，不触碰玩家偏好。

当前产品建议先使用不超过 250×250 的地面三层新图；这是实测规模建议，不是引擎硬上限或对任意重度殖民地的速度承诺。325、大于三层和多年游戏运行仍未验证。

### 当前版本与中文 M1 回验

程序集版本定为 0.1.0，启动日志收敛为一条补丁数量摘要；新增简中结构地面/空域/隔离区与支撑标签。`m1-20261018-174929` 与世界出口几何接入后的 `m1-20261018-181433` 均在当前 1.7.4981 rev136 / MVID f1e6382600a84191bbc6d8a184fd6b57、全部五 DLC、ChineseSimplified 下生成 41 / 读档 43，零失败；纯布局 862803 项通过。已查看后者两张加载图：河流/道路正常、楼层中文控件清晰、上层为空域、不泄漏下层实体。

### 世界出口不是存储 Map 的外沿

审计发现私有 ExitMapGrid 仅重建地面还不足够：原生家园 MapUsesExitGrid=false；Best/Random/Near 出口取样、CanReachMapEdge 与 Goto 最后一步仍按真实存储外沿。新增 Navigation/WorldExitPatches：世界边界取地面矩形，出口附近采样仅投影查询起点，不改 Pawn.Position；可达性以原生 Region 邻接加合法有向楼梯求解，不合并 Region。普通 Goto / Flee / TakeAndExitMap 的实际离图检查和方向计算采用同一地面几何，飞行、传送及运输不被全局地面检查拦截。

首个 Core `v1-world-exit-20261018-180747` 已证明真实 NPC 两次下楼后从逻辑北边 (60,0,119) 离图、库存 9 银保留、原生 Lord 不记战损；该场景对玩家普通 Goto 创建商队的预期错误，不能算完整通过。核实原生家园不允许该分支，改用实际 StartFormingCaravan，全流程由原生 LordJob_FormAndSendCaravan 驱动，抵达地面出口附近后才形成世界商队，未篡改 ExitMapGrid 政策。

181121 生成 28 项通过，初次加载保留 Job 却在地面结束而未离图，复现 Toil 重建早于 MapComponent 布局恢复：仅在 MakeNewToils 时判断 Managed 会漏装加载后的到达检查。旧失败日志/结果保存为 load-before-arrival-fix-*。同一有效中途存档修复后加载 16 项零失败。全 DLC `v1-world-exit-20261018-181329` 生成 28 / 读档 16，零失败；Near 查询另对同输入执行 20 次。180913 的一次 Near 返回 false 保留为早期失败，后续重复输入诊断没有复现，不据此编造固定原因。

实现随后进一步收敛：直接改原生实际执行委托中的几何调用，取消 Toil 包装和线程静态到达范围，加载后执行时自然读取已恢复布局。补齐已核实的 Flee 与 TakeAndExitMap 两个普通步行离图调用方，正在 Core/全 DLC 验证真实 Flee / Steal。当前本任务继续负责最终结果、性能回归和文档验收，第一版尚未据此标完成。


## 0.1.0 最终交付

最终出口接入已收敛为定向原生执行委托的几何替换，不包装 Toil，不保留线程静态到达范围。Core `v1-world-exit-20261018-181553` 与全部五 DLC `v1-world-exit-20261018-181730` 各生成 37 / 新进程读档 25，零失败、无测试期间 Log.Error：普通 Goto、真实 Flee、实际 Steal/携带目标和原版家园商队全部经二层下到地面再离图；原生一次提交、库存与 Lord 清理有效。Near 同输入 20 次检查在两组均通过。飞行/传送/运输未被全局阻断，未改变原生家园出口网格策略。

曾在 M1 加载仍占用输出 DLL 时尝试构建，MSBuild 文件复制失败；等待本任务的 M1 自然退出后重建及后续启动均 0 警告、0 错误，没有终止玩家或测试来强制解锁。

最终出口补丁后，复用 `v1-scale-20261018-172821` 同一个有效中途检查点，只重跑全部 DLC 250×250 / 三层 / 60 人加载。12000 tick、四次全程往返、原生 Lord/队伍与所有楼层身份通过，12 项零失败。前一有效加载结果保存在 load-before-world-exit-*，未重新生成输入。新加载 3083.6232 ms，路径均值 2.2469133 / p95 2.353 ms；tick 均值 1.6980349 / p95 4.4834 / p99 26.5233 / max 85.6375 ms；结束 WS 3419770880 / private 4911562752 bytes。绘制均值 / p95：MapInterface 0.0143081 / 0.023、MapDrawer 0.2849439 / 0.3952、DynamicDrawManager 0.7598076 / 3.8904 ms。仍是方法经过时间，不是 FPS。地图共 140 Pawn，测试队伍固定 60 人。

README、PLAN、About 已更新为 0.1.0，新增 V1-VALIDATION 按完整需求对应有效证据、方法与后续范围。M1 当前中文画面已查看，门、家具、机体等关键交互画面沿用同候选适用证据。首版固定三层，建议地面不超过 250；不从有限规模结果推导任意规模、任意 mod 或多年游戏均已通过。

最终静态交付检查：121 份源码/文档/配置可严格按 UTF-8 解码，19 份目标 XML/工程语法通过；README、PLAN 与 V1-VALIDATION 相对链接均存在。所有生产源码时间不晚于已验证 DLL；Assemblies 仅有 RimLayers.dll / pdb / deps.json，没有复制游戏、运行时或 Harmony DLL。最终相关原生测试进程均已自然终止。


## 0.1.1：出生落点、下行入口、背景、整片规划、热键与旧档扩层

2026-10-18。实现与当前逐项证据集中在 [六项更新验收](USABILITY-VALIDATION.md)，README / PLAN / About 同步到实际新契约。新增旧图转换取代 0.1.0 的“仅新地图”限制，仍不支持已有布局热改层数或坐标重排。

Core usability 191806 生成17 / 读档4；全五 DLC usability 192601 生成18 / 读档4，零失败，实际81格原生供料/施工完成。实际规划、完工和读档画面已查看，绿色规划边界、真实下层背景与雾、下行标记可见。键盘事件由原生 UIRoot_Play 消费；原生 Targeter 返回下层真实 Pawn，交互未移动实体。

Core migration 191919 生成4 / 读档10；全DLC migration 192317 生成4 / 加载10 / 转换后再次加载9，零失败。后两段复用同一有效源存档，原有失败JSON及旧转换保存保留。完整3600格网格/6区域/压缩物体前缀、5 Pawn ID及1629个非Pawn非污渍持久记录的18项离线审计通过。原生银37/床材质/实体ID/Job loadID和已受管地图不重复扩容有运行检查。

早期迁移191423楼梯覆盖支柱造成断路，夹具移动为独立相邻支柱后通过。192317第二次加载误将天气已融化的积雪与行走前0.35比较；按转换保存时快照修正后，用同一有效原档加载/行走/保存/再加载通过（0.2899233→0.2899214量化）。192759随机生成报告central-spot兜底Log.Error，保留失败，未以清空错误或忽略检查处理。

用户指明3822131067为Nanami Startrail，未修改创意工坊内容。Core/全DLC无HAR初期探针在其生成阶段失败；全DLC+HAR+Nanami的192107实际新生与空域救回通过，原角色ID及原剑盾ID保持，17+3功能检查通过。但零运行错误失败，年龄/关系/需求有空引用。不启用RimLayers的193124对照也复现年龄生成300次失败及Arrival failed；独立对照进程在取得证据后仅结束自身。尚不标记Nanami整体兼容。

本轮所有运行使用独立Artifacts配置/存档；背景没有改变Map的查看层、实体坐标或Job。新的完整背景性能、旧河流存档及其他第三方网格组合不从首版指标推导通过。


### 旧河流存档的追加核对

193925 选中的原生地块流场全零，夹具未成立，不记真实河流通过。194404 改用实际有河水格的河流/道路地块：1350个河水格、28800个原生flow float。第一次按JSON浮点快照逐值相等检查失败；原存档和转换存档的二进制逐列对照一致，说明快照表示精度不足。改用从原始原生保存解码的float位模式，复用194404同一有效源存档，首次加载11 / 转换后二次加载10，零失败，生成5项也通过。

WaterInfo.ExposeData 外层并非只在LoadingVars进入，但实际字符串赋值由Scribe_Values.Look<T>限定LoadingVars；后续阶段局部null不会覆盖流场。此前只读外层所作的重复读取推断撤回，临时LegacyRiverLoadPatch已移除；原有Map.LoadingVars末尾一次x-major重排足够，运行再次加载证据支持此顺序。未保留为不存在的风险增加的重复补丁。

194404完整审计19项零失败：14400格地面、全部流场原始位模式、6区域、15 Pawn ID及3794个非Pawn非污渍静态持久记录一致；新上层流场全零。源存档与旧转换失败保存、JSON和日志保留，未替换生成输入。最终构建消除诊断代码nullable警告，为0警告0错误。


### 最终简中界面与交付检查

最终面板补齐快捷键方向文本，直接显示“下层：F7 / 上层：F8”，仍读取原生MainKeyLabel反映重绑定。独立简中195124复用有效81格施工存档，读档4项零失败；已查看真实图片，中文恢复提示、下行按钮及键位方向完整可读。最终构建0警告0错误。134份源代码/文档/配置通过UTF-8检查，23份XML/工程语法通过；README/PLAN/增量验收/首版验收链接及双语Keyed占位符检查无误，Assemblies只含本mod DLL/pdb/deps。未新增运行依赖。


## 0.1.2：地板遮盖、首次头像居中、纹理雾、双向瞄准与尸体

2026-10-18；继续使用 1.7.4981 rev136 / MVID f1e6382600a84191bbc6d8a184fd6b57，net10.0 / Harmony 3。完整说明、截图及复现入口见 [显示验收](DISPLAY-VALIDATION.md)。本轮无存档格式变更。

### 修正实际绘制顺序，不改变实体位置

BackgroundPass 以自有材质副本隔离各下层绘制队列，压平原生高度；当前结构楼板和木地板盖住背景，不修改 TerrainGrid 或共享材质。Corpse 及动态实体保持真实 DrawPos，经原生 EnsureInitialized/ParallelPreDraw/Draw，在最终网格提交时投影。核实 UnityStubs 的 MaterialPropertyBlock 共同 DrawMeshCore 与 DrawPropertyBlock 独立十参数重载，分别只接一次；原生枪械直接用 Graphics.DrawMesh，不能只补 GenDraw。即时肖像/图集绘制不接入，弹丸及飞行物保持专用显示所有权。

LayerView 将切层前移到原生 JumpLocalInternal 前，跨层停止旧 CameraPanner 和速度并 Cut，同层保留 Pan。LogicalAiming 在原生装备角度/偏移及身体面向使用逻辑向量；只读 Nanami Rendering.cs 确认固定70/290度，追加共用装备入口的跨层有效瞄准修正，保留闲置/同层、NeverAimWeapon/Portrait 和 Verb 角度覆写。不修改工坊内容，不新增运行依赖。

内置 image_gen 生成 ThinMist.png，原始1254×1254 RGBA、Alpha0–253，直接复制保留透明度；DriftingMist 通过可见范围的错位、旋转与低速漂移动画合成。材质位于下层背景之后、当前楼面之前，不画全屏灰色滤镜。生成来源、完整最终提示词及内容路径保存在 Textures/Atmosphere/README.md。

### 当前有效原生证据

| Tests/Artifacts 目录 | 组合 | 生成 / 新进程读档 |
|---|---|---|
| v1-presentation-20261018-204702 | 全部五 DLC | 16 / 9，零失败 |
| v1-presentation-20261018-204856 | Core | 16 / 9，零失败 |
| v1-presentation-20261018-205000 | 全 DLC + HAR + Nanami | 功能16 / 9通过；两段各一个“无运行错误”检查失败 |
| v1-combat-shot-20261018-205233 | 全部五 DLC | 14 / 9，零失败；真实弹丸/在途重启/一次伤害 |

相机检查高/低目标从三层出发的首次跳转，事先启动旧Pan，再执行真实CameraDriver.Update；仍精确居中。原生实际角度高90°/低270°；最终提交还核对低层武器矩阵/高度/队列，防漏绘和重复移位。两具真实Cow尸体的开放列/覆盖列、Pawn/Corpse身份和木地板/结构底层在重启后保留。已查看全DLC固定镜头A/B及加载图、Core雾/楼面图、Nanami生成/加载图：楼面清晰、下层尸体/装备可见、雾形及位置变化。

Nanami实际PostDraw被观测到，功能项均通过；年龄生成、生命阶段、需求与关系空引用继续记录为失败。此前不启用RimLayers的193124对照已复现同类问题，未将本轮角度修复扩大为整体兼容。完整错误保留，未添加忽略名单。脚本在生成后如实终止；随后只对205000同一有效保存手动启动专用加载进程，未替换输入或重生成来掩盖失败。

### 已纠正的探针

203122计数通过但相机漂走，画面不作为最终验收。原生隐藏进程仍会读取屏幕边缘鼠标；只关闭独立测试profile的edgeScreenScroll，并加入下一次相机更新后坐标断言，不改玩家配置。203642为中间Core12/6通过，未覆盖最终网格桥和Nanami。

204419最终网格桥通过但角度143/217失败：射手(62,58)弹道被自身楼板挡住，原生Warmup按规则中断成为携枪姿态。将夹具移到合法边缘(64,58)，并显式检查双方原生CanHitTargetFrom；204702/204856实际保持Warmup，未冻结Pawn或屏蔽原生Tick。原始失败JSON及截图保留。新增Nanami观测、清理测试射线走廊均仅是诊断安排，生产实现没有为测试放宽LOS或战斗规则。

README / PLAN / About / 显示验收同步至0.1.2；原0.1.1验收保留历史并链接后续修正。已核实的材质队列、最终提交、躺卧位置及相机时序写入工作区assets-localization.md和sources.md。Core/全官方DLC无测试期间Log.Error；第三方组合真实失败单列。长期大规模新增背景性能、CE和其他第三方特效仍未验证，旧CPU提交计时不当作本版FPS。

最终交付检查：128份生产源码/文档/配置与测试入口可严格UTF-8解码，18份生产XML/工程语法通过，README/PLAN/显示验收/旧增量/素材记录链接存在，双语Keyed占位符一致。生产源码均不晚于已运行验证的DLL；输出仅RimLayers.dll/pdb/deps.json。本轮测试子进程均已自然退出，无遗留测试游戏。


## 0.1.3：把布尔可达性与完整路线分开

2026-10-18，继续使用1.7.4981 rev136 / MVID f1e6382600a84191bbc6d8a184fd6b57。用户报告模拟中32–61秒长停顿，暂停切层不复现、不切层也复现。按其只读采样授权，用WinDbg正常入口-pv及游戏DAC取得两次主线程托管栈：GetFood→SpawnedFoodSearchInnerScan→CanReach→LayerReachPatch→LayerRouter.Leg→PathFinderJob.Execute。内部CDB执行被Windows拒绝后没有直接绕行，先取得正常WinDbg入口授权；采样后正常关闭本任务调试器，游戏未被暂停或结束。历史只读记录在Tests/Artifacts/stall-20261018-live。

用户随后明确授权实施。此时玩家游戏已退出，备份原DLL/PDB与PathPatches.cs到Tests/Artifacts/reachability-before-0.1.3；没有为更新DLL结束玩家进程。新增Navigation/LayerReachability：原生层内CanReach及缓存＋全部合法有向楼梯的布尔端点搜索，独立内部原生调用作用域；不构造PawnPath，不加跨查询缓存，不裁最近楼梯，也不假定层号单调。PathPatches只更换CanReach接入。实际完整LayerRouter、真实成本比较、Job/Driver、预订与跨层提交复核不改；无存档格式变化。

原生CanReach并非所有模式都只用Region：PassAllDestroyable等和NoPassClosedDoorsOrWater还有格级连通分支；新实现委托原版，未自行重写这些政策。程序集/启动日志/About/README为0.1.3。完整规则、现场证据和实际运行结果见 [可达性修复验收](REACHABILITY-VALIDATION.md)。

### 当前原生证据

| Tests/Artifacts目录 | 组合 | 生成 / 新进程读档 |
|---|---|---|
| v1-reachability-20261018-213506 | 全五DLC | 16 / 5，零失败 |
| v1-reachability-20261018-214032 | Core | 16 / 5，零失败 |
| v1-navigation-maze-20261018-214137 | Core | 13 / 6，零失败 |
| v1-navigation-door-20261018-214232 | 全五DLC | 15 / —，零失败 |
| v1-navigation-rules-20261018-214302 | Core | 11 / —，零失败 |
| v1-work-save-20261018-214327 | 全五DLC | 17 / 15，零失败 |
| v1-roping-20261018-214428 | Core | 30 / 24，零失败 |
| v1-combat-ai-20261018-214524 | 全五DLC | 17 / —，零失败 |

所有实际activeMods与游戏MVID在结果JSON中，测试期间无Log.Error。已查看Core重启后的真实三层进食画面；Pawn实际经过二层，原Job/真实食物在重启后仍匹配。非单调路线实际为0→1→2→1→0；动态门改敌对/原生开门、网格自定义偏置/堵落脚点、连续多次取料、双动物畜栏与战斗射位保持通过。未复测不受改动影响的薄雾美术或物理伤害完整矩阵。

同输入对照在同一进程临时接回旧CanReach完整路径实现，分别调用同一原生FoodScan。固定250地面三层、191250存储格、130真实食物（128地面＋可达三层＋无连接高层孤岛），先预热、记录完整130项布尔系列与计时。全DLC旧822.1789ms/1675 PathFinderJob→新0.2949ms/0；Core旧636.6508ms/1675→新0.2827ms/0，选中同一个真实食物。不是完整帧、冷启动、TPS或玩家165模组的计时，不据此承诺其他热点已经消失。

### 失败与反例记录

213007生成唯一失败为禁用楼梯：测试Pawn仍征召，原生IsForbidden会按规则忽略禁用。调整仅发生在夹具的禁用检查阶段，暂用未征召Pawn，之后恢复；生产规则未变。

213222首次生成的130项旧/新布尔比较失败，但当时未保存逐项反例；该生成保持失败，未改成通过或归因为未经证实的原因。随后增加逐项JSON记录，用同一份213222保存执行加载审计：130项一致、17项零失败，文件为load-reach-candidates/load-reachability-results及load-audit-stdout。未重生成覆盖输入，后续213506/214032的全新生成和重启另作独立有效证据。接口继续采用原生连通性语义，实际路径执行仍作完整严格检查，没有为了通过测试加入全路径回退或静默放宽结构规则。

本轮所有游戏验证使用独立Artifacts数据。玩家原组合的现场回验仍需其重启0.1.3后进行；原日志中的Nanami年龄/关系及JoyToleranceSet越界不在此次修改范围。

最终候选Release构建0警告0错误。130份生产源码/文档/配置及测试入口UTF-8检查、18份XML/工程解析、当前验收/计划/README/现场记录相对链接检查通过；全部已列最终结果JSON的failures=0且errors为空。生产源码不晚于已验证DLL，Assemblies仍只有自有DLL/PDB/deps。本任务原生测试均已退出。


## 0.1.4：原版征召命令入口与 Dashboard 启动中断

2026-10-18；目标仍为1.7.4981 rev136 / MVID f1e6382600a84191bbc6d8a184fd6b57。用户报告右键移动/攻击及射击按钮无响应。原21:53玩家日志59条菜单异常落在GetProviderOptions，前面的ReportProbablyMissingAttributes因RimirisDashboard.ConsoleTap.savedLevels缺失UnityEngine.StackTraceLogType而失败。定向反编译确认DoPlayLoad同一回调的后续FloatMenuMakerMap.Init被跳过。原日志和0.1.3 DLL/PDB保存在Tests/Artifacts/command-input-before-0.1.4。

新增Compatibility/DashboardStartupInspection，仅Dashboard激活时安装，限定程序集、类型、字段和StackTraceLogType的TypeLoadException；跳过这一非资产字段并输出一次明确警告，其他字段和异常照原版处理。既不伪造类型，也不吞菜单提供器异常/初始化后代建菜单。Dashboard自身控制台堆栈功能依然不兼容，需其作者更新。原可达性/完整路径/Job/预订/存档格式及战斗物理不动。

初次构建期间用户重新启动了游戏，DLL被玩家PID34776锁定，构建复制失败；没有强行关闭或更换路径绕过锁。询问正常退出后，用户回复“已退出”，再继续构建和独立验证。当前版本0.1.4已输出到Assemblies；编译零警告零错误。

新增V1CommandInput实际调用Pawn.GetGizmos的Command_VerbTarget.ProcessInput，鼠标事件经完整UIRoot→Selector/Targeter分发，再实际Tick到达同层/三层目标并发射。关闭自由射击，以每次指令前的LastShotTick为基线；始终验证原始目标Pawn身份，覆盖切到目标所在层和当前高层背景拾取。诊断作用域同时固定屏幕/地图鼠标、type/rawType，并逐阶段记录事件消费。实际用例不直接创建Goto/AttackStatic。

复现221350未加补丁时providers=null，右键移动/攻击失败，射击图标仍能工作。221514补丁正确隔离已知字段，但独立测试漏带RimIris Beta，额外缺失RimIrisPerfSample；补齐真实软依赖后221630的19项通过，未扩大异常过滤。221739尝试Nanami原角色，第三方年龄/需求失败导致角色未生成，保留失败，不改Nanami或游戏数据。完整165组合221936因Ideology描述语法null key在世界生成阶段失败，未到地图，240秒后结束自身测试进程；日志还有原有Shader/资源/XML错误，未处理为命令修复。

完整UI早期夹具222350/222828事件提前消费，生成命令失败；早期只设置type却留下rawType=Repaint，初版未同步屏幕鼠标。保留原失败，不擅自断定对应生产缺陷。222606 Core+RimIris Beta+Dashboard、223109全DLC同组合各19项通过。修正type/rawType后，最终223314全DLC+Harmony+RimLayers+RimIris Beta+Dashboard 19项零失败、无运行错误。Core 223419对照10项失败，Event.Use采样明确为GizmoGridDrawer→GenUI.AbsorbClicksInRect：固定(512,320)命中真实控件区；此前只按MapInterface阶段定位不足。改用逻辑UI中上方空白点并记录控件命中矩形，保持原生事件吸收不变，最终223722 Core 18项、223746全DLC+RimIris Beta+Dashboard 19项均零失败、无运行错误。223314保留为中间通过，不以它覆盖随后的反例。223052一度诊断代码命名空间歧义构建失败，修正System.Environment后零警告零错误。全部原始JSON/标准输出保留，未重写失败。

README/About/PLAN/COMMAND-INPUT-VALIDATION和知识库debugging/sources已更新。独立测试不覆盖玩家原档中的实际武器与目标；原档射击按钮异常尚未同源复现，应重启后回验，若仍失败则定位该实体和命令入口。不能以本次启动兼容宣称任意第三方战斗整体兼容。


最终Release零警告零错误；有效结果为223722/223746，完整激活列表和游戏MVID在JSON中。已按原请求核对真实右键/射击入口→目标身份→原生Job→实际移动/发射，未将玩家完整旧档的射击异常标为已修复。最终文档链接、UTF-8与XML检查通过，自有DLL/PDB/deps为唯一程序集输出；本任务测试进程均已退出。

## 2026-10-19：Odyssey 多层逆重飞船开发候选

本轮游戏已更新为 1.7.4982 rev1280，Assembly-CSharp MVID a46e6f45b6164261bb63a9471bf7402a。新增 Ships 四个模块，使用原版 Gravship 字典作为唯一实体所有者；在有完整玩家核心和有效原版基底时扩大上层收集格，载荷内规范化为逻辑相对层号，落地按新地图 stride 解码。原版燃料、推进器、驾驶台、冷却及航程未放宽；必要飞船设施仍按原版规则放在核心甲板。墙面附着物随支撑墙。跨界上层多格建筑/区域拒绝起飞；落点预览和确认都复核上层完整占地。

保存仅补充结构与 top/under/temp 事实，Pawn、物品、容器、Bills、区域由原版保存。落地先建立承重和屋顶，再贴原版地形，最后恢复完整表层，早于任何上层 Pawn/楼梯生成。清理原图随船楼面及雪沙/气体/指令并重建新图服务。普通新地图生成规则不变，只有合法入站载荷可使 NPC / 太空新目的地图预分配楼层；这两个实际场景尚未运行验证。

有效全 DLC 候选 094429：20/20/9 项通过，包含三层、船边墙灯、容器服装、原 Pawn 和银堆身份、工作台 Bills、允许区/区域优先级、不同尺寸地图、136↔170 stride、90°及回程180°旋转、飞行中/落地重启以及第二次原版起降返回已有多层主地图。095051 进一步使用原版 DesignateSingleCell 提交回程落点，10 项通过，画面无悬留设计器。Core 094730 原版真实指令回归18项通过；errors 均为空。最终 Release 编译零警告零错误。完整入口与截图见 GRAVSHIP-IMPLEMENTATION.md。

保留初期夹具失败：阶段330与原 corpse-refresh阶段320分开；按1.7实际API创建允许区、初始化地基与临时地形；推进器必须避开原版排气区；JSON只记数值坐标；清单在真正封装时采样。原版确认按钮会清除已销毁marker，夹具曾遗漏导致落地存档重复深保存，现按原版调用者释放；没有改原版保存契约。093451/terrain-probe-093712 揭示真实缺陷：临时积水参与原版承载检查把木地板回退为Soil，现分阶段恢复表层且运行验证通过。094147清场误删marker；return-ui-094834独立Deselect重置未提交旋转，均由夹具修正，原失败文件保留。

未完成依赖：已有但未扩层地图的实时扩容。替换/重建其原生网格和缓存的实现写入请求被自动安全审查拒绝，存在损坏地图及后续存档风险，未执行、未写入 ShipMapExpansion。已询问用户是否允许先在独立测试档实施和验证，尚未收到答案。当前在原版选目的地validator内、耗油前拒绝该类目的地；已有多层地图及新地图主流程可用。该依赖仍由本任务负责，获准后继续，不能据当前候选声称完整兼容。未操作玩家原存档、配置、原版或参考项目。

## 2026-10-19：现有飞船目的地扩容——授权后先隔离再接入

用户明确批准“先隔离验证，通过后再接入”，替代之前被安全审查阻止的运行时扩容方案。先创建 ExpansionBuffers/LiveMapExpansion，仅由显式独立夹具调用；河流地表 140546 的 19/7 项和 250×267 太空地图 140758 的 17/7 项均零失败、errors 为空后，才连接正式确认出发动作。

事务保留同一 Map、实体、Job/Driver/预订；显式扩展原生每格缓冲，按坐标重映射三维仓储与河流数据；独立重建洪水填充、区域连接图、寻路及绘制缓存，成功提交才释放旧所有者。注入故障后恢复尺寸/布局/原网格和活动路径。修复中确实发现并保留了 140017 洪水填充哨兵、140248 section 初始化/旧区域浅拷贝、140423 仓储三维网格越界的失败，未屏蔽日志或放宽断言。

生产 Report 只读；CheckConfirmSettle 包装最终确认动作，扩容成功才执行原版毁树/扣油/起飞，失败保留原船。InitiateTakeoff 保留幂等兜底。Pocket/地下仍拒绝。普通 NPC 和太空地图没有普遍自动扩容，只准备合法多层飞船的目的地。

全 DLC 原版往返已有太空地图 141241：22/22/11；已有地表目的地与真实确认委托 141547：26/22/11。飞行、落地均新进程重载，目的地图原有货物/资源和 ID 保留，上层舱室在原版恢复提交点的温度/真空值一致。141016 的无世界图标远古派系聚落和未 Spawn marker 夹具错误修复为真实派系和原版设计器提交，没有改生产选址规则。查看太空画面后取消真空中的地表薄雾，保留轨道背景。

完整数据、截图、复现命令与后续边界见 [LIVE-EXPANSION-VALIDATION](LIVE-EXPANSION-VALIDATION.md)。当前仍为开发候选，未提升版本或声称第三方飞船框架兼容；未改玩家配置/存档、原版或参考 mod，也没有自动保存玩家游戏。

最后的轨道视觉重放使用同一 141241 飞行检查点；142017 将夹具平台完全清空后，原版“必须连接陆地”选址断言失败，保留失败结果。补为小型合法接地点后 142255 的 22 项通过，errors 为空，已查看实际无地表雾的轨道画面。生产没有修改原版接地限制。最终源码已构建，UTF-8、双语 Keyed 与文档链接检查通过；独立测试进程全部退出。

## 2026-10-19：飞船开局楼梯按需创建楼层

用户要求改为楼梯建成时创建尚不存在的上层，不再以未扩层为由禁止建造。新增 Structure/StairLayerBootstrap；PlaceWorker 放行受支持单层地图的楼梯，仍查人工屋顶和原版地形条件。施工候选/tick 统一校验；最终 Frame 提交在原版材料销毁和替换前准备地图，成功才完工。SpawnSetup 的绕过 Frame/读档成品仅发出派生请求，由安全地图 tick 扩容和建立出口；旧地表地图加载迁移后的成品也补建出口，已有布局不重置。通用楼梯不增加逆重核心先决条件，随船迁移仍查合法核心。

全 DLC 太空原版建造 150332 为 18/5，Core 150815 为 19/6；蓝图/取消/施工不提前扩层，失顶保留原始材料，Pawn 实际上楼，第二楼梯不重扩，未执行的成品请求在读档重建。Core 150530 空 NPC 夹具被原版回收而超时；补入驻留玩家 Pawn 并检查不可回收，未关闭原版回收机制。

共享施工生成回归 151024 的 31 项通过；其加载有一项坍塌断言失败：原 SpawnSafety.Recover 抢先将待坍塌 Pawn 移到同层安全格。增加“仍有结构楼板且格在 PendingCollapse 中则交给坍塌处理”的恢复次序，普通非法出生恢复不变；同一失败保存的 stair-structure-load-20261019-151343 重放 15 项全通过。原始失败保留，未换种子或放宽断言。

已查看真实太空上层和 Core 读档上层画面，出口与 Pawn 可见。构建零警告零错误；详细文件、原始结果和使用步骤见 [楼梯验收](STAIR-BOOTSTRAP-VALIDATION.md)。测试使用独立数据，无玩家原档/配置更改，当前仍按 0.1.4 开发候选提供。


## 2026-10-19：多层飞船、逐层扩容与电梯

按用户完整计划实现，继续提供本地0.1.4开发候选，未发布新版本。游戏基准1.7.4982 rev1280。动手前备份目标mod Source/Defs/Languages/About/Assemblies及测试入口至 `Tests/Artifacts/deck-elevator-before/`；原版、参考mod、玩家配置及存档不修改，所有游戏测试使用独立数据目录。

扩容改为 EnsureLayerCount(1..3)，每次只填充新坐标带，旧层前缀不再清空；撤下加载时重写存档XML的LegacyMapMigration。楼梯/单层电梯真实Frame完工前扩容，失败仍保留原Frame材料；成品/读档按安全tick队列补建出口。记录分配、派生、填充、区域及绘制时间，保留Expand兼容入口。真实二次追加/失败回滚修复了原生待生成Room列表和Sky数组的旧布局引用。

新增ShipStructureService原始结构图、专用foundation/Substructure飞船楼板、原版设施生命周期接入及全船承载/数量限制；同图多船隔离，引擎任意甲板，上下设施按逻辑距离。候选结构与设施活动分离，避免延展器递归。旧船楼板幂等转换，保留top/under/temp、颜色及实体。拆除和坍塌按实际floor种类退款，新生成返还物使用原版安全Near放置；暂时无空间只保存剩余新生成材料账目，加载后重试，不重复保存/销毁原货物。

ShipLayerData v2以最低所属甲板为零，保存engineDeckOffset；目的地只准备实际span。最低甲板专用foundation落地投影成原版基架，原始字典不修改。单甲板高层飞船也走逻辑恢复，但无需扩展目的地。通用普通支撑层不能因上面有船体而被吸收。原版唯一持有Pawn、物品、容器、工作台Bills及区域，无复制实体或第二套预订。

连接器泛化支持类型、固定耗时和垂直井道，保留旧三参构造/解构接口；Dijkstra按实际native path成本加耗时选路，同端点重复边选择更低成本。单层25/180tick，垂直30+45d /90+120d；井道逐层构件、可禁用停靠，全路径通电才快速，断电应急。原Driver、队列与预订贯穿全程，执行前复核并重规划。电梯不桥接电网或环境传播。原版power teardown重置开关的行为仅对电梯局部保留用户状态。

屋顶使用17×17区块不透明表面缓存，普通/飞船材质区分，当前楼面覆盖。动态实体和点选共用屋顶遮挡，未探索格不揭露，结构/迷雾变化局部失效，卸图释放；不改变真实弹道几何。两种电梯及屋顶使用项目自制纹理，有可重生成源及归属文件。

有效证据：Core stairs160339 19/6；全DLC太空single lift165109 21/6；河流未扩层旧档165232 5/12/9；河流二次追加及故障回滚165707 22/7；全DLC引擎二层原版往返已有地面164444 48/23/11；底层引擎设施高层163531 35/22/10；Core电梯165827 19/6；电梯三层多次取料165426 17/15；非单调路线165607 13/6；普通结构165922 31/15；Core真实命令165534 18。相应errors均为空。新单甲板/认领归属171347 43项通过，原版捕获→未扩层地图PlaceGravshipInMap保留基架、木地板、积水、银堆且普通下层留原图。已查看实际屋顶/电梯和飞船层画面。

逐项复现、性能对照、保留失败及完整最新结果见 [多层飞船与电梯](DECKS-AND-ELEVATORS.md)。此前失败未删除：160519回滚Room/Sky、161513普通楼板误吸收、161733裸基架事实丢失、162400电梯开关重置、163923裸基架PathGrid缓存/退款放置；修复后的实际场景回归通过。171112单甲板夹具缺驾驶台、171249驾驶台越界，补齐真实前提后通过，未放宽原版起飞规则。


最终补充：`v1-gravship-20261019-171424` 全DLC三层核心/原版太空完整往返47/23/10项通过；`ship-v1-replay-20261019-171729`直接复制此前141241版本1飞行检查点（version及offset标签均按旧默认省略），不重生成，24项通过，包括实际升级v2、既有太空目的地原版落地、装饰地形栈及舱室数值。该旧船按新的全船承载规则可能超载，保留甲板，不用它执行返航断言。两个结果errors为空。已查看最终返航三层核心和电梯/屋顶真实截图。最终Release构建0警告0错误；无晚于DLL的源码，UTF-8/XML/文档相对链接/双语Keyed一致通过，输出仅自己的DLL/PDB/deps。自身隔离测试进程已全部退出。使用和迁移入口已更新README/About/PLAN/专章；大型新渲染长期性能及第三方框架仍未验证。


## 2026-10-19：非一层查看时起降船体透明

用户报告切层后起飞/降落只见特效、船体透明。本轮在不改变实体/载荷的条件下修复两条显示路径：SectionViewPatch保留原版捕获范围的图块，捕获期间不提交观察用下层投影；ShipCutsceneView按当前查看层临时转换动画中心/引擎位置、镜头Pan与地表幕布，finalizer恢复展示字段。撤去强制起飞切核心甲板前提。另核实BeginTerrainRender不更新公共captureBounds，补充托管地图地表捕获范围，不沿用原核心甲板范围。

新增ShipVisual诊断和Run-V1开关，记录真正捕获PNG、alpha像素以及动画中二/三层绘制坐标。早期172354/172506为Game.UpdatePlay夹具发起，一层对照172757也得到空贴图，不能当作隔离根因的有效证据。改为GUI并先呈现新网格后172933生成51、加载26通过，其返航17中1项仍因旧发起路径失败；返航同步修正。173300等待GUI时多推进tick导致无人目的地图被原版回收，确认回调空引用，结束自身测试进程并保留日志；等待GUI时不推进测试tick，未修改地图回收规则。

有效173514全DLC三层核心、二层捕获、二/三层起降及动画切层、既有太空地图原版往返56/27/18项通过；最终地表范围补丁用同一飞行检查点cutscene-load-final-173843回放28项通过，errors均空。三层核心捕获1170144/1477440、返航725698/921240个alpha>127像素；已查看起降真实图像。完整入口、PNG和范围见SHIP-CUTSCENE-VALIDATION.md。仍采用原版核心甲板二维快照，不扩展为三维多甲板动画。旧透明贴图不伪造重绘，新一次起飞重新捕获。源码/程序集均为目标mod，本地开发候选版本不变。


最终Core真实指令回归174409为18项通过、errors为空。前一173929合成点击被原版MainButtonWorker/GUI.ButtonImpl消费，10项失败；仅增加诊断记录重跑后未命中按钮且通过，未改输入逻辑、未删除失败或宣称已解决夹具稳定性。最终Release零警告零错误，新增记录之外的正式渲染代码与173514/最终回放候选一致；已检查实际最终三层太空降落画面、UTF-8/链接及自有程序集输出。测试进程均已结束，重启游戏使用本轮DLL。


## 2026-10-19：其他甲板乘员阻塞原版发射仪式

用户报告Pawn在船内但不在同层会阻止发射。原版ArrivedCheck对spectators/pawnsToBoard/pawnsToLeave三次直接查核心二维ValidSubstructure，绕过现有多层ValidSubstructureAt；GotoShip/LeaveShip驱动完工又直接查一次并错误移除待登船乘员。新增ShipBoardingPatches，按3/1个IL锚点精确替换两个成员的查询，不扩大二维getter，保留驾驶员到岗、60tick节奏、名单及失败消息逻辑。原版登/离船落点的层内Region BFS失败时补上托管合法船体、真实楼面和跨层可达枚举；不传送、不引入运输Job或预订副本。

新增V1ShipBoarding并接入ship-structure场景。全DLC二层核心182314、仅Core+Odyssey三层核心182513均55项通过、errors=[]。覆盖真实原版到齐触发器的三层普通乘员、驾驶员未到岗负例、船外乘员负例、上层待离船访客阻塞及离船后放行、实际GotoShip驱动hasChecked完成且保留原名单、独立上层平台经过楼梯登船和跨层离船候选、拆除唯一楼梯后无路。原有设施/承载/施工/独立船体及单甲板真实捕获落地检查同场景继续通过。未将直接InitiateTakeoff替代这些仪式入口。本次未重新执行完整起降动画/跨图保存矩阵，未修改其生产代码。

首次诊断构建误用Core.RimLayersDefOf命名空间失败，已修正为项目实际DefOf；平台夹具采用真正隔离的上层楼面，不让同层相邻船面掩盖跨层搜索。最终Release零警告零错误，输出为自己的DLL/PDB/deps。使用与复现见SHIP-BOARDING-VALIDATION.md；修改DLL后重启。玩家配置、存档及原版不改。


## 2026-10-19：山体多层、矿脉、山侧入口与竖向开采

用户确认薄岩顶需要人工铺板，厚岩顶自带不可拆天然楼板和含矿岩体；同轮廓向上延伸，允许山外二层平台直接接入开采，内部楼梯施工包含竖向开采，旧层只显式迁移。保持三层上限、逐层扩容、独立 mod 与原生实体唯一持有。

新增 Mountains/MountainService、VerticalMining、MountainMigration，由 LayerMapComponent 唯一持有和保存山体状态。地面轮廓及石种快照、逐层稳定矿脉计划、已处理格和天然楼面独立保存，Mineable/Job 不重复保存。矿物来自原版 Def 权重、RocksFromGrid 参数及 IrregularLump，单层逻辑面积计数、全矿簇限域，不复制地面矿脉。新层只写新增带；扩容回滚撤销新实体及事实并恢复塌顶队列；迁移另恢复迷雾与房间气候。薄岩顶进入人工楼面支撑，天然楼板独立承载且不能拆除，装修与临时地表仍按原版层栈处理。船体归属明确排除天然楼板，落地即使遇到采空且无屋顶的天然地面也拒绝覆盖。

山侧使用原版 MineAIUtility、Mine Job 和 Touch 接近；跨层行走保持同一原版 Driver。竖向采矿由独立 Mining WorkGiver/Driver 从下层框架邻格操作真实上层岩石，逐次调用原版 Mine.DoDamage，保留收益累计、历史/记录/经验及矿物产物。Frame 在开挖完成前保留真实材料并退出建造尝试；真实建造 Job、矿工竞争、中断、半途重启均验证。候选使用原生框架及楼梯 Def 索引，不扫描全山岩体。旧层面板提供预览和确认，已有规划/实体/楼面跳过，矿脉计划不因分次迁移重抽；未自动保存。

有效结果：Core195048生成31、重启15；全DLC单层电梯192945为30/11；普通结构193049为31/15；全DLC已有地面目的地图、二层引擎原版生成193607的51项，以及同一飞行检查点独立目录194013的25/11项；全DLC命令195145为18项。errors均为空。最新Core实际1500HP钢矿19次敲击得到45钢铁；按照原生回调delta12/15逐次核对HP与耗工，共1137有效tick。已查看山侧实景、迷雾、楼面与原生迁移窗口，未把构建等同于运行通过。193514的120×120、2429山体格二次追加831.8ms，山体82.2、区域119.1、绘制564.2ms；不推导大图长期性能。

保留原始失败：191042把矿物误判为!isNaturalRock，改用原版isResourceRock；191254/191428/191535的同tick造景后Pather失败通过层内对照与PathFinderMapData.GatherData查清，每tick只采样一次，结束诊断造景批次再派任务，未改生产寻路。191822/192014的装修断言误将采矿后的RoughHewn与初始naturalTerrain等同，改为严格比较铺装前实际石面。自然基底显式不继承默认空域/沙地under。192139耗时JSON直接序列化IntVec3导致转换属性循环，改写原始字符串及数值。192332薄顶蓝图检查误选已经铺好的道路格，改查同一屋顶上的空白格；192536从地面构造仅上层可用的迁移窗口，改通过真实上层视角打开。

飞船回归193149未传NativeShip却同时要求ExistingMap，导致独立放置分支与目标检查不匹配，且空敌方聚落原生判定战败时没有玩家Pawn生成故事。改为原版起降入口，目的地夹具保有真实玩家居民和扩层前准备的地面停机坪。193607加载时一只原生松鼠(61,0,74)仍占据停机坪，原版/多层检查正确拒绝；194013使用相同飞行保存，仅把测试野生动物移出夹具落点，生产不放宽碰撞、不清除山体，并新增天然裸楼板拒绝落地断言，完整落地重载及返航通过。

194157命令夹具所有开局Pawn均无暴力能力，First查询失败；只在隔离诊断中补生成有能力的真实殖民者，不改变玩家Pawn权限。194400/194701的矿物耗时断言错误使用最终间隔或逐tick敲击假设，已核实原版DriverTickInterval(delta)与实际批次回调；195048按每次实际delta和HP独立重放原生敲击计数，未修改生产采矿节奏、未扩大时间容差。

源码/程序集仍为0.1.4开发候选，无原版和参考项目修改。备份见 Tests/Artifacts/mountain-before-20261019-190345；使用和迁移入口见 MOUNTAINS.md，README/About/PLAN及工作区结构/来源专题已同步。新大图长期性能、地下地图和第三方地质生成框架未验证。


## 2026-10-20：三维战斗、角度掩体与跨层灾害

按批准方案实现 CombatService.SolveWeapon、独立 CoverHeightExtension、九点姿态/体型暴露、直射层差 0.8–1.2 倍射程、直线/曲线统一路径和首次碰撞。跨层原版 TryCastShot 跳过原二维掩体随机分支，发射快照只抽一次身体采样；原版精度、散布、弹药和伤害链保留。同层普通射击保持原生规则，同层曲射也追踪上方空间。人物射位加入几何暴露与防护，炮台扩展候选范围并保留原版资格，敌方迫击炮独立随机建筑分支修正水平距离和隐藏实体过滤。原生操作、供弹与跨层路线不另造业务状态。

已有三维爆炸冻结覆盖、气体守恒交换、跨层热交换与 FireLinks 20%/5% 传播符合方案，保留其原生实体/生命周期，使用实际回归验证。新增英文/简中瞄准面板显示射程、暴露、实体障碍名称/层/坐标，曲线截击显示接触格；已检查游戏实际截图。

新增 combat-3d、combat-manned 与纯几何覆盖。combat-manned 使用原版 ManTurret 的取弹、预订、装填和实际操作位；从携弹保存和炮弹在途保存分别独立重启，核对炮管、炮弹、Job 和发射者。五种原版 Shell 使用真实 Projectile/Explosion，核对爆炸损伤、燃烧/燃料污物、机体 EMP 眩晕、烟雾浓度和灭火泡沫。

保留失败与修复：首次构建 DrawLineBetween 误用 Color，按当前签名改为 SimpleColor；测试 ShotPoint 属性误写 Y，改为 Height。172358 截图因先切层再选射手被 Selection 切回一层，调整诊断次序并加入共享面板 Query 与目标层断言，172908 实际画面通过。173745 全 DLC 动态楼板测试沿用成年标准枪口高度，实际开局 Pawn 身体高导致枪口仅 1.08，原 x=71 的楼板不在射线上；改为从本次不可变发射快照计算高度 6 交点，174032 同一验收条件通过，未修改实际弹道。174345 屋顶截击夹具只覆盖 x=103..107，曲线在 x≈102.9 已降至顶面下；扩展屋顶至真实交点，174451/174518 Core/全 DLC 各 96 项通过。英文键值编辑中一次默认编码把分隔点误读为“路”，由实际截图发现并改为 ASCII 分隔符。记录保留在各自 Artifacts 目录。

构建零警告零错误，纯几何 31 项通过；气体与热量各 100000 次随机守恒/边界检查通过。完整本轮游戏目录、断言数及范围见 COMBAT-3D.md 的验收表。源码恢复点为 Tests/Artifacts/combat3d-backup-20261020-101336.zip；不改原版 DLL/Data 或玩家配置与存档。


最终契约检查补充：原版 CompChangeableProjectile.Projectile 在 Loaded=false 时返回 null，扣弹后重新读取 Verb 会丢失弹药类型；未装弹迫击炮也可能被误当直射。改为在 TryCastShot 作用域固定本次 Projectile Def，并根据原生 VerbProperties.isMortar 保持未装弹的曲射分类、500 格原射程和发射净空。增加原版 Notify_ProjectileLaunched 返回后的快照断言，不修改原版消耗。175320 全 DLC 的角度比较因随机较小体型打破“枪口高于沙袋”前提而两项失败；限定该几何对照为 30 岁 baseliner 并显式检查 BodySize=1，特殊体型/倒地仍由 combat-policy 单独验证。175537 最终全 DLC 专项 98 项通过，已查看最终英文实际瞄准截图，分隔符与障碍名称/坐标均正确。

最终有人迫击炮 175607 全 DLC 生成/取弹重载/炮弹重载为 17/15/5 项，均零失败、errors=[]。全部本轮有效回归目录与计数已写入 COMBAT-3D.md，最终源码与已输出 DLL 一致；普通同层掩体、友伤、特殊体型、AI、炮台、上层屋顶、护盾、冻结爆炸、延迟引爆、火灾、气体和热量均有对应有效运行。非投射物特殊 Verb 与第三方战斗框架仍需独立适配；长期规模性能未测。


## 2026-10-20：真实袭击测试、二三层射击及投掷兵空等修复

新增 V1Raid、V1GrenadeLayers、V1UpperCombat 与 Run-V1 场景。真实 IncidentWorker_RaidEnemy 生成原生 Lord 与攻击者，记录战斗过程、实际飞行、伤害、上楼、绑架和离图；不手动分配敌方战斗 Job。独立恢复点为 Tests/Artifacts/raid-backup-20261020-125922.zip。

200630 隔离袭击虽发生交火且守军失能，但逐时轨迹显示数名投掷兵长期 Wait_Combat。原“发生交火/守军失能”断言不能发现这类局部空等，故保留当时存档，增加目标诊断与明确的等待循环检查。202856 从同一战场保存重启，确认 5 人 canHit=true、canSee=false、auto=null、busy=false，失败证据保留。原生 JobGiver_AIFightEnemy.TryGiveJob 只用弧线可达决定等待，JobDriver_Wait.CheckForAutoAttack 则以 NeedLOSToAll 再索敌；平台遮挡导致两条路径不一致。

新增 Combat/AIFiringVisibility.cs，将 AI TryGiveJob 内当前射位判断及 FiringPositions 候选筛选共同约束为视线和弹道可用。作用域经 Finalizer 恢复，普通直射/同层和玩家显式投掷不增加前提。203104 使用原卡住保存：5 人全部离开无效射位，实际投出 2 枚手榴弹，2400 ticks 后没有同类等待循环，恢复保存内原炮台已被移除。203307 普通 AI 17 项、203336 新隔离袭击 13 项通过；二三层实弹 201746 为 25 项通过，手榴弹全方向/散布/重载见 RAID-COMBAT-VALIDATION.md。

诊断夹具错误均保留，不算生产缺陷：200047 只查精确 Pirate 名称忽略 DLC 海盗子类；200232 采样阵亡者 mindState、200356 采样伴随动物 equipment 未判空；随后只修诊断采样。201638 原版实弹一发击杀二层 Pawn 后诊断仍移动该尸体引用，改为先做站位检查再实弹，201746 通过。200938/201817 动态楼板的固定 x 条带不覆盖原版格内偏移后的曲线交点，最终以不可变发射快照解析求解 y(t)=6，再布置拦截面；不改弹道或降低碰撞断言。构建中诊断遇到私有 forcedMissRadius、受保护 TickInterval 及局部变量重名，按本地成员签名修复。Core 种子 1/2 生成纯近战，缺少上射不代表射击故障；枪械场景改用原版 TryGetRandomPawnGroupMaker 预览选择含 Pirate 的原生编组，202510 种子 3 生成 14 人并记录 51 发上射。

手榴弹使用真实武器 TryCastShot、原版 Projectile_Explosive 引信和 Explosion。精确组只关闭强制散布/野射，20 枚散布组恢复原属性。二层到三层/反向、向地面/上层、动态楼板、落地重载、同层物体损伤和隔板保护分别有断言。结果、路径和未覆盖袭击类型集中记录在 RAID-COMBAT-VALIDATION.md。生产修改限于一个 AI 补丁及射位筛选的一处调用；无玩家存档或原版 Data/DLL 修改。

## 2026-10-20：地下 B1/B2 开挖

落实已确认的地下两层方案，保留单 Map 追加式存储；布局版本 2 将稳定 LayerId 与物理 Elevation 分开。向下楼梯/单层电梯在框架完工阶段准备下一层，复用真实 Mineable 竖向采矿。IceSheet/SeaIce 整图拒绝，其他入口检查真实基底，成品直放也不能绕过。

地下使用原生岩种、闭合洞穴、矿簇和 Hive 创建入口，不对旧整图重跑生成器；保存生成标记与天然楼面，异常加入既有扩容事务。虫灾保留原生资格和事件频率，只隔离存储带间的几何污染。上下切层、环境交换、弹道/爆炸、落物与飞船载荷改按真实高度处理。

原始反例包括 RoughHewn 出口误判、起始视图不明确的断言、250 地图选择单格孔的虫灾夹具，以及大图普通岩体撤销的长暂停。分别以原生岩体地形关联、明确视图、真实洞穴及缓存刷新、WillReplace 批次撤销修正。故障回滚恢复正确，大图仍约 84 秒；正常单层新增约 2.4–2.6 秒。

Release、纯几何/布局/环境测试、Core 地下、全 DLC 250 地下、原结构/山体/飞船共存均通过；混合布局原生双层船实际捕获和着陆另有 28 项重放通过，修复了该夹具最初漏放驾驶台的错误；真实版本 1 历史档重启后继续原采矿任务通过。使用说明见 UNDERGROUND.md，逐次结果、原始失败记录、短模拟数值与实际截图见 UNDERGROUND-VALIDATION.md。源文件改动前备份位于 Tests/Artifacts/underground-implementation/originals。

## 2026-10-20：飞船停靠高度上限

先更新 SHIP-COLONY-COEXISTENCE.md：按物理落层加完整载荷跨度计算最高层，镂空计跨度；超过地上第3层整船拒绝，不裁切、不下移、不扩出第4层。ShipLanding.CheckHeight集中返回带落层范围/上限的中英文提示，预览、确认和最终放置共用；高度预览失败同步原生validCells/invalidCells，避免只有拒绝文字却仍画白色占地。原生三处成员与证据入口补入根docs/odyssey-gravship.md和sources.md。

新增V1ShipLandingLimits真实双层载荷夹具，覆盖二层合法、三层超限、三个入口、地图/载荷/标记只读、合法位置恢复、单层顶层和镂空跨度。Run-V1增加默认English的Language选项，可选ChineseSimplified；没有改变玩家配置。

保留首轮v1-ship-coexistence-20261020-214847：行为断言通过，但夹具在ArriveExistingMap后误对尚未Spawn的marker DeSpawn、重复移除已由抵达流程移除的WorldObject，导致运行错误检查失败。删除夹具的两次错误生命周期操作，215016的39/6通过。随后核实原生SelectedUpdate用红白格列表而非报告着色，补全颜色处理；215249的41/6通过。截图阶段切层会取消设计器，夹具改为切层后重新选择，未改变生产切层规则。

最终English 215450与ChineseSimplified 215655均生成41/重载6、errors=[]；Release构建0警告/0错误，实际查看两种语言整船红色斜纹与范围提示。完整目录、截图和复现命令见SHIP-COLONY-VALIDATION.md。源文件修改前备份在Tests/Artifacts/ship-landing-limit-before-20261020-144728。当前仍为本地0.1.5开发候选，更新DLL后需重启。

## 2026-10-21：可选 VGE Chapter 1 / 2 飞船兼容

先记录 VGE-COMPATIBILITY.md，再实现按 packageId 启用的动态适配，不新增 VGE/VEF 程序集引用或 modDependencies。用户确认保留 VGE 单活动引擎；无 VGE 的原生多船不变。新增 gravjumper / gravhulk 识别、两处设施筛选、subscaffold 容量、全甲板发电负荷、彩色真空屏障顶编码与投影、仪式最终执行前配额/分离确认、第二章逻辑空间干扰。修复 VEF 阴影 transpiler 的执行顺序。VGE 选目的地只打开可取消仪式窗口，因此不在原生 CheckConfirmSettle 回调预占额度。

原生 024545 的 49/26/10、Chapter 1 025241 的 43/5、两章 025425 的 47/5 游戏断言通过，errors=[]。真实 VGE 管网 200 单位燃料按其 provider 扣至 192.5；空网、远程锁、地面干扰器、非法目的地均保留拒绝。原生起飞/降落/返航单独回归，不把 VGE 通过当作原生支持证据。初始失败、夹具修正、源码副本、范围与复现命令见 VGE-COMPATIBILITY.md。完整 VGE 仪式 UI / 舰战任务未逐场验收。

## 2026-10-21：手动换层俯视升降

新增独立 GPU 过渡控制器：320 ms 实时时钟、SmoothStep、4% 中心缩放、1% 竖向位移、实时目标层与旧画面交叉淡入；固定 UI 不参与。手动入口改用 Request，LayerView.Switch 保持同步，自动定位立即取消动画。连续请求合并最新目标并复用当前合成；最多三张临时纹理，无存档动画状态。显示页新增默认开启的中英开关。

最初显式 Camera.Render 捕获遗漏 PersistentDraws，实际帧图暴露光照跳变；改用原生 MapRenderFeature 完整帧提交，原生 RenderItems 传递背景和太阳方向。合成继续使用独立禁用 Camera 和私有 Transparent 材质。捕获失败退回同步操作并释放资源。

新建与重载、地下/上层双向、原版按钮/快捷键/地图输入、不同 UI 比例、曲线测试及性能证据集中在 TRANSITION-VALIDATION.md。所有游戏运行使用独立测试数据目录；生产代码不截图读回或落盘，逐帧导出仅由显式启用的诊断场景执行。

## 2026-10-21：原版跳跃背包 / 蝗虫装甲跨层

落实研究方案，新增 Navigation/JumpGeometry、JumpService、LayerJumpFlyer、JumpPatches 和独立载体 Def。只接管分层地图的 Verb_Jump 跨层分支；同层和能力跳跃不改变。原版预热还会调用通用 TryFindShootLineFromTo，因此共享入口显式识别跳跃，不能只改 CanHitTargetFrom。逻辑海拔、三维射程、人物净空与整段扫掠共用同一曲线；固体楼板/屋顶/地表不因楼梯连接而打开。

真实游戏暴露落地恢复队列后再次覆盖查询导致 Goto 被 Wait_Combat 替换，修为原生落地完成后一次性恢复。携带物保留原生 PawnFlyer 所有权链，交接期间短作用域保护清理；载体放置失败不能销毁未落地货物，人物与残留货物均有工作中的重试路径。人物选中状态也提供重试按钮。

保留所有失败目录。最初银堆未真正拾取是诊断误用 bool TryStartCarry，修为数量重载并先验证真实持有；预热屋顶最初放在轨迹尚未接触的 x=51，改到实际相交的 x=53 并增加受阻前置检查。未放宽物品身份、数量、队列身份或穿板断言。细节见 JUMP-VALIDATION.md。

最终全 DLC 中文场景 58/11、纯几何 58，以及 Core 原有射击 14/9 通过；实际检查选点射程圈、轨迹、中文状态、飞行与重载落地截图。新布局不迁移；在途新载体依赖新 DLL。用户说明与范围见 JUMP-PACK.md。

## 2026-10-21：降落物品、Pawn、植物对齐原版

核实当前 1.7.4984 的 IsValidCell 与 SpawnFoundations/SpawnTerrain/ClearThingsAt：原版不按 Item/Plant 类别拒绝，Pawn 仅 Humanlike 或敌对时阻挡；实际基架格清理可销毁实体。用户确认后移除 RimLayers 四处全物品/全 Pawn 拒绝，统一 BlocksLanding，保留显式禁止标记和既有建筑保护。植物无需额外放行；普通草/树/作物继续遵循原版，特殊禁止标记仍有效。未增加竖直柱清理，也未更改原版放置流程。

新增 ship-landing-items 独立场景，真实三层镂空载荷在地面与旋转高层放置，验证覆盖格清理、镂空/船外保留、只读预览、特殊标记、人形/敌对 Pawn 拒绝及原货物身份。最终 v1-ship-landing-items-20261021-193127 为37/37、errors=[]，Release 0警告/错误。中间夹具上层设置和地图边缘失败、源码位置、复现命令记入 SHIP-COLONY-VALIDATION.md；原生结论与成员证据补入根 docs/odyssey-gravship.md / sources.md。运行与并行 Cargo 任务串行协调，保留其注册、参数及船舶吊篮夹具。
## 2026-10-22：坍塌后持续卡顿与二楼连带三楼回归

在隔离的 250×250、20 名工作殖民者、全 DLC 场景复现秒级停顿。坍塌与恢复队列已空，热点实际是搬运工作验证为拒绝容器反复完整寻路。StorageQueries.cs 改为先执行原生接受、阵营、禁用、预订与可达性检查，再计算分层路径分数；保留优先级、等分顺序、吊篮端口成本及同步评分不可用时的旧哨兵分数，无存档格式改动。

最终连锁场景 27 项、原卡顿检查点重放 22 项通过；二楼和三楼共 128 格各结算一次，两个原始银堆身份和数量不变。同一坏档塌后 P99 从 2010.783 ms 降至 13.308 ms，重铺后 5.984 ms。吊篮生成/重载 22/20，飞船生成/落地/落地重载返航 62/35/15 通过，errors=[]；飞船旧夹具按现有物品落地与真实自然地板规则修正，未改生产规则。Release 0 警告/0 错误，本地 DLL 已更新，需完全重启游戏。详细证据、原始失败和复现命令见 [COLLAPSE-STUTTER-FIX.md](COLLAPSE-STUTTER-FIX.md)。
## 2026-10-23：Dubs Mint Minimap 兼容

用户报告 minimap 被拉长、失去定位作用并伴随警告。静态核对两端后确认：minimap 把 `Map.Size`/`Map.AllCells` 当作整图，而分层地图的 `Map.Size.z` 是存储高度（120×120 三层为 408），因此纹理按 120×408 分配、宽高比 3.4:1、所有标记被 `LerpDoubleClamped` 夹到带边。所谓“警告”其实是每帧异常：minimap 的 `Section.TryUpdate` 前缀用 `cellIndices.CellToIndex(cell)` 写 `dirtyGirls`，该数组只在 null 时按 `AllCells` 重建，扩层后仍为 14400 项，新带 z=136 的索引为 16320，越界抛出；原版 `Section.TryUpdate` 只对 `Regenerate()` 有 try/catch，不含 Prefix，`Refresh=true` 永远执行不到，于是每帧复现直到关闭小地图。

新增 Compatibility/DubsMinimapCompatibility.cs，按 packageId 条件启用，不改 minimap 程序集、`Map.Size` 或存档。核心是线程作用域翻译：仅在 minimap 自身调用栈内把 `Map.Size`/`AllCells`/`Area`/`Thing.DrawPos`/`CameraDriver.rootPos`/`cellIndices.CellToIndex` 读成当前查看层的本地坐标，`procCell` 单点回译到存储坐标，其余原生网格查询不受影响；`Harmony_TryUpdate.Prefix` 只放行完全落在查看层内的 section；相机位置与两个 `JumpToCurrentMapLoc` 重载分别减/加层偏移；检测到查看层变化后强制 `UpdateMapTex` 重建；`RL_OpenAir`/`RL_Isolation` 因 `dontRender` 采到缺失纹理洋红，改为返回扁平中性色。

两处 Harmony 陷阱：静态目标上 `__instance` 绑定 null 实例（改位置绑定 `__0`）；同一补丁类内的两个重载参数名不同会抛 `Parameter "cell" not found`，且因发生在 `RimLayersMod` 构造函数里连带整个 mod 实例化失败，必须拆成独立补丁类。

新增 Diagnostics/V1Minimap.cs 与 `Run-V1.ps1 -Scenario minimap -Minimap`。最终 v1-minimap-20261023-053834 为 16 项断言通过、failures=0、errors=[]；截图确认 120×120 正方形小地图、暗色中性空域、相机框居中跟随当前层。回归 `-Scenario navigation` generate 12 / load 4 通过，确认未装 minimap 时兼容层完全惰性。Release 0 警告 / 0 错误。详细证据与限制见 [MINIMAP-COMPATIBILITY.md](MINIMAP-COMPATIBILITY.md)。

## 2026-10-24：地下生态（砾石土壤、蒸汽间歇泉、洞穴植物）

在原地下生成器上补出类似微光丛林的地下生态，全部复用原版 Def，不新增也不 patch 任何 XML（用户硬约束：def 按原版规则、不补）。新增 `Source/RimLayers/Underground/UndergroundEcology.cs`：`PlanSoil` 只取洞穴地面格，用 `EnvironmentMath.Band` + `QuantileThreshold` 按覆盖率取前 35% 铺原版 `Gravel`（肥力 0.7、带 GrowSoil）；`SpawnGeysers` 用原版 `SteamGeyser`，按层面积换算 0.85/万格，要求 2×2 全在空旷洞穴地面、彼此 ≥20 格；`SpawnFlora` 走 `WildPlantSpawner.SpawnPlant`（public static，绕过 Fertility/WildPlantSpawner 的全部 gate），产物是正常可生长可收获的 Plant。植物池 Glowstool/Agarilux/Bryolux/Plant_Timbershroom + Odyssey 的 Plant_Psilocap/Boomshroom/Plant_Willowgill，缺 DLC 由 `GetNamedSilentFail` 静默剔除。

首轮 `v1-underground-20261024-064444` 5 项失败，两个真根因：①`HiveUtility.SpawnExtras` 在 Odyssey 下会给每个虫巢铺约 30 格 `InsectSludge`（肥力 0），先种植物会被顶掉（记录 1491 砾石、实际剩 1464，另有 3 株草落在石面岩上）→ 改为 afterRegions 里虫巢先、植物后，并只接受仍是砾石的空格，记录在 flora 回调里重扫真实值；②`SteamGeyser.destroyable=false`，`MountainService.RemoveSpawned` 的事务回滚 `t.Destroy()` 报 `Tried to destroy non-destroyable thing` 且没移除 → 不可销毁实体改走 `DeSpawn(DestroyMode.Vanish)`。

`v1-underground-20261024-070014` 生成 23/0、读档 28/0 通过，detail `record soil=1459 flora=89 geysers=1; gravel=1459; plants=89`。设置侧新增 Ecology 组（三参数 + 逐种植物权重，`EnvironmentSettings.EcologyEnabled/EcologySoil/EcologyGeysers/EcologyPlants/PlantWeights`）。`Tests/EnvironmentTests` 增至 222927 项，13 个离线套件全过，Release 0 警告/0 错误。细节见 [UNDERGROUND.md](UNDERGROUND.md) 与 [ENVIRONMENT-IMPLEMENTATION.md](ENVIRONMENT-IMPLEMENTATION.md)。

同期修复设置 UI harness：`SettingsMenu` 改成六页 3×2 网格后，`Diagnostics/V1SettingsUi.cs` 仍按旧单行 `+42` 基准注入鼠标坐标，6 项原生控件断言假失败；改坐标表并加 Environment 页取证链，`v1-settings-ui-20261024-073823` 23/0 通过。另记一条并发教训：凡带 `Build failed`/`No runtime results` 的 V1 失败都不可信，跑前确认 `tasklist | grep -i rimworld` 为空。

## 2026-10-25：VGE 兼容收尾（管道改连接器方案 + 五个原始问题全绿）

跑通 VGE 兼容的五个原始问题（控制台跨层误报、船板不可拆、管道不跨层、普通建筑跨层连不上引擎、全部引擎算引擎）后，唯一长期未收敛的是管道。原先的实现想手动驱动 PipeSystem 的网络生命周期：替换 `NeighbourThingsCardinal` 补纵向邻居，再按列 `unregister/register`。这条路走不通，原因是 `PipeNet.Destroy()` 和 `CompResource.PostDeSpawn` 都不清理各 comp 的 `PipeNet` 引用，逐 comp 重建会把已合并的两层重新拆散（`PipeSystemDebug` 日志里能直接看到 `Network(s) number: 2`）。

改成显式连接器方案后一次成型：新增 `RL_AstrofuelPipeRiser` / `RL_OxygenPipeRiser`（`Patches/VGE_PipeRisers.xml`，用 `PatchOperationConditional` 检查 `VGE_AstrofuelNet` / `VGE_OxygenNet` 存在性来注入，因为 `PatchOperationFindMod` 比的是显示名而不是 packageId，写 packageId 会静默永不匹配）。接头是 `PipeSystem.Building_Pipe` + PipeSystem 自己的 `CompProperties_Resource`，所以合并/拆分全由原生 `RegisterConnector` 完成；`PipeLinks` 只负责给接头补相邻层同 (x,z) 邻居，跨层资格是“对面那层也有接头”，不看船归属，船外地面同样可用。同时删掉 `RepairColumn`、`Flush`、`SuppressRepair` 全部死代码。

`Gravship..ctor` 那个从 run7 一直存在的 `NullReferenceException` 最终定位为**测试夹具位置冲突**：`VGE_PilotBridge` 是 5×3，占 `x78..82, z74..76`，而测试把 filler 货和 `VGE_GravshipStructuralBeam` 分别 spawn 在 `(81,76)`、`(81,75)`，都在 bridge footprint 内；`GenSpawn` 把 bridge 挤掉后 VGE 的 `Gravship_AddThing_Patch` 就没机会设置 `pilotConsole`，`DetermineLaunchDirection` 里 `pilotConsole.Rotation` 直接空引用。移到 `(81,77)` / `(77,74)` 后消失。

回归抓到一处我引入的真回归：`ValidSubstructure` 拓宽对原生多船路径是错的（`native-footprint-remains-core-deck-with-logical-cross-layer-ownership` 要求它只含引擎所在层）。改为按 `VanillaGravshipCompatibility.Active` 门控——未装 VGE 时保持原生单层，装了才返回全甲板，因为该补丁的唯一消费者就是 VGE。教训：改了共享属性就应同时跑目标场景和一个反例场景。

`gravship -NativeShip` 的 `legal-rotated-ground-origin-accepted` 失败用 `git stash` 做了基线对比，基线同样失败，确认为**既有问题**，与本次工作无关。

最终验证全部 0 失败：VGE Ch1 generate 95 + load 5；Ch2 generate 98 + load 5（含三个 Chapter 2 专属断言）；`ship-structure` 55；`structure` generate 32 + load 16。产物目录 `v1-ship-vge-20261025-220928`、`v1-ship-structure-20261025-221712`、`v1-structure-20261025-223241`。Release 0 警告 / 0 错误。

夹具设计约束（后续改这些测试必读）：VGE 引擎把结构容量卡在 100 格，船体不能超过 7×7；而 `VGE_GravhulkEngine` 是 5×5，几乎占满 7×7 船体。所以燃料/管道夹具只能放在**船外地面**，靠同层管道与接头相连。另：`CompResource.RemovePipes()` 会销毁接头自己 footprint 上的管道，接头不能和管道同格。

## 2026-10-26：实机反馈「2 层控制台/推进器仍提示未连接引擎」

实机（玩家 250×250 存档）报告 1 层放引擎、2 层放 VGE 控制台与推进器仍显示「未连接至逆重引擎」，而自动化夹具一直全绿。定位到两个独立原因。

**一、层存储跨带距离撞上 VGE 的 `maxDistance`。** `LayerLayout.CalculateStride(height) = ((height+17)/17)*17`，层存储是 `z = layer*Stride + z`，所以相邻两层在存储坐标里相差正好 `Stride`。120 地图 stride 136，250 地图 stride 255。VGE 驾驶台/驾驶桥/副驾驶台的 `maxDistance` 是 200，而原生 `CompGravshipFacility.CanBeActive` 用 `IntVec3.InHorDistOf`（只看 x/z）遍历引擎，遇到**第一个**超距引擎就直接 `return false`。于是玩家的 250 图上只要引擎不在同一层，所有设施恒判「未连接」；夹具跑 120 地图则全部免费通过——这就是「夹具全绿、实机全红」的盲区。

**二、RimLayers 自己的门是失败关闭的。** `ShipFacilityLinkOwnership` 在 `Ships.Belongs` 返回 false 时硬 `__result=false`，而 `Belongs` 依赖船体缓存刷新、非 Ambiguous、且设施 footprint 全在 `ship.Cells` 内；`ShipFacilityActive` 更用 `InField`（引擎 footprint 半径，`VGE_GravjumperEngine` 只有 7.9）当活跃判据，比原生 `OnValidSubstructure` 严得多。任何缓存未刷新或 footprint 跨甲板边的情况都被静默翻译成「未连接」。

修法是把六个门全部改成**只在原生为 false 时放宽**的 Postfix：`ShipFacilityActive`、`ShipFacilityLinkOwnership`、`ShipFacilityCandidates`、`ShipLooseConnection`、`ShipFacilityPlacement`、`ShipLogicalFootprint`，都以 `if(__result) return;` 开头。活跃判据换成「脚下整块 footprint 都是本船 substructure」+「该格不属于另一艘船」，不再依赖船体缓存，也不再用 7.9 的 footprint 半径冒充 200 的设施距离。`ShipFacilityCandidates` 在逻辑结果为空时交回原生，避免用空列表覆盖正确的原生候选。另加 `ShipFacilityTrace`：站在分层船甲板上但 `LinkedBuildings` 为空的设施打一次性 Warning，带上船体状态、linkable 判定、CanLink、Belongs、逻辑距离，这类故障以后不再静默。

**推进器另有一处必然阻塞。** `CompGravshipThruster.IsBlocked` 把尾焰范围内**任何** substructure 格当作障碍。单层船上推进器挂在船体外沿，所以永不触发；分层船的上层甲板本身就是 substructure，于是所有甲板推进器 `CanLink()` 恒为 false → 永不连线 → `ThrusterNotConnected`。`ShipThrusterDeckBlock` 只保留「尾焰压到**别的**船的甲板」这一种阻塞，`blockedBy`（挡风建筑）与户外要求完全不动。

**夹具必须坐在出问题的参数区间里。** `ship-vge` 现在默认 `-MapSize 250`，并新增断言 `*-fixture-reproduces-live-deck-gap`（要求控制台与引擎的存储距离大于 `maxDistance`）。没有这条断言时，小地图会让所有跨层断言免费通过——正是这个盲区让问题多活了两轮。

验证（250 地图）：VGE Ch1 generate 98 项 / 0 失败（`storage dz=-255 / maxDistance=200 / stride=255`，跨层控制台 `links=1`、`CanBeActive=True`，燃料 `shared=250`），load 5 项 / 0 失败；产物 `Tests/Artifacts/v1-ship-vge-20261026-065945`，截图确认 Floor 3 上层的旋转驾驶台、货物与洋红色真空屏障顶。Release 0 警告 / 0 错误。

教训：兼容补丁「替换」原生判定时，每多一个前置条件就多一条让原生本来正常的场景静默坏掉的路径；改成「只在原生为 false 时放宽」的 Postfix 无法让原生行为变差。

**收尾：第七个门也改成 Postfix。** `Environment/FacilityPatches.cs` 的 `LayerFacilityScopePatch`（`CanPotentiallyLinkTo_Static`）原来是 Prefix，引擎分支直接 `return true` 放行、非引擎分支在 `!PowerLinks.SameLayer` 时硬 `__result=false`。现在拆成「Prefix 只管非引擎的同层规则 + Postfix 只在原生为 false 时放宽」，放宽条件是 `props.linkableBuildings` 含该引擎 def、非 `mustBePlacedAdjacent*` 系列、且**解码后**的逻辑距离落在 `[minDistance, maxDistance]` 内。注意为什么放宽 `maxDistance` 拒绝是安全的而放宽 `minDistance` 拒绝不是：存储距离恒大于等于解码距离，所以原生因 maxDistance 拒绝时逻辑距离确实可能合格；反过来原生因 minDistance 拒绝说明存储距离偏小，而逻辑距离只会更小，不可能变合格。

**顺带删掉一处冗余接管（后又部分补回，见下段）。** `Ships/ShipCapturePatches.cs` 里 `LayerShipOnboardCell`（`Building_GravEngine.ValidSubstructureAt` 的 Prefix）被整块删除。它本是「替换」型：`Cells.Contains(cell) && InField(cell)`，而 `InField` 受引擎 footprint 半径约束（`VGE_GravjumperEngine` 只有 7.9），设施却允许 `maxDistance 200` —— 离引擎远的控制台会白丢发射按钮。而且当时判断它根本不需要：`ValidSubstructure` 拓宽到全甲板后，原生 `ValidSubstructureAt` 就是 `ValidSubstructure.Contains(cell)`（`/tmp/rwdec/RimWorld/Building_GravEngine.cs:348`），上层控制台交互格天然为真；不装 VGE 时又必须保持单层语义，没东西可放宽。

**删除判断错了一半：`ValidSubstructure` 与 `ValidSubstructureAt` 是两个契约。** 下次重跑原生 `ship-structure` 时冒出 5 条失败，全是「上层乘员不被当成在船上」：`upper-pawn-native-onboard-cell-is-recognized`、`native-launch-stage-accepts-boarded-crew-deck-1/2`、`native-launch-stage-waits-for-upper-deck-visitor-to-leave`、`native-upper-deck-gotoship-finish-keeps-required-passenger`。原因是我把 `ShipEngineDeckView`（`ValidSubstructure`/`ValidSubstructureNoRegen` 的 Postfix）在 `!VanillaGravshipCompatibility.Active` 时提前返回了——那是刻意的，因为原生 capture 与 `native-footprint-remains-core-deck-with-logical-cross-layer-ownership` 都要求原生模式下那个 HashSet 只有核心层。于是在无 VGE 的原生路径上，`ValidSubstructureAt` 退回单层，而它的原生消费者全是**走到 Pawn 位置**的判定。

正确分工：**HashSet 按配置区分，谓词恒跨层。** `ValidSubstructure`（`Gravship..ctor` 喂给 `RemoveGravshipTerrainUnsafe`，那条原版操作是平的）在原生模式下必须保持单层；`ValidSubstructureAt` 回答的是「这格是不是我的船体」，原生消费者 `StageEndTrigger_RoleArrivedAndSpectatorsOnSubstructure.ArrivedCheck`、`JobDriver_BoardOrLeaveGravship`（`CheckFailedToBoardOrLeave` 里还有一段绕过谓词直接读 HashSet 的转译，由 `ShipBoardingMembership` 负责）、`GravshipUtility.IsOnboardGravship_NewTemp` / `ConsideredKidnapped`、`CompPilotConsole.CanUseNow` 全都只要谓词语义。自己船的 2 层甲板上站着的 Pawn 就是登船状态，和引擎在哪层无关。

补回的 `LayerShipOnboardCell` 是 **fail-open Postfix**：`if(__result) return;` 之后才用 `ShipStructureService.Core(engine)`（Spawned/!Destroyed/引擎 def/玩家）与 `Ships.Get(engine) is {Ambiguous:false} s` 判一次 `s.Cells.Contains(cell)`，因此绝不可能把原生已为真的判定改成假，也彻底去掉了 `InField` 那个 7.9 半径。递归安全：船体缓存 `ShipStructureService.Refresh` 全程不碰原生 `ValidSubstructure`（只读 `terrainGrid.FoundationAt` 与自己的 `claims`），`Get()` 又是脏标记门控的。

教训：拓宽一个原生答案之前先分清它是**集合**还是**谓词**。集合往往被灌进原版的平面操作（capture、地形移除、bounds），必须保持原样；谓词才承载新语义。把两者当成一件事，就会在「桥掉集合拓宽」的同时把谓词一起丢掉。

**`GetNamed` 在 MayRequire 缺失时抛异常，而且抛在地形查询内部。** 修完谓词后重跑五个场景，`structure`（**不加载 Odyssey**）冒出 8 条 `Failed to find Verse.TerrainDef named RL_ShipStructuralFloor`，并连带 3 条拆解断言失败。`RL_ShipStructuralFloor` 带 `MayRequire="Ludeon.RimWorld.Odyssey"`，可取它的 `ShipDeckTerrain.Def` 用的是会抛的 `DefDatabase.GetNamed`，而引用者 `ShipFoundationRemovalAvailability` 是 `TerrainGrid.CanRemoveFoundationAt` 的 Prefix——**每张地图每次地形查询都跑**，`foundation != ShipDeckTerrain.Def` 这句在没有 Odyssey 时直接抛。改成 `GetNamedSilentFail` + 缓存 miss，`Install` 在 def 缺失时变 no-op。教训：任何 `MayRequire` 门控的 def 都不得用 `GetNamed` 从**跨配置加载**的补丁里取，因为这类补丁的安装时机与 def 是否存在无关。

另外那 3 条拆解断言失败有一半是**我自己的参数错误**：`Run-V1.ps1` 对 `structure` 默认只加载 `ludeon.rimworld`，而上一轮绿的那次带了 `-AllDlc`。跑原生场景时先确认 `activeMods` 里到底有没有 Odyssey，再判断是产品红还是夹具红。

**最后一版代码的完整验证**（谓词 + def 解析 + 双边成员资格三处落地之后）：`ship-structure` generate 55、`structure` 32 + 16、`gravship` 53 + 26 + 8（landed-load）、VGE Ch1 generate 100 / load 5、Ch2 generate 103 / load 5，全部 0 失败、`errors=[]`；产物 `v1-ship-structure-20261026-205515`、`v1-structure-20261026-205600`、`v1-gravship-20261026-204335`、`v1-ship-vge-20261026-205658`、`v1-ship-vge-20261026-205825`。日志 `0.1.5 loaded (367 patch classes)`。

**第三层根因：成员资格是单边的。** 上面七道门全部改成 fail-open 之后，实机仍然有一条控制台连不上，日志给出了决定性证据（`Player-prev.log:75789`）：

```
WARN [RimLayers] Gravship facility has no engine link: PilotConsole at (131, 0, 406)
  engine=VGE_GravjumperEngine@(134, 0, 149) ship=cells=198
  allowed=False canLink=True belongs=True distance=3.6/200
  loose=False engineFacilities=0
```

`distance=3.6/200` 说明距离完全够，`canLink=True`、`belongs=True` 都正常，唯一失败的就是 `allowed=False`——也就是引擎侧 `linkableFacilities` 的成员资格。原因是**成员资格天然单边**：VGE 的 `Patches/GravEngine.xml` 只把自家设施追加进原版 `GravEngine` 的清单，从来不把原版 `PilotConsole` 加进 `VGE_GravjumperEngine` / `VGE_GravhulkEngine`；而原版 `CompAffectedByFacilities.PotentialThingsToLinkTo` 的枚举完全由引擎的 `linkableFacilities` 驱动。所以「原版控制台 + VGE 引擎」这对玩家真实会用的组合永远进不了候选。

修法是 `Ships/ShipFacilityPatches.cs` 新增共享谓词 `ShipFacilities.LinksToDef(props, engineDef)`：设施 def 的 `CompProperties_Facility.linkableBuildings` 含该引擎 def，**或**含任意被识别为引力引擎的 def，即视为可连（另外 `EngineFacilityDef` 兼容 VGE 那种「只声明 linkableBuildings、不声明具体引擎」的普通设施）。四个调用点统一改用这一个谓词：`ShipFacilityCandidates`、`ShipFacilities.IsEngineFacility`、`LayerFacilityScopePatch`、`LayerFacilityActivePatch`。

决定性验证：`*-vanilla-console-links-to-this-engine-across-decks` 同时打印 `links=VGE_GravjumperEngine; declares=GravEngine; inEngineList=False; canLink=True`——原生清单里确实没有它（`inEngineList=False`），链接仍然成立，证明放宽发生在 RimLayers 这一侧，而不是靠改 VGE 的 Def。

**最终验证（双边成员资格之后）**：VGE Ch1 generate **100** / load 5，Ch2 generate **103** / load 5，均 0 失败、`errors=[]`，日志 `0.1.5 loaded (366 patch classes)`、`Gravship facility has no engine link` 计数 0；产物 `v1-ship-vge-20261026-175351`、`v1-ship-vge-20261026-175616`。Ch2 那轮另过 `chapter2-remote-lock-still-vetoes-launch`、`chapter2-ground-jammer-blocks-upper-deck-landing`、`chapter2-jammer-removal-restores-landing`。截图复核 `vge-restored-platform.png`（Floor 2 空白板 + 顶部已拆解碎片）与 `vge-landed-upper-deck.png`（Floor 3 上旋转后的驾驶桥、银色货物、洋红真空屏障顶、地板屋顶正确）。

**第四层根因（真正的根）：链接有两个方向，只修一边等于没修。** 上面双边成员资格让 `engineFacilities` 从 0 涨到 **5**（推进器与 VGE 自家设施都连上了），可原版 `PilotConsole` 仍然不在其中。实机新日志：

```
allowed=False linksTo=True engineCanLinkTo=True canLink=True belongs=True
  distance=3.6/200 engineFacilities=5
```

原生创建链接实际上有**两条彼此独立的路**：

| 方向 | 枚举入口 | 枚举的清单 |
|---|---|---|
| 引擎侧 | `CompAffectedByFacilities.PotentialThingsToLinkTo` | `engine.linkableFacilities` |
| 设施侧 | `CompFacility.LinkToNearbyBuildings` | `facility.Props.linkableBuildings` ← 由 `CompProperties_Facility.ResolveReferences` 反查生成 |

前几轮修的统统是**引擎侧**。而 `CompFacility.LinkToNearbyBuildings` 走的是 `Props.linkableBuildings`，那是「哪些 def 的 `linkableFacilities` 点名了我」的反查结果——原版 `PilotConsole` 只被原版 `GravEngine` 点名，所以它那份清单里**根本没有** `VGE_GravjumperEngine` / `VGE_GravhulkEngine`。设施自己去找引擎时永远看不到 VGE 引擎。

**这就是「夹具绿、实机红」的真正机制**，比层跨度那次更隐蔽：`GenSpawn` 生成设施时，引擎侧的 `RelinkAll` 碰巧把它带上；而**读档**走的是 `PostSpawnSetup → LinkToNearbyBuildings`，恰好是没修的那条。缺陷只存在于读档路径，正好是玩家每一次进游戏的路径。

修法 `Ships/ShipFacilityPatches.cs` 的 `ShipFacilityOwnCandidates`：在 `LinkToNearbyBuildings` 执行期间把各引力引擎 def **临时补进** `linkableBuildings`，`Postfix` 立刻还原（该字段被整个 ThingDef 共享，也会驱动原生的连线预览，不能留残留）。

关键约束：**不重写原生循环**。`UnlinkAll`、`CanLinkTo` 闸门、`maxSimultaneous` 容量、`Notify_NewLink`、以及设置 `CompGravshipFacility.engine` 的 `OnLinkAdded` 事件全部保持原生；追加 def 只是让引擎进入候选，最终仍由 `CanPotentiallyLinkTo_Static`（已由 `LayerFacilityScopePatch` 放宽）与 `IsValidFacilityForMe` 裁决，所以不可能凭空造出原生会拒绝的链接。

**对照实验删掉了一个多余补丁。** 最初我还加了 `ShipFacilities.RepairLinks`（在船体缓存刷新时做一次确定性的两面对账）。但新断言读的是 `Ships.Tick()` 之后的状态，而 `RepairLinks` 也在 tick 里跑，两套机制会互相掩盖。于是做对照：把 `RepairLinks` 关掉重跑，**102 项仍然 0 失败、respawn 断言仍 `before=True; after=True`**——证明设施侧清单修复本身已经足够。该补丁随即删除，没有留在工程里。教训：修一个现象时若叠了两层机制，必须各自单独验证；否则交付的是「至少有一个在起作用」而不是「每个都有用」。

新断言 `*-vanilla-console-survives-a-respawn-like-a-save-load` 用 despawn + respawn 精确复现读档路径。它在修复前必失败——旧的 `*-vanilla-console-links-to-this-engine-across-decks` 抓不到这个缺陷，因为它只在引擎已连线的状态下 spawn。

**最终验证**：VGE Ch1 generate **102** / load 5、Ch2 generate **105** / load 5、`ship-structure` 55，全部 0 失败、`errors=[]`；日志 `0.1.5 loaded (368 patch classes)`、`Gravship facility has no engine link` 计数 0、异常 0；产物 `v1-ship-vge-20261027-022717`、`v1-ship-vge-20261027-022858`、`v1-ship-structure-20261027-023041`。提交 `310d7be`（源码）+ `11bdba6`（DLL，sha256 `607fa7a3…`）。

**夹具坑（会白烧一轮）**：`Gravship..ctor` 抛 `NullReferenceException` 不是产品缺陷。`VGE_PilotBridge` 是 5×3 占 `x78..82,z74..76`，而夹具把 filler 银和 `VGE_GravshipStructuralBeam` spawn 在 `(81,76)` / `(81,75)`，都在 bridge footprint 内，`GenSpawn` 把 bridge 挤掉 → VGE 的 `Gravship_AddThing_Patch` 没机会设置 `pilotConsole` → `DetermineLaunchDirection()` 里 `pilotConsole.Rotation` 空引用。移到 `(81,77)` / `(77,74)` 后消失。

## 2026-10-27：VGE 气燃料推进器「未连接引擎」是误报文案

实机反馈（`VF测试3`）：需要宇航燃料的小/大/巨型推进器显示「未正常运作: 未连接至逆重引擎」，电推进器正常。

**取证结论：链接是好的，油罐是空的。** 判定依据不是猜测，而是截图里同时存在的三行：

```text
未正常运作: 未连接至逆重引擎。
需要接入宇航燃料管道网络才能正常运作，且管道网络内至少要有一个宇航燃料贮罐。   ← VGE_DisabledNoFuel
PipeNet: 宇航燃料 Stored: 0 AvailableCapacity: 2050                            ← dev mode 专有
```

`AvailableCapacity 2050` 非零 ⇒ 储罐已注册进网络、容量聚合成立；`Stored 0` ⇒ 罐里没燃料。`AstrofuelPipeNet.PipeSystemTick()` 里 `HasFuel = Stored > 1f` → false → VGE 的 `CompGravshipThruster_CanBeActive_Patch`（Postfix）把 `CanBeActive` 压成 false。

原生 `CompGravshipThruster.CompInspectStringExtra` 最后一行：

```csharp
if(text.NullOrEmpty() && parent.Spawned && (LinkedBuildings.NullOrEmpty() || !CanBeActive))
    text += "ThrusterNotConnected".Translate();
```

`|| !CanBeActive` 是缺陷所在：**燃料耗尽与真的没链接打出完全相同的字**。玩家看到「未连接至逆重引擎」只会去查连接，而连接本来是对的。

电/气燃料的分裂也由此解释：`CompElectricThruster : CompGravshipThruster`（vge2dec）**继承**原生推进器，`CanBeActive` 走原生链；气燃料用 `VanillaGravshipExpanded.CompResourceThruster : PipeSystem.CompResource`，是完全不同的基类，才踩到 `CanBeActive` 这一支。

**修法**（`Ships/ShipFacilityPatches.cs` 的 `ShipThrusterInspectTrace`，原为无 `__result` 的纯 trace，现加 `ref string __result`）：

三个条件同时成立才动文案，缺一不可：
1. `LinkedBuildings` 非空（确已链接）；
2. `__instance.engine != null`（链接确实解析到了引擎）；
3. 该 thing 有 `CompResourceThruster` 且 `HasFuel == false`。

满足则从 `__result` 里**删掉**那行「未功能：未连接至逆重引擎」，并追加自己的 `RL_GravThrusterNeedsFuel`（英 `Out of astrofuel` / 中 `缺少宇航燃料`）。VGE 自己的 `VGE_DisabledNoFuel` 行本来就在（`ThingWithComps.InspectStringPartsFromComps` 会聚合所有 comp），所以玩家最终看到的是「缺燃料」而不是「没连接」。

真没链接时（条件 1 或 2 不成立）完全不动，仍走原生文案 + `ShipFacilityTrace.MissingLink` 诊断。

**一次被推翻的假设，记下来免得重犯**：我曾认为「`CompGravshipThruster` 的 override 不调 base，所以 VGE 的燃料行丢失」。错。`ThingWithComps.GetInspectString` → `InspectStringPartsFromComps` 会遍历**所有** comp 的 `CompInspectStringExtra` 再拼接，燃料行一直都在。真正需要的只是别让未连接那行喧宾夺主。

**新断言**（`Diagnostics/V1Vge.cs` 的 `CheckVgeFuel`）：
- `vge-dry-thruster-does-not-blame-the-engine-link` — 干罐状态下断言 `LinkedBuildings` 非空 **且** `!CanBeActive` **且** 文案不含 `ThrusterNotConnected`。
- `vge-fueled-thruster-inspect-is-clean` — 加燃料后断言 `CanBeActive` 且文案无 `ThrusterNotConnected`。

两条都用 `thruster.GetInspectString()` 取真实聚合文本，而不是直接调某个 comp，避免又只测到单层。

**行拼接坑（白白烧了一轮）**：原生 `CompGravshipThruster` 用 `stringBuilder.AppendLine()`，在 Windows 上是 `\r\n`。我第一版按 `'\n'` 切分后只过滤空串，被删掉的是最后一行时，剩下那行末尾的 `\r` 就裸露成了返回值结尾，随即被 `ThingWithComps.InspectStringPartsFromComps` 的 dev 检查拄住：

```text
RimWorld.CompGravshipThruster CompInspectStringExtra ended with whitespace: Max per gravship: 10\r
```

改成 `Split(new[]{"\r\n","\n"})` + `TrimEnd('\r')`。教训：**改写原生字符串结果时，换行符不是 `\n`**；只有 DevMode 下才会报，正式版会默默带个 CR 进 UI。

## Dubwise 五 mod 跨层兼容（DBH / DCH / Rimatomics / Rimefeller / Dubs Skylights）

> 完整方案与逐 mod 细节见 `Docs/DUBS-COMPATIBILITY-PLAN.md`。这里只记结论、坑与验证。

**四类需求的落点。** ①跨层端子：新增 `Compatibility/LayerPipePorts.cs` + `Patches/LayerPorts.xml`（10 个 `RL_*Riser` def，用 `PatchOperationConditional` 以对方 defName 存在性为门槛）。它们在运行时整体替换 `FloodFiller.FloodFill` 的 BFS，仅当出队格带 `CompLayerPort` 时额外向 `owner.Adjacent(cell,±1)` 扩展；`[ThreadStatic] depth` 只在四个 mod 的重建入口内为真，因此寻路/房间/气体洪泛完全不受影响。②地下水与石油只在地表/地下 LayerAllowed（`Elevation <= 0`）：`DubsWater.cs` / `RimefellerOil.cs` 在 mod 自己的 `Regen*` 之后按层清零 + `PlaceWorker` 前置拒绝。③天窗：`DubsSkylights.cs` 在 `RegenGrid()` 之后按 `owner.Sky.IsExposed(cell)` 清 `SkylightGrid`——这一处同时喂到 Dubs 的 `GlowGrid` Postfix、它的 LightingOverlay Prefix、DCH 太阳能锅炉和 DBH 反射读的 `skylights`，四个消费者一次修好。④扩层韧性：`LiveMapExpansion.Committed` + `GridCapacityRepair` 的 rank-2 支持 + `NestedGrids`。

**为什么必须换掉整个洪泛，而不是包 `passCheck`。** `FloodFill` 只对**出队格的平面四邻**调 `passCheck`，另一层的格子永远不会被问到；`extraRoots` 只在开场入队一次且**不经过** `passCheck`，而这四个 mod 的 `passCheck` 闭包正是做 `GridID`/`pipeNetRef`/`PipeGrid`/`PipedThings` 记账的地方——用 `extraRoots` 灌进去的格子永远不被登记，下次重建会再开一个 net。所以只能整体替换 BFS。

**扩层时按 cell 索引的缓冲必须在事务**内**扩好。** `EnsureElevation` 里 `map.mapDrawer.RegenerateEverythingNow()` 早于 `NotifyCommitted`，overlay `SectionLayer` 会先用旧长度数组读一遍（DBH `SectionLayer_Irrigation` 直接 `grid.Grid[index]`）。因此 `PrepareManaged`/`NestedGrids` 负责扩，`Committed` 只用来重算派生数据（重灌管网、重播种、重建 drawer 尺寸）。

**最费时的一处：`Verse.Area.innerGrid` 与「被组件私自持有、却已从 `AllAreas` 摘掉」的 Area。** 起初 7804 条 `IndexOutOfRangeException`，栈是 `BoolGrid.get_Item ← Area.Set ← MapComponent_Hygiene.SetWaterCellsCache ← DBH 的 TerrainChanged Postfix ← MountainService/UndergroundService.SetTerrain`。两个独立缺陷叠在一起：

1. `Area` 的 `innerGrid` 在 `Map.ConstructComponents` 阶段按**当刻 `map.Size`（= 地面矩形）**创建，既不是 `oldSize`（第二次扩层时已含上层带）也不是新尺寸，所以原来以 `oldSize` 为前置条件的修复永不命中。改为按 buffer 自身一致性判断（`Width==full.x && Height!=full.z`），拷贝长度取 `Min(arr.Length, Width*Height)`。
2. **DBH 的 `RefreshAreas()` 是失败广播式的**：三条 `RemoveAll` 都用 `x != cleanWater` 比较，于是 `Area_WaterDirty` / `Area_WaterOcean` 每建即被移除，而组件仍通过 `_dirtyWater`/`_oceanWater` 字段写它们。这类 Area 在 `areaManager.AllAreas` 里根本查不到（探针实测 `listed=False`），必须遍历 `map.components` 的 `Area` 类型字段才能找到。修完这两条，警告从 8200 直接归零。

教训：修「第三方网格没扩容」时，只要该对象**存在两条可达路径**（manager 列表 / 组件私有字段），就必然有一条是断的；探针打印 `listed=` 比读源码快得多。

**夹具自身的两个坑。** `Tests/Run-V1.ps1` 固定 `-Layers 2..3`，所以 `dubs` 必须加进 `LayerGeneration.V1Preallocated()` 的排除表，否则 `EnsureElevation` 全部 early-return（表现为 `65280 -> 65280`）。另外 Rimatomics 的 `HighVoltage`(x=56)/`controllerLoom`(x=60) 组超出旧测试台 `PrepareDubsDeck(30,30,24)` 的范围，冒出 8 条 `[RimLayers.Recovery] reason=UnknownBuilding`，改成 `(30,30,44)` 后归零。

**最终验证。** `Tests/Run-V1.ps1 -Scenario dubs -Dubs -AllDlc -TimeoutSeconds 900`：generate **66 项断言 / 0 失败**，`errors=[]`，`IndexOutOfRangeException`=0、`BoolGrid.get_Item`=0、`[RimLayers.Recovery]`=0；产物 `Tests/Artifacts/v1-dubs-20261026-085843`。关键值：层结构 `0,1,-1` → `0,1,-1,2`；`PipeGrid 48960 -> 65280`（rows=4, cols=65280）；水 `upper=0 / ground=10542`、深水 `0 / 9323`；石油 `0 / 484`（深油 `0 / 3628`，油田 5/8 个且 `RootCell` 不在上层）；10 组端子全部 `net-crosses` + `net-splits-without-the-riser`；天窗 `open-column-is-lit exposed=True`、`covered-column-is-not-lit exposed=False`。Release 0 警告 / 0 错误。

## Vanilla Expanded PipeSystem 跨层兼容（VCHE / VHGE / VNPE）

> 适配 Vanilla Chemfuel Expanded (VCHE)、Vanilla Helixien Gas Expanded (VHGE)、Vanilla Nutrient Paste Expanded (VNPE)。

**需求落点：**
① 跨层管道端子：新增 `Compatibility/VEPipeSystemCompatibility.cs` + `Patches/VE_PipeRisers.xml`（4 个 `RL_*PipeRiser` def：化合燃料 `RL_ChemfuelPipeRiser`、深层化学品 `RL_DeepchemPipeRiser`、螺旋气 `RL_HelixienPipeRiser`、营养膏 `RL_NutrientPastePipeRiser`）。继承 `BuildingBase`，`thingClass` 使用 `PipeSystem.Building_Pipe`，挂载 `PipeSystem.CompProperties_Resource` 与 `RimLayers.Environment.CompProperties_ResourcePort`。通过 `PipeLinks.RegisterRiser` 注册到跨层判定表，复用 PipeSystem 现有的 `NeighbourThingsCardinal` 垂直邻居补丁进行原生合并与拆分。
② 地下资源与深井泵层级限制：
- `PipeSystem.PlaceWorker_DeepExtractor.AllowsPlacing` 与 `VHelixienGasE.Placeworker_Pump.AllowsPlacing` 挂 Prefix，在非地表/地下层（`!DeepResourceScope.LayerAllowed`）直接拒绝并返回原版拒绝提示，阻断 2 层及以上蓝图放置。
- `DeepResourceScope.PlacementReport` 挂 Postfix，将 `VCHE_DeepchemPumpjack` 与 `VHGE_HelixienPump` 加入深层资源建筑白名单判定。
- `VHelixienGasE.HelixienGasHandler.CanScatterAt` 与 `CanSpawnAt` 挂 Prefix，在非地表/地下层直接返回 false，阻断螺旋气天然喷口在上层散落或生成。
③ 扩层与网格自愈：订阅 `LiveMapExpansion.Committed`，在地图扩层时自动触发 `PipeNetManager` 重算并复位。

**夹具与验证：**
`Tests/Run-V1.ps1 -Scenario vefpipes -VePipes`：28 项断言 0 失败，`errors=[]`。
- 地下资源限制：深层化合泵与螺旋气泵在上层结构拒绝与蓝图拒绝全部通过，地表层正常允许；螺旋气天然喷口在上层 scatter/spawn 均被正确拦截。
- 管道网络跨层：化合燃料、深层化学品、螺旋气、营养膏四套管网在双层分别放置管道与端子时，两层管道成功合并入同一 `PipeNet`；拆除端子后两层管网正确拆分为独立网络。
- 产物：`Tests/Artifacts/v1-vefpipes-20261026-132442`。

## 2026-10-26：载具升降机上下行打通（实机反馈右键上不了楼 / 上方下不去）

**反馈（实机）：** 载具坡道正常，载具能上下楼，但载具升降机站不正常——车辆右键上不了楼，上方也下不去。附带存档 `VF测试2`。

**存档诊断：** 全场只有 1 座 `RL_VehicleLift`（地表 storage `(104,0,146)`），上层没有第二座；`RL_VehicleDeck` 出现 0 次；`structuralCells` 399 格全在 layer 1，且完整覆盖该站 81 格足迹（x100–108 / z142–150）。即"地表站上方盖着一整块玩家结构楼板、地表站自家 81 格还压着 `RoofConstructed`"。

**根因（两处互锁）：**
1. `VehicleShaftExcavation` 只认向下：`LowerShaftStation` 带 `owner.Elevation(at.LayerId)>0` 空返回，`ShaftExcavationGizmos()` 对 elevation>0 直接 `yield break`，所以地表站只能与下方层配对，**地表→上层没有任何开凿入口**。
2. `VehicleFacilitySpace.Validate` 的上层分支要求 81 格地形已是 `RL_OpenAir`/deck 且下方无屋顶，而 `InstallDeck` 只把 `RL_OpenAir` 转成 deck，`CanDismantleSlab` 又清不掉内部 81 格洞——**上层站永远放不下**。
3. 结果 `ShaftIntact` 恒 false → `FloorDestinations` 空 → `VehicleGizmos` 静默 `yield break`，玩家看不到任何按钮也没有原因，正合"右键没反应"。

**修复：**
- `Vehicles/VehicleShaftExcavation.cs` 重写为**方向参数化**：新增 `ShaftPartner(direction)` / `ShaftDeckRect(direction)`（平台格永远取井道较高一侧的 9×9）、`shaftDirection` 存档字段（默认 `-1`，旧档按向下读）、`ShaftWorkAt(cell)`（天然岩 600、人造铺装/楼板 120）、`NextShaftCell`、`StartShaftExcavation(int)`；向下仍要求接收舱存在（`RL_VehicleDigNeedStation`），**向上允许井口先于上层建站打通**。提交顺序固定为 `StructuralCells.Remove` → `SetRoof(below,null)` → `SetTerrain(RL_VehicleDeck)`，避免屋顶变更把新井口排进塌落队列。向下的"人工屋顶须先拆"契约保留（`RL_VehicleDigRoof`），向上则是唯一允许消耗玩家楼板/地表屋顶的路径。
- `Vehicles/VehicleTraffic.Interface.cs`：`FloorDestinations` 改用 `ShaftIntact(...,powered:true)`，避免列出规划器会拒绝的楼层；`VehicleGizmos` 不再静默消失——车停在载车区内但无可达井道时给出**禁用态**"前往楼层"并附 `RL_VehicleNoFloorRoute` 原因。
- `Vehicles/Building_VehicleConnector.cs`：`DrawGhost` 把已成形的 `RL_VehicleDeck` 视为合法，不再把已开挖的井口画成红色。
- 文本键中英各 57 条（新增 `RL_VehicleDigStartUp` / `RL_VehicleDigCompleteUp` / `RL_VehicleDigProgressUp` / `RL_VehicleDigHelpUp` / `RL_VehicleDigNoLevel` / `RL_VehicleNoFloorRoute`）。
- 夹具从"手动预挖洞"改为**真实玩家路径**：`V1VehicleLift.BeginVehicleLift` 造 9×9 `RL_StructuralFloor` + 地表 `RoofConstructed`，断言上层站先被拒绝，再走"开凿上层井段 → 上层变 deck / 地表屋顶清除 / 移出 StructuralCells → 上层站可放置 → 整车往返"。

**验证：**
- 上层方向 `Run-VehicleGround.ps1 -Scenario vehicle-lift`（`Tests/Artifacts/vehicle-ground-20261026-225512`）：generate **23/23**、load **23/23**、recover **7/7**，三阶段 `no-runtime-errors` 全绿。
- 向下回归 `-Underground`（`Tests/Artifacts/vehicle-ground-20261026-225659`）：generate 20、first-load 20、excavate 19、recover 14，共 **73/73 / 0 失败**，地表→B1→B2 与拆站成真实井口等旧契约全部保持。
- Release `net10.0` 构建 0 警告 / 0 错误。

**顺带修复：** `Tests/Run-VehicleGround.ps1` 的退出码竞态——`Start-Process -PassThru` 在带超时的 `WaitForExit(ms)` 之后 `ExitCode` 可能仍为空，导致"Game exited with code :"误判失败；改为补一次无参 `WaitForExit()` 再读，并对空值放行。

## 2026-10-27：拆掉引擎换新的，全甲板变红、控制台发射不了

实机反馈（`VGE测试2`）：把引擎拆掉换个新的，结果地板全红，控制台也发射不了。提示 `禁用：驾驶台未连接逆重飞船`（`PilotConsoleInaccessible`）。

**存档取证。** `shipCellOwners` 里有 **198 格**，全部标着 `51239`；但存档里唯一的引擎是 `GravEngine57697`（`(137,0,147)`）。也就是说整艘 11×9 双层船体的每一格，都还挂在**一台已经不存在**的引擎的 `thingIDNumber` 上。`shipBuildTarget` 为 0，说明玩家也没有手动选中任何核心。

红色本身不是 RimLayers 画的：`GravshipUtility.ConnectedSubstructureColor = ColorLibrary.Cyan`、`DisconnectedSubstructureColor = ColorLibrary.Red`，`SubstructureGrid.CellBoolDrawerGetExtraColorInt` 只在「某个小人阵营的 `Building_GravEngine.ValidSubstructureAt(cell)` 为真」时给 Cyan，否则 Red。**红 = 没有任何引擎回答这一格。**

**根因（时序竞态，不是判断错误）。** `Refresh()` 的播种循环里：

```csharp
if(decks[l]!=0&&decks[l]!=engine.thingIDNumber){set.Clear();break;}
```

足迹里只要有一格被别人的 id 占着，这台引擎就**整台**放弃播种。新引擎的足迹压在旧 id 的甲板上 → 一台船都产不出来 → `Refresh` 空手而归 → 全红、控制台交互格 `ValidSubstructureAt` 为假。

而当时收养逻辑（`AdoptRebuiltCores`）**只在 `Tick()` 里**调用，`Tick()` 又开头 `if(!dirty)return`；偏偏 `Refresh()` 自己会清掉 `dirty`。于是同一帧里任何一次缓存读（放置 UI、屋顶重建、设施诊断）都会先把 `Building.SpawnSetup` 发出的 `MarkDirty()` 消费掉，`Tick()` 再进来时 `dirty` 已是 false，收养永远不执行，孤儿 id 就这样活进了存档。

**修法三处：**

1. **收养前移到 `Refresh()` 开头**，用 `private bool dirty=true,flushing,adopting;` 里的 `adopting` 做重入保护。现在**每个缓存重算都先收养**，所以第一个读者就会把地图修好，任何读者都不可能观察到过期 id。
2. **孤儿 id 必须继承、绝不清理。** 迷你化的引擎保留自己的 `thingIDNumber`，如果只是把这些 claim 删掉，玩家"收起来再放回去"就会丢半艘船。所以判定的是「这个 id 不对应任何**活着的**玩家核心」。
3. **判据从"足迹重叠"改成"沿甲板泛洪触碰"**（`NearestOrphan`）。玩家最常见的情形是 1×1 的 VGE 核心换成 3×3 的原版引擎——新足迹根本盖不住旧核心的格子。改成从新核心足迹出发、沿 `Deck()` 做有界（4096 格）泛洪，撞到第一个孤儿格就认领那一整套 id。存在多个孤儿时**取最近的**：一台核心只能继承一艘船，宁可认领最近的那艘，也不能因为"周围有两艘"就两个都不救（原本 `touched.Length!=1 → continue` 会让并排拆掉的两艘船一起全红）。

**夹具自己错了两轮，两次都值得记。** 第一版把替换引擎摆在 `(114,0,94)`，而旧引擎足迹是 `(113,93)` 起的 3×3——**两者重叠 2×2**。于是即使收养规则只在"足迹重叠"时才生效，这条断言也会绿。

第二版改成 `(110,0,90)`，仍然红，而且红得莫名其妙：日志里那台替换核心 `decked=False`。原因是 **`GenSpawn` 的 `IntVec3` 是足迹的「中心格」**（奇数尺寸正好是正中；偶数尺寸取中央 2×2 的左上格）——`(110,90)` 生成出来是 `(109,89)~(111,91)`，往左上岔出去一格，整个掉在我搭的 `CellRect(110,90,7,7)` 甲板外面，`Deck()` 自然为假，收养按设计跳过它。**是夹具的坐标谎报，不是规则的错。** 最终把甲板放大到 `CellRect(108,88,11,11)`（够放两台互不重叠的 3×3），替换核心摆 `(109,0,89)`，旧核心摆 `(113,0,93)`。

另一个自欺的断言：第一版写的是 `replacement.OccupiedRect().All(c=>ClaimAt(c)!=deadId)`，但**死核心的 claim 本来就覆盖整片甲板**（这正是要修的东西），所以它测的其实是"甲板上有孤儿 claim"的反面，恒假。改成直接比较两个 `CellRect` 是否 `Overlaps`，并在 detail 里把两个矩形都打出来。

补一条防呆：`replacement-fixture-does-not-overlap-the-dead-footprint` — 用自己的夹具先证明测的是真场景；`replacement-core-does-not-annex-a-distant-dead-hull` — 远处 `(140,0,90)` 放一个假孤儿 `999999`，泛洪不能顺手把它吞了。

这正是之前 VGE 那一轮踩过的同一个坑：**夹具必须落在失败的参数区间里**，否则测试通过只是因为条件恰好不成立。

**还有一条产品侧的竞态。** `GenSpawn.Spawn` → `SpawnSetup` 里 `Building.SpawnSetup` 就发出 `MarkDirty`，但 `SetFaction(OfPlayer)` 在它**之后**才调。`SpawnSetup` 内部会重建区域/屋顶，这些读取会先把 `dirty` 消费掉，于是缓存"干净地"算完，却少了一台还没成为玩家核心的引擎——收养再也不会触发。修法：`Get(engine)` 在缓存干净但不认识这台引擎、而它确实是玩家核心且足迹全在甲板上时，先 `MarkDirty()` 再 `Refresh()`。**"问我一个我不认识的核心"本身就是数据过期的证据**，比继续指望某个通知可靠。

这个自愈必须限次：`Get` 会被检查面板和放置 UI 按设施、按帧调用，如果一台核心**合理地**无法播种（它的足迹落在另一艘活船已占的甲板格里，播种循环会 `set.Clear()` 放弃它），无条件的 `MarkDirty` 就等于每帧全图重扫。`private readonly HashSet<int> probed` 只允许每轮 `Refresh` 重试一次——重试要么修好缓存，要么证明在状态变化前它本来就无解。

**新增断言（`Diagnostics/V1ShipRebuild.cs`，6 条）：** `replacement-fixture-seeds-every-deck-of-its-hull`（11×11 甲板 × 3 层，一台核心播种出 363 格）、`destroyed-core-leaves-its-deck-claims-behind`、`replacement-fixture-does-not-overlap-the-dead-footprint`、`replacement-core-adopts-orphan-decks-on-first-read`（**刻意不调 `MarkDirty`/`Tick`**，走玩家放置 UI 的同帧路径）、`replacement-core-does-not-annex-a-distant-dead-hull`、`replacement-core-restores-native-onboard-cell`。

## 2026-10-29：上下层对位建造按钮 + 开发者快速打通

**需求**：电力端子、竖直管道、电梯站点这类"上下两层同一格都建成才生效"的建筑，玩家得在两个楼层各画一次蓝图、靠肉眼对齐。加一个按钮，选中这一端就能把另一端排进蓝图队列。顺带要开发者模式下的"立即打通楼层"。

**主干做法**：`DefModExtension LayerPairProperties` 只声明配对关系（`partner` / `directions` / `companion`），按钮放在 `Thing.GetGizmos` 的 postfix 里统一产出。放下去的是**原生蓝图**（`GenConstruct.PlaceBlueprintForBuild` + `def.blueprintDef`），不是凭空生成的成品——另一端照样要花材料、被搬运、被施工。这一条是刻意的：自动生成的成品等于送玩家半个建筑，而且会绕过本 mod 的提交闸门。

`PlaceBlueprintForBuild` 自己**不做任何合法性检查**（反编译确认：只 `MakeThing` + `SetFactionDirect` + `GenSpawn.Spawn`），所以合法性全部前移到 `Check()`：层存在 → 目标带内每格 `HasSurface` → 无同 def 成品/蓝图 → 最后整条 `GenConstruct.CanPlaceBlueprintAt(..., godMode:false, ...)`。**先在查询里跑完再落一次实**，按钮的灰显原因和被拒时的消息是同一句话，两边不会漂移。

蓝图 def 由 `ThingDefGenerator_Buildings.NewBlueprintDef_Thing` 生成时会 `new List<Type>(def.placeWorkers)` 复制 placeWorkers，所以自动蓝图照样会经过 `PlaceWorker_LayerSupport` / `PlaceWorker_Cargo`——不用额外补校验。

**没做的两件事，都是故意的。** 一是不替玩家开辟楼层：需求写的是"确保层已创建"，新开的带是 `RL_OpenAir`，得先铺落脚楼面才有意义，那是楼梯 bootstrap 的活儿，绕过它就等于跳过挖掘节奏。缺层时按钮直接灰掉并提示"先造通往该层的楼梯或竖井"。二是吊篮**不产生蓝图**：它的存取口是 mod 自己拥有的结构件，主机造价已含两端，所以 `companion=true` 走"直接生成成品"，和主机自己完工时的行为一致。

**两个真踩到的坑：**

1. **`ThingDef.HasComp(Type)` 比的是 `comps[i].compClass`，也就是 `Comp` 类型，不是 `CompProperties` 类型。** 我先写成 `HasComp(typeof(CompProperties_PowerRiser))`，编译通过、单测通过、日志还照样报 `4 def(s) registered`——因为吊篮那 4 个 def 是靠 `CargoProperties` 命中的，竖直件的匹配全落空。只有游戏内场景才暴露：电力端子的按钮一个都不出现。改成 `typeof(CompPowerRiser)` / `typeof(CompLayerPort)` / `typeof(CompResourcePort)` 才对。**顺带发现竖直管道有两条独立实现**（Dubs 系用 `CompLayerPort`，Vanilla Expanded 系用 `CompResourcePort`），只登记一个会漏掉整族 riser。
2. **`source.def.rotatable && ...` 写反了语义**：不可旋转的建筑反而被改成 `Rot4.North`/`Rot4.East`。矩形占地要"长边同轴"，方形占地要沿用原朝向（保住交互格），判据应是 `!rotatable || size.x == size.z → source.Rotation`。

**夹具又自欺了一次。** 第一版 `ThingMaker.MakeThing(def, ThingDefOf.Steel)` 直接抛 `Log.Error`：`RL_PowerRiser` / `RL_LiftVertical` / `RL_CargoManualUp` 都**没有** `madeFromStuff`，传 stuff 是非法的。这些 `Log.Error` 被场景的 `Error()` 收进错误清单，把 `no-runtime-errors` 断言打红——而这跟功能一点关系都没有。**给不可选材质的 def 传 stuff 是夹具的错，不是产品的错。**

**验证**：`Tests/PairingTests` 127 项坐标换算断言（含"目标带只算一次"的反向断言）；`Run-V1.ps1 -Scenario pairing` 游戏内场景覆盖按钮可用性、原生蓝图、缺层/缺楼面拒绝、电梯对位、吊篮存取口补回、开发者按钮显隐与成品化。

测试过程中 `Run-V1.ps1` 那 14 条"赋值表达式无效"是**非 PowerShell 分析器的误报**（报的是 `param()` 块里的合法参数声明）。判据用 `[System.Management.Automation.Language.Parser]::ParseFile` 跑 AST 解析：0 错误，`git diff --numstat` 只有 1 行。这类误报别当真去"修"。

## 2026-10-29：扩层失败不再被无限重试

**事故**：玩家报告"触发错误会无限循环卡死游戏"。截图里的拒绝报告是 `Action: 创建楼层`、`Definition: 向下楼梯 (RL_StairsDown)`，异常栈顶是 `SuperSword.LifeSupportStateCache.InvalidateNegativeCache` 里无锁 `HashSet.Remove` 抛"non-concurrent collections must have exclusive access"，中间经过 `wowgag.guiperformancepatch` 的 `GppParallel.MainOnlySpawnSetup` 前缀。

**根因是两层，产品这一层是我们自己的错。** 第三方那一层：扩层生成虫巢时会走 `GenStep_CaveHives.TrySpawnHive` → `HiveUtility.SpawnHive` → `Pawn.SpawnSetup`，而 `Pawn.SpawnSetup` 被并行化补丁挪到了池线程，`SuperSword` 的静态缓存没有加锁，于是并发写坏。我们的那一层：`StairLayerBootstrap.Prepare` 失败后只 `EndJobWith(JobCondition.Incompletable)`，框架仍然 `CanConstruct`，`WorkGiver_ConstructFinishFrames.JobOnThing` 下一次扫描立刻把同一个框架再派给殖民者，于是每轮都完整跑一次 `EnsureElevation`（重建区域、寻路、地图绘制器、气候）再完整回滚——一轮就够重，重复起来就是卡死。窗口的 250 tick 冷却只是挡住了刷屏，挡不住重跑。

**修法：把"失败"变成一等状态。** `LayerMapComponent.BlockedStairs`（`HashSet<IntVec3>`，存盘）记录扩层失败的楼梯格；`Prepare` 入口查表直接拒绝；`ConstructionSupport.Validate` 对记录过的格子返回 `RL_StairExpansionBlocked`，于是 `CanConstruct` 变 false，原生 `JobDriver_ConstructFinishFrame` 的 `build.FailOn(()=>!GenConstruct.CanConstruct(Frame,pawn))` 立刻结束任务，`JobOnThing` 也不再派活——循环断开。重试入口是框架/楼梯上的 **重试创建楼层** 按钮（`Structure/BlockedStairGizmos.cs`），点了清记录并重跑真实扩层；拆掉楼梯也清记录。

**记账必须走 `LayerMapComponent.Raw`，不是 `For`。** `For()` 对还没扩层的地图故意返回 null（那是"这张图还没有楼层"的语义），而首次扩层失败恰恰发生在地图还没扩层的时候。我第一版用 `For()`，记录会静默丢失——`BlockedStairs` 加进去的瞬间组件根本不存在。为此加了 `internal static LayerMapComponent? Raw(Map? map) => map?.GetComponent<LayerMapComponent>();`，并在 doc 注释里写清为什么不能用 `For`。

**虫巢单独兜底。** `UndergroundService.SpawnCaveHives` 逐个 hive try/catch：虫巢生成是整条扩层链里唯一会执行任意第三方代码的地方，一个 MOD 崩掉不该让玩家丢掉整层，所以只跳过那一个 hive 并弹窗说明（`RL_HiveSpawnSkipped`），扩层继续。这条是"降低第三方伤害面"，不是"容忍第三方 bug"。

**新增场景 `blocked-stair`（`Diagnostics/V1BlockedStair.cs`，14 条断言）**：用 Harmony 前缀往 `LiveMapExpansion.EnsureElevation` 注入失败，跑真实的提交/回滚路径。断言覆盖：失败后框架与材料完整、地图没扩层、格子被记录、只弹一个窗口、`WorkGiver_ConstructFinishFrames.JobOnThing` 与 `GenConstruct.CanConstruct` 都拒绝该框架、10 个 tick 内没有任何重试、重试按钮存在、点完重试真的建成楼层并清掉记录、记录跨存档保留。

**夹具又踩了三次，都记下来：**

1. `V1Integration.Update` 有一道 `owner==null → throw new Exception("No layer map.")` 的通用守卫，而本场景**必须**在未扩层地图上跑，得把 scenario 加进白名单。
2. `stage==600` 已经被 `pairing` 占用，我先加了一条重复的 `if(stage==600)`，被前面那条抢先命中，`BlockedStairStep` 一次都没执行，表现是场景 30 tick 后自己 `Finish()`。改用 `stage==601`。
3. 场景是"自动化运行"，`RejectionReporter` 的 `automatedRun` 默认把窗口静音；断言"弹了窗口"必须先 `RejectionReporter.DialogGateOverride=true`。这是同一个开关 `rejection-dialog` 场景用的。
4. 注入的失败会走 `Log.Error`，被 `Error()` 收进错误清单打红 `no-runtime-errors`。加 `suppressInjectedExpansionError` 只在注入调用期间过滤那一条 `Stair-triggered map expansion failed`，其余日志照旧计入。

### 追加：竖井吊篮扩展站按钮（实机反馈）

实机截图反馈「吊篮没有对应的按钮」。排查后确认按钮没坏，是**规则覆盖漏了一族**：吊篮有两种，我之前只登记了相邻层那种。

- **相邻层吊篮**（`direction ≠ 0, shaft:false`）造价含两端，主机完工时 `SpawnSetup` 自己 `GenSpawn.Spawn` 一个 `RL_CargoPort`，所以按钮只是补回丢失的存取口（`companion=true`，直接给成品）。
- **竖井吊篮**（`shaft:true, station:false, direction:0`）只买主机，每一层要自己加 `RL_CargoShaftStation`。这才是真正的"对位"需求：以前得在家具菜单选扩展站、再手工把蓝图对到主机正上方/正下方同格。

补的规则就是 `Derive` 里加一个分支：`CargoProperties{shaft:true, station:false}` → `partner = RL_CargoShaftStation`、`directions = {1,-1}`、**`companion=false`**（扩展站是普通付费建筑，必须走原生蓝图）。扩展站自己（`station:true`）不登记，避免套娃。

**教训**：`CargoProperties` 这一个扩展同时编码了三种吊篮形态（相邻层端 / 竖井主机 / 竖井站点），只看 `direction` 一个字段会把另外两族静默漏掉。派生规则要按**所有**判别字段组合穷举，而不是找一个"看起来最像"的。

新增断言（`Diagnostics/V1Pairing.cs`，5 条）：`pairing-derives-for-cargo-shaft-master`（partner 是扩展站、`companion=false`、双向）、`cargo-shaft-offers-both-directions`、`cargo-shaft-enables-the-station-above`、`cargo-shaft-button-places-a-station-blueprint-above`（产物是 `Blueprint_RL_CargoShaftStation`）、`cargo-shaft-button-places-a-paid-station-not-a-free-one`（扩展站有 `costList` 且当场没有成品）。

验收：`v1-pairing` **39 断言 0 失败**，纯逻辑 16/16 套件。

### 追加：吊篮内容物页签改用原版容器列表

原来 `ITab_CargoContents` 自己用 `Listing_Standard` 打一串 `t.LabelCap`，纯文本：没有图标、不能点选、没有信息卡。用户要求"和原生物品架一样能看图标、点一下就选中"。

原版物品架 `Shelf` 其实只挂 `ITab_Storage`（筛选 + 优先级），**没有图标列表**；"图标 + 点选"的观感来自容器类页签，它们全部继承 `ITab_ContentsBase`（`ITab_ContentsTransporter`、`ITab_ContentsCasket`、`ITab_ContentsBooks`）。所以正解不是模仿，而是直接继承。

- `ITab_CargoContents : ITab_ContentsBase`，`container => main.Contents`（`ThingOwner<Thing>` 本身就实现 `IList<Thing>`，不必 `.ToList()`）。
- 只覆写 `DoItemsLists`：分隔标题用现成的 `RL_CargoContentsCount`（"Stored stacks: {0}/{1}"），逐行调基类 `DoThingRow`，空篮用 `Widgets.NoneLabel(ref curY, width, "RL_CargoEmpty".Translate())`（**三参重载才有自定义文案**，两参会用原版 `NoneBrackets`）。
- 免费得到：`Widgets.ThingIcon` 图标、`TooltipHandler` 悬浮名、`TargetHighlighter.Highlight` 高亮、`Widgets.InfoCardButton` 信息卡，以及关键的一行——`DoThingRow` 内的 `if (Widgets.ButtonInvisible(rect)) SelectLater(things);`，`FillTab` 末尾把它交给 `ITab_Pawn_FormingCaravan.SelectNow`，于是点击即 `Find.Selector.Select` + `CameraJumper.TryJump`。
- **`DoThingRow` 是 non-virtual**，所以"点选"这条路径无法自己实现，只能靠继承；这也是为什么不能只覆写 `FillTab` 保留原有渲染。
- `canRemoveThings = false`：基类丢弃按钮走 `GenDrop.TryDropSpawn(t.SplitOff(count), SelThing.Position + DropOffset, ...)`，会绕过 `CargoAccess` 的操作位占用和升降行程，凭空把货丢在端口格上。取出仍走右键菜单与搬运任务。

验收（`v1-pairing`，**46 断言 0 失败**）新增 7 条：页签确实继承 `ITab_ContentsBase` 且 `containedItemsKey=="RL_CargoContents"`、丢弃按钮已关闭、`container` 返回篮内实物本体（引用相等）、有篮时可见、`DrawMatSingle.mainTexture != null`（行图标有贴图可画）、`ITab_Pawn_FormingCaravan.SelectNow` 后 `Find.Selector.IsSelected(steel)`（**行的点击回调最终就是这一句**）、无篮建筑上不可见。

### 追加：吊篮"选择篮中物品"快捷按钮（实机反馈）

用户截图对比：原版钢铁物品架选中后，快捷按钮栏有 `选择牦牛肉 x90`、`选择钢铁 x52`，而吊篮没有。第一反应是"物品架有什么页签我们漏了"，反编译后才发现方向错了：

- **原生物品架 `Shelf` 只挂 `ITab_Storage`**，那个页签只有优先级下拉和筛选窗口，**没有图标列表**。
- 那排"选择 XX"按钮来自 `RimWorld.Building_Storage.GetGizmos()` 尾部：

```csharp
if (Find.Selector.NumSelected != 1) yield break;
foreach (Thing heldThing in slotGroup.HeldThings)
    yield return ContainingSelectionUtility.CreateSelectStorageGizmo(
        "CommandSelectStoredThing".Translate(heldThing),
        ("CommandSelectStoredThingDesc".Translate() + "\n\n" + heldThing.LabelCap.Colorize(ColoredText.TipSectionTitleColor)
         + "\n\n" + heldThing.GetInspectString()).Resolve(),
        heldThing, heldThing, groupable: false);
```

吊篮拿不到它有两个原因：`Building_CargoPort` 不是 `Building_Storage` 子类；而且篮内货物在 `ThingOwner<Thing>` 里，**没有 `slotGroup`**。所以只能自己重写这段循环。

新增 `Building_CargoPort.StoredThingGizmos()`：`Master is {Spawned:true}` 才出按钮，沿用原版"`NumSelected == 1` 才显示"的习惯，逐堆调 `ContainingSelectionUtility.CreateSelectStorageGizmo(...)`（`groupable:false`，`groupKey = thingIDNumber` 由原版方法自己设）。`GetGizmos()` 里紧跟 `StorageSettingsClipboard.CopyPasteGizmosFor` 之后 yield 它，所以**主机和每个存取口都有**——`Building_CargoHoist` 没有自己的 `GetGizmos` 覆写，一处改动覆盖全部。

本地化直接复用原版 `CommandSelectStoredThing`（`{0_label}`）/`CommandSelectStoredThingDesc`，不新增 key。**`"CommandSelectStoredThing".Translate(t)` 传 Thing 是对的**：`GrammarResolverSimple` 对 `obj is Thing` + `subSymbol == "label"` 直接返回 `thing.Label`，与 `NamedArgument.label` 无关（`.Named("0")` 是另一条路，两者都能出 `{0_label}`）。

验收：`v1-pairing` **53 断言 0 失败**，新增 7 条覆盖按钮数量与篮内堆数一致、类型是 `Command_SelectStorage`、label 含物品名、有图标、点 action 后 `Find.Selector.IsSelected` 为真、多选时不显示、存取口上同样有。纯逻辑 16/16。

## Smart Combat（Immersive Warfare）兼容

2026-10-30。报告现场：`Player.log` 里楼梯建层失败，`[RimLayers] Stair-triggered map expansion failed on map 0: System.NotSupportedException: Unmapped native per-cell buffer: SmartCombat.OrdinaryRoadMap.offsets`。同一张地图 15:45:43 扩层成功、15:48:01 失败，因为 `offsets` 是**懒分配**的：殖民者开始普通工作（`Goto`/`HaulToCell`/`DoBill`/… 白名单）时，`OrdinaryRoadPreference.ApplySafeBatch` 才在 `PathFinder.ComputeWorkThisTick` 的后缀里调 `PrepareSafeBatch` 建出 `NativeArray<ushort>`。

修法为**丢弃**而**不是**迁移：`SmartCombatCompatibility.Install()` 只登记 `RegisterBeforeResize(OrdinaryRoadMap, DiscardGrid)`，在事务里调 Smart Combat 自己的 `DisposeGrid()`。理由：`PrepareSafeBatch` 本来就自带 `offsets.Length != numGridCells → DisposeGrid()` 的自愈分支，下一次批量会按新尺寸全量重建（`changed=true` → `RecycleGridJobData` → 重算）；而保留旧前缀会留下 `roadCells`/`roadCount` 与新尺寸不一致的半状态。

同时修掉两个真问题：

1. **核心侧 `Length` 判定**：`NativeArray<T>.Dispose()` 之后字段仍是那个 struct，`IsCreated == false` 但 `Length` 保留旧值。`LiveMapExpansion.PrepareManaged` 与 `GridCapacityRepair.Fix` 的未登记原生缓冲分支只看 `Length`，于是"适配器刚丢掉的缓冲"被当成"长度正好是旧格数的未登记缓冲"继续抛同样的异常。两处改为把 `IsCreated` 当作存活判据（没有该属性的类型行为不变）。
2. **`ModsConfig` 大小写**：1.7 的 `ModsConfig.RecacheActiveMods()` 把 `ModsConfig.xml` 里的字符串**原样**放进 `activeModsHashSet`，而 `IsActive(id)` 用 `id.ToLower()` 查。所以配置里写驼峰 `lordfelix.SmartCombat` 时 DLL 照常加载、`AccessTools.TypeByName` 找得到类型，但 `ModMetaData.Active` 恒 false、`GetActiveModWithIdentifier` 恒 null。适配器改用 `AerocraftCompatibility` 早就有的双路判定（`ModLister` + `RunningModsListForReading` 大小写无关兜底）。

验收：`-Scenario smartcombat` **23 断言 0 失败**（`Tests/Artifacts/v1-smartcombat-20261030-171022`）；回归 `-Scenario expansion -AllDlc -River -VVRace` generate 28 / load 7、`-Scenario stairs-bootstrap` 20+6、`-Scenario rejection-dialog` 22 全部 0 失败。

顺带发现但**与本改动无关**的既有夹具缺陷：`blocked-stair` 用共享的 `BootstrapSite`（铺 `Concrete`）却建**向下**楼梯，而 `StairLayerBootstrap.Validate` 对向下楼梯要求 `UndergroundService.TerrainAllows`，混凝土没有 `Diggable` 权限，于是注入故障的 `EnsureElevation` 根本没被调用（`attempts=0`，3 条断言红）。在 `BeginBlockedStair` 里把那格改成 `TerrainDefOf.Soil` 后 15 断言全通过。

详见 [SMARTCOMBAT-COMPATIBILITY](SMARTCOMBAT-COMPATIBILITY.md)。
## 2026-10-30：楼层面板可以用鼠标拖着走

楼层面板一直是 `MapComponent` 覆盖层，不是 `Window`，所以拿不到 `Window.InnerWindowOnGUI` 末尾那句 `GUI.DragWindow()`；位置写死在 `UI.screenWidth - 238f, 160f`，玩家装了别的东西压在上面就只能忍。这次给面板加了一条 14 像素高的抓手：按住拖动整块面板，双击抓手回到原角落。

- **命中矩形只有一个来源。** `LayerView.ControlsRect` 现在把「原生锚点 + 存档偏移」过一遍 `PanelDragMath.Clamp` 再返回；`MouseOverFloorControls`、抓手、`LayerTransition.OnGUI` 的地图点击闸门和诊断夹具都调它，不存在第二份可能漂移的矩形。
- **坐标不做任何翻转。** `Event.current.mousePosition` 与 `ControlsRect` 同一空间，手指偏移 `dragGrab = pointer - panel.position` 全程恒定，拖动不会累积舍入误差；也不需要 `UIScale` 换算。
- **边长会变，但高度基线固定。** 面板高度随海拔页脚、恢复提示条在 70/106 之间变，拖动时用的是当帧真实高度，所以放大/收起提示条后再拖仍然贴着抓手。
- **不许拖出屏幕。** 抓手本身要留在屏幕内，所以 `y` 的下界是 `4 + 14`；换分辨率后旧偏移会被**重新收紧**而不是丢掉，`Normalize()` 只清 `NaN/∞`。
- **拖动必须自己 `Event.current.Use()`。** 面板背景不消费输入，`DesignatorManager.ProcessInputEvents` 和 `Selector.HandleMapClicks` 都不看指针是否在 UI 上：不 `Use()` 就会在面板背后的地图格上放置命令或拉框。
- **松手即存。** `LoadedModManager.GetMod<RimLayersMod>().WriteSettings()`，指针移出窗口收不到 `MouseUp` 时用 `!Input.GetMouseButton(0)` 兜底结束。

新增 `Source/RimLayers/Presentation/PanelDragMath.cs`（不引用 Unity，纯几何）、`RimLayersSettings.FloorPanelOffset`（`Scribe_Values` 存成 `(x, y)`）、`RL_PanelDragHelp` 中英各一条。抓手在 `Widgets.DrawWindowBackground` 之后、第一个按钮之前绘制，只吃自己那 14 像素，按钮和背景照常工作。

验收：新增纯几何套件 `Tests/PanelDragTests` **26938 断言 0 失败**（四种分辨率下原生锚点必须原样通过、±4000 偏移全扫不出屏、超大面板与 `NaN/∞`、偏移往返）；`Run-UnitTests.ps1` 18/18 套件通过。游戏内新增场景 `-Scenario panel-drag`：生成 **14 断言**、新进程读档 **3 断言**，零失败（[183635](../Tests/Artifacts/v1-panel-drag-20261030-183635)）——覆盖拖动跟随、抓手持久、松手保留、新矩形接管命中、旧角落不再吞点击、移动后的“+”仍能排楼层、拖不出屏、双击复位、设置文件真的写出 `floorPanelOffset`，以及读档后面板位置与尺寸不变。

顺带核实两条**与本改动无关**的既有失败（已由并行会话确认）：`-Scenario transition` 的 `blend-right-click-submits-native-cross-floor-goto`（注入的右键被 `MainButtonsRoot.DoButtons` 优先吃掉）与 `-Scenario usability` 的三条 HasSurface 断言（未提交的 deck-source/`StructuralSupport` 改动）。
