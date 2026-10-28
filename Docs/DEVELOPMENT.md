# RimLayers 开发记录与验证入口

2026-10-20 整理发布介绍时保留的原 README。当前玩家介绍见 [README](../README.md)；以下保留当时的开发说明、构建命令和证据入口。

**RimWorld 1.7 的单 Map 多层 mod，当前版 0.1.5。** Pawn 通过楼梯连续跨层寻路、搬运、取料、使用家具和工作台；人工高层楼板需要紧邻下层的有效人工或薄岩屋顶；厚顶岩上层自带不可拆天然楼板和含矿岩体。支持跨层射击、原版射位 AI、护盾与三维爆炸。

2026-10-18 已完成 M1–M5 的首版实现；0.1.1 增加落点保护、下行楼梯标记、下层背景、整片楼板规划、可改键切层及旧档扩层。0.1.2 修复叠层地板、首次头像定位、双向武器瞄准和下层尸体，薄雾改为生成的透明纹理动态漂移。0.1.3 将可达性查询与完整寻路分离，修复寻找食物时反复完整寻路造成的长卡顿，见 [可达性修复](REACHABILITY-VALIDATION.md)。0.1.4 增加 Dashboard 启动检查的定向兼容，避免原版右键菜单初始化被中断；实际命令输入验证见 [命令修复](COMMAND-INPUT-VALIDATION.md)。显示改动与实际画面见 [0.1.2 显示验收](DISPLAY-VALIDATION.md)，此前六项改动见 [0.1.1 更新](USABILITY-VALIDATION.md)。逐项结果、实际组合和规模数据见 [第一版验收](V1-VALIDATION.md)；设计契约见 [PLAN](PLAN.md)，失败与修复过程见 [实施记录](WORK-LOG.md)。

## 三维战斗与跨层灾害（开发候选）

直射武器向下一/两层的射程为 110%/120%，向上为 90%/80%；曲射与投掷保持原射程。跨层射击按九点身体暴露检查角度掩体，沙袋/路障逻辑高 1.0，楼板和屋顶真实挡弹；同层普通掩体仍由原版处理。人物、自动炮台和有人炮台共用射击解，迫击炮包括同层发射也检查上方空间。爆炸冻结起爆时遮挡，气体守恒交换，火通过开放通道逐层传播。使用方式、配置与独立游戏证据见 [三维战斗](COMBAT-3D.md)。修改程序集后重启游戏。

## Odyssey 飞船支持（开发候选）

多层船体已接入原版逆重飞船的装载、航行、保存与落地流程；只对拥有完整玩家逆重核心和有效原版基底的船生效。上层 Pawn、物品、容器、楼梯、工作台与区域随同一艘船切换地图，燃料与冷却继续由原版决定。船外建筑不随船；跨起飞移动边界的建筑会在确认后销毁，真实容器内容和框架材料留岸。跨界区域仍需先拆分。

可前往新目的地图或已有多层地图；**现有未扩层的地表/太空目的地图会在确认出发后、扣油前扩容，失败则取消出发**。逆重引擎可位于任意甲板，驾驶台、推进器、延展器与油箱可跨层关联；各层基架共同计入承载量。详见 [飞船实施](GRAVSHIP-IMPLEMENTATION.md) 和 [扩容验收](LIVE-EXPANSION-VALIDATION.md)。这是开发候选，尚未作为新版本发布。

起降动画的高层查看修复与实际截图见 [起降显示验收](SHIP-CUTSCENE-VALIDATION.md)：捕获不再受查看层过滤，动画和镜头跟随当前查看层。修改后须重启游戏。

同船其他甲板上的乘员现可通过原版发射到齐检查；驾驶员仍须到岗，船外乘员和未离船访客仍会阻止结束。跨层登/离船只使用真实可达路线，见 [发射登船修复](SHIP-BOARDING-VALIDATION.md)。

## 专用飞船楼板与电梯（开发候选）

