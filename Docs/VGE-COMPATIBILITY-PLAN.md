# VGE 兼容未完成项：修复实施方案（调研稿）

基准：本机 1.7 程序集与两个工坊目录（2026-10-25 复核）。

- 游戏：`Assembly-CSharp.dll` MVID `d368493b52bd4740bc7507c1b0386895`
- Chapter 1：`3609835606/1.7/Assemblies/VanillaGravshipExpanded.dll` MVID `57be97aa5bc146fbafd0a60f764bf91e`
- Chapter 2：`3799737423/1.7/Assemblies/VanillaGravshipExpanded2.dll` MVID `05f048ed829d4ceca8a79bd49c74b77c`
- PipeSystem（VEF 自带）：`2023507013/1.7/Assemblies/PipeSystem.dll` MVID `247f4d6406454186872bd544c096fe06`

本文只给方案与证据，不改代码。所有“未验证”的条目都标了需要的游戏内复核。

---

## 0. 结论摘要

| # | 现象 | 根因（已核实） | 修法 | 规模 |
|---|---|---|---|---|
| 1 | 控制台放在与引擎不同层会提示“未连接引擎” | `CompGravshipFacility.CompInspectStringExtra` 用**平面** `InHorDistOf` + `listerBuildings.AllBuildingsColonistOfClass` 判定“是否在引擎范围内”；`CanBeActive` 已被 RimLayers 接管，但**检视文本没有** | Postfix 重算该两行；必要时同时接 `CompPilotConsole.CompInspectStringExtra` 的“可达性”入口 | S |
| 2 | 结构楼板建错了拆不掉 | `TerrainDef.Removable => layerable`，而 `RL_ShipStructuralFloor` 是 `isFoundation=true` + `layerable=false` → `CanRemoveFoundationAt` 恒 false；`ShipFoundationRemovalAvailability` 是 **`__result&&` 守卫的 Postfix**，永远救不回来。另外带装修的船板：`CanRemoveTopLayerAt` 要求 `underGrid!=null`，而 `SetFoundation` 要求 `underGrid==null` → 装修层也拆不掉，且 `RemoveTopLayer` 会走成 `RemoveFoundation` | 可用性补丁改 Prefix（无守卫）；为“船板上还有装修层”补 `CanRemoveTopLayerAt` + `RemoveTopLayer` 的单独分支 | M |
| 3 | 宇航燃料/氧气管道不跨层 | `PipeSystem.PipeNetManager.NeighbourThingsCardinal` 只有 `GenAdjFast.AdjacentCellsCardinal`（同层四邻），`networkGrid`/`valveGrid` 是 map 尺寸 BoolGrid，本身支持分层，缺的只是**邻接** | Prefix 替换 `NeighbourThingsCardinal`，加入同 (x,z) 上下层邻居；新建 `PipeLinks` 服务负责“结构变化后按列重新注册”，对齐现有 `PowerLinks` 设计 | M |
| 4 | VGE 大量建筑不能跨层连引擎 | 现补丁只认 `CompProperties_GravshipFacility`/`CompGravshipFacility`。VGE 的 `VGE_GravshipStructuralBeam` 用的是**普通** `CompProperties_Facility`（`linkableBuildings` 指向引擎）→ 两条设施筛选、`IsFacilityActive` 都把它当“非船设施”按同层处理；`PlaceWorker_InRangeOfGravSource`（延展器/放大器选址）未接；`engine.ValidSubstructure` 仍是**单层**，被 VGE 的 `CompHeatManager`、`GravheatUtility`、`SectionLayer_GravshipMask` 直接读取 | ①设施筛选从“Gravship 专用 comp”泛化到“父物体是引擎 + 设施 linkableBuildings 含该引擎”；②接 `PlaceWorker_InRangeOfGravSource`；③Postfix `Building_GravEngine.ValidSubstructure`/`ValidSubstructureNoRegen` 返回全甲板集合（单点修复多个消费者） | M |
| 5 | 3 种引擎都要算引擎 | `EngineDef` 是**硬编码 defName 白名单**（`GravEngine` + `VGE_GravjumperEngine` + `VGE_GravhulkEngine`），第 3/4 个引擎或改名就漏 | 改为按类型派生：`GenTypes.SameOrSubclassOf<Building_GravEngine>(def.thingClass)`，与 VGE 自己的 `GravshipHelper.GravEngineDefs` 口径一致；玩家归属仍由 `Core()`/`Faction==Player` 过滤，敌方/机械族引擎自然排除 | S |

S≈0.5 天，M≈1–2 天（含游戏内验证）。

---

