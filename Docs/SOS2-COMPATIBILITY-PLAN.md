# Save Our Ship 2 楼层兼容：修复实施方案（调研稿）

基准：本机 1.7 目录（2026-10-30 复核）。

- 游戏：`Assembly-CSharp.dll`（15,633,408 B，MD5 `5ed8944e952298fd75748388071c2470`）
- Save Our Ship 2：`Mods/SaveOurShip2/1.7/Assemblies/ShipsHaveInsides.dll`（1,396,736 B，MD5 `63f72afa3e63933c01c241009b466ea1`），packageId `kentington.saveourship2`，支持 1.0–1.7
- RimLayers：`Mods/RimLayers/Assemblies/RimLayers.dll`（2,366,464 B，MD5 `af4e1592f054c495b6483a06af0ab6eb`），packageId `rimlayers.core`

SOS2 同时提供 `Source/1.4|1.5|1.7` 源码（`Source/1.7` 共 429 个 `.cs`），下文行号均指该 1.7 源码；
RimLayers 行号指 `Mods/RimLayers/Source/RimLayers/` 下的文件。

**实施状态：已实现并通过游戏内验收。** 落地文件：`Source/RimLayers/Compatibility/`
下的 `Sos2Compatibility.cs`（适配器主体与反射缓存）、`Sos2CellGraph.cs`（跨层邻接枚举）、
`Sos2Geometry.cs`（纯逻辑几何，可脱离游戏单测）、`Sos2AdjacencyPatches.cs`（6 个邻接/接管补丁）、
`Sos2GeometryPatches.cs`（3 个 `Map.Size` 语义补丁）、`Sos2HeatPatches.cs`（热量网跨层）、
`Sos2FoamPatches.cs`（泡沫补船分层）、`Sos2SubsystemPatches.cs`（引擎排气/散热/港口/太空图尺寸/战斗边缘格）；
`Structure/StructuralSupport.cs` 新增 `RegisterDeckSource` / `RegisterSupportRoof` 两个扩展点；
`RimLayersMod.cs` 在补丁类循环前注册适配器；`About/About.xml` 的 `loadAfter` 已加 `kentington.saveourship2`。
验收：`Tests/Sos2GeometryTests`（56 项纯逻辑检查）+ `Tests/Run-V1.ps1 -Scenario sos2 -Sos2`
（真实游戏 generate 42 / load 13 条命名断言，0 失败，`Tests/Artifacts/v1-sos2-20261030-194202/`）
+ `Tests/Run-UnitTests.ps1`（19 套件全绿）。

九个子系统（维生、热量热容散热、炮台电容、舰桥、驾驶核心、传感器、引擎、穿梭机港口、飞船战斗）
的逐项结论见 §8b：其中五个需要定点修正，四个经核实无需补丁但也都有断言覆盖。

一处**必须记住的失败模式**：`Sos2HeatPatches` 一开始只有 Transpiler 而没有 Prefix，于是
`Sos2CellGraph.Current` 没有被设置，垂直邻居被静默丢弃 —— 补丁装上了、锚点命中了、没有异常、
没有警告，但上下两层各自成网（`nets=2`）。跨层热量是否生效**不能**用「每个件都归属于某个网」
来断言（两个网都满足），必须断言「两者 `GridID` 相同」。

> **重要边界**：适配器**不**把 SOS2 船纳入 RimLayers 的 `Ships` 模块（`ShipStructureService` /
> `ShipTopology` / `ShipLayers`）。Odyssey 逆重船与 SOS2 船是两套独立模型，混用会让
> `ShipStructureService.Core()` 的 `Building_GravEngine` 判定与 SOS2 的 `Ship_Engine`
> （`thingClass` 就是 `Building`）互相污染。RimLayers 只提供坐标编解码、扩层事务和三个扩展点。

> **一句话结论**：SOS2 的船体模型是「一张 Map 上的 `Dictionary<IntVec3, …>` 船格表 + 同层四邻 BFS」。
> RimLayers 的存储坐标是 `z = layer*Stride + localZ`，**`IntVec3` 键天然就能装下多层**，所以数据层几乎不用改；
> 真正会坏的是四类「把存储坐标当逻辑坐标」的假设：
> ① 所有 BFS/邻接只走同层四邻；② `Map.Size.z` 被当成南北长度；③ `map.AllCells` 被当成「整张船」；
> ④ SOS2 的船体地形/船顶不被 RimLayers 认作楼板与支撑。
> 对应四条修法见 §0 的 #1/#2/#3/#4，其余 10 条都是它们的派生。

---

## 0. 结论摘要

