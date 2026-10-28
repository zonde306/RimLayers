# 区域、分区与楼层边界

原版把整张地图的区域信息存在**一个扁平网格**里：`Verse.Area` 只有一个
`BoolGrid innerGrid`，尺寸等于 `map.cellIndices.NumGridCells`；`Verse.Zone` 把格子存成一个
`List<IntVec3>`。RimLayers 把 N 个楼层塞进同一张 `Map` 的存储空间
（`storage z = layerId * stride + z`，`stride` 之间还有隔离带），于是原版这两套设施
会同时看到「所有楼层 + 隔离带」。

本模块在原版画笔与自动扩散的唯一入口上加一层闸门，把这些操作限制在当前查看的楼层。

## 为什么原版代码会越界

`Verse.Designator_AreaHome`、`Designator_AreaBuildRoof`、`Designator_AreaNoRoof`、
`Designator_AreaIgnoreRoof`、`Designator_AreaSnowClear`、`Designator_AreaPollutionClear`、
`Designator_AreaAllowedExpand/Clear`、`Designator_ZoneAdd`、`Designator_ZoneDelete`
全部只做 `c.InBounds(Map)`（`Designator_ZoneAdd.IsZoneableCell` 再多查 `Fogged`、地图边带和
`CanOverlapZones`，**从不检查有没有地板**）。`InBounds` 判断的是存储地图，所以
「另一个楼层的格子」「两层之间的隔离带」都算合法。

三类次级问题来源相同：

| 入口 | 原版行为 | 后果 |
| --- | --- | --- |
| `Area.Invert()` | `innerGrid.Invert(); Drawer.SetDirty();` | 整张扁平网格取反：其他楼层与隔离带全部变 `true`，而且**不发** `AreaSource.Notify_AreaDelta`，寻路缓存与真实网格脱节 |
| `RimWorld.AutoHomeAreaMaker.MarkHomeAroundThing` / `Notify_ZoneCellAdded` | 以 `t.Position` 为中心做 `+8` / `CellRect.CenteredOn(c,4)` 的矩形，逐格 `Home[cell] = true` | 矩形在存储空间里是扁平的，建筑靠近楼层带尾部时几何上就跨到下一层 |
| `Verse.AutoBuildRoofAreaSetter.TryGenerateAreaNow` | 房间边界外扩后直接写 `map.areaManager.BuildRoof[cell] = true` | 候选集可越过房间所在楼层；写进去的格子之后会让 `HasRequiredRoof` 误判 |

`DesignationDragger.UpdateDragCellsIfNeeded` 与 `DesignatorManager.ProcessInputEvents`
的单点点击都只走 `CanDesignateCell`，所以**在 `CanDesignateCell` 上装门就能同时挡住
拖拽高亮、拖拽提交和单点**，不需要再补 `DesignateSingleCell`。

活动区还有一处不对称：原版 `PathGridJob.CostForCell` 对
`PathRequest.area` 之外的格子是 `num += Cost_OutsideAllowedArea`（600）——**软惩罚，不是墙**。
跨层连接边在扁平存储空间里是一步跳跃，原生格子代价网格看不到它，所以
`Navigation/LayerRouter.cs` 必须给落点补上同样的惩罚。`Verse.Reachability.CanReach`
原版就完全不看活动区，这里保持不加过滤。

## 实现

### `Source/RimLayers/Areas/AreaLayerRules.cs`

纯逻辑、不引用 `Verse` / `RimWorld`，供 `Tests/AreaLayerTests` 直接编译。

```csharp
public enum Gate { Allow, OffLayer, NoSurface }
public static Gate Evaluate(bool decoded, int cellLayer, int viewedLayer, bool hasSurface)
public static bool AcceptsSpread(int cellLayer, int anchorLayer, bool hasSurface)
public static bool InvertValue(bool current, bool hasSurface)
```