## 1. 控制台跨层“未连接引擎”

### 证据

原生 `RimWorld.CompGravshipFacility`：

```csharp
public override bool CanBeActive {
  get {
    // 已有 RimLayers Prefix 接管（Ships/ShipFacilityPatches.cs:8-19）
    foreach (var e in tmpEngines)                    // listerBuildings.AllBuildingsColonistOfClass
      if (!parent.Position.InHorDistOf(e.Position, Props.maxDistance)) return false;
      ...
  }
}
public override string CompInspectStringExtra() {    // 未被接管
  if (LinkedBuildings.NullOrEmpty() || !CanBeActive)  => "NotConnectedToGravEngine"
  bool flag = false;
  foreach (var e in parent.Map.listerBuildings.AllBuildingsColonistOfClass<Building_GravEngine>())
    if (parent.Position.InHorDistOf(e.Position, Props.maxDistance)) flag = true;
  if (!flag) => "MessageMustBePlacedInRangeOfGravEngine"   // 跨层时恒为真
}
```

`IntVec3.InHorDistOf` 只用 `x/z`（`Verse/IntVec3.cs:173`）。RimLayers 分层把逻辑层映射到存储 `z = layer*Stride + z`（`Layout/LayerLayout.cs:94-96`，`Stride` 至少比地图高 17 行），因此**同一条船上下层的平面距离是几十格**，超过 200/500 之外的 `maxDistance` 时就会出现：

- 设施其实已 `LinkedBuildings` 且 `CanBeActive=true`（RimLayers 已接管），但检视里仍多出一行红字“必须放置在逆重引擎的作用范围内”；
- 若 `LinkedBuildings` 因为别的原因没建立，玩家看到的第一行就是“未连接至逆重引擎”，且**放置预览**也没有可链接提示。

注意 `CanBeActive` 的原生循环本身还有一个“原版就有的”缺陷：任一引擎超距就 `return false`，不会继续找近的那个。多船/多层时这一条会被放大。RimLayers 的 Prefix 已绕开它，属于同一类修法。

### 方案

1. `Ships/ShipFacilityPatches.cs` 新增 `ShipFacilityInspect`：

```csharp
[HarmonyPatch(typeof(CompGravshipFacility),nameof(CompGravshipFacility.CompInspectStringExtra))]
internal static class ShipFacilityInspect
{
    static void Postfix(CompGravshipFacility __instance,ref string __result)
    {
        if(LayerMapComponent.For(__instance.parent.Map) is not {} o) return;
        // 只重算“未连接 / 不在范围”这两行，其余（stat 偏移、maxSimultaneous）保持原样。
        // 连接判定用 o.Ships.Belongs + 逻辑距离，与 CanBeActive 同一口径。
        ...
    }
}
```

实现要点：

- 不要 `return false` 重写整个方法（原生还会输出 `StatOffsets`、`MaxConnectedString`），用 Postfix 逐行替换更稳。
- “是否在范围内”改成：`o.Ships.Get(engine) is {Ambiguous:false}` 且 `o.Ships.Distance(设施, 引擎) <= maxDistance`；`engine` 取 `__instance.engine`，为空时用 `o.Ships.BuildOwner(parent.Position)` 兜底（放置预览阶段没有 link）。
- 文案仍用原生 key（`NotConnectedToGravEngine` / `MessageMustBePlacedInRangeOfGravEngine`），避免新增翻译。

2. 若复核发现“放不下去”而不是“文本错误”，则补 `PlaceWorker_InRangeOfGravEngine.DrawGhost`（VGE 的 transpiler 只替换 `ThingsOfDef`，仍用 `InHorDistOf`），以及 VGE `PlaceWorker_InRangeOfGravSource`（见 §4.2）。

3. 需要游戏内复核的点（当前无法从代码确定用户看到的具体字符串）：
   - 放 `VGE_PilotCockpit` 到上层，记录 `AcceptanceReport.Reason` 与检视文本；
   - 若 reason 是 `TerrainCannotSupport_TerrainAffordance`，那是“建在普通结构楼板而不是飞船结构楼板”（`RL_StructuralFloor` 的 affordances 不含 `Substructure`，`RL_ShipStructuralFloor` 才有）——属于 §2 的“建错了”范畴，不是设施补丁问题。

---

## 2. 结构楼板建错了拆不掉

### 证据 A：船板地基 `Removable=false`

```csharp
// Verse/TerrainDef.cs:304
public bool Removable => layerable;

// Verse/TerrainGrid.cs:493-500
public bool CanRemoveFoundationAt(IntVec3 c)
  => foundationGrid[num] != null && foundationGrid[num].Removable;
```