| # | 现象 | 根因（已核实） | 修法 | 规模 |
|---|---|---|---|---|
| 1 | 上层甲板上的部件永远不归船，`MapShipCells` 里查不到，泡沫/热量/舱压全部漏掉 | 唯一自动归船入口是 `CompShipCachePart.PostSpawnSetup` 里的 `GenAdj.CellsAdjacentCardinal(parent)`（`Comp/CompShipCachePart.cs:212`），只有同层四邻；`AttachAll`/`RebuildCache`/`CheckAndMerge` 同样只走 `GenAdj.CellsAdjacentCardinal`（`Comp/ShipMapComp.cs:298`、`SpaceShipCache.cs:779`） | 在 `MapShipCells` 之上加一层「跨层邻接」：`Sos2CellGraph.Neighbors(IntVec3)` = 同层四邻 + 同 (x,z) 列的上下相邻层（层差 1）。所有 BFS 与 `cellsToMerge` 收集点换成它。RimLayers 侧已有等价算法 `Ships/ShipTopology.Resolve`（同层四邻 + 向下找第一个甲板，遇 barrier 停）可直接复用 | M |
| 2 | 太空图（`EmptySpaceMap`）造不了第二层船：铺甲板被拒、建部件被拒 | ① `Structure/StructuralSupport.cs:33` 的 `SupportedRoof` 只认 `RoofConstructed`/`RoofRockThin`，SOS2 船顶是 `ResourceBank.RoofDefOf.RoofShip`（`1.7/Defs/Misc/RoofDefs/Roofs.xml`，`isThickRoof=false`）；② `HasSurface`（`StructuralSupport.cs:27-30`）没有 SOS2 船体地形分支 | 加两个扩展点：`StructuralSupport.RoofCompat`（`RoofDef -> bool` 委托列表）与 `Structure.DeckSources`（`Func<LayerMapComponent,IntVec3,bool>` 列表，`HasSurface` 里与现有 `VehicleFacilitySpace.HasDeck` 并列）。`Sos2Compatibility` 注册 `RoofShip` 与「该格有 `CompShipCachePart.Props.isPlating/isHull`」 | S |
| 3 | 扩层后 SOS2 的 `grid`（热量网索引）被零填充，新层热量网整块不重建；炮塔/散热片跨层后失联 | `Comp/ShipMapComp.cs:94` `grid = new int[map.cellIndices.NumGridCells];` 是 `int[]`，`Generation/GridCapacityRepair.cs` 的 `Array`+`GrowCells` 分支会把它扩容，但追加带是 **0**；而 SOS2 的语义是 `grid[i] == -1` 表示未访问（`Comp/ShipMapComp.cs:135/139/224`） | 订阅 `Generation/LiveMapExpansion.Committed`（唯一「layout 与 cellCount 已定、不可回滚」的时点），把 `grid` 重建成 `new int[NumGridCells]` 并全填 `-1`，同时 `heatGridDirty = true; breathableZoneDirty = true;`。注意 `LayerCellBufferCompatibility.RegisterNativeCellBuffer` **只支持 `NativeArray<T>`**（`Compatibility/ThirdPartyCellBuffers.cs`），`int[]` 用不上 | S |
| 4 | 扩层后一进入移动/消耗流程，整张图里「不在船上」的东西被 Destroy/Kill | `Comp/ShipMapComp.cs:2750` `foreach (IntVec3 v in map.AllCells.Except(MapShipCells.Keys)) //kill anything off ship`，唯一调用点 `:2590`。扩层后 `map.AllCells` 覆盖所有 band，包括 `RL_Isolation` 隔离带与其它层 | Prefix 接管 `KillAllOffShip`：只遍历 `ShipsOnMap.Values.SelectMany(s => s.Area)` 之外的本层格（按 `LayerLayout` 逐层构造枚举），而不是 `map.AllCells` | S |
| 5 | 飞船起飞/降落时整船被搬到存储带中段或错层 | `ShipInteriorMod2.cs:2433` `IntVec3 size = originMap.Size;`、`SpaceShipCache.cs:573-582` `CenterShipOnMap` 用 `Map.Size.x/2`、`Map.Size.z/2`；`ShipInteriorMod2.cs:2509-2517` `TransformForShipMove` 用 `targetMap.Size.x`/`targetMap.Size.z` 做旋转镜像 | 三处全部改用「地面尺寸 + 层基址」：`GroundSize(map)` = `Layout.Width × Layout.Height`，镜像在**本层 localZ 内**做，再 `+ layer*Stride` 还原。`MapShipCells` 的键本身就是存储格，平移逻辑不用改 | M |
| 6 | 舰对舰炮塔在本层开火，投射物却飞到最上层带 | `Building/Building_ShipTurret.cs:465-487 MapEdgeCell(int miss)` 用 `Map.Size.z - 1` / `Map.Size.x - 1` 当「北/东边缘」，`:607`/`:624` 同样；扩层后 `Map.Size.z - 1` 是最后一个 band 的末行 | Postfix `MapEdgeCell`：边缘改成本层带内边界（`bandTop = layer*Stride + Height - 1`，`bandRight = Width - 1`）。`FindProjectileSpawnDirection`/`FindClosestEdgeCellLowSpread`（`Comp/ShipMapComp.cs:3130-3195`）同样处理 | S |
| 7 | 泡沫补船在上层完全不工作（部件不进 `BuildingsToFoam`，或进了但没有 distributor 能覆盖） | 入队条件在 `CompShipCachePart.cs:262-300 PreDeSpawn` 里读 `mapComp.MapShipCells[parent.Position].Item2`，上层格不在表里会直接抛；`SpaceShipCache.cs:636-660 FoamFill` 与 `:1155-1178 SlowTick` 都用 `OrderBy(t => t.Item3)`（到核心的 BFS 步数）排序，跨层时该值不存在 | 修好 #1 后自动解决 80%；剩余部分：`SlowTick`/`FoamFill` 里按 `distributor.parent.Position` 所在层过滤 distributor（`Sos2CellGraph.LayerOf(cell)`），避免上层破洞被下层 distributor 隔着楼板补 | S |
| 8 | 太空图永远只有一层，玩家没有任何入口能建第二层 | `Generation/LayerGeneration.cs:33 GenerateMapPatch` 的守卫显式排除 `parent.Tile.Layer.Def.isSpace`（非「随船生成」路径），所以 `EmptySpaceMap` 生成期不扩层；运行期 `LiveMapExpansion.CanExpand` 并**不**排除太空图（`Generation/LiveMapExpansion.cs`），但 SOS2 玩家不会去建 RimLayers 的楼梯/电梯 | 给 SOS2 加一个 RimLayers 提供的入口：`RL_ShipDeckExpand` 设计器（或 `Building_ShipBridge` 上的 gizmo），点击时 `LiveMapExpansion.EnsureElevation(map, top+1)` → `Quota.Reserve` → `Ships.Assign`。走与 `Ships/ShipLandingPatches.cs:118-130 EnsureCapacity` 完全相同的事务路径 | M |
| 9 | 上层 SOS2 船体上不能放 RimLayers 的结构楼板/楼梯，RimLayers 的建造校验也不认 SOS2 船 | `Structure/StructuralSupport.cs:57 Validate` 里 `slab`/`stair` 分支要求 `HasShipRoof`/`HasRequiredRoof`；`HasSurface` 无 SOS2 分支；`Ships/ShipStructureService.cs Deck(c)` 要求 `FoundationAt(c)?.IsSubstructure == true`（SOS2 船体地形都不是 foundation） | 与 #2 同一组扩展点。**不要**把 SOS2 船体混进 RimLayers 的 `ShipStructureService`（那是 Odyssey 逆重船模型，`Core(e)` 要求 `Building_GravEngine` + `Faction.OfPlayer`），只加「等效甲板」分支 | S |
| 10 | `PlaceWorker_ShipHull` / `PlaceWorker_ShipPlating` 在上层空腔里放不下，或反过来能放到别的船上 | 两者只查 `map.roofGrid.RoofAt(...) == RoofDefOf.RoofRockThick` 与 `GetAffordances(...).Contains(TerrainAffordanceDefOf.Heavy)`（`PlaceWorker/PlaceWorker_ShipHull.cs`、`PlaceWorker_ShipPlating.cs`），不查层归属；`PlaceWorker_ShipPlating.cs:23` 还只看 `loc`（原版 bug，occupiedRect 循环里用的是 `loc` 而不是 `vec`） | Postfix `AllowsPlacing`：要求目标格 `owner.TryDecode` 成功、`owner.Elevation(layer) >= 0`、且 `Structure.HasSurface(cell)`（即已铺甲板）；同层已有 `ShipIndexOnVec != -1` 且属于别的船则拒绝 | S |
| 11 | 跨层热传导/舱压贯通不成立：上下两层船的散热片各自成网 | `Comp/ShipMapComp.cs:204-240 AccumulateToNetNew` 只用 `GenAdj.CellsAdjacentCardinal`；RimLayers 的 `Environment/HeatLinks.cs`/`VolumeLinks.cs` 只服务 RimLayers 自己的 `Heat`/`Air` 模块，SOS2 不走它们 | 在 `AccumulateToNetNew` 的邻接枚举里并入「上下相邻层」：条件 = `owner.Connectors.HasTransferEdge(lower, upper, LayerChangeKind.Heat)`（由 `ConnectorTransmission` DefModExtension 的 `heat` 开关驱动，见 `Navigation/ConnectorGraph.cs`）或 `!owner.Structure.HasSurface(upper)`（开放竖井）。舱压同理走 `VolumeLinks.Open(...)` | M |
| 12 | `ShipHeatNet` 的 `Turrets`/`Sinks`/`Shields` 按 `CompShipHeat` 分类，跨层后 `AccuracyBoost`、`TacCons` 取到别的层的舰桥 | `ShipHeatNet.Register`（`ShipHeatNet.cs:180-228`）按类型归类，与层无关；`Building_ShipTurret.cs:384 TryStartShootSomething` 里 AI 选目标直接 `mapComp.ShipCombatTargetMap.GetComponent<ShipMapComp>().MapRootListAll.RandomElement()` | 目标选择本身跨图、与层无关，**不需要改**；只需保证 #11 修好后同一 `ShipHeatNet` 不会跨船混层（同一张图上的两艘多层船各层不要连成一张网）。用 `SpaceShipCache.Index` 作为网的分组上限 | S |
| 13 | 扩层/回滚窗口内 SOS2 的 `grid`/`breathableZone` 尺寸与 `cellIndices` 不一致，抛 `ArgumentOutOfRangeException` | 扩层事务在 `Generation/LiveMapExpansion.cs` 里 `map.info.Size = full; map.cellIndices = new CellIndices(map);` 之后才 `NotifyCommitted`；期间 `ShipMapComp.MapComponentUpdate` 若被 tick 到会用旧 `grid` 配新 `cellIndices` | `MapComponentTick` 里加闸门：`LiveMapExpansion.TransactionActive` 为真时直接 return。同时 `Sos2Compatibility` 在 `Committed` 里重建 `grid`（见 #3） | S |
| 14 | 存读档后上层船消失或 `MapShipCells` 只剩一层 | `MapShipCells` 没有任何 `Scribe_*`（grep 0 命中），完全靠 `RecacheFromRoots`（`Comp/ShipMapComp.cs:648-681`）+ `RecacheMap`（`:683-719`）重建；`RecacheFromRoots` 里 `cachePart.cellsUnder = cachePart.parent.OccupiedRect().ToHashSet()` 是 `OccupiedRect()`，本身多层安全 | **数据层不需要改**；但 `RecacheMap` 里 `MapShipCells` 残留 `-1,-1` 的格若无 `CompShipCachePart` 会 `Log.Warning("… Recaching found N invalid cells! FIXING.")` 并被移除 —— 扩层后的 `RL_OpenAir` 空腔格会命中这条。修 #1 后需回归验证这条警告不再刷屏 | S |

S≈0.5 天，M≈1–2 天（含游戏内验证）。

**架构结论（最重要的一条）**：SOS2 的船**不是** RimLayers 的 Odyssey 逆重船。
`Ships/ShipStructureService.cs` 的 `Core(e)` 要求 `EngineDef(e.def)`（`typeof(Building_GravEngine).IsAssignableFrom(def.thingClass)`）且 `Faction == Faction.OfPlayer`；
`Deck(c)` 要求 `terrainGrid.FoundationAt(c)?.IsSubstructure == true`。SOS2 的引擎是 `Building`（`1.7/Defs/ThingDefs_Buildings/Buildings_Ship.xml:1027 <thingClass>Building</thingClass>`），
船体地形是 `FakeFloorInsideShip` 系列（`1.7/Defs/TerrainDefs/Terrain_Space.xml`，无 `isFoundation`、无 `Substructure` tag）。
所以**不要**把 SOS2 船塞进 RimLayers 的 `Ships` 模块，也不要指望 `Ships/ShipFacilityPatches.cs` 或 `Environment/FacilityPatches.cs` 生效
（SOS2 除 `SpaceCrib` 与 archotech 核心外完全不用 `CompAffectedByFacilities`）。