二、三层先建普通结构楼板，再在上面建原版“逆重飞船基架”和引擎；已有引擎的上甲板仍用“飞船结构楼板”，并遵守屋顶支撑、范围与共享承载。普通和飞船楼板可横向对接，同格仅船底可覆盖普通停机坪，起飞后还原底层及装修。

飞船按同船连通块收集，允许下探镂空寻找合法船体锚点和有原生屋顶支撑的横向外伸；普通楼板不传递归属，多艘船保持独立身份。可在已有二、三层选择停靠位置，船底落在选定层，引擎保留相对高度；整船不能超过三层，起降通道必须净空。高层空位仍需合法下层屋顶支撑，不支持无支撑悬停。

旧通用甲板不再自动转换：查看对应上层并选中引擎，使用“转换旧甲板”预览和确认；多船相邻时可先“设为造船核心”，蓝图保存所选归属。原版已检查引擎进度、研究与逆重锚政策不变；**无锚强行起飞仍放弃原地图**。完整规则及独立运行证据见 [飞船与普通楼层共存](SHIP-COLONY-COEXISTENCE.md) 和 [共存验收](SHIP-COLONY-VALIDATION.md)。

单层电梯通电 25 tick、应急 180 tick。垂直电梯在各层同坐标建构件，可关闭中间停靠但必须井道贯通；耗时为通电 `30+45×层差` / 应急 `90+120×层差` tick。各站独立接电，选路比较真实步行与通行总成本，不模拟共享轿厢。材料、研究、迁移和证据见 [多层飞船与电梯](DECKS-AND-ELEVATORS.md)。

## 山体与矿脉（开发候选）

厚顶岩上方逐层生成天然岩石楼面与独立矿脉，开采后留下可建造的石面；薄顶岩上方仍是空域，需要自己铺结构楼板。可从山外二层平台直接接到山壁开采，也可在下层建楼梯，由采矿工作先挖通上层出口，再完成建筑。既有空白上层通过楼层面板“补生成山体…”预览和确认，不自动覆盖旧建设。进入方式、矿物生成和迁移说明见 [山体多层](MOUNTAINS.md)。

## 地下两层（0.1.5 开发候选）

在“结构”中建造**向下楼梯／向下单层电梯**，按需开挖 B1、B2。地下由原生岩石、独立矿脉、洞穴和虫巢构成；挖通后可连续跨层采矿、搬运和战斗，也参与符合条件的原版虫灾。冰盖和海冰整图禁挖，水冰、熔岩、桥面、基架与悬空处不能作入口。材料在出口真实挖通前保留。详情见 [地下使用与存档](UNDERGROUND.md)、[地下验收](UNDERGROUND-VALIDATION.md)。

## 开始使用

1. 使用 RimWorld **1.7**，启用对应的 **Harmony 3** 与 RimLayers，并重启游戏。无需 IsoRim、ProjectZ 或 Baal。
2. 新地图和从未使用过楼层的旧地图起初只有地面，不因读档扩容。向上楼梯或单层电梯完工时只追加所需上一层，地表及上层合计最多三层；向下连接器另可逐层增加 B1、B2；已有多层存档不缩减。建议地面不超过 **250×250**，首次使用后先另存新槽。
3. **F7 下层、F8 上层**，可在“选项→按键”中改键；右侧 `- / +` 和 `Alt + ↑ / ↓` 也可切层。查看层不移动 Pawn 或重派工作；上层楼梯口有下行图标及楼层按钮。
4. 未预留楼层的地图可先造楼梯，第一座完工时自动创建上层；人工平台先完成下层人工或薄岩屋顶，再建楼梯生成上层落脚平台；厚顶岩内按山体开挖流程处理。选中结构楼板可见绿色可规划边界，一次拖矩形铺下整片蓝图；工人从真实边缘逐步施工，无需每建一圈重新放置。人工楼面的**整个旋转后占地**仍需有效下层屋顶支撑；天然岩石楼面独立承载。
5. 正常下达原版工作和战斗命令。楼梯和单层电梯逐层经过；垂直电梯可沿贯通井道直达已有站点；普通工作保持同一 Job / Driver / 原版预订，饥饿、征召和取消仍服从原版仲裁。

