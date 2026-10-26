# Vanilla Gravship Expanded 兼容

启用 Chapter 1（vanillaexpanded.gravship）时自动启用适配；Chapter 2（vanillaexpanded.gravship2）按实际启用状态追加规则。不安装 VGE 时继续走原生路径，不增加程序集或 modDependencies 硬依赖。

## 安装与使用

加载顺序：Harmony → Odyssey / Vanilla Expanded Framework → VGE Chapter 1 → Chapter 2（如使用）→ RimLayers。现有加载排序由 loadAfter 提示；VGE 未加入 modDependencies。更新 DLL 后重启游戏。

在普通结构平台上建原生逆重基架，再放对应引擎；上甲板仍用 RimLayers 飞船结构楼板。Gravjumper 使用 pilot cockpit，gravhulk 使用 pilot bridge，普通引擎使用 pilot console，适配不会解除 VGE 的驾驶设施型号匹配。

燃料罐和推进器可随上层甲板运输、链接到其他层的本船引擎；相邻两层各建一个竖直接头（`RL_AstrofuelPipeRiser` / `RL_OxygenPipeRiser`）即可让宇航燃料/氧气管道跨甲板连通，同一层内的连接仍走 PipeSystem 原生逻辑，因此不同船或无关殖民地管网不会被焊接。多层船的着陆保持完整净空检查，不通过 VGE 撞毁障碍式降落清空殖民地楼板或建筑。

## 已确定规则

- 按用户确认，VGE 下保留其全局单活动引擎规则；纯原生仍允许多船，船体归属不能合并。
- 引擎口径为“thingClass 派生自 Building_GravEngine”的**所有**玩家可用引擎（与 VGE 自己的 `GravshipHelper.GravEngineDefs` 一致），不再是 defName 白名单；敌方/机械族引擎派生自 Building，天然排除，玩家归属另由 `Faction==OfPlayer` 把关。
- 引擎设施不再只认 `CompGravshipFacility`：任何 `CompProperties_Facility`，只要其 `linkableBuildings` 含该引擎 def（例如 VGE 的 `VGE_GravshipStructuralBeam`）都按跨层处理；`mustBePlacedAdjacent*` 这类天然同层约束保留原生语义。
- `engine.ValidSubstructure` / `ValidSubstructureNoRegen` 在本船返回**全甲板**格集（`ShipDeckTerrain.DeckCells`），因此 VGE 的热管理、热吸收器、船体遮罩与起飞前清树都覆盖上层甲板。
- 保留燃料管网、推进器缺燃料、维护、研究、引力异常、Chapter 2 干扰与远程锁定。管网仍遵循 VGE 的实际连接，不凭楼层投影免费连通。
- 普通楼板与飞船楼板继续按格独立归属；高层建造、起飞分离、跨界建筑确认拆除、停靠高度与全存档楼层配额沿用 RimLayers 现有规则。
- 飞船结构楼板可正常拆除：地基用 `CanRemoveFoundationAt` 前缀接管（原生 `Removable` 等于 `layerable`，两种船板都是 `layerable=false`）；船板上的装修层走独立的“只拆装修、保留甲板”分支。占用格、上方屋顶/结构、无合法邻格仍拒绝。
- VGE subscaffold 仍随船运输但不占承载容量；不能因适配而变成可放建筑的普通船板。
- 真空屏障顶颜色按船内层坐标保存，旋转/高层降落后还原到对应楼层。
- VGE 仪式直接起飞入口必须在扣燃料前完成分离确认与目标配额预留。
- **跨层设施判定不得替换原生结论，只能在其为 false 时放宽**。原生 `CompGravshipFacility.CanBeActive` 用 `IntVec3.InHorDistOf`（只看 x/z）量存储坐标，而层存储是 `z = layer*Stride + z`、`Stride = ((height+17)/17)*17`：120 地图 stride 136，250 地图 stride 255。VGE 驾驶台 `maxDistance` 是 200，所以玩家的 250 地图上「引擎在下层、控制台在上层」在原生眼里永远超距 → 恒报“未连接至逆重引擎”。RimLayers 的放宽按**解码后的逻辑坐标**判定，并叠加“脚下整块 footprint 都是本船 substructure”与“该格不属于另一艘船”两道校验。
- 相应的六个设施门（`CanBeActive`、`CanLinkTo`、`PotentialThingsToLinkTo`、`LooselyConnectedToGravEngine`、`PlaceWorker_InRangeOfGravEngine`、`GravshipUtility.InsideFootprint`）全部是 Postfix：原生为真就直接返回，绝不再加条件。早期版本用 Prefix 直接覆盖原生判定，把“船体缓存还没刷新”“footprint 跨甲板边”“超出引擎 footprint 半径”这些内部状态都翻译成了用户可见的“未连接”，而且是静默的。
- `ship-vge` 场景默认 `-MapSize 250`，并保留断言 `*-fixture-reproduces-live-deck-gap`（要求控制台与引擎的存储距离**大于** `maxDistance`）。没有这条断言时，夹具跑 120 地图会让所有跨层断言免费通过，正是这个盲区让“夹具全绿、实机全红”持续了两轮。
- **设施成员资格必须双边宽松（`ShipFacilities.LinksToDef`）**，不能只看引擎的 `linkableFacilities`。VGE 只把自己的设施加进原版 `GravEngine` 的清单，从不把原版 `PilotConsole` 加进自家引擎；而 `PilotConsole` 仍照 VGE 的原意可建造（换贴图、`VGE_NoLinkedGravhulk` 遮罩、燃料面板）。原生枚举由引擎清单驱动，所以原版控制台 + VGE 引擎这对组合永远不成为候选。规则：设施 def 的 `CompProperties_Facility.linkableBuildings` 含该引擎 def，**或**含任意被识别为引力引擎的 def，即视为可连。`ShipFacilityCandidates`、`ShipFacilities.IsEngineFacility`、`LayerFacilityScopePatch`、`LayerFacilityActivePatch` 全部改用这一个谓词。
- 上层设施与上层推进器的实机组合（原版 `PilotConsole` / `SmallThruster` + `VGE_GravjumperEngine`）已进夹具：`*-vanilla-console-links-to-this-engine-across-decks`。