正确的形态是：**SOS2 保留自己的船模型，RimLayers 只提供三样东西**——
(a) 坐标编解码与层布局（`LayerMapComponent.TryDecode`/`LayerLayout`）；
(b) 运行期扩层事务（`LiveMapExpansion.EnsureElevation` + `Committed` 事件 + 配额）；
(c) 三个扩展点（支撑屋顶、等效甲板、跨层邻接），让 SOS2 的同层假设变成跨层。

---

## 1. 坐标契约：为什么数据层几乎不用改

### 证据

`Layout/LayerLayout.cs`：

```csharp
public StorageCell ToStorage(LayerCell cell) => new(cell.X, 0, checked(cell.LayerId * Stride + cell.Z));
public int CalculateStride(int height) => ((height + 17) / 17) * 17;
public const int MinElevation = -9;
public const int MaxElevation = 9;
```

`Core/LayerMapComponent.cs`：`LayerRect(layer) => new CellRect(0, layer * Stride, Width, Height)`（返回的 `z` **已经是存储坐标**）。

SOS2 侧：

```csharp
// Comp/ShipMapComp.cs:41
public Dictionary<IntVec3, Tuple<int, int>> MapShipCells;   // Item1 = shipIndex, Item2 = path
// SpaceShipCache.cs
public HashSet<IntVec3> Area;
```

`MapShipCells` 的键就是 `IntVec3` 存储格，`Area` 也是 `HashSet<IntVec3>`。
RimLayers 的存储坐标把层号编进 `z`，所以**同一个字典天然可以同时容纳所有层**，
`ShipsOnMap[shipIndex].Area` 也能跨层。这是本次兼容最大的运气：不需要新的容器、不需要迁移存档。

`MapShipCells` 也不参与序列化（grep 无 `Scribe_*`），每次读档由
`RecacheFromRoots()`（`Comp/ShipMapComp.cs:648-681`）→ `RecacheMap()`（`:683-719`）从真实 `Building` 重建，
所以「多层」不会破坏存档，只会影响重建时的邻接 BFS。

### 结论

数据层唯一需要做的是 **#1 的跨层邻接**。不要为多层改 `MapShipCells` 的类型或键格式。

---

## 2. 太空图能不能扩层

### 证据

`Generation/LiveMapExpansion.cs`：

```csharp
public static bool CanExpand(Map map) =>
    !map.Disposed && !map.IsPocketMap && map.generatorDef?.isUnderground != true &&
    map.Size.y == 1 && map.Size.x > 0 && map.Size.z > 0;
```

**不排除 `map.IsSpace()`**，所以运行期扩层在太空图上是允许的。

`Generation/LayerGeneration.cs:33`（生成期）却是排除的：

```csharp
if(parent==null||isPocketMap||stepDebugger||mapGenerator==null||mapGenerator.isUnderground
   ||mapSize.x<=0||mapSize.z<=0
   ||(!incoming&&(parent.Faction!=Faction.OfPlayer||parent.Tile.Layer.Def.isSpace)))return;
```

SOS2 的太空图是真实 Map：`ShipInteriorMod2.cs:741 GeneratePlayerShipMap` 里
`MapGenerator.GenerateMap(size, orbiter, orbiter.MapGeneratorDef, null, null, false)`，
`MapGeneratorDef` 是 `1.7/Defs/MapGeneration/SpaceMapGenerator.xml` 的 `EmptySpaceMap`
（`canBePlayerHome=true`，`pocketMapProperties` 在 XML 里是注释掉的）。

所以：**太空图能扩层，但只能运行期扩，且必须由 SOS2 的入口触发**（#8）。
`RimLayersSettings.SpaceExpansion`（默认 `true`）与 `ExpansionPolicy` 的 `space` 分支
（`Layout/ExpansionPolicy.cs`：`if(space&&(!allowSpace||requested<0)) return new(additional,ExpansionDenial.Space);`）
已经为「太空扩层」准备了开关与拒绝路径，无需新增设置。

### 需要注意的副作用

`Generation/LayerGeneration.cs` 的 `EmptySpace.Fill` 把新带填成 `RL_OpenAir`（可解码）或 `RL_Isolation`（17 行 padding）。
`RL_OpenAir` 是 `Impassable`、`pathCost=300`、`layerable=false`（`Defs/TerrainDefs/RimLayers_Terrains.xml`），
与 SOS2 的 `EmptySpace`（`pathCost=50`、`tags=Space`、`extinguishesFire=true`）语义不同。
上层空腔应当保持 `RL_OpenAir`（RimLayers 靠它判定 `HasSurface == false`），
一旦 SOS2 的 `CompShipCachePart.SetShipTerrain` 在那格铺了 `FakeFloorInsideShip`，RimLayers 的 `HasSurface` 就成立（见 #2 的 DeckSources）。

`GenStep_EmptySpace`（`1.7/MapGen/GenStep_EmptySpace.cs`）用 `foreach(IntVec3 current in map.AllCells)` 全图刷 `EmptySpace`，
并写 `MapGenerator.Elevation[allCell]` / `MapGenerator.Fertility[allCell2]`。这两个 `MapGenFloatGrid` 是否随扩层增长需要游戏内复核
（RimLayers 的 `LiveMapExpansion` 只处理 `Map` 字段、`Area`/`Zone`/`Plan`、`map.components`，没有处理 `MapGenerator.Elevation`）。

---

## 3. 船体归属：把同层四邻换成跨层邻接

### 证据：唯一自动归船入口

`Comp/CompShipCachePart.cs:184-222`（`PostSpawnSetup` 尾部）：

```csharp
foreach (IntVec3 vec in cellsUnder) //init cells if not already in ShipCells
{
    if (!mapComp.MapShipCells.ContainsKey(vec)) { mapComp.MapShipCells.Add(vec, new Tuple<int,int>(-1,-1)); allOnSame = false; }
}
if (mapComp.CacheOff) { cellsToMerge.Clear(); return; }
if (allOnSame) { /* ShipIndexOnVec(parent.Position) > 0 → AddToCache */ return; }
foreach (IntVec3 vec in GenAdj.CellsAdjacentCardinal(parent))   // ← 同层四邻
    if (mapComp.MapShipCells.ContainsKey(vec)) cellsToMerge.Add(vec);
if (cellsToMerge.Any()) { cellsToMerge.AddRange(cellsUnder); mapComp.CheckAndMerge(cellsToMerge); }
else ShipInteriorMod2.WorldComp.AddNewShip(mapComp.ShipsOnMap, parent as Building);
```

同样只走同层四邻的还有：

- `Comp/ShipMapComp.cs:298`（`AttachAll` 的 BFS 扩散）
- `SpaceShipCache.cs:779`（`RebuildCache` 的 BFS）
- `Comp/CompShipCachePart.cs:212`（上面的 `cellsToMerge` 收集）
- `SpaceShipCache.cs:467/589/612/626/1125/1185/1199/1243`（`OuterNonShipWalls`/`OuterCells`/`BorderCells` 等）

`grep GenAdj.CellsAdjacentCardinal` 在 SOS2 `Source/1.7` 共 25 处。

### 修法

新增 `Sos2CellGraph`（放在 RimLayers 的 `Compatibility/Sos2*.cs` 里，通过反射调用 SOS2 类型，不引用其程序集）：

```csharp
// 逻辑：同层四邻 + 同 (x,z) 列的上下相邻层
internal static IEnumerable<IntVec3> Neighbors(IntVec3 cell)
{
    foreach (var n in GenAdj.CellsAdjacentCardinal(cell)) yield return n;
    var c = LayerMapComponent.For(map);
    if (c == null) yield break;
    if (!c.TryDecode(cell, out var at)) yield break;
    foreach (int dir in new[]{ -1, 1 })
        if (c.Layout.TryGetAdjacentLayer(at.LayerId, dir, out int other))
            yield return c.ToStorage(new LayerCell(other, at.X, at.Z));
}
```

替换点全部是 Harmony Transpiler/Prefix，不改 SOS2 源码。Transpiler 锚点按 RimLayers 现有惯例处理
（`Coordination/MechanitorRangePatch.cs`、`Environment/PowerPatches.cs` 都是「锚点数量精确匹配，否则抛异常」，
防止静默失效）：