`Defs/TerrainDefs/RimLayers_ShipFloor.xml`：`<isFoundation>true</isFoundation><layerable>false</layerable>` → `Removable=false` → `CanRemoveFoundationAt` 恒 false。

RimLayers 现有补丁（`Ships/ShipFoundationRemoval.cs:7-12`）：

```csharp
static void Postfix(IntVec3 c,Map ___map,ref bool __result)
{ if(__result && ... ) __result=o.Structure.CanDismantleSlab(c); }
```

`__result` 本来就是 `false`，Postfix 永不生效。**这就是“拆不掉”的主因**（原生 `Designator_RemoveFoundation.CanDesignateCell`、VGE `Designator_RemoveSubstructure.CanDesignateCell`、`WorkGiver_ConstructRemoveFoundation.HasJobOnCell`、`JobDriver_RemoveFoundation.DoEffect` 全部先看它）。

### 证据 B：船板上还有装修层时，装修层也拆不掉

```csharp
// Verse/TerrainGrid.cs:479-487
public bool CanRemoveTopLayerAt(IntVec3 c)
{
  if (tempGrid[num]?.Removable == true) return true;
  if (topGrid[num].Removable) return underGrid[num] != null;   // ← 要求有 under
  return false;
}
// Verse/TerrainGrid.cs:289-292
if (foundationGrid[num] != null && underGrid[num] == null) { RemoveFoundation(c, doLeavings); return; }
```

`SetFoundation` 明确拒绝 `underGrid != null`（`TerrainGrid.cs:319-345`）。所以“船板地基 + 木地板装修”这种状态：`top=WoodPlankFloor(layerable)`、`under=null`、`foundation=RL_ShipStructuralFloor` → `CanRemoveTopLayerAt=false`；即便放行，`RemoveTopLayer` 也会转去 `RemoveFoundation`，把整块甲板拆掉而不是只拆装修。

RimLayers 现有 `Structure/ConstructionPatches.cs`：

- `StructuralFloorRemovalPatch`（:105-125）在 `FoundationAt(c)!=null` 时直接 `return true`（交回原生）→ 命中上面的错误分支；
- `StructuralRemovalAvailabilityPatch`（:141-150）只在 `IsBareSlab(c) && FoundationAt(c)==null` 时接管。

### 方案

1. **可用性改 Prefix（无 `__result` 守卫）**，`Ships/ShipFoundationRemoval.cs`：

```csharp
static bool Prefix(IntVec3 c,Map ___map,ref bool __result)
{
    if(LayerMapComponent.For(___map) is not {} o) return true;
    if(___map.terrainGrid.FoundationAt(c) is not {IsSubstructure:true} f) return true;
    if(!o.TryDecode(c,out var l) || l.LayerId<=0) return true;
    // 只接管“我们自己的船板”：RL 船板，或本层已登记的结构格
    if(f!=ShipDeckTerrain.Def && !o.StructuralCells.Contains(c)) return true;
    __result=o.Structure.CanDismantleSlab(c);
    return false;
}
```

2. **装修层单独分支**：`CanRemoveTopLayerAt` 前缀里，当 `FoundationAt(c)==ShipDeckTerrain.Def && UnderTerrainAt(c)==null && TempTerrainAt(c)==null` 时，`__result = TerrainAt(c).Removable && !IsBareSlab(c)`（即“有装修层才允许拆装修”）。

3. **`RemoveTopLayer` 对应分支**：同样条件下只清装修层（`GenLeaving.DoLeavingsFor` + 把 top 设回 `RL_OpenAir`），绝不进 `RemoveFoundation`；保留 `StructuralFloorRemovalPatch` 现有的“无地基裸板”逻辑。

4. 保留既有护栏不变：`CanDismantleSlab` 仍要求无屋顶、上方列无结构/屋顶、格内无 Pawn/Building/Blueprint/物品、有合法邻格（`Structure/StructuralSupport.cs:43-55`）。

5. 回归注意：`Diagnostics/V1Gravship.cs:395-402`（`foundation-removal-cannot-delete-support-under-occupied-building`、`bare-ship-foundation-refunds-exact-native-recipe-once`）与 `V1ShipCoexistence.cs:66` 依赖“有建筑时不可拆”，改 Prefix 后必须仍为 false——因为 `CanDismantleSlab` 已含该条件。

---

## 3. 宇航燃料 / 氧气管道跨层

### 证据