## 验证

Release 构建通过，0 警告 / 0 错误。以下均为独立配置的真实游戏运行，结果 JSON 中 errors=[]：

| 配置与场景 | 结果 | Artifacts 目录 |
|---|---|---|
| **Chapter 1，250 地图（实机几何，含双边成员资格）** | **生成 100、重载 5 项通过** | v1-ship-vge-20261026-175351 |
| **Chapter 1 + 2，250 地图（实机几何，含双边成员资格）** | **生成 103、重载 5 项通过** | v1-ship-vge-20261026-175616 |
| 原生 `ship-structure`（设施门改为 fail-open 后） | 生成 55 项通过 | v1-ship-structure-20261026-071431 |
| 原生 `structure`（同上） | 生成 32、重载 16 项通过 | v1-structure-20261026-071507 |
| 无 VGE / VEF，原生高层引擎完整起飞、降落、返航 | 生成 49、飞行重载 26、落地重载 10 项通过 | v1-gravship-20261021-024545 |
| 仅 Chapter 1，三种引擎双层转运（120 地图，旧） | 生成 43、重载 5 项通过 | v1-ship-vge-20261021-025241 |
| Chapter 1 + 2，含仪式选择、远程锁与干扰（120 地图，旧） | 生成 47、重载 5 项通过 | v1-ship-vge-20261021-025425 |

上表前两行的 250 地图是必须的：120 地图的层跨带距离（stride 136）小于 VGE 控制台的 `maxDistance 200`，会让所有跨层断言免费通过。这两次运行里 `*-fixture-reproduces-live-deck-gap` 均报 `storage dz=-255 / maxDistance=200 / stride=255`，即夹具确实坐在实机失败区间内；跨层控制台 `links=1` 且 `CanBeActive=True`，燃料 `shared=250`，推进器 `canLink=True`。

前两行是**最后一版代码**（第七个门 `Environment/FacilityPatches.cs` 改成 Postfix 放宽、`ShipCapturePatches.cs` 删除冗余的 `ValidSubstructureAt` 接管、`ShipFacilities` 成员资格改为双边宽松之后）的验证，日志报 `0.1.5 loaded (366 patch classes)`，`Gravship facility has no engine link` 计数为 0。最关键的一条证据是原版控制台在 VGE 引擎上的真实配对：`*-vanilla-console-links-to-this-engine-across-decks` 报 `links=VGE_GravjumperEngine; declares=GravEngine; inEngineList=False; canLink=True` —— 原生引擎清单里根本没有 `PilotConsole`，链接仍然成立，说明放宽发生在 RimLayers 这一侧而不是靠改清单。Chapter 2 那轮另外通过 `chapter2-remote-lock-still-vetoes-launch`、`chapter2-ground-jammer-blocks-upper-deck-landing`、`chapter2-jammer-removal-restores-landing`。