`Evaluate` 先判层（未解码的隔离带统一算 `OffLayer`），再判地板，因此玩家看到的是
「只能标记当前查看楼层」而不是含糊的拒绝。

### `Source/RimLayers/Areas/AreaLayerPatches.cs`

`AreaLayerGate.Check(Map, IntVec3)` 是唯一判定点：

```csharp
var owner = LayerMapComponent.For(map);
if (owner == null) return AcceptanceReport.WasAccepted;              // 非分层地图保持原版
if (!owner.TryDecode(cell, out var logical) || logical.LayerId != owner.ViewedLayer)
    return OffLayerKey.Translate();                                   // RL_AreaOffLayer
if (!owner.Structure.HasSurface(cell)) return "RL_NoSurface".Translate();
return AcceptanceReport.WasAccepted;
```

`HasSurface` 直接复用 `Structure/StructuralSupport.cs` 的既有语义（第 0 层恒有地板，
上层要 `StructuralCells` + `HasRequiredRoof`，山体自然地板与载具甲板另行放行），
所以「本层空域」会被拒而不是被当成可标记的格子。

十个 `CanDesignateCell` 用统一的 Postfix 形态接入——只在原版已经通过时才追加判定，
从不放宽原版结论：

```csharp
static void Postfix(IntVec3 c, Designator __instance, ref AcceptanceReport __result)
{
    if (!__result.Accepted) return;
    var report = AreaLayerGate.Check(__instance.Map, c);
    if (!report.Accepted) __result = report;
}
```

覆盖的类：`Designator_AreaHome`（抽象基类，`_HomeExpand`/`_HomeClear` 共用）、
`Designator_AreaSnowClear`、`Designator_AreaPollutionClear`、`Designator_AreaAllowedExpand`、
`Designator_AreaAllowedClear`、`Designator_AreaBuildRoof`、`Designator_AreaNoRoof`、
`Designator_AreaIgnoreRoof`、`Designator_ZoneAdd`（`_Stockpile`/`_Dumping`/`_Expand`/
`_Resources`/`_Growing`/`_Fishing` 全部经由它）、`Designator_ZoneDelete`。
活动区的 expand/clear 各自 `override CanDesignateCell`，没有共享入口，只能分别挂。

**参数名必须与原生一致。** Harmony 按名字绑定 Postfix 参数，名字对不上就抛
`HarmonyException`，而 `RimLayersMod` 是逐类 try/catch 的——结果是**整个补丁类被静默跳过**，
日志里只有一行，功能看起来「已实现」但完全没生效。十个里九个是 `(IntVec3 c)`，
唯独 `Designator_ZoneDelete.CanDesignateCell(IntVec3 sq)` 是 `sq`；写成 `c` 会让删除活动区的
闸门整个消失。因此 `areas` 场景额外断言 `RimLayersMod.FailedPatchClasses` 为空——
这是唯一能在运行时立刻发现参数名写错的信号，`PatchClassCount` 同时打印已安装的类数。

`HomeSpreadAroundThing` 与 `HomeSpreadAroundZoneCell` 用 Prefix 替换原版循环：

```csharp
static readonly Func<bool> ShouldAdd = AccessTools.MethodDelegate<Func<bool>>(
    AccessTools.Method(typeof(AutoHomeAreaMaker), "ShouldAdd"));
```

**必须自己复现 `ShouldAdd()` 的守卫**（`Find.PlaySettings.autoHomeArea &&
Current.ProgramState == ProgramState.Playing`）——前缀把整个方法体换掉了，
漏掉这一步就等于把玩家的「自动家庭区」开关永久打开。

`AutoRoofSpreadGate` 是 Postfix：读 `justRoofedCells` 私有列表，把落在其它层的
`BuildRoof` 撤回。用 Postfix 而不是重写整个 setter，是因为原版算法里
`RoofCollapseUtility.WithinRangeOfRoofHolder` 这类判定没有稳定的可复制性。