```csharp
var hits = instructions.Where(i => i.Calls(AccessTools.Method(typeof(GenAdj), nameof(GenAdj.CellsAdjacentCardinal),
    new[]{ typeof(Thing) }))).ToList();
if (hits.Count != 1) throw new InvalidOperationException("SOS2 adjacency anchor changed: " + hits.Count);
```

### 为什么不用 RimLayers 的 `ShipTopology.Resolve`

`Ships/ShipTopology.cs` 的语义是「同层四邻 + **向下**逐层找第一个 deck（遇 barrier 停）」，是给 Odyssey 逆重船的
「甲板块向下找锚点」设计的，`Compatible(a,b)` 还要求 `decks[a]==0||decks[b]==0||decks[a]==decks[b]`。
SOS2 的语义是「船格连通即成船，不需要锚点」，而且 SOS2 自己已经有 `CheckAndMerge`/`Mass` 选最大船的合并规则。
**算法思想可借鉴，实现不要复用**：直接给 SOS2 的四邻枚举加跨层边，让 SOS2 自己的 BFS 跑通即可。

---

## 4. 楼板语义：SOS2 地形不是 RimLayers 楼板

### 证据

`Structure/StructuralSupport.cs:27-34`：

```csharp
public bool HasSurface(IntVec3 cell) =>
    owner.TryDecode(cell, out var logical) && owner.map.terrainGrid.TerrainAt(cell)!=RimLayersDefOf.RL_OpenAir &&
    (logical.LayerId == 0 || owner.Mountains.IsNaturalFloor(cell) ||
     owner.StructuralCells.Contains(cell) && HasRequiredRoof(cell) ||
     RimLayers.Vehicles.VehicleFacilitySpace.HasDeck(owner,cell));

internal static bool SupportedRoof(RoofDef? roof)=>roof==RoofDefOf.RoofConstructed||roof==RoofDefOf.RoofRockThin;
internal static bool ConnectorRoof(RoofDef? roof)=>SupportedRoof(roof)||roof==RoofDefOf.RoofRockThick;
```

`ResourceBank.RoofDefOf.RoofShip` 不在 `SupportedRoof` 白名单里。
SOS2 的船顶就是这个 def：`Comp/CompShipCachePart.cs:173-181`

```csharp
foreach (IntVec3 pos in cellsUnder) //set roof
{
    var oldRoof = map.roofGrid.RoofAt(pos);
    if (!ShipInteriorMod2.IsRoofDefAirtight(oldRoof) && Props.hermetic)
        map.roofGrid.SetRoof(pos, ResourceBank.RoofDefOf.RoofShip);
    else
        map.roofGrid.SetRoof(pos, RoofDefOf.RoofConstructed);
}
```

`ShipInteriorMod2.IsRoofDefAirtight`（`ShipInteriorMod2.cs:561-577`）的白名单是
`RoofShip` / `RoofRockThick` / `RoofConstructed` + 静态 `compatibleAirtightRoofs`。

SOS2 的船体地形（`1.7/Defs/TerrainDefs/Terrain_Space.xml`）：`FakeFloorInsideShip`、`FakeFloorInsideShipMech`、
`FakeFloorInsideShipArchotech`、`FakeFloorInsideShipFoam`、`ShipWreckageTerrain`、`SoilShip`——
抽象模板 `ShipFloorBase` 只有 `layerable=true` / `tags=Ship` / `fertility=0`，**没有 `isFoundation`、没有 `Substructure`**。

### 修法：两个扩展点，而不是硬编码 SOS2

`Structure/StructuralSupport.cs`：

```csharp
internal static readonly List<Func<RoofDef?,bool>> RoofCompat = new();
internal static bool SupportedRoof(RoofDef? roof) =>
    roof==RoofDefOf.RoofConstructed || roof==RoofDefOf.RoofRockThin ||
    (roof != null && RoofCompat.Any(f => f(roof)));
```

`Core/LayerMapComponent.cs` 的 `Structure` 组合根上加：

```csharp
internal readonly List<Func<LayerMapComponent,IntVec3,bool>> DeckSources = new();
public bool HasSurface(IntVec3 cell) => … || DeckSources.Any(f => f(owner, cell));
```

`Sos2Compatibility.Install()` 里注册：

```csharp
StructuralSupport.RoofCompat.Add(r => Sos2Defs.RoofShip != null && r == Sos2Defs.RoofShip);
owner.Structure.DeckSources.Add((o, c) =>
    o.TryDecode(c, out var at) && at.LayerId > 0 &&
    c.GetThingList(o.map).Any(t => Sos2Defs.IsShipPlatingOrHull(t)));
```

`IsShipPlatingOrHull(t)` 读 `CompShipCachePart.Props.isPlating || Props.isHull`（`1.7/Comp/CompShipCachePart.cs` 头注释定义了这两个 props 的语义）。

这条与 `Vehicles/VehicleFacilitySpace.HasDeck` 是同一范式（`RL_VehicleDeck` 地形 + `Building_VehicleConnector`），
只是把「第三方设施给自己开等效楼板」做成了可注册列表，而不是每来一个 mod 就改一次 `HasSurface`。

### 明确不做的事

- 不把 `FakeFloorInsideShip` 的 `isFoundation` 改成 `true`：`ShipInteriorMod2` 全仓有 20+ 处按 `FoundationAt` 判 `IsSubstructure` 的逻辑
  （`PlaceWorker_ShipPlating.cs:23`、`ShipUnderlays.CanHost`、`Ships/ShipStructureService.cs Deck(c)`），改地基会同时污染 Odyssey 逆重船路径。
- 不往 SOS2 的 `CompShipCachePart` 加 RimLayers 的 `Comp`：SOS2 的 `AddToCache`/`RecacheFromRoots` 会遍历 `Building`，
  但 RimLayers 的 `ShipStructureService.Refresh()` 只认 `Building_GravEngine`，加 Comp 也不会被认领。
- 不改 SOS2 的 `About.xml`（第三方 mod，且 Steam 更新会覆盖）。

---

## 5. 起降与跨图迁移

### 证据：三处把存储 z 当南北长度

`ShipInteriorMod2.cs:2429-2470 LaunchShip`：

```csharp
IntVec3 size = originMap.Size;                 // ← 扩层后 Size.z == Layout.StorageHeight
if (size.x < Find.World.info.initialMapSize.x || size.z < Find.World.info.initialMapSize.z)
{ size = Find.World.info.initialMapSize; mapIsLarger = true; }
Map map = GeneratePlayerShipMap(size);
…
adj = ship.CenterShipOnMap();
```

`SpaceShipCache.cs:573-582 CenterShipOnMap`：

```csharp
IntVec3 adj = new IntVec3(Map.Size.x/2, 0, Map.Size.z/2) - new IntVec3(size.x/2, 0, size.z/2);
return adj - min;
```

`ShipInteriorMod2.cs:2509-2517 TransformForShipMove`：

```csharp
if (rotb == 2) return new IntVec3(targetMap.Size.x - pos.x, 0, targetMap.Size.z - pos.z) + adjustment;
if (rotb == 3) return new IntVec3(targetMap.Size.x - pos.z, 0, pos.x) + adjustment;
return pos + adjustment;
```

### 修法

统一引入三个 helper（`Sos2Geometry`）：

```csharp
static IntVec3 GroundSize(Map map)   => new(c.Layout.Width, 1, c.Layout.Height);          // 逻辑尺寸
static int     LayerOf(IntVec3 cell) => c.TryDecode(cell, out var at) ? at.LayerId : 0;
static IntVec3 BandOrigin(IntVec3 cell) => new(0, 0, LayerOf(cell) * c.Layout.Stride);     // 本层带基址
```

- `LaunchShip`：`size = GroundSize(originMap)`；`mapIsLarger` 比较也用 `GroundSize`。
- `CenterShipOnMap`：`adj = new IntVec3(GroundSize.x/2, 0, GroundSize.z/2) - new IntVec3(size.x/2, 0, size.z/2)`，
  返回的 `adj` 是**逻辑**偏移，应用时 `+ BandOrigin`。`size` 由 `SpaceShipCache.Size(out min)` 算出，本身取自 `Area` 的 min/max，多层安全。
- `TransformForShipMove`：镜像在 local 坐标内做，再还原带基址：

```csharp
int layer = LayerOf(pos);
int localZ = pos.z - layer * stride;
int newLocalZ = rotb switch {
    2 => layout.Height - localZ - 1,
    3 => pos.x,
    _ => localZ
};
int newX = rotb switch { 2 => layout.Width - pos.x - 1, 3 => layout.Width - localZ - 1, _ => pos.x };
return new IntVec3(newX, 0, layer * stride + newLocalZ) + adjustment;
```