画面复核：v1-ship-vge-visual-20261021-025605 复用最终飞行存档，重载 5 项通过、errors=[]；实看第 3 层的旋转驾驶台、白银、subscaffold 与洋红色真空屏障顶。初始截图停在世界地图，修正测试视图后取得实际甲板画面：

![VGE 飞船上层甲板重载后](../Tests/Artifacts/v1-ship-vge-visual-20261021-025605/vge-landed-upper-deck.png)

目录均在 Tests/Artifacts 下。Chapter 1 与原生证据在后续不影响这些路径的改动中继续有效；最后的仪式窗口修正由两章场景专项验证。

VGE 三种引擎逐一测试：本船跨两层 98 格、匹配驾驶设施跨层连接、subscaffold 只计 97 格承载、原生载荷捕获、普通停机坪保留、旋转高层落地、原货物身份、再次起飞无重复。最终原生型号的 VGE 双层世界飞行对象保存、重载、落地，保持上甲板颜色与 37 个白银原堆叠。

燃料测试使用真实 VGE 油罐、管道、推进器：空网禁用，注入 200 后启用，原生启航资格通过；VGE provider 按 25% 航程比例实际从 200 扣至 192.5。非法目的地从 VGE 仪式执行入口拒绝，扣费与过场均未发生。第二章远程锁保持原拒绝文本；地面干扰器阻止同逻辑位置上层着陆，移除后恢复允许。可取消仪式选址动作保持未包装，额度在执行起飞入口处理。

尚未单独跑完 VGE 的完整仪式 UI、整场舰战、突袭/轨道任务及每种特殊敌舰流程；本次验证的是玩家多层船的转运、设施、资源与起降约束。保留 VGE 对这些玩法的原实现。

复现命令（游戏根目录，PowerShell 7）。`ship-vge` 默认 250 地图：层存储跨带距离 `Stride=255` 超过 VGE 控制台的 `maxDistance=200`，必须在这个尺寸下测才能复现实机的跨层断连；

    ./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-vge -Vge 1 -AllDlc -TimeoutSeconds 900
    ./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-vge -Vge 2 -AllDlc -TimeoutSeconds 900
    ./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario gravship -NativeShip -AllDlc -EngineLayer 1 -TimeoutSeconds 480

### 发现并修复的问题