`AreaInvertGate` 用 Prefix 完全接管 `Area.Invert()`：只遍历
`owner.LayerRect(owner.ViewedLayer)` 里 `HasSurface` 的格子，逐格走
`__instance[cell] = ...`（即原版 `Set` → `MarkDirty` → `AreaSource.Notify_AreaDelta`），
保证寻路缓存被正确同步。

### `Source/RimLayers/Navigation/LayerRouter.cs`

原版对活动区是**两条规则并存**，两侧都反编译核实过：

| 位置 | 行为 | 依据 |
| --- | --- | --- |
| 路径代价 | 区域内外的**每个访问格**加 `Cost_OutsideAllowedArea`（600） | `PathGridJob.IndexCost`：`if (allowedGrid.Length > 0 && !allowedGrid.IsSet(index)) num += 600;` |
| 目标合法性 | 区域外的**目标格**直接不可达 | `GenPath.ShouldNotEnterCell` → `IntVec3.IsForbidden(pawn)` → `InAllowedArea` |

关键在于 `Reachability.CanReach` **不**看活动区（`Region.Allows` 里没有任何 area 分支，
IL 已逐条确认），所以 `pawn.CanReach` 为 false 与活动区无关，那是目标 forbidden 造成的。

跨层连接边在扁平存储空间里是一跳，原生格子代价网格永远看不到它，因此 router 必须
自己补上「代价」这一半：

```csharp
var allowed = PathUtility.GetAllowedArea(parms.pawn);
int LandingPenalty(IntVec3 cell) =>
    allowed is { TrueCount: > 0 } && !allowed[cell] ? PathGridJob.Cost_OutsideAllowedArea : 0;
```

覆盖率实测：区内目标 `free=492 / allowed=5892`，差 `5400 = 9 × 600`——正好是这条路线
踏在区域外的 9 个格子，说明 `LandingPenalty` 只补跨层那一步、没有重复计数。
「目标合法性」那一半由原生 `ValidateInt`/`CanReach` 负责，router 不重复实现。

`LandingPenalty` 同时进 `CrossingCost(edge)`、`CheapestEdge` 的下界计算和 `RouteBounds`
的 `directProven` 判定——下界必须含同一份惩罚，否则被惩罚的跨层边会显得比实际便宜，
错误地「证明」直达更优。

## 测试

### 纯逻辑：`Tests/AreaLayerTests`

```powershell
dotnet run --project Mods/RimLayers/Tests/AreaLayerTests/AreaLayerTests.csproj -c Release
```

直接编译 `AreaLayerRules.cs` + `Layout/LayerLayout.cs`（现有 `Tests/LayoutTests` 的
相同手法，不加载游戏程序集）。覆盖 `Evaluate` 的 `decoded × layer × viewed × surface`
全组合真值表、隔离带优先判 `OffLayer`、off-layer 与 no-surface 必须是不同 `Gate`
（否则玩家分不清两种拒绝的原因）、`AcceptsSpread` 真值表、`InvertValue` 的
双重取反映射；再对真实存储布局做全量扫描（`(40,30)`、`(120,120)`、`(17,17)`、`(75,44)`，
第 1 层每 3 列缺地板）验证三个闸门只接受 viewed 层的真实地板；最后用
`radius = stride - height + 3`、`anchorZ = anchorLayer * stride + height - 1` 复现
「建筑压在楼层带最后一行」的回归 fixture，并要求 fixture 确实同时触到隔离带和邻层
（`gapReached > 0 && offLayerReached > 0`），否则测试视为空转。

当前输出：`PASS: 1551 area layer checks.`

### 运行时：`Tests/Run-V1.ps1 -Scenario areas`

```powershell
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario areas
```

`Source/RimLayers/Diagnostics/V1Areas.cs`，`generate` + `load` 两段。
夹具在 layer 1 顶部建一条 `3×7` 的结构地板带（`areaStrip = (70,113,3,7)`），
只给这一条带地板，所以原版 `±4` 格气泡在几何上必然溢出到空域或隔离带。
取 `strip` 位置是为了在 `stride = 136`（`MapSize 120`）时确实越过楼层带尾部。