> 注意 `rotb == 3` 时原来的写法把 `pos.x` 放进了 `z`，`pos.z` 放进了 `x`，是 90° 旋转而不是镜像，
> 上面的 `switch` 只是把「原来的 2 个分支」逐一改成「先转 local、再加带基址」，语义等价。

### 起降闸门

RimLayers 的 `Ships/ShipLandingPatches.cs` 是一整套「每个入口点各一个补丁」的清单式接管，
SOS2 的起降需要同构的一套，但**落点完全不同**（SOS2 不用 `Gravship`/`GravshipPlacementUtility`）：

| SOS2 入口 | 需要做什么 |
|---|---|
| `ShipInteriorMod2.CanShipLandOnMap(Map, Map)` | Postfix：目标图上逐格 `CanPlaceShipOnVec` 之外，追加「本层 band 是否存在」（`Elevation >= 0`）与 `LayerExpansionQuota.For(targetMap, neededTop)` |
| `ShipInteriorMod2.CanPlaceShipOnVec(IntVec3, Map, bool, bool)` | Prefix：`v.InNoBuildEdgeArea(map)` 用存储坐标，扩层后 `InNoBuildEdgeArea` 用的是 `Map.Size`，会把隔离带算进边界 → 改成按 `GroundSize` + band 判定 |
| `ShipInteriorMod2.MoveShip(...)` | 入口处 `LiveMapExpansion.EnsureElevation(targetMap, neededTop)`；成功后 `Ships.MarkDirty(); Connectors.Invalidate(); Power.Rebuild(); Air.Rebuild(); Sky.Rebuild();` |
| `ShipInteriorMod2.RemoveShipOrArea(...)` | 离开后若该图最高层已空，`Quota.Release` 但**不**自动缩层（与 RimLayers 现有策略一致：降低上限不删除旧层） |
| `Comp/ShipMapComp.cs:2750 KillAllOffShip` | 见 #4 |

SOS2 已有自己的「移动中」闸门 `ShipInteriorMod2.MoveShipFlag`（54 处引用，`Source/1.7` 全仓），
RimLayers 的扩层事务必须与它互斥：扩层期间置 `MoveShipFlag = true`，或反过来在 `MoveShipFlag` 为真时拒绝扩层。
建议后者（`EnsureElevation` 前置检查），因为扩层失败会整图回滚，不能半途被打断。

---

## 6. 泡沫补船

### 证据

入队（`Comp/CompShipCachePart.cs:262-300 PreDeSpawn`）：

```csharp
if ((mode == DestroyMode.KillFinalize || mode == DestroyMode.KillFinalizeLeavingsOnly) &&
    parent.def.Size == IntVec2.One && !Props.isCorner && !ship.IsWreck && ship.FoamDistributors.Any())
    ship.BuildingsToFoam.Add(new Tuple<bool,IntVec3,int>(
        Props.isHull, parent.Position, mapComp.MapShipCells[parent.Position].Item2));
```

消耗（`SpaceShipCache.cs:1155-1178 SlowTick`）：

```csharp
if (!IsWreck && BuildingsToFoam.Any() && FoamDistributors.Any())
    foreach (var dist in FoamDistributors.Where(d => d.fuelComp.Fuel > 0 && d.powerComp.PowerOn))
    {
        var target = BuildingsToFoam.OrderBy(t => t.Item3).First();   // Item3 = 到核心的 BFS 步数
        …
        GenPlace.TryPlaceThing(replacer, target.Item2, Map, ThingPlaceMode.Direct);
        BuildingsToFoam.Remove(target); ConsumeFuel(1);
    }
```

批量补（`SpaceShipCache.cs:636-660 FoamFill`）同样按 `Item3` 排序、每格只用一个 distributor 后 `break`。

### 修法

- **前提**：修好 #1，否则 `mapComp.MapShipCells[parent.Position]` 在上层格上直接抛 `KeyNotFoundException`。
- **path 语义**：`AttachAll`（`Comp/ShipMapComp.cs:298`）与 `RebuildCache`（`SpaceShipCache.cs:779`）的 BFS 逐圈 `path++`，
  加了跨层边之后 path 仍然是「到核心的最少步数」，`OrderBy(Item3)` 的「由内向外」语义自动成立，不需要改排序。
- **distributor 分层**：`SlowTick`/`FoamFill` 里过滤 `LayerOf(dist.parent.Position) == LayerOf(target.Item2)`，
  否则上层破洞会被下层 distributor 隔着楼板补上（`GenPlace.TryPlaceThing` 到上层格是合法的，因为格子在 `Map` 里）。
  反过来，若希望「下层 distributor 能补上层」，需要一个明确的跨层泡沫通道（复用 `ConnectorTransmission{air=true}`），
  这一条留作可选增强，默认关闭。
- **屋顶修补**：`HarmonyPatches.cs` 的 `SealHole`（`RoofCollapserImmediate.DropRoofInCells` Postfix）已经逐格 `ShipIndexOnVec(cell)`，
  多层安全，不需要改。
- **`CompHullFoamDistributor`**（`1.7/Comp/CompHullFoamDistributor.cs`，73 行）的 gizmo 已被注释掉，
  触发点在 `SpaceShipCache.SlowTick`/`FoamFill`，所以**不需要**给它加分层 UI。

---

## 7. 战斗

### 证据：两条完全不同的弹道

1. **RimLayers 管的三维弹道**：`Combat/ProjectilePatches.cs` 的 `LayerLaunchPatch`（`Projectile.Launch` Postfix → `Combat.Launched`）、
   `LayerProjectilePositionPatch`（`ExactPosition` getter 把 `z` clamp 到逻辑高度再 `+ flight.StorageLayer*Stride`）、
   `LayerProjectileSweepPatch`（`CheckForFreeInterceptBetween` Prefix → `Combat.Sweep(flight)`）。
   这套只在 `flight != null` 时生效。

2. **SOS2 的舰对舰弹道**：`1.7/Projectile/ShipCombatProjectile.cs` 是 `IExposable` **纯数据**
   （`turret/target/range/spawnProjectile/missRadius/accBoost/burstLoc/speed/Map`），**不继承 `Projectile`**。
   真正跨图命中在 `Comp/ShipMapComp.cs:1503-1525`：

```csharp
if (proj.range >= OriginMapComp.Range)
{
    spawnCell = proj.burstLoc == IntVec3.Invalid
        ? FindClosestEdgeCellLowSpread(ShipCombatTargetMap, proj.target.Cell)
        : proj.burstLoc;
    newProjectile = (Projectile)GenSpawn.Spawn(proj.spawnProjectile, spawnCell, ShipCombatTargetMap);
    IntVec3 a = proj.target.Cell - spawnCell; float angle = a.AngleFlat;
}
```

`Verb/Verb_LaunchProjectileShip.cs:57-72` 仍然会 `GenSpawn.Spawn(projectile, resultingLine.Source, caster.Map)`
生成一个真实 `Projectile`，飞向 `currentTarget.Cell`，而 `currentTarget` 在非 `GroundDefenseMode` 下是
`Building_ShipTurret.cs:436 currentTargetInt = MapEdgeCell(5);`（玩家）/`:591 MapEdgeCell(20)`（AI）——
**炮塔打的是「本图边缘格」，不是真实目标**。这是 SOS2 的战斗设计，不要改。

### 修法

- `Building_ShipTurret.MapEdgeCell(int miss)` Postfix：把 `Map.Size.z - 1` / `Map.Size.x - 1` / `0` 换成
  「本层带内的边」——`bandTop = layer*Stride + Height - 1`、`bandRight = Width - 1`、`bandBottom = layer*Stride`、`bandLeft = 0`。
  `miss` 的随机偏移也要 clamp 在 `[bandBottom, bandTop]` 内。
- `Comp/ShipMapComp.cs:3130-3162 FindProjectileSpawnDirection` 与 `:3165-3195 FindClosestEdgeCellLowSpread`
  同样处理（两者都用 `map.Size.x/2`、`map.Size.z/2`、`map.Size.z-1`）。
- RimLayers 的 `Combat/WeaponIntegration.cs` **不要**接管 `Building_ShipTurret`：
  `LayerTurretOrderPatch`/`LayerTurretAcquisitionPatch`/`LayerMortarClearancePatch` 都是给原版 `Building_TurretGun` 的
  `TryFindNewTarget`/`OrderAttack` 用的，SOS2 的 `TryStartShootSomething` 已经重写了目标选择（`Building_ShipTurret.cs:384`），
  两套 AI 叠加会互相打断。**只需在 `Combat.WeaponIntegration.Managed` 里排除 `Building_ShipTurret`。**