高层空域显示下层实景、可见 Pawn、尸体和缓慢漂移的半透明薄雾；本层结构楼板与装修地板盖住背景；下层屋顶以不透明表面遮住屋内实体，迷雾信息保持隐藏。点击不同层的殖民者头像可首次正确居中。普通点选和建造只作用于查看层；攻击模式可点无遮盖的下层 Pawn，也可保留射手选择后切到上层瞄准。实际楼板及屋顶仍会挡弹。

非法空域中的新生非飞行 Pawn 会改到真实安全楼面；读档也会救回已卡空域的 Pawn，保留身份、装备和物品，只结束失效工作。

**楼层存档持续需要 RimLayers，不能安全移除。** 首次扩层不覆写源文件，正常保存后的新档包含楼层数据。楼层只通过受控追加增加，最多地表、两个上层和两个地下层；宽高、步长和已有坐标带不能重排。原生 Pocket / 独立地下地图不自动扩容；普通 NPC / 太空地图也不扩容，合法入站逆重飞船的新目的地图、已确认出发准备的既有目的地图，以及主动建成楼梯的地图除外。

## 楼层规则

- 楼梯对未牵引的放牧动物按围栏门处理；真正牵引时可以通行。畜栏仍按本层围护判定，最近目标比较实际路线。机体控制范围使用逻辑距离，正常过层保留原生控制组和 Lord 成员。
- 垂直电力端子在相邻层同一局部位置、两端启用且有支撑时并网；每层分别建造，楼梯不导电。关闭端子也切断该格的本层输电；普通设施增益限同层，合法逆重飞船设施按同一船体跨层关联。
- 完整楼板阻气。开放井口或独立授权的楼梯通道可传气、换热、传播火焰与惊扰；禁止 Pawn 走楼梯不关闭这些通道，关闭落脚门才封闭该通道。声音仍可能从其他开口绕行。
- 天空暴露按整列楼板/屋顶计算；日光、天气和游戏数值一致，不写假屋顶。人工灯光首版限同层。
- 射程以逻辑水平距离衡量，直射按目标层应用高度倍率；弹道/爆炸检查真实高度和遮挡，近战限同层。没有步行路线不直接等于不能射击。危险爆炸可驱动原版 Sprint，但不会覆盖玩家强制命令。
- 来袭物按完整占地落到最高有效楼面。提示“落点受阻”时清理物品/实墙或扩建本层，原生容器保留剩余内容物并自动重试，不必重新生成来袭物。
- 普通步行离图必须走到地面逻辑出口；家园商队仍使用原版组建流程，不用普通 Goto 绕过原版政策。

## 支持范围与验证

本轮基准为 **1.7.4982 rev1280 / Waylith / .NET 10**。0.1.2 的显示、相机和原生/自定义武器入口验证见 [显示验收](DISPLAY-VALIDATION.md)。0.1.1 在 Core 和全五 DLC 中完成出生恢复、楼层 UI、81 格原生连续施工及重启测试；旧地图含真实河流的扩层、另存再次加载与完整网格/实体审计通过。Nanami 的落点及原装备保留通过，其独立年龄/关系报错见更新记录。0.1.0 的 250×250 三层、60 名测试 NPC、12000 tick 规模记录保留在第一版验收中；新增背景的长期规模性能未由旧数据推导。

天然岩顶的本轮支持与证据见山体文档。地下层支持与实际证据见 [地下文档](UNDERGROUND.md)。三维叠层显示、CE 和具体第三方框架尚未适配；不声称任意原版/DLC Job 或其他 mod 自动兼容。不模拟建筑重量。旧档和 Nanami Startrail 的具体验证及限制见 [0.1.1 更新](USABILITY-VALIDATION.md)。

## 构建与测试

程序集/命名空间 `RimLayers`，包标识 `rimlayers.core`，单工程 `net10.0`。从游戏根目录运行：