```csharp
// PipeSystem/PipeNetManager.cs（全部网络注册/合并/拆分都走它）
private List<Thing> NeighbourThingsCardinal(Thing thing) {
  List<IntVec3> cells = GenAdjFast.AdjacentCellsCardinal(thing);  // 只有同层四邻
  ... map.thingGrid.ThingsListAt(cell) ...
}
// PipeNet.RegisterComp / UnregisterComp：只改 networkGrid（map 尺寸 BoolGrid，分层存储天然可用）
// GetPipeNetAt(cell, def) => networkGrid[cell] || valveGrid[cell]
```

即：**存储层已经分层，缺的只是邻接**。RimLayers 已有完全对口的先例：`Environment/PowerLinks.cs` + `Environment/PowerPatches.cs:10-24`（transpiler 把 `GenAdj.CellsAdjacentCardinal` 换成 `PowerLinks.Neighbors`，含 `VerticalNeighbors`），并用 `Register/Flush/Changed/AfterUpdate` 在结构变化后让原生重建网络。

### 方案

新增 `Environment/PipeLinks.cs`（PipeSystem 为可选依赖，全部走反射，模式同 `VanillaGravshipCompatibility`）：

1. `VanillaGravshipCompatibility.Install` 里探测 `AccessTools.TypeByName("PipeSystem.PipeNetManager")`；存在才启用，记 `PipeSystemActive`。
2. **邻接 Prefix**：

```csharp
[HarmonyPatch] // TargetMethod = PipeSystem.PipeNetManager.NeighbourThingsCardinal(Thing)
static bool NeighboursPrefix(Thing thing, Map ___map, ref List<Thing> __result)
{
    // 同层四邻（保持原生顺序：cardinal[0..3]）
    // + 同 (x,z) 的上一层、下一层格子里所有 Thing
    // 条件：上下两格都存在 CompResource 且 pipeNet 相同，且
    //       (a) 两格都属于同一条玩家船（Ships.OwnerAt 相同且非 null），或
    //       (b) 该列存在显式竖井（RL 楼梯/电梯列，复用 owner.Connectors.TouchesColumn）
}
```

用 Prefix 而不是 transpiler 的理由：`NeighbourThingsCardinal` 是私有方法、返回 `List<Thing>`，Prefix 直接给结果最稳，且一次性覆盖 `RegisterConnector`、`UnregisterConnector`、`CreatePipeNetFrom` 三个调用点（合并/拆分语义自动正确）。

3. **结构变化后重注册**（对齐 `PowerLinks.Flush`）：
   - `PipeLinks.Mark(IntVec3)`：记录 `(x,z)` 列；
   - `LayerMapComponent.Changed`（`LayerChangeKind.Structure`）里标记该列；
   - `Ships.Tick()`（`ShipStructureService.cs:169-173` 已有 `Relink` + `ForceSubstructureDirty`）之后，对标记列里的每个 `CompResource` 调用 `PipeNetManager.UnregisterConnector` → `RegisterConnector`；
   - 只处理“该列确实有 `CompResource`”的格，避免全图扫描。
4. 阀门：`CompPipeValve.TransmitResourceNow` 走 `RegisterConnector/UnregisterConnector`，自动继承；`valveGrid` 仍是 map 尺寸 BoolGrid，无需改。
5. 竖井规则（决策点，见 §6）：默认建议 **“同一条船内同 (x,z) 相邻层自动连通”**（氧气/燃料管道本来就是穿甲板立管），不引入新建筑；若要求显式立管，则新增 `RL_PipeRiser`（多 `CompResource`，每个 pipeNet 一个），把条件 (a) 换成“该列有 riser”。

风险与护栏：

- 只允许 `pipeNet` 相同的 `CompResource` 相连（原生已如此），不会把氧气接到燃料。
- 只在 `OwnerAt != null`（同一条玩家船）或显式 riser 时纵向连通，避免把殖民地地面管道和任意上层“免费”连通。
- `PipeNet.networkGrid` 不因纵向边而变化，`GetPipeNetAt` / 覆盖图仍按层显示，视觉不串层。
- 若某 mod 的 `pipeNetClass` 在 `RegisterComp` 里做了自定义分组（VGE `AstrofuelPipeNet.thrusters`），纵向合并会走同一个 `RegisterComp`，无需特殊处理。

---

## 4. VGE 建筑跨层连引擎

### 4.1 已覆盖的部分（无需改动）

`Ships/ShipFacilityPatches.cs` 已接管：