- `mapComp.ShipCombatTargetMap` 是另一张图（`Scribe_References.Look<Map>(ref ShipCombatTargetMap, "ShipCombatTargetMap")`），
  跨图逻辑与层无关，不需要改。
- 护盾/拦截：`CompProjectileInterceptor` 在 `Projectile.CheckForFreeInterceptBetween` 里枚举 `Map` 内所有 interceptor
  （见 `Docs/COMBAT-3D.md` 的已知限制），SOS2 的 `CompShipHeatShield` 不走这条路，所以不受影响。

---

## 8. 设施链接、热量网、舱压

### 证据：SOS2 不用 `CompAffectedByFacilities`

`Mods/SaveOurShip2` 全仓 `CompAffectedByFacilities` 只有 2 处：
`1.7/Defs/ThingDefs_Buildings/Buildings_Ship.xml:2719`（`SpaceCrib` 的 `SleepAccelerator`）
与 `1.7/Defs/ThingDefs_Buildings/Buildings_ShipTechArcho.xml:967`（archotech 核心的 4 个 pillar，`radius 9.9`）。

SOS2 的「设施链接」是 **`ShipHeatNet` + `SpaceShipCache` 的 HashSet 缓存**：

```csharp
// Comp/ShipMapComp.cs:204-240 AccumulateToNetNew
comp.myNet = net; net.Register(comp);
foreach (IntVec3 cell in GenAdj.CellsOccupiedBy(comp.parent))
    grid[map.cellIndices.CellToIndex(cell)] = net.GridID;
foreach (IntVec3 cell in GenAdj.CellsAdjacentCardinal(comp.parent))    // ← 同层四邻
    if (cell.InBounds(map) && grid[map.cellIndices.CellToIndex(cell)] == -1)
        foreach (Thing t in cell.GetThingList(map))
            if (t is Building b && b.TryGetComp<CompShipHeat>() is {} heat) newBatch.Add(heat);
```

`ShipHeatNet.Register`（`ShipHeatNet.cs:180-228`）按 `CompShipHeatSink` / `Building_ShipTurret` / `CompShipHeatSource` /
`CompShipHeatShield` / `Building_ShipBridge` 分类，与层无关。

`SpaceShipCache.AddToCache`（`SpaceShipCache.cs:800+`）按 `CompShipCachePart` props 把 `Building` 分进
`Parts`/`Pods`/`Bays`/`Bridges`/`AICores`/`Sensors`/`FoamDistributors`/`LifeSupports`/`Turrets`，也与层无关。

### 修法

1. **热量网跨层**（#11）：`AccumulateToNetNew` 的邻接枚举换成 `Sos2CellGraph.Neighbors`。
   跨层边的成立条件建议复用 RimLayers 的语义：
   - `owner.Connectors.HasTransferEdge(lower, upper, LayerChangeKind.Heat)`——由 `ConnectorTransmission` DefModExtension
     的 `heat` 开关驱动（`Navigation/ConnectorGraph.cs`）；给 SOS2 的 `Ship_Conduit`/`Ship_HeatSink` 加这个扩展（XML Patch 在 RimLayers 侧），
     或者
   - `!owner.Structure.HasSurface(upper)`（开放竖井/镂空层），此时上下两层的热量网应连通。
2. **舱压跨层**：`Comp/ShipMapComp.cs:152-171` 的 `breathableZone.innerGrid.Set(vec, true)` 逐 `ship.Area` 逐格判定
   `VecHasLS(vec) && !ShipInteriorMod2.ExposedToOutside(vec.GetRoom(map))`。
   `ShipInteriorMod2.ExposedToOutside(Room)` 是 `room == null || room.OpenRoofCount > 0 || room.TouchesMapEdge`，
   而 RimLayers 已经把 `Room.OpenRoofCount` / `Room.TouchesMapEdge` 两个 getter 换成了 `owner.Sky.OpenRoofCount` / `owner.Sky.TouchesEdge`
   （`Environment/HeatPatches.cs`），所以**舱压判定天然分层**，不需要额外补丁。
   `breathableZone.innerGrid` 是 `BoolGrid`，扩层时由 `Generation/LiveMapExpansion.cs` 的 `PrepareManaged`
   （roots 含 `areaManager.AllAreas`）自动 `ClearAndResizeTo`；这一条**需游戏内复核**（见 §11）。
3. **设施链接**：SOS2 的两处 `CompAffectedByFacilities`（`SpaceCrib`、archotech pillar）会受 RimLayers
   `Environment/FacilityPatches.cs` 的 `LayerFacilityScopePatch` 影响 —— 它对非引擎 def 一律要求
   `PowerLinks.SameLayer(myMap, facilityPos, myPos)`，即**同层**。这对 SOS2 是正确行为（crib 与加速器应同层），
   但需要确认 SOS2 没有跨层的 crib/pillar 用法。**不需要改代码，只需回归验证。**
4. **`AccuracyBoost`**：`ShipHeatNet.AccuracyBoost` 取 `TacCons` 最高炮术，`TacCons` 来自同一张网里的 `Building_ShipBridge`
   （`ShipHeatNet.Register` 的 `br.TacCon` 分支）。跨层连通后，上层舰桥的炮术加成会传导到下层炮塔 —— 这是**期望行为**，
   但要确认不会把「另一艘船的舰桥」连进来：用 `SpaceShipCache.Index` 做网的分组上限（#12）。

---

## 8b. 九个子系统的逐项结论

SOS2 的九个子系统按「它们怎么跨层失败」分成三类，而不是按名字分类。这个划分决定了要不要写补丁。

| 子系统 | SOS2 实现 | 跨层失败方式 | 处理 |
|---|---|---|---|
| 维生系统 | `Comp/CompShipLifeSupport.cs`，`CompTick` 每 360 tick，`ReceiveCompSignal` 只置 `breathableZoneDirty` | **不会失败**：零邻接、零 `Map.Size`、零 `GetRoom`；舱压由 `ShipMapComp.MapComponentUpdate` 按 `ship.Area` 逐格算，而 `Room.OpenRoofCount`/`Room.TouchesMapEdge` 已被 RimLayers 换成分层实现 | 无补丁，只加断言 |
| 炮台电容 | `Building/Building_ShipCapacitor.cs` 只重写 `DrawAt` 画 `CompPowerBattery.StoredEnergyPct` | **不会失败**：真正的跨层电力在 `PowerNet`，RimLayers 的 `PowerLinks` 只对带 `CompPowerRiser` 的建筑架桥 | 无补丁；跨层供能是玩家铺 `RL_PowerRiser` 的显式行为 |
| 传感器 | `Comp/CompShipScanner.cs`，`CanUseNow` 只看 `Spawned && PowerOn && IsSpace`，`DoUsed` 全局扫 `Find.WorldObjects` | **不会失败**：零邻接 | 无补丁，只加断言 |
| 舰桥 / 驾驶核心 | `Building/Building_ShipBridge.cs`；`mannableComp == null` 即 AI core | 设施链接不受影响（SOS2 不走 `CompAffectedByFacilities`）；但 `GeneratePlayerShipMap(Map.Size)` 会把存储高度当南北长度 | `Sos2SubsystemPatches.BridgeMapSizePatch` |
| 引擎 | `Comp/CompEngineTrail.GetExhaustArea` 用 2D `CellRect` 长出排气锥 | 靠 band 顶部时溢出到 padding 行、再到上一层，`TakeDamage(DamageDefOf.Bomb, 100)` 会烧错层 | `EngineExhaustPatch` 只保留本层格 |
| 穿梭机港口 | `Comp/CompShipBay.CanLaunchShuttle` 判 `bayRect.Contains(v)` | `CellRect` 天然跨不了 band，但「跨不了」是存储布局的副作用而不是决策 | `BayShuttleScopePatch` 显式判层 |
| 热量 / 热容 / 散热 | `CompShipHeatSink.PushHeat` → `GenAdj.CellsAdjacent8Way` + `GenTemperature.PushHeat(vec, map, heat)` | 八邻可能选到上一层格，且 `PushHeat` 是存储格写入 → 上层散热片会加热下层房间 | `HeatSinkPushPatch` 拒绝跨层推热 |
| 热量网（导管） | `ShipHeatConduit` 本身就是 `CompShipHeat`；`AccumulateToNetNew` 同层四邻 BFS | 上下层各自成网，上层网没有 sink → 上层武器打几发就过热 | 已有 `Sos2HeatPatches` 的跨层邻接（§8.1），无需新补丁 |
| 飞船战斗 | 两条完全不同的弹道（§7）；`MapEdgeCell` / `FindClosestEdgeCell(LowSpread)` 用 `Map.Size.z` 当南北长度 | 弹道生成点在存储矩形的任意 band 边缘，飞不到目标船 | `CombatEdgeCellPatch` / `CombatRandomEdgeCellPatch` |