- 023908：VEF 在 RimLayers 替换 RoofAt 后找不到阴影补丁位置；已明确补丁顺序。初始测试误给两种特殊引擎放普通 console，改用其定义允许的 cockpit / bridge。
- 024104：型号改正后跨层仍未连接，定位到另一处同层设施筛选仍只认原生 GravEngine，已与候选筛选一并修复。
- 024330：测试推进器尾焰朝向船内，原生正确拒绝 CanLink；将测试推进器朝外后燃料功能通过，没有放宽游戏净空规则。
- 024457：测试把第二章静态 Instance 字段当属性读取，修正测试反射入口。
- 024844：生成 46 项通过，重载发现测试只保存临时 Game.Gravship 引用；改成原生世界对象持有后 025128 的 46/5 项通过。未改生产序列化来迁就测试。
- 构建曾与自己的原生重载进程争用 DLL，等待该进程完整结束后构建成功，没有终止玩家进程。
- 025425：补充 VGE 先选目的地再开仪式的可取消边界，47/5 项通过。
- 20261025：管道方案从“同格自动跨层 + 手动重建网络”改为“显式竖直接头 + 原生注册”。旧方案的问题是把 PipeSystem 的网络生命周期当成了可以手动驱动的状态：`PipeNet.Destroy()` 和 `CompResource.PostDeSpawn` 都不清理各 comp 的 `PipeNet` 引用，逐 comp 的 unregister/register 会把已经合并的两层重新拆散（日志可见 `Network(s) number: 2`）。新方案只补纵向邻接，合并/拆分全交原生逻辑。
- 20261025：`PatchOperationFindMod` 静默不匹配的坑（比显示名不比 packageId），改用 `PatchOperationConditional` 检查 `VGE_AstrofuelNet` 存在性。
- 20261025：`Gravship..ctor` 的 `NullReferenceException` 最终定位为**测试夹具位置冲突**，不是产品缺陷。`VGE_PilotBridge` 是 5×3，占 `x78..82, z74..76`；测试把 filler 货和 `VGE_GravshipStructuralBeam` 分别 spawn 在 `(81,76)` 和 `(81,75)`，都在 bridge footprint 内，`GenSpawn` 把 bridge 挤掉后 VGE 的 `Gravship_AddThing_Patch` 就没有机会设置 `pilotConsole`，`DetermineLaunchDirection` 里 `pilotConsole.Rotation` 直接空引用。移到 `(81,77)` 和 `(77,74)` 后 NRE 消失。
- 20261025：回归发现 `ValidSubstructure` 拓宽对原生多船路径是错的（`native-footprint-remains-core-deck-with-logical-cross-layer-ownership`），改为按 VGE 是否启用门控。教训：改了共享属性就应同时跑目标场景和一个反例场景，不要等全绿后才跑回归。
- 20261025：`gravship -NativeShip` 的 `legal-rotated-ground-origin-accepted` 失败经 `git stash` 基线对比确认为**既有问题**，与本次 VGE 工作无关。
- 20261026（实机反馈“1 层引擎、2 层控制台/推进器还是提示未连接”）：根因是层存储跨带距离与 VGE `maxDistance 200` 的冲突。`LayerLayout.CalculateStride(height) = ((height+17)/17)*17`，存储 `z = layer*Stride + z`；120 地图 stride 136（< 200，夹具全绿），250 地图 stride 255（> 200，玩家实际存档）。原生 `CompGravshipFacility.CanBeActive` 对**第一个**超距引擎就直接返回 false，所以只要引擎在另一层，所有设施都是“未连接”。同时发现 RimLayers 自己的六个门是**失败关闭**的：`ShipFacilityLinkOwnership` 在 `Belongs` 失败时硬返回 false，`ShipFacilityActive` 用 `InField`（引擎 footprint 半径 7.9）当活跃判据——比原生 `OnValidSubstructure` 严得多。两者都改成了只在原生为 false 时放宽的 Postfix，活跃判据改为“脚下整块都是本船 substructure”，不再依赖船体缓存。
- 20261026：推进器在分层船上必然 `Blocked`。`CompGravshipThruster.IsBlocked` 把**尾焰范围内任何** substructure 格当成障碍；单层船上推进器挂在船体外沿所以永不触发，而分层船的上层甲板本身就是 substructure。结果所有甲板推进器 `CanLink()` 恒为 false → 永不连线 → `ThrusterNotConnected`。修法是在 `IsBlocked` 的 Postfix 里只保留“尾焰压到**别的**船的甲板”这一种阻塞，`blockedBy`（挡风建筑）与户外要求完全不动。
- 20261026：教训——兼容补丁“替换”原生判定时，每多一个前置条件就多一条让原生本来正常的场景静默坏掉的路径；改成“只在原生为 false 时放宽”的 Postfix 无法让原生行为变差。另一条教训：夹具必须坐在出问题的参数区间里，否则它只是在证明别的东西。

最终验证（全部 0 失败）：VGE Chapter 1 generate 95 项 + load 5 项；Chapter 2 generate 98 项 + load 5 项（含 `chapter2-remote-lock-still-vetoes-launch`、`chapter2-ground-jammer-blocks-upper-deck-landing`、`chapter2-jammer-removal-restores-landing`）；`ship-structure` 55 项；`structure` generate 32 项 + load 16 项。产物目录 `Tests/Artifacts/v1-ship-vge-20261025-220928`（Ch2）、`v1-ship-structure-20261025-221712`、`v1-structure-20261025-223241`。
- 夹具设计约束（后续改测试必读）：VGE 引擎把结构容量卡在 100 格，所以船体不能超过 7×7；而 `VGE_GravhulkEngine` 是 5×5，几乎占满 7×7 船体。因此燃料/管道夹具只能放在**船外地面**，靠同层管道与接头相连（同层连接不需要船归属）。`CompResource.RemovePipes()` 会销毁接头自己 footprint 上的管道，所以接头不能和管道同格。

源码修改前副本：Tests/Artifacts/vge-before-20261020-193610。

本机依据：2026-10-21 两个指定目录的 1.7 DLL、XML。Chapter 1 MVID 57be97aa5bc146fbafd0a60f764bf91e；Chapter 2 MVID 05f048ed829d4ceca8a79bd49c74b77c。定向核实 GravshipHelper、GetConnectedSubstructure、ExtraGravshipDataComp、ExecuteGravshipLaunch 与 Chapter 2 的 CanLaunch / IsValidCell 补丁。

## 实现入口

