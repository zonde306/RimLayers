# RimLayers 0.1.1：六项可用性修复

2026-10-18；RimWorld **1.7.4981 rev136 / Waylith / .NET 10 / Harmony 3**。根程序集 MVID `f1e6382600a84191bbc6d8a184fd6b57`。本页保留 0.1.1 历史增量；地板遮盖、首次相机定位、动态薄雾、武器朝向与尸体显示的后续修正见 [0.1.2 显示验收](DISPLAY-VALIDATION.md)；首版其他功能及历史规模数据见 [0.1.0 验收](V1-VALIDATION.md)。

## 重启后这样使用

- **F7 下层、F8 上层**，在“选项→按键”可重绑定；保留面板和 Alt+上/下。
- 上层楼梯口显示真实楼梯的下行图标及“↓ N 层”按钮，按钮切换视图，不创建另一份楼梯。
- 空域显示真实下层地形、静态建筑和可见 Pawn，按深度叠淡雾。普通点选、建造及格子命令仍限定查看层；攻击模式可选无遮盖的下层实体。向上瞄准可保留射手选择后切层。楼板和屋顶的物理挡弹规则继续有效。
- 选择结构楼板可看绿色可规划边界；一次拖矩形放整片蓝图。工人从真实可用边缘逐步供料、施工；蓝图本身不算可站立楼面。每格仍须有紧邻下层的有效人工屋顶。
- 新生非飞行 Pawn 的非法空域落点会重定向到真实安全格；加载结束时也救回已卡空域的 Pawn，保持实体身份、装备和物品，结束失效工作。合法落点与飞行不改。
- 旧玩家地表殖民地图首次加载时追加三层布局，保留原地面坐标。**先另存新档**：转换只改本次加载内存，正常保存才写入楼层。保存后的新档持续需要 RimLayers。已有布局不重复扩层；NPC、Pocket、地下和太空图不自动转换。

## 游戏进程验收

所有目录均为本 mod 的独立 `Tests/Artifacts` 测试数据，未使用玩家存档或修改玩家启用列表。构建为 0 警告、0 错误。

| 场景 | 证据 | 结果 |
|---|---|---|
| Core 可用性 | [191806](../Tests/Artifacts/v1-usability-20261018-191806) | 生成 17 / 新进程读档 4，零失败。非法出生、两处下行标记、81 格规划及原生连续施工、热键、背景选目标、旧空域 Pawn 身份与装备保留 |
| 全五 DLC 可用性 | [192601](../Tests/Artifacts/v1-usability-20261018-192601) | 生成 18 / 新进程读档 4，零失败；新增原版 Targeter 取得下层真实 Pawn 的调用链检查。81 格施工于 16246 tick 完成，原生 AI 实际搬料和施工，无逐圈重新指定 |
| Core 旧图首次加载 | [191919](../Tests/Artifacts/v1-migration-20261018-191919) | 原生未扩容地图生成 4 / 首次转换加载 10，零失败；新增楼梯后真实 Pawn 走到上层 |
| 全五 DLC 旧图与再次加载 | [192317](../Tests/Artifacts/v1-migration-20261018-192317) | 生成 4 / 首次加载 10 / 转换后再次加载 9，零失败。后两段复用同一有效源存档；原始失败结果保留为 `*-before-snapshot-fix-results.json`，有效日志为 `*-after-snapshot-fix-stdout.log` |
| 全五 DLC 真实河流旧图 | [194404](../Tests/Artifacts/v1-migration-20261018-194404) | 生成 5 / 首次加载 11 / 转换后再次加载 10，零失败；120×120→120×408，原生 1350 个河水格，全部 28800 个流场 float 位模式保留，上层流场为零。有效加载日志为 `*-after-bit-snapshot-stdout.log` |
| 完整地面数据审计 | 同目录 [migration-grid-audit.json](../Tests/Artifacts/v1-migration-20261018-192317/migration-grid-audit.json) | 18 项零失败：全部 3600 地面格的地形各层/颜色、屋顶、迷雾、气体、压缩物体网格及 6 个区域原始字节前缀逐一相同；5 个 Pawn ID 及 1629 个非 Pawn、非污渍持久记录的 ID/定义/材料/位置/数量一致 |

输入旧图是原版生成的 **60×60、未受管 Map**，保存后仅从测试副本移除空的 RimLayers 组件节点；加载成为 **60×204、步长 68、三层**。同存档已有的 120×408 三层 Map 不重复扩容。原生 Job loadID、床的实体/木材材质、背包银 37 的实体 ID 与数量均保留。新层为空域，隔离格不可通行。转换后的上层楼面和楼梯再次加载不被重置。

完整审计另列原版加载/模拟的变化：重建压缩石块和建筑时原版 FullRefund 会清除重叠污渍，本例 134→108；动物及测试 Pawn 正常移动。积雪随行走期间天气从约 0.35 变为 0.2899233，第二次读档为 0.2899214，与保存时快照在原版量化误差内一致。未把这些变化混作实体丢失或要求天气停止。

快捷键测试向原版 UIRoot_Play 输入 KeyDown，检查事件被消费、楼层变化且 Pawn/Job 不变。瞄准测试用定向鼠标坐标夹具调用原版 Targeter，结束后撤销夹具；目标始终是真实 Thing，不创建背景副本。