「不会失败」的三个子系统也**写了断言**，因为「读代码得出它没问题」和「跑起来证明它没问题」是两件事；
一个日后给维生系统加 `GetRoom` 调用的改动会让它静默变成第三类。

---

## 9. 补丁挂点总表

新增文件建议放在 `Source/RimLayers/Compatibility/`，与 VGE/Aerocraft 同构（可选绑定 + 反射 + 缺类型即抛）。

| 文件 | 内容 |
|---|---|
| `Sos2Compatibility.cs` | `Active => ModsConfig.IsActive("kentington.saveourship2")`；`Install(Harmony)`：反射取 `ShipMapComp`/`CompShipCachePart`/`Building_ShipTurret`/`ShipInteriorMod2`/`SpaceShipCache`/`ResourceBank+RoofDefOf` 的类型与成员，缺失即 `throw new InvalidOperationException("[RimLayers] SOS2 type not found: " + name)`（照 `Compatibility/VanillaGravshipCompatibility.cs` 的写法）；注册 §4 的两个扩展点；订阅 `LiveMapExpansion.Committed` |
| `Sos2CellGraph.cs` | `Neighbors(IntVec3)`、`LayerOf(IntVec3)`、`GroundSize(Map)`、`BandOrigin(IntVec3)`；`IsShipPlatingOrHull(Thing)` |
| `Sos2AdjacencyPatches.cs` | Transpiler：`ShipMapComp.AttachAll`、`SpaceShipCache.RebuildCache`、`CompShipCachePart.PostSpawnSetup` 里的 `GenAdj.CellsAdjacentCardinal(Thing)` → `Sos2CellGraph.Neighbors`（锚点精确匹配，否则抛） |
| `Sos2HeatPatches.cs` | Prefix `ShipMapComp.AccumulateToNetNew`（换邻接枚举）；Prefix `ShipMapComp.KillAllOffShip`（#4）；`Committed` 回调里重建 `grid`（#3） |
| `Sos2GeometryPatches.cs` | Postfix `Building_ShipTurret.MapEdgeCell`、`ShipMapComp.FindProjectileSpawnDirection`、`ShipMapComp.FindClosestEdgeCellLowSpread`；Prefix `ShipInteriorMod2.CanPlaceShipOnVec`；Prefix `ShipInteriorMod2.TransformForShipMove`、`SpaceShipCache.CenterShipOnMap`、`ShipInteriorMod2.LaunchShip`（#5/#6） |
| `Sos2BuildPatches.cs` | Postfix `PlaceWorker_ShipHull.AllowsPlacing`、`PlaceWorker_ShipPlating.AllowsPlacing`、`PlaceWorker_OnShipHull.AllowsPlacing`（#10）；`RL_ShipDeckExpand` 设计器的入口（#8） |
| `Sos2FoamPatches.cs` | Prefix `SpaceShipCache.SlowTick` 的泡沫循环，按「先同层、再按 path」排序（#7） |
| `Sos2SubsystemPatches.cs` | 五个子系统的定点修正：`CompEngineTrail.GetExhaustArea`（排气锥分层）、`CompShipHeatSink.TryPushHeat`（拒跨层推热）、`CompShipBay.CanLaunchShuttle`（港口判层）、`ShipInteriorMod2.GeneratePlayerShipMap`（存储尺寸 → 逻辑尺寸）、`ShipMapComp.FindClosestEdgeCell(LowSpread)`（弹道边缘格落回目标所在层） |

`RimLayersMod.cs` 的安装顺序里，`Sos2Compatibility.Install(harmony)` 必须加在
**「Optional-adapter registration must run before the patch loop」那条注释所辖的区块内**（即
`VVRaceCompatibility` / `AerocraftCompatibility` / `SmartCombatCompatibility` / `DubsCompatibility` / `VEPipeSystemCompatibility`
之后、补丁类循环之前），因为 §4 的扩展点要在任何 `HasSurface` 调用发生前注册。

`Mods/RimLayers/About/About.xml` 的 `loadAfter` 加 `<li>kentington.saveourship2</li>`。
SOS2 侧不改（第三方 mod）。

---

## 10. 明确不需要做的事

| 不需要 | 原因 |
|---|---|
| 改 `MapShipCells` / `Area` 的容器类型 | 都是 `IntVec3` 键/元素，存储坐标已含层号（§1） |
| 给 SOS2 船加 RimLayers 的 `Comp` 或 `DefModExtension`（除 §8 的 `ConnectorTransmission` 外） | SOS2 的 `AddToCache`/`RecacheFromRoots` 只认自己的 `CompShipCachePart`；RimLayers 的 `ShipStructureService` 只认 `Building_GravEngine` |
| 把 SOS2 船体地形改成 `isFoundation` | 会同时污染 20+ 处 `FoundationAt?.IsSubstructure` 判定（§4） |
| 复用 `Ships/ShipTopology.Resolve` 做 SOS2 归船 | 语义不同：SOS2 不需要「向下找锚点」，已有 `CheckAndMerge`/`Mass` 规则（§3） |
| 接管 `Building_ShipTurret` 的 AI/射程 | 它已重写 `TryStartShootSomething`，两套 AI 叠加会互相打断（§7） |
| 让 `Combat/ProjectilePatches.cs` 覆盖 `ShipCombatProjectile` | 那是 `IExposable` 纯数据，不是 `Projectile`（§7） |
| 改 SOS2 的 `About.xml` / `MoveShipFlag` 语义 | 第三方 mod；`MoveShipFlag` 有 54 处依赖，语义不能动 |
| 自动缩层 | 与 RimLayers 现有策略一致：降低上限不删除旧层（`RimLayersSettings.Normalize`） |

---

## 11. 已知退化与未验证项

1. **`MapGenerator.Elevation` / `MapGenerator.Fertility`（`MapGenFloatGrid`）是否随扩层增长**：`LiveMapExpansion.PrepareManaged`
   只处理 `Map` 字段、`Area`/`Zone`/`Plan`、`map.components`。SOS2 的 `GenStep_EmptySpace` 会写这两个 grid，
   扩层后新带的这两个 grid 若仍是旧尺寸，`GenStep` 重跑会越界。**需游戏内复核。**
2. **`breathableZone.innerGrid` 扩容**：`BoolGrid` 理论上由 `PrepareManaged` 的 roots 覆盖（`areaManager.AllAreas`），
   但 `breathableZone` 是 `ShipMapComp` 的 private 字段、由 `map.areaManager.TryMakeNewAllowed` 注册。
   **需游戏内复核**：扩层后 `breathableZone.innerGrid` 的 `SizeX/SizeZ` 是否等于新的 `Layout.Width/StorageHeight`。
3. **`Map.Size` 的 120 处用法**：本次只核对了 23 处（`Building_ShipTurret.cs:472/485/486/607/624`、
   `Comp/ShipMapComp.cs:1278/1287/3148/3150/3152/3180/3186/3187/3496`、`BlastLanding.cs:231`、`DetachedShipPart.cs:54`、
   `Events/GameCondition_SpaceDebris.cs:57/62/63/68/72/73`、`Events/IncidentWorker_SpaceDebris.cs:32/36/44`、
   `MapGen/GenStep_DownedShip.cs:35`、`MapGen/GenStep_InsectPillarCaves.cs:34/35/37/41`、`MapGen/GenStep_ShipDebris.cs:42`、
   `MapGen/GenStep_StashedShip.cs:244`、`MapGen/GenStep_ValuableAsteroids.cs:49/74`、
   `PlaceWorker/PlaceWorker_MoveShip.cs:29/48`、`PlaceWorker/PlaceWorker_NeedsSpinalMountPort.cs:33`、
   `ScenPart/ScenPart_StartInSpace.cs:182/317`、`HarmonyPatches.cs:4409`、`Verb/Command_SelectShipMap.cs:58`、
   `Obs-Shuttle/TransportPodsArrivalAction_ShipAssault.cs:43/136/138/229/231`）。其余需要按「语义是南北长度还是存储高度」逐个判。