```powershell
dotnet build Mods/RimLayers/Source/RimLayers/RimLayers.csproj -c Release
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-M1.ps1 -River -Language ChineseSimplified
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario command-input -AllDlc -Dashboard
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario reachability -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario presentation -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario usability -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario migration -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-coexistence -NativeShip -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario mountains
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario work-save
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario world-exit -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario scale -MapSize 250 -PawnCount 60 -AllDlc
```

工程用 `RimWorldDir` / `HarmonyDll` MSBuild 属性覆盖引用路径，输出仅含自己的 DLL 与辅助文件，不复制游戏或 Harmony DLL。测试只使用 `Tests/Artifacts/<run>` 的独立配置和存档，启动、退出自己的隐藏进程；等待游戏退出释放 DLL 后再构建，修改代码后必须重启。

**提交二进制与可复现性。** 工程设置了 `<SuppressImplicitGitSourceLink>true</SuppressImplicitGitSourceLink>` 和 `<Deterministic>true</Deterministic>`，版本戳固定为 `0.1.5`。不要移除这两项：SDK 默认会把当前 HEAD 的 commit hash 拼进 `InformationalVersion`（`0.1.5+<sha>`），而 hash 是编译输入，于是程序集哈希取决于构建那一刻的 git 状态而不是源码。这会让「提交了的二进制」在原理上无法复现——DLL 只能嵌它自己那笔提交**之前**的 sha，所以「提交后在 HEAD 重建并比对」永远不成立，换目录、换时间 checkout 也会得到不同 MVID。关掉 source link 之后，同一份源码树的任意两次 Release 构建逐字节一致，且与 HEAD 无关。

因此提交顺序是：源码 commit → `rm -rf obj bin` → Release 重建 → `chore(build)` 单独提交 DLL；提交后可在该 HEAD 上重建验证与已提交的那颗逐字节相同。仅把 `-p:OutputPath` 指到别处不影响结果，也不再需要「必须在真实模块路径构建」这类限制。

V1 默认 Core + Harmony + RimLayers；范围盾另加 Royalty，机体另加 Biotech，衣架容器另加 Odyssey；`-AllDlc` 启用全部官方 DLC。JSON 记录实际加载列表。其他场景见测试脚本的 Scenario 列表与验收文档。

## 文档与研究入口

- [飞船与普通楼层共存](SHIP-COLONY-COEXISTENCE.md)、[验收与复现](SHIP-COLONY-VALIDATION.md)
- [地下两层、地形限制和虫巢](UNDERGROUND.md)；[地下验收](UNDERGROUND-VALIDATION.md)
- [山体多层、矿脉与旧层迁移](MOUNTAINS.md)
- [区域、分区与楼层边界](AREAS-AND-ZONES.md)
- [0.1.5 高层背景黑屏：相机远裁剪面](CAMERA-DEPTH-FIX.md)
- [0.1.4 原版命令输入修复](COMMAND-INPUT-VALIDATION.md)
- [0.1.3 可达性与卡顿修复](REACHABILITY-VALIDATION.md)
- [0.1.2 显示验收](DISPLAY-VALIDATION.md)、[雾素材与提示词](../Textures/Atmosphere/README.md)
- [第一版验收](V1-VALIDATION.md)、[架构与阶段计划](PLAN.md)、[实施记录](WORK-LOG.md)、[历史 M1](M1-VALIDATION.md)
- [第三方许可声明](../ThirdPartyNotices.md)：部分生成适配基于 MIT ProjectZ，保留许可与归属。
- [调研结论](../../../docs/multilayer-readiness.md)、[单 Map 参考](../../../docs/multilayer-reference-isorim.md)
- [工作查询](../../../docs/multilayer-queries.md)、[任务仲裁](../../../docs/multilayer-coordination.md)、[严格建造](../../../docs/multilayer-structure.md)
- [战斗](../../../docs/multilayer-combat.md)、[公共系统](../../../docs/multilayer-services.md)、[证据基准](../../../docs/sources.md)、[工作区规则](../../../AGENTS.md)

工作区知识库链接不是运行依赖；单独移动此目录时可一并保留研究资料。