### 实际画面已检查

最终简中 UI 用 192601 的同一有效存档独立副本 [195124](../Tests/Artifacts/usability-chinese-20261018-195124) 加载，4 项零失败；面板明确显示“下层 F7 / 上层 F8”，救援消息和下行标签无缺字或截断。

- [整片蓝图、绿色边界、下行入口与下层背景](../Tests/Artifacts/v1-usability-20261018-192601/usability-planning-backdrop.png)
- [最终简中界面、快捷键方向、下行入口与淡雾](../Tests/Artifacts/usability-chinese-20261018-195124/usability-loaded-backdrop.png)
- [施工完成并重启后的下层实景及淡雾](../Tests/Artifacts/v1-usability-20261018-192601/usability-loaded-backdrop.png)
- [真实 Nanami 的空域救援及装备保留后画面](../Tests/Artifacts/v1-usability-20261018-192107/usability-loaded-backdrop.png)

真实河流目录的 [完整审计](../Tests/Artifacts/v1-migration-20261018-194404/migration-grid-audit.json) 为 19 项零失败：全部 14400 格网格、原生流场逐列重排、6 个区域、15 个 Pawn ID、3794 个静态持久实体记录均通过；污渍由原生加载清理 937→631，另列在报告中。

## Nanami Startrail：落点通过，其他报错单列

用户提供的 `3822131067` 是 **Nanami Startrail**，包 ID `shimakaze.nanami.startrail`。只读取该目录，未修改其代码、定义或 DLL。其工厂在没有已有殖民者时以 `map.Center` 为锚点，不能把真实 Map 的中心视为逻辑地面中心；RimLayers 在通用 Spawn 入口保护实际楼面，不绑定该 MOD 的私有类型。

[全 DLC + HAR + Nanami 实测](../Tests/Artifacts/v1-usability-20261018-192107)：新角色实际出现在安全地面；17 个生成阶段功能检查通过。故意将同一 `NST_Nanami11343` 存在空域后重新启动，救援、原始 `NST_SwordShield11345` 装备与整片楼板保存的 3 项均通过。

该组合的“零运行错误”检查失败：存在年龄阶段、关系/需求空引用。**[不启用 RimLayers 的独立对照](../Tests/Artifacts/nanami-control-20261018-193124/control-result.json)** 也复现 `Tried 300 times to generate age` 和 `Arrival failed`。这部分需在 Nanami 自身或其依赖组合中修复；本轮不改动创意工坊副本，不把落点通过写成全面兼容。

## 实现入口与恢复边界

- `Generation/SpawnSafety.cs`：新生落点检查、加载末尾一次恢复；不逐 tick 扫描搬动 Pawn。
- `Presentation/LayerUsability.cs` / `Defs/KeyBindingDefs/RimLayers.xml`：上层入口、规划边界及原生热键。
- `Presentation/LowerFloorBackdrop.cs` / `CombatOverlays.cs` / `LayerView.cs`：真实网格/Pawn 的显示投影、背景目标和高亮；不改模拟坐标。
- `Structure/StructuralSupport.cs`：分开规划与执行资格。供料、施工与最终提交仍使用严格执行校验，普通建筑不放宽。
- `Generation/LegacyMapMigration.cs`：原版分配前准备内存 XML、补齐类型化网格、恢复上层；保留原宽度和地面索引，随后原版恢复实体与预订。路径与网络重建，不重复深存 Pawn/Job。

旧档中的自定义第三方网格、天然岩顶及太空/地下转换仍需独立适配；真实河流旧档的完整流场与再次加载已验证，x-major 重排保留全部 float 位模式。0.1.0 的绘制计时不作为新增背景的帧率依据；本轮实际画面和有限原生模拟通过，未新增多年或大规模背景性能结论。

## 失败记录与复现

早期迁移夹具把楼梯放在承重柱原格，导致原生替换柱后无法上楼；改为相邻独立支柱后通过。再次读档最初误要求积雪仍等于行走前数值，现按保存时快照核对。河流初探 193925 选中的地块流场全零，未用作真实河流证据；194404 换成实际有河水格的河流/道路地块。最初 JSON 浮点快照与原生存档中的 float 位模式不完全一致，改用源存档的精确位模式，并复用同一输入完成再次加载；原始失败保留为 `load-before-bit-snapshot-results.json`。核对 WaterInfo.ExposeData 内部的 Scribe_Values.Look 后确认实际读取只在 LoadingVars，无需额外多阶段补丁，已撤掉临时补丁。

另一次随机新图生成出现原版 central-spot 随机兜底 Log.Error（[192759](../Tests/Artifacts/v1-migration-20261018-192759)），该次不记通过，后续复用已有效的 192317 旧档完成转换后加载和完整审计。

```powershell
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario usability -AllDlc
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario migration -River -AllDlc
python Mods/RimLayers/Tests/Inspect-Migration.py Mods/RimLayers/Tests/Artifacts/v1-migration-20261018-192317
```

`migration` 自动包含生成、旧图加载、转换后再次加载三个原生进程。`-Nanami -Har -AllDlc` 是明确的第三方组合探针，仍会如实报告上述外部错误。