- Compatibility/VanillaGravshipCompatibility.cs：按 packageId 绑定反射接口，不引用 VGE/VEF DLL；引擎按类型派生识别、subscaffold 容量、全甲板发电负荷、颜色编码、`PlaceWorker_InRangeOfGravSource` 跨层选址和 VGE 仪式扣费前确认；选址时的可取消仪式窗口不预占额度。
- 两处原生设施筛选同时识别 VGE 引擎与“普通 CompProperties_Facility 但 linkableBuildings 含引擎”的设施（`Ships/ShipFacilityPatches.cs` 的 `ShipFacilities` 谓词），保留其 linkableFacilities 与 CanLink；型号不匹配、尾焰阻塞和缺燃料照常拒绝。
- `Ships/ShipFacilityPatches.cs` 的六个跨层门都是 Postfix：`ShipFacilityActive`（`CompGravshipFacility.CanBeActive`）、`ShipFacilityLinkOwnership`（`CompAffectedByFacilities.CanLinkTo`）、`ShipFacilityCandidates`（`PotentialThingsToLinkTo`，空结果时交回原生）、`ShipLooseConnection`（`LooselyConnectedToGravEngine`）、`ShipFacilityPlacement`（`PlaceWorker_InRangeOfGravEngine.AllowsPlacing`）、`ShipLogicalFootprint`（`GravshipUtility.InsideFootprint`）。均以 `if(__result) return;` 开头，只把原生 false 翻成 true（`LinkOwnership` 唯一例外：只在设施所在格明确属于**另一艘**船时才翻成 false）。`ShipThrusterDeckBlock`（`CompGravshipThruster.IsBlocked`）只消除“尾焰压到本船自己甲板”这一伪阻塞。
- `ShipFacilityTrace` 对“站在分层船甲板上但 `LinkedBuildings` 为空”的设施打一次性 Warning（defName、位置、引擎、船体状态、linkable 判定、CanLink、Belongs、逻辑距离、loose 标记），使这类故障不再静默。
- `CompGravshipFacility.CompInspectStringExtra` 的“是否在引擎范围内”改为逻辑距离判定，跨层设施不再误报 `MessageMustBePlacedInRangeOfGravEngine`。
- `ValidSubstructure`/`ValidSubstructureNoRegen` 在 **VGE 启用时**用 `ShipDeckTerrain.DeckCells` 覆盖全甲板，单点修复 VGE 热管理/遮罩/清树等直接消费者；未装 VGE 时保持原生单层语义，因为原生 `CanLaunch` 与多船路径依赖它（回归断言 `native-footprint-remains-core-deck-with-logical-cross-layer-ownership`）。
- `Ships/ShipFoundationRemoval.cs` 用前缀接管船板地基可用性；`Structure/ConstructionPatches.cs` 的 `ShipDeckFinish` 负责“装修层可拆、甲板保留”。
- `Environment/PipeLinks.cs`：PipeSystem（VEF 自带）可选绑定，只替换 `PipeNetManager.NeighbourThingsCardinal` 给**显式竖直接头**补上相邻层的同 (x,z) 邻居。网络合并/拆分全部交给原生 `RegisterConnector`/`UnregisterConnector`；RimLayers 不做手动 unregister/register（`PipeNet.Destroy()` 与 `PostDeSpawn` 都不保证状态一致，手动重建会让两层落进不同网）。跨层资格是“对面那层也有接头”，不看是否同属一艘船，因此船外地面同样可用。
- `Patches/VGE_PipeRisers.xml`：用 `PatchOperationConditional` 检查 `VGE_AstrofuelNet` / `VGE_OxygenNet` 是否存在来注入两个接头 Def，避免硬依赖。**不能用 `PatchOperationFindMod`**：它比的是 `ModMetaData.Name`（显示名 `Vanilla Gravship Expanded - Chapter 1`），不是 packageId，写 packageId 会静默永不匹配。接头必须是 `PipeSystem.Building_Pipe` + PipeSystem 自己的 `CompProperties_Resource`，否则原生注册不会把它们当连接器。
- VGE 的 subscaffold 2D 洪泛由本船归属与本层格替换；原生多层总容量按付费甲板数检查。
- VEF 屋顶阴影 transpiler 先运行，RimLayers 再替换实际屋顶查询，避免互相找不到指令。
- Chapter 2 干扰器只修正空间距离：同一逻辑 x/z 的不同层不会因存储带间距逃出干扰。供电、眩晕、范围与远程锁定继续由 VGE 判断。
- 原生与 VGE 的撤离后地图保留政策分别由各自原流程处理；适配不删改 VGE 世界战斗控制器。