4. **`ShipImorter.cs` 的图纸导入**：`PickFlippedCornerAt` 用 `Math.Sign(pos.x - map.Size.x/2)` / `Math.Sign(pos.z - map.Size.z/2)` 判角，
   扩层后中心偏移到存储带中段。图纸导入是低频操作，列为**已知退化**（导入后位置偏，但不会崩）。
5. **`CompGravshipConversion.ConvertHullTile`**（`1.7/Comp/CompGravshipConversion.cs:70-140`）用
   `parent.Position + (Rand.InsideUnitCircleVec3*radius).ToIntVec3()` 平面采样，只会转换当前层；
   它用 `ShipIndexOnVec` 判同船（跨层安全），所以不会误伤，只是「上层不参与转换」。列为**已知退化**。
6. **`Comp/ShipMapComp.cs:683-719 RecacheMap` 的 "invalid cells" 警告**：扩层后的 `RL_OpenAir` 空腔格会命中，
   修好 #1 后需回归确认警告不再刷屏（§0 #14）。
7. **性能**：扩层后 `map.AllCells` 规模 = `Width × StorageHeight`，`KillAllOffShip`/`GenStep_EmptySpace`/`RecacheFromRoots`
   都是全图遍历。`RecacheFromRoots` 只在读档与 `CacheOff` 解除时跑，可接受；`KillAllOffShip` 每次移动都跑，
   必须在 #4 里改成按 `Area` 遍历，否则移动会随层数线性变慢。
8. **未做**：CE（Combat Extended）兼容。SOS2 自己的 CE 桥是 `ShipHeatNet.IsCompatibleCETurret` 反射
   `CombatExtended.Compatibility.SOS2Compat.Building_ShipTurretCE`，与 RimLayers 的 CE 缺口（`About.xml` 明写「CE、其他飞船框架尚未适配」）叠加，
   本次不处理。

---

## 12. 实施顺序与验收

**顺序**（每步都能单独验收，前一步不做后一步必然失败）：

1. §4 的两个扩展点 + `Sos2Compatibility.Install()` 骨架（S，无行为变化，只保证 `HasSurface`/`SupportedRoof` 认得 SOS2）
2. §3 跨层邻接 + `Sos2AdjacencyPatches`（M，让上层部件归船）
3. §0 #3 的 `grid` 重建 + #4 的 `KillAllOffShip`（S，扩层安全）—— 实测不需要 #13 的 tick 闸门：
   `LiveMapExpansion.Committed` 在 layout 与 `cellIndices` 都已定、且不可回滚之后触发，
   一次性重填 `-1` 就够；逐 tick 轮询反而会在每次扩层后白跑一轮 4.9 万格。
4. §5 的几何 helper + `Sos2GeometryPatches`（M，起降/移动/炮塔边缘）
5. §6 泡沫 distributor 分层（S）
6. §8 热量网跨层（M）
7. §8 的 `RL_ShipDeckExpand` 入口（M，太空造多层船的最后一块）

**验收矩阵**（`Tests/Run-V1.ps1 -Scenario sos2 -Sos2`，照 `-Aerocraft`/`-VePipes` 的写法把
`kentington.saveourship2` 加进 `$active`；还必须同时启用 Vehicle Framework 提供者，因为 SOS2 的
`ShipsHaveInsides.dll` 链接 `Vehicles.dll`，缺了它会 `ReflectionTypeLoadException` 并让整个程序集不可用）。

已落地的是**单一 `sos2` 场景**（`Diagnostics/V1Sos2.cs`，generate + load 双腿），它覆盖了下表中
最关键的路径；下表保留为完整意图清单，尚未逐条拆成独立场景：

| 已实现的断言（`V1Sos2.cs`） | 覆盖的意图 |
|---|---|
| `sos2-adapter-is-installed` / `sos2-map-component-is-attached` | 适配器在 SOS2 存在时绑定成功 |
| `sos2-upper-deck-joins-the-same-ship` / `sos2-lower-deck-still-tracked` | `sos2-deck-build`：跨层邻接让上层部件归入同一艘船 |
| `sos2-band-length-is-not-the-storage-length` | `sos2-move`：`Map.Size.z` 语义修正 |
| `sos2-turret-edge-stays-on-the-ground-band` / `sos2-move-mirror-keeps-the-band` | `sos2-combat` / `sos2-move`：几何不跨带 |
| `sos2-heat-nets-were-rebuilt` / `sos2-heat-grid-covers-the-upper-deck` | `sos2-heat-cross`：同一热量网跨两层 |
| `sos2-expansion-grows-the-map` / `sos2-expansion-refills-the-heat-grid-with-minus-one` | `sos2-expand`：扩层后 `grid` 重建为 `-1` 而非零填充 |
| `sos2-loaded-ship-keeps-its-upper-deck` / `sos2-loaded-heat-nets-rebuild-across-decks` | `sos2-save`：存读档后上层甲板仍在 |
| `sos2-engine-exhaust-stays-on-its-own-deck` | 引擎：排气锥不溢到上一层，不会烧错甲板的人 |
| `sos2-heat-sink-refuses-to-push-heat-across-decks` | 散热：上层散热片不加热下层房间 |
| `sos2-shuttle-bay-rect-never-covers-the-ground-deck` | 穿梭机港口：只在自己那层收放 |
| `sos2-space-map-is-generated-at-the-logical-size` | 舰桥：太空图按逻辑高度生成，不是存储高度 |
| `sos2-combat-edge-*-spawns-on-the-targets-deck` / `-on-a-band-edge` | 飞船战斗：来袭弹道生成在目标所在层的地图边缘 |
| `sos2-upper-deck-{life-support,sensor,engine}-joins-the-ship` | 维生 / 传感器 / 引擎：上层部件重建缓存后仍归本船 |
| `sos2-capacitor-stays-on-its-own-power-net` | 炮台电容：跨层不自动互联，需玩家铺 `RL_PowerRiser` |
| `sos2-heat-conduit-links-both-decks` | 热量导管：上下两层的导管落在同一张网 |

尚未自动化、需要单独补场景的意图：

| 场景名 | 断言 |
|---|---|
| `sos2-deck-build` | 太空图 `EmptySpaceMap` 上：建 `RL_ShipStructuralFloor` 成功；上层 `CompShipCachePart` 加入 `MapShipCells`；`ShipsOnMap[idx].Area` 同时含两个 band 的格 |
| `sos2-expand` | `RL_ShipDeckExpand` 触发 `EnsureElevation`；扩层后 `grid.Length == NumGridCells` 且全为 `-1`（除已建网）；`heatGridDirty` 被置真后 60 tick 内重建出跨层网 |
| `sos2-heat-cross` | 下层散热片 + 上层炮塔（中间开放竖井）→ 同一 `ShipHeatNet.GridID`；`Connectors.HasTransferEdge` 为真时连通，铺满甲板时**不**连通 |
| `sos2-foam` | 上层 1×1 hull 件被毁 → 进入 `BuildingsToFoam` 且 `Item3` 为跨层 path；`SlowTick` 用同层 distributor 补齐；下层 distributor 不补 |
| `sos2-move` | 多层船 `MoveShip` 到新太空图：`MapShipCells` 键的 `layer*Stride` 部分保持不变，`CenterShipOnMap` 落在地面层而非存储带中段；`rotNum=1..3` 时层不变、local 旋转正确 |
| `sos2-combat` | 上层炮塔 `MapEdgeCell` 落点在本层带内（`z ∈ [layer*Stride, layer*Stride+Height-1]`）；`ShipCombatTargetMap` 跨图命中不变 |
| `sos2-save` | 存读档后 `RecacheFromRoots` 重建出同样层数的 `Area`；无 "invalid cells" 警告；`RecacheMap` 不丢层 |
| `sos2-killoffship` | 扩层后 `KillAllOffShip` 只销毁本层船外物体，`RL_Isolation` 带与其它层的东西**不**被 Destroy/Kill |

游戏内人工复核项（不在自动化里）：`MapGenFloatGrid` 扩容（§11.1）、`breathableZone.innerGrid` 尺寸（§11.2）、
`RecacheMap` 警告（§11.6）、移动性能随层数变化（§11.7）。

---

## 13. 一句话总结

SOS2 的数据结构本来就是「一张 Map 上的格字典」，RimLayers 的 `z = layer*Stride + localZ` 让它天然多层；
**唯一真正的冲突是「四邻接 BFS」和「`Map.Size.z` 当南北长度」这两类假设**，加上 SOS2 的船顶/船体不被 RimLayers 认作楼板。
把这三处做成可注册的扩展点 + 一组精确锚点的 Transpiler，就能拿到「多层飞船、太空造多层船、泡沫补船、多层战斗、跨层设施链接」，
且不需要改 SOS2 一行源码、不需要迁移存档。