| 组 | 断言 |
| --- | --- |
| 1 画笔闸门 | `home`/`zone`/`roof` 三类：跨层 → `RL_AreaOffLayer`、隔离带 → `RL_AreaOffLayer`、本层空域 → `RL_NoSurface`、本层地板 → 接受 |
| 2 `Area.Invert()` | 只重写 viewed 层地板（`TrueCount == surfaces`），其他层与隔离带不动，且已标记的越界格被正确清掉 |
| 3 自动家庭区（建筑） | `Home.TrueCount == areaStripSurfaces` 且零越界格；`Find.PlaySettings.autoHomeArea = false` 时确实不写 |
| 4 自动家庭区（分区格） | `Zone.AddCell` 触发的 9×9 气泡同样只落在本层真实地板 |
| 5 自动屋顶 | `AutoBuildRoofAreaSetter` 在有真实上层房间时确实产出 `BuildRoof`，且每个 active cell 都在 layer 2 且 `HasSurface` |
| 6a 活动区·区内目标 | 活动区覆盖起点、所有落点与终点时，跨层路线仍然 `Found`，且 `(allowed - free)` 为 `Cost_OutsideAllowedArea` 的整数倍（实测 `5400 = 9 × 600`） |
| 6b 活动区·区外目标 | 目标不在活动区内时，跨层路线 `!Found` 且 `!CanReach`，并且 `upperTarget.IsForbidden(pawn)`（证明拒绝来自原生语义而非本模块自创规则） |
| 7 分区删除（`sq` 陷阱） | 跨层删除被 `RL_AreaOffLayer` 拒；同层删除被接受；`ZoneDeleteGate` 不在 `RimLayersMod.FailedPatchClasses` 里 |
| 8 补丁完整性 | `RimLayersMod.FailedPatchClasses.Count == 0`（任何补丁类被 Harmony 跳过就让整套测试失败） |

第 6 组容易写错，因为它同时含「代价」与「拒绝」两种语义，且两者的触发条件不同。
最初的写法把两边混成一条「限制后仍能到达 + 代价 ≥ free + 2×600」，实测立刻红：
`CanReach=False`。逐条查 IL 后才确认正确形状是 6a/6b 两条独立断言。

`load` 段（`RestoreAreas`）读 `areas-baseline.json`，按 ID 找回 pawn / zone / 两个
allowed area，断言：zone 仍在 layer 1 单格；Home **逐格**满足「可解码 + 所在层有真实地板」
（Home 本来就是多层的——原版在 map spawn 时为每个玩家建筑在它自己那层扩散，所以
`TrueCount` 会从 21 增长到 142，不可作为往返不变量）；inverted area 无空域格、无隔离带格、
且恰好只有 1 格 off-layer——就是第 2 组故意留在 layer 0 的那格；两个 allowed area 是不同条目。

## 已知边界

- 闸门用 `owner.ViewedLayer`，不是「鼠标所在格子的层」。这是刻意的：玩家看到的就是
  当前楼层，画笔跟随视野才符合直觉；`LayerView.Switch` 已经会 `EndDrag` 掉旧楼层的拖拽。
- `Area.Clear()` 没有 UI 入口（反编译里无调用者），因此没有加闸门。若以后有 mod 调用，
  需要按 `AreaInvertGate` 的方式接管。
- 原版 `Dialog_ManageAreas` 的复制按钮（`TexButton.Copy`）会逐格 `area3[cell] = true`
  复制 `ActiveCells`。因为源区域本身已被约束在本层，复制不会引入新的越界格，故不改。
- 策划（`Designator_Plan`）与地板涂色（`Designator_PaintFloor`）也只用 `InBounds`，
  但它们属于「规划/装饰」而非原版区域系统，本模块未纳入；如果要做，复用
  `AreaLayerGate.Check` 即可。