| 原生成员 | 覆盖情况 |
|---|---|
| `CompGravshipFacility.CanBeActive` | Prefix，逻辑距离 + `Ships.CanConnect` |
| `CompAffectedByFacilities.PotentialThingsToLinkTo` | Prefix，`Ships.Belongs` + 逻辑距离 + `maxSimultaneous` |
| `Building_GravEngine.LooselyConnectedToGravEngine` | Prefix，`Ships.Belongs` |
| `PlaceWorker_InRangeOfGravEngine.AllowsPlacing` | Prefix，`Ships.BuildOwner` + 逻辑距离 |
| `GravshipUtility.InsideFootprint` | Prefix（`thingToExclude==null` 时） |
| `CompAffectedByFacilities.CanLinkTo` | Prefix（归属校验） |
| `Environment/FacilityPatches.cs` `CanPotentiallyLinkTo_Static` | Prefix（引擎↔Gravship 设施） |

### 4.2 漏掉的部分（本次要补）

**A. 普通 `CompProperties_Facility` 的引擎设施（结构性横梁）**

`VGE_GravshipStructuralBeam`（`Buildings_StructuralBeam.xml`）：

```xml
<li Class="CompProperties_Facility">      <!-- 不是 Gravship 版 -->
  <maxSimultaneous>9999</maxSimultaneous><requiresLOS>False</requiresLOS><maxDistance>9999</maxDistance>
</li>
<li Class="CompProperties_SubstructureFootprint"><radius>3.9</radius></li>
<li Class="VanillaGravshipExpanded.CompProperties_StructuralBeam"/>
```

引擎的 `linkableFacilities` 里有 `VGE_GravshipStructuralBeam`（两个 VGE 引擎与原生引擎补丁都加了），所以 `linkableBuildings` 反向含引擎。

但两条筛选都要求 `GetCompProperties<CompProperties_GravshipFacility>()` / `TryGetComp<CompGravshipFacility>()`：

- `Environment/FacilityPatches.cs:14` → 横梁不命中 → 落到 `PowerLinks.SameLayer` → 跨层 false；
- `Ships/ShipFacilityPatches.cs:29` → 横梁不是候选 → 永远连不上；
- `Ships/ShipFacilityPatches.cs:91`（`CanLinkTo`）同理；
- `Environment/FacilityPatches.cs:26`（`IsFacilityActive`）的早退条件也只认 `CompGravshipFacility`，横梁会走同层判定。

修法：把判定抽成一个共享谓词

```csharp
// 引擎的“可链接设施”：任何 CompFacility，只要其 props.linkableBuildings 含该引擎 def
internal static bool EngineFacility(Thing engine, Thing facility) =>
    engine is Building_GravEngine e && facility.TryGetComp<CompFacility>() is {} f &&
    f.Props?.linkableBuildings?.Contains(e.def)==true;
```

- `CanPotentiallyLinkTo_Static`：命中后按 `CompProperties_Facility` 语义处理——`mustBePlacedAdjacent`/`mustBePlacedAdjacentCardinalToBedHead` 仍要求同层相邻；`maxDistance/minDistance` 用 `Ships.Distance`；`requiresLOS` 保持原生（`GenSight` 同层判定，跨层直接跳过 LOS，因为横梁/管道本来就不看视线）。
- `PotentialThingsToLinkTo` / `CanLinkTo` / `IsFacilityActive`：把 `CompGravshipFacility` 判定换成上面的谓词；`maxSimultaneous` 取 `CompProperties_Facility.maxSimultaneous`（横梁 9999）。
- 注意 VEF 的 `AffectedByFacilitiesExtension` / `FacilityExtension.copyLinksFrom` 在 `LongEventHandler.ExecuteWhenFinished` 里补 `linkableFacilities`/`linkableBuildings`（`VEF.Buildings/AffectedByFacilitiesExtension.cs:33-60`），所以**谓词必须在运行时查 `linkableBuildings`，不能在静态初始化时缓存**。

**B. `PlaceWorker_InRangeOfGravSource`（VGE 延展器/放大器选址）**

```csharp
// VanillaGravshipExpanded/PlaceWorker_InRangeOfGravSource.cs
foreach (var def in ((CompProperties_Facility)props).linkableBuildings)
  foreach (var item in map.listerThings.ThingsOfDef(def))
    if (loc.InHorDistOf(item.Position, radiusOrMaxDistance)) return WasAccepted;
// 另有 VGE_GravFieldAmplifier 的同样循环
```

跨层时 `InHorDistOf` 恒超距 → 上层放不了延展器/放大器。修法：`Ships/ShipFacilityPatches.cs` 加同名 Prefix，用 `Ships.BuildOwner(loc)`（放置预览时引擎未 link）+ `Ships.Distance`，半径优先 `CompSubstructureFootprint.radius`，否则 `maxDistance`。`DrawGhost` 同样补一条（只影响连线提示，可后置）。

