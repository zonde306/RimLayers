# 飞船与普通楼层共存：验收记录

2026-10-19；RimWorld **1.7.4982 rev1280 / Waylith / net10.0**，主程序集 MVID a46e6f45b6164261bb63a9471bf7402a。设计契约见 [SHIP-COLONY-COEXISTENCE](SHIP-COLONY-COEXISTENCE.md)。历史共存基线为本地 0.1.4；2026-10-20 停靠高度上限验证使用当前 0.1.5 开发候选。

## 2026-10-21：物品、Pawn、植物降落规则已验证

基准 **1.7.4984 rev1242**，MVID `484efdc5765b463ca9cc54a6131e38f0`。`ShipLanding.BlocksLanding` 统一四处检查：普通物品、植物和非敌对非人形 Pawn 不阻挡；人形或敌对 Pawn、显式禁止降落标记及原有建筑保护继续阻挡。没有增加额外销毁流程，继续使用原版按真实基架格清理。

最终独立运行：[v1-ship-landing-items-20261021-193127](../Tests/Artifacts/v1-ship-landing-items-20261021-193127/generate-ship-landing-items-results.json)，**37/37 通过，errors=[]**；Release 构建 0 警告/0 错误。

- 从真实引擎、驾驶台、72 格甲板与中间镂空生成三层载荷；先在地面原向放置，再次起飞，旋转 90° 停在第二层（最高第四层，独立夹具上层设置为 3）。
- 石块、钢铁、驯养/野生牦牛、橡树、草和水稻允许降落；预检不销毁实体。原生 `IsValidCell` 与多层完整检查都覆盖，显式物品/植物禁止标记、人形殖民者、敌对动物及镂空通道内墙体仍拒绝。
- 直接调用真实 `PlaceGravshipInMap`，覆盖格的物品、动物、植物按原版被销毁；镂空格和船外实体保持原对象/位置，旁边钢铁堆数量不变。镂空不补楼板；船上原银堆 37 个及对象身份保持。
- 此轮验证落点检查和实际放置，未重跑着陆动画/存档流程；未改存档字段、动画或原版放置清理。

保留的中间结果：192457 地面检查通过，高层夹具被默认 UpperLimit=2 拒绝；仅把测试配置调为 3。192616 物品/Pawn 29 项通过。192928 植物加入后 36/37，地面树/作物夹具 x=9 落入原版边缘禁建区；将整船夹具向内移 10 格后最终全部通过，未放宽边缘规则。193046 共享 Cargo 新测试缺少必需参数导致构建中止，由其任务修复后重跑；没有改写 Cargo 文件。

复现：

```powershell
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-landing-items -TimeoutSeconds 360
```

源码：`Ships/ShipLandingPatches.cs`、`Diagnostics/V1ShipLandingItems.cs`。更新 DLL 后重启游戏。落点内要保留的物资和动物应提前移走。
## 2026-10-20：超高停靠已验证

两层飞船停第2层占第2–3层，允许；停第3层占第3–4层，整船拒绝。预览将完整船底占地标红，并显示所需范围及第3层上限；中间镂空按完整载荷跨度计入高度。规则先写入共存契约，再实施校验、提示与回归。

| 语言 / 独立运行目录 | 生成 / 重载 | 结果 |
|---|---|---|
| English：v1-ship-coexistence-20261020-215450 | 41 / 6 | 全通过，errors=[] |
| ChineseSimplified：v1-ship-coexistence-20261020-215655 | 41 / 6 | 全通过，errors=[] |

本轮已执行：
- 从真实引擎、两层甲板和原版驾驶台生成126格原生载荷；二层落点通过，三层落点准确显示3–4层及上限3层。
- 原生设计器预览、BeginLanding确认和最终PlaceGravshipInMap分别拒绝；不开始动画，标记和飞船保留，随后可在合法高度放置。
- 拒绝前后核对完整地图尺寸、布局引用、所有格顶层地形/基架/屋顶及实体集合；载荷楼板记录、原物品对象和数量不变，不截断上甲板，不自动下移。
- 核对原生预览列表：超限为0格白色、63格红色；合法高度仍为原生合法格。单甲板仍可达到第3层高度，镂空三层载荷上移一层会超限。
- 沿用共存场景，真实高层停靠动画、相邻双船隔离、跨界建筑分离、普通平台恢复和重载后再次起飞均通过。
- Release构建0警告/0错误；中英文XML及三个数值占位符通过检查。实际查看两种语言的截图，整船红色斜纹与范围提示正常。

实际画面：
- [简中超限预览](../Tests/Artifacts/v1-ship-coexistence-20261020-215655/landing-height-limit.png)
- [英文超限预览](../Tests/Artifacts/v1-ship-coexistence-20261020-215450/landing-height-limit.png)

测试入口是 Source/RimLayers/Diagnostics/V1ShipLandingLimits.cs；只在独立测试配置启用。上限内按需追加楼层仍走既有确认路径；本轮没有提高地上高度上限或改变存档格式。更新DLL后须重启游戏。

## 已执行

| 场景 | 结果 | 覆盖 |
|---|---|---|
| ShipTopologyTests | 10项通过 | 用户四种示意、纯镂空跨层、普通层隔断、斜角负例、触碰双船独立与无主冲突 |
| v1-ship-structure-20261019-210128 | 55项通过、errors=[] | 第一阶段候选的设施、登船、承载、原版施工及高层单甲板落地回归；后续镂空/停靠补充另验 |
| v1-ship-coexistence-20261019-212007 | 生成22 / 重载6，errors=[] | 三层真实镂空与支撑屋顶封装/恢复；高层原版基架真实Frame；相邻两船独立；2×1跨界建筑、旋转容器、框架销毁；原金/木材对象留岸；停机坪装修、家园、仓库与移除地板指令还原；二层90°落地及存读档后再次分离 |

上述目录均位于 Tests/Artifacts/，只使用独立配置和存档。原版设计器、二层确认与着陆动画的端到端证据已包含在2026-10-20的两种语言运行中。

## 保留的失败与修复

- 210528：停机坪装修/结构/家园恢复失败，仓库仍正确。原因是屋顶 RemoveRoofUnsafe 回调提前释放覆盖记录，后续原版及扩展清理又擦掉地面；将释放限制到完整捕获清理末尾，211012 的19/6项通过。
- 211304：新镂空夹具直接往带 under terrain 的地面 SetFoundation，原版拒绝。按原版层栈顺序清理夹具，不放宽游戏API。
- 211411：夹具用对外二维 Roofs getter 查高层屋顶，断言错误；改核对真实补充载荷。连续第二艘船封装另触发原版空引用。
- 211730 / 211839：逐阶段追踪定位第二次空引用在 CheckAffectedGoodwill。旧归一化循环清空了所有 tmp 字典，其中包含原版静态 tmpFactionMembersKidnappedCount；改为只清实例缓存。212007 同进程连续两船封装、保存重载再次封装通过，未跳过原版友好度处理。

## 复现

    dotnet run --project Mods/RimLayers/Tests/ShipTopologyTests -c Release
    pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-coexistence -NativeShip -AllDlc -TimeoutSeconds 480
    pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-coexistence -NativeShip -AllDlc -Language ChineseSimplified -TimeoutSeconds 480
    pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario gravship -NativeShip -AllDlc -EngineLayer 1 -TimeoutSeconds 480

原版完整航行/返航的独立最终结果未收录在本记录；上方记录覆盖本轮停靠上限与共存场景。旧版飞行载荷仍读版本1/2；本轮地图覆盖/归属扩展和可选支撑屋顶需当前DLL，勿用旧DLL覆盖新存档。