**C. `engine.ValidSubstructure` 仍是单层**

原生 `Building_GravEngine.ValidSubstructure` 返回 `validSubstructure`（引擎所在层单层洪泛）。VGE 直接消费它的地方：

- `CompHeatManager`（`Engine.ValidSubstructure.Contains(...)` 决定“船内房间”）→ 上层房间不参与热管理；
- `GravheatUtility.IsRoomProtectedByGravheatAbsorber` → 上层房间不受热吸收器保护；
- `SectionLayer_GravshipMask.IsValidSubstructure` 用 `ValidSubstructureNoRegen` → 遮罩/船体描边只画引擎层；
- VGE `CompPowerPlantGravEngine`（已由 `PowerDecks` transpiler 单独修过）；
- `GravshipUtility.DestroyTreesAroundSubstructure(engine.ValidSubstructure)`（起飞前清树，VGE 仪式入口也调）。

修法（**单点高杠杆**）：Postfix 两个 getter

```csharp
[HarmonyPatch(typeof(Building_GravEngine),nameof(Building_GravEngine.ValidSubstructure),MethodType.Getter)]
[HarmonyPatch(typeof(Building_GravEngine),nameof(Building_GravEngine.ValidSubstructureNoRegen),MethodType.Getter)]
static void Postfix(Building_GravEngine __instance,ref HashSet<IntVec3> __result)
{
    if(LayerMapComponent.For(__instance.Map)?.Ships.Get(__instance) is {Ambiguous:false} s) __result=s.Cells;
}
```

注意：

- 返回 `ShipStructure.Cells`（已缓存的 HashSet），**不要复制**（getter 在渲染/热管理热路径上）。消费方都是只读，需在注释里写清契约。
- `AllConnectedSubstructure*` 不动：`LooselyConnectedToGravship`/`ValidSubstructureAt`/`OnValidSubstructure` 已由 RimLayers 单独接管，改它只会引入回归。
- `UpdateSubstructureIfNeeded` 内部用的是**字段**，不会递归。
- 回归重点：`ShipLayers.Eligible`（`ShipLayerData.cs:83-86`）与 `ShipLayerData.AttachNativeSingle` 会读该属性，需确认语义仍成立（上层也算“本船甲板”，对它们更正确）。

### 4.3 明确不做的

- 不改 VGE 的“全局单活动引擎”规则（`PlaceWorker_OnlySingleGravEngine` + `GravEngineTracker`），它按 `GravshipHelper.GravEngineDefs` 全类扫描，与 §5 的口径一致。
- 不改 `CompMultipleGravEnginesHandler`、`RitualOutcomeComp_MannedGravshipFacilities` 等只看 `engine.GravshipComponents` 的逻辑（链接一旦成立就自然正确）。
- 不改 VGE 的仪式、舰战、干扰器、远程锁定。

---

## 5. 三种引擎都算引擎

### 证据

```csharp
// RimLayers/Compatibility/VanillaGravshipCompatibility.cs:22-23
internal static bool EngineDef(ThingDef def)=>def==ThingDefOf.GravEngine ||
    Active && def.defName is "VGE_GravjumperEngine" or "VGE_GravhulkEngine";
```

VGE 自己的口径（`GravshipHelper..cctor`）：

```csharp
GravEngineDefs = DefDatabase<ThingDef>.AllDefs
    .Where(x => GenTypes.SameOrSubclassOf<Building_GravEngine>(x.thingClass)).ToArray();
```

差异：

- 今天两者等价（`GravEngine`、`VGE_GravjumperEngine`、`VGE_GravhulkEngine` 三个 def 的 `thingClass` 都是 `Building_GravEngine`）；
- 但白名单是**按名字**的：任何新增/改名/第三方引擎（VGE 后续章节、兼容 mod）都会被 RimLayers 当成“普通建筑”，出现“引擎不认、船体不成立、设施不跨层”。
- 敌方/机械族引擎本来就不是子类：`VanillaGravshipExpanded2.Building_EnemyGravEngine : Building`、`VanillaGravshipExpanded.Building_MechanoidGravEngine : Building` → 按类型派生天然排除，不需要再靠 defName 排除。

### 方案

```csharp
private static ThingDef[] engineDefs = Array.Empty<ThingDef>();
private static void RefreshEngineDefs() =>
    engineDefs = DefDatabase<ThingDef>.AllDefsListForReading
        .Where(d => d.thingClass != null && typeof(Building_GravEngine).IsAssignableFrom(d.thingClass))
        .ToArray();

internal static bool EngineDef(ThingDef def) => engineDefs.Contains(def);
internal static IEnumerable<Building_GravEngine> Engines(Map map) =>
    engineDefs.SelectMany(d => map.listerThings.ThingsOfDef(d)).OfType<Building_GravEngine>();
```

- 在 `Install()` 里（Def 数据库已就绪）刷新一次；`ModsConfig` 变更或读档后重刷（`RimLayersMod` 已有 `LongEventHandler.ExecuteWhenFinished`）。
- `Engines()` 从 `AllThings.OfType<>()`（全图遍历）改为按 def 取，顺带修一个热路径。
- 保留 `Core()` 的 `Faction==Faction.OfPlayer` 与 `ShipStructureService.Core` 的玩家归属过滤——这才是“敌方特殊引擎不视作玩家多层船”的正确闸门。
- 若 VGE 未启用，集合只剩 `GravEngine`，与纯原生行为一致。

> 需要用户确认的“第 3 种”具体指谁（见 §6 问题 2）。若是 `VGE_MechanoidGravEngine`（`Building`，不可建造、摧毁给 gravcore），按类型派生仍会排除它；若要把它也算作“引擎”（例如允许它作为多层平台核心），那需要单独把它加进来并同时放开 `Faction==Player` 的假设——语义上不建议。

---

## 6. 待确认的决策点

1. **管道跨层模型**：同一条船内同 (x,z) 相邻层自动连通（推荐，零新内容，符合“穿甲板立管”直觉），还是必须建显式竖管建筑（更贴近 RimLayers 电力的 `RL_PowerRiser` 风格，但 VGE 玩家要重新学一套）？
2. **“3 种引擎”的第三种**：指 VGE 章节内 `gravjumper / 原版 grav engine（VGE 改名 gravship engine）/ gravhulk`（现状已支持，§5 只是把口径改成类型派生），还是指机械族 `VGE_MechanoidGravEngine`？
3. **是否允许覆盖 `ValidSubstructure`**（§4.2C）：这是收益最高、也是唯一会改变原生属性语义的一步。若担心第三方 mod 依赖“单层”语义，退路是逐个接 `CompHeatManager`、`GravheatUtility`、`SectionLayer_GravshipMask` 三个消费者（工作量 +2，风险更低）。

---

## 7. 实施顺序与验证

### 顺序

1. §2 拆不掉（最影响玩家，纯本地逻辑，先修先验）。
2. §1 检视文本 + §5 引擎口径（小改动，立刻改善“提示未连接”）。
3. §4.2A/B 普通设施泛化 + 延展器选址。
4. §4.2C `ValidSubstructure`（单独一次提交，便于回滚）。
5. §3 管道跨层（新服务，最后做，独立开关）。

### 静态检查

```powershell
dotnet build 'Mods/RimLayers/Source/RimLayers/RimLayers.csproj' -c Release '-p:RimWorldDir=G:/SteamLibrary/steamapps/common/RimWorld'
```

要求 0 warning / 0 error，且 `Assemblies/` 里只有 `RimLayers.dll`（不带游戏 DLL）。

### 游戏内验证矩阵

现有场景先回归（不改配置）：

```powershell
./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-vge -Vge 1 -AllDlc -TimeoutSeconds 480
./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-vge -Vge 2 -AllDlc -TimeoutSeconds 480
./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario gravship -NativeShip -AllDlc -EngineLayer 1 -TimeoutSeconds 480
./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-structure -AllDlc -TimeoutSeconds 480
```

新增断言（建议并入 `Diagnostics/V1Vge.cs`，或新场景 `ship-vge-decks`）：

| 断言 | 覆盖 |
|---|---|
| 上层放 `VGE_PilotCockpit`：`AcceptanceReport.Accepted`，且检视文本不含 `MessageMustBePlacedInRangeOfGravEngine` | §1 |
| 上层 console `CanBeActive` 为真且 `engine` 已链接 | §1 |
| `map.terrainGrid.CanRemoveFoundationAt(上层船板)` 为真；`RemoveFoundation(c,true)` 后该格 `HasSurface==false` 且退款一次 | §2A |
| 船板 + `WoodPlankFloor`：`CanRemoveTopLayerAt` 为真；`RemoveTopLayer` 后**地基仍在**、装修消失 | §2B |
| 有建筑/物品/上层屋顶时 `CanRemoveFoundationAt` 仍为假 | §2 护栏 |
| 上层 `VGE_GravshipStructuralBeam` 能链接引擎，且为引擎贡献 footprint | §4.2A |
| 上层 `VGE_GravFieldAmplifier` 能放置（`PlaceWorker_InRangeOfGravSource`） | §4.2B |
| `engine.ValidSubstructure` 含上层甲板格（`Count` 与 `Ships.Get(engine).Cells.Count` 一致） | §4.2C |
| 上层氧气罐 + 管道 → 下层泵：`GetPipeNetAt` 同一 `PipeNet`，`PipeNet.Stored` 跨层共享 | §3 |
| 上层燃料罐 + 管道 → 下层推进器：`CompResourceThruster.HasFuel` 为真、`CanBeActive` 为真 | §3 |
| 拆掉上层管道后网络重新分裂（`pipeNets.Count` 增加），地面殖民地管道不受影响 | §3 护栏 |
| 用任意非白名单引擎 def（测试夹具里克隆 `GravEngine` 改 defName）也能被 `EngineDef` 认可 | §5 |

同时确认 `errors=[]`（现有 `Run-V1.ps1` 已校验），并检查 `Player.log` 没有新增 Harmony transpiler 失配错误（VGE 的 `PlaceWorker_InRangeOfGravEngine_AllowsPlacing_Patch` 期望 1 处 `ThingsOfDef`，RimLayers Prefix 短路后不再执行，不应报错；`ListAllGravEngines_Patch` 期望 1 处，也不受影响）。

### 文档更新

- 本文件 → 实施完成后改写为结论，并把已验证项并入 `Docs/VGE-COMPATIBILITY.md`（新增“跨层设施/管道/拆除”三节 + 证据目录）。
- `Docs/WORK-LOG.md` 追加一条带 `Tests/Artifacts/<run>` 的记录。
- 若新增 `RL_PipeRiser`，补 `Languages/{English,ChineseSimplified}/Keyed/RimLayers.xml` 与 `Docs/DECKS-AND-ELEVATORS.md` 的竖井说明。

---

## 8. 附：本轮核实到的原生/VGE 关键事实（可直接引用）

1. `TerrainDef.Removable => layerable`（`Verse/TerrainDef.cs:304`）。
2. `CanRemoveFoundationAt` 只看 `foundationGrid.Removable`（`Verse/TerrainGrid.cs:493-500`）。
3. `CanRemoveTopLayerAt` 在 top 可移除时要求 `underGrid != null`（`TerrainGrid.cs:479-487`）；`SetFoundation` 要求 `underGrid == null`（`TerrainGrid.cs:319-345`）。
4. `RemoveTopLayer` 在 `foundation!=null && under==null` 时转调 `RemoveFoundation`（`TerrainGrid.cs:289-292`）。
5. `CompGravshipFacility.CanBeActive` / `CompInspectStringExtra` 都用 `IntVec3.InHorDistOf`（只算 x/z）遍历 `listerBuildings.AllBuildingsColonistOfClass<Building_GravEngine>()`。
6. `CompAffectedByFacilities.CanLinkTo` → `CanPotentiallyLinkTo` → `CanPotentiallyLinkTo_Static` → `IsPotentiallyValidFacilityForMe`（LOS 由**设施**的 `CompProperties_Facility.requiresLOS` 决定）；VGE 所有 Gravship 设施都是 `requiresLOS=false`。
7. `CompProperties_Facility.ResolveReferences` 用 `CompProperties_AffectedByFacilities.linkableFacilities` 反向填 `linkableBuildings`；VEF 的 `AffectedByFacilitiesExtension`/`FacilityExtension` 在 `LongEventHandler.ExecuteWhenFinished` 里再补一遍。
8. VGE `GravshipHelper.GravEngineDefs` = 所有 `thingClass` 为 `Building_GravEngine` 或其子类的 def。
9. VGE `PlaceWorker_InRangeOfGravSource` 用 `InHorDistOf` 遍历 `linkableBuildings` 与 `VGE_GravFieldAmplifier`。
10. `PipeSystem.PipeNetManager.NeighbourThingsCardinal` 只做同层四邻；`PipeNet.RegisterComp` 按 `OccupiedRect` 写 `networkGrid`；`GetPipeNetAt` 查 `networkGrid || valveGrid`（都是 map 尺寸 BoolGrid）。
11. VGE `CompHeatManager`、`GravheatUtility`、`SectionLayer_GravshipMask.IsValidSubstructure`、`CompPowerPlantGravEngine` 直接读 `engine.ValidSubstructure(NoRegen)`。
12. Chapter 2 只对 `Building_GravEngine` 打 `CanLaunch` / `UpdateSubstructureIfNeeded` 补丁，没有新增玩家可用引擎 def。
