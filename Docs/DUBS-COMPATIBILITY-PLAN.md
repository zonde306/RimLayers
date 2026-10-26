# Dubs 全家桶 + Rimefeller 跨层兼容方案

> **状态：已实施。** 本文最初是调研稿，现已按下面方案落地代码与 XML。
> 与初稿有出入的地方均已在对应小节里标注并说明原因（§2.2 是最主要的一处）。

范围：`Dubwise.DubsBadHygiene`(836308268)、`Dubwise.DubsCentralHeating`(2619214952)、
`Dubwise.Rimatomics`(1127530465)、`Dubwise.Rimefeller`(1321849735)、`Dubwise.DubsSkylights`(833899765)。

需求（用户）：管道/电线/数据线要能跨层连通（提供跨层端子）；地下水与地下石油只能生成在
地表层与地下层（elevation ≤ 0），2 层及以上禁止；天窗只在「上方各层都没有屋顶/楼板遮挡」时有效。

本文记录已核实的结论与实施方案。§2.2 / §2.6 / §3 已按**实际实现**修订；§4 保留逐 mod 的调研结论。
所有 RimLayers 侧文件路径相对 `Mods/RimLayers/Source/RimLayers/`，mod 侧路径相对各自 `1.7/`。

## 0. 基准

| 程序集 | MVID | 规模 |
|---|---|---|
| `Assembly-CSharp.dll`（1.7.4981 rev136） | `d368493b52bd4740bc7507c1b0386895` | 16222 types / 77419 methods |
| `836308268/1.7/Assemblies/BadHygiene.dll` | `85b03536f71c47808aaa3c790e11de9a` | 481 / 2273 |
| `2619214952/1.7/Assemblies/DubsCentralHeating.dll` | `b74b2d858cc54d2a993ce923f6496344` | 75 / 330 |
| `1127530465/1.7/Assemblies/Rimatomics.dll` | `2ebdd5f1cb8644f789b8134092dc5da5` | 393 / 1795 |
| `1321849735/1.7/Assemblies/Rimefeller.dll` | `bc5c8f90b8fd4631a0f0f21f7e5c664f` | 163 / 736 |
| `833899765/1.7/Assemblies/Dubs Skylight.dll` | `ec82302f1aea40d2ba8d0242f646cf59` | 36 / 124 |

RimLayers 初稿时的现状已改变：`Compatibility/{LayerPipePorts,DubsCompatibility,DubsWater,RimefellerOil,DubsSkylights}.cs`、
`Patches/LayerPorts.xml`、`Languages/ChineseSimplified/DefInjected/ThingDef/RimLayers_LayerPorts.xml`、
`Diagnostics/V1Dubs.cs` 均为本次新增；`Generation/{LiveMapExpansion,GridCapacityRepair,ExpansionBuffers,LayerGeneration}.cs`
为配合扩层做了修改。

## 1. 结论摘要

| # | 现象 | 根因（已核实） | 修法（已实现） | 规模 |
|---|---|---|---|---|
| 1 | 所有管道网都不跨层 | 4 个 mod 的管网重建都用 `map.floodFiller.FloodFill(...)`，只走平面四邻 | `FloodFill` Prefix 在管网重建作用域内**接管整段 BFS**，带端子的格额外向上下层同一位格扩散（§2.2） | M |
| 2 | 跨层端子放不下 | 4 个 mod 的 `RegisterPipe` 都报 `"More than 1 pipe comp in the same cell at {current}, things with pipes cannot overlap!"` | 端子必须是**该介质的管道本身**（def 继承各自 pipe 基类 + 加 RimLayers 标记 comp），不能叠在管子上 | S |
| 3 | 扩层后管网崩 / 读错 | `PipeGrid` 是 `Int32[,]`（Rank==2），`GridCapacityRepair` 与 `LiveMapExpansion.PrepareManaged` 原只扩 Rank==1 数组 | `IsCellBuffer` 支持「末维 == 旧 cell 数」的 rank-2 数组，`GrowCells` 逐行前缀拷（§3） | M |
| 4 | 扩层后 mod 数据不刷新 | `LiveMapExpansion` 只调 `owner.FinalizeInit()`，**不调** `MapComponentUtility.MapGenerated/FinalizeInit` | `LiveMapExpansion.Committed` 事件（仅提交成功路径 + 生成路径）+ `DubsCompatibility.OnLayersCommitted` | S |
| 4b | 扩层中 overlay 先于适配器抛 `IndexOutOfRangeException` | `map.mapDrawer.RegenerateEverythingNow()` 在 `NotifyCommitted` **之前** | 内嵌一层的 `GridLayer`/`OilGrid` 在事务内由 `NestedGrids` 扩（§3） | M |
| 5 | 地下水在 2 层及以上也生成 | `MapComponent_Hygiene.RegenWaterGrid/RegenDeepWaterGrid` 用 `foreach (var c in map.AllCells)`，扩层后 `AllCells` 覆盖所有存储带 | Postfix 事后清零 elevation>0 与隔离带的格 | S |
| 6 | 深层/浅层石油在 2 层及以上也生成 | `Rimefeller.OilGrid.GenerateFields` 用 `for (i < SeedGrid.Length)`，`RegenSeedGrids` 用 `map.AllCells` | Postfix 清零 `SeedGrid` 上层 + 剔除上层 `Fields`；扩层后 `Replant()` 补新字段而**不**调 `GenerateFields`（§4.4） | S |
| 7 | 天窗被上层楼板盖住仍然透光/发热 | `MapComp_Skylights.RegenGrid` 完全不看其它层；`Building_skyLight.TickRare` 只看自身 `CurSkyGlow`；`Patch_GameGlowAt` 直接覆写 `__result` | Postfix `RegenGrid` 按 `LayerMapComponent.Sky.IsExposed` 逐格清位；Prefix `TickRare` 在被遮挡时跳过 | S |
| 8 | 存档膨胀 | `MapSerializeUtility.SerializeUshort/Int` 按 `map` 的全部 `IntVec3` 遍历，storage 高度 = `Stride*LayerCount` | 无法避免（三层 250×250 图约 3.06×），文档记录即可 | — |
| 9 | 上层管道贴图错连 | `Graphic_LinkedPipe.ShouldLinkWith` 只看 `ZoneAt(c,mode)` | **实际不会发生**：它只收到 cardinal 邻格，而层间偏移是 `±Stride`、永不相邻。只给端子加了 `<color>` 染色（§2.5） | — |

## 2. 核心方案 A：FloodFiller 竖向桥

### 2.1 为什么不能直接用 `extraRoots`

`Verse.FloodFiller.FloodFill`（`d368493b…:06003792`）关键行为已核实：

- 只有 `GenAdj.CardinalDirectionsAround` 四邻；`traversalDistance` 去重。
- `extraRoots` 会被无条件入队（`visited.Add(idx); traversalDistance[idx]=0; openSet.Enqueue(x);`），
  **但 root 与 extraRoots 本身不再过 `passCheck`**；且 `extraRoots != null` 时连 root 的
  `passCheck(root)` 也会被跳过。
- `int area = map.Area;`（扩层后 = 全 storage 面积，所以溢出阈值不会误报）。

而 4 个 mod 的 `passCheck` **同时承担「判定 + 记账」**（写 `GridID`、`pipeNetRef`、
`PipeGrid[P,idx]`、`newNet.PipedThings.Add`），`processor` 是 `NoProcessor`。
→ 如果把邻层格塞进 `extraRoots`，那格不会被记账，`RebuildPipeGrid` 后续仍认为它
`GridID == -1`，会**再开一个 net 把该层切走**。这是最容易踩的坑。

### 2.2 正确做法：整体替换洪泛（最终实现）

> 本节曾写成「只包装 `passCheck`」，**那是错的**，已在实现时否证：
> `FloodFill` 只对**出队格的平面四邻**调用 `passCheck`，对侧层的格子永远不会被问一次，
> 所以包装函数根本没有机会回答「跨层」。真正的实现见 `Compatibility/LayerPipePorts.cs`
> 的 `FloodPrefix` → `Replace`。

在 `Verse.FloodFiller.FloodFill` 的 `Func<IntVec3,int,bool>` 重载上加 **Prefix**，
在管网重建作用域内（`depth > 0`）**整段接管**这次洪泛，跑一个与原生等价的 BFS：

```
Replace(map, owner, root, passCheck, processor, maxCellsToProcess):
  if root.IsValid && !passCheck(root) → return          # 与原生一致
  enqueue root(distance 0)
  while queue:
    cell = dequeue
    if processor(cell, travelled) → break               # 与原生的截断语义一致
    if ++processed == maxCellsToProcess → break
    for each GenAdj.CardinalDirectionsAround: TryEnqueue(cell + dir, d+1)
    if cell 上没有 CompLayerPort → continue              # 零开销：无端子的格不进跨层分支
    TryEnqueue(owner.Adjacent(cell, -1), d+1)            # 下一层同位格
    TryEnqueue(owner.Adjacent(cell, +1), d+1)            # 上一层同位格

TryEnqueue(next, d):
  if !next.InBounds(map) → return
  if visited[next] → return                             # 每格**恰好**过一次 passCheck
  if !passCheck(next) → return
  visited[next] = true; distance[next] = d; enqueue(next)
```

要点：

- 每个格子**恰好**经过一次 `passCheck`，而 4 个 mod 的记账（`GridID`、`pipeNetRef`、
  `PipeGrid[P,idx]`、`PipedThings.Add`、`cells.Add`、`escaped`）全在 `passCheck` 里做完，
  所以净结果与原生单层洪泛完全一致，不需要伪造任何状态。
- `processor` 与 `maxCellsToProcess` 照搬原生语义（这几个 mod 传的是 `NoProcessor`，但保留
  语义是为了不误伤别的调用者）。
- 只对带 `CompLayerPort` 的格开跨层分支，普通地图上开销 = 每格一次 `GetComp` 命中失败。
- `visitedStamp` 用「戳」数组（`int[]` + `stamp`）而不是每次 `Array.Clear`，避免每次重建
  O(NumGridCells) 清零。
- 溢出检查 `queue.Count > area` 与原生同义；`map.Area` 扩层后是全 storage 面积。
- `rememberParents || extraRoots != null` 时直接放行原生实现（这几个 mod 恒传 `false`/`null`）。

最终实现里另有一个 `replacing` 重入守卫：`Replace` 自己调 `passCheck` 时若 `passCheck`
内部又触发洪泛，不会再次递归进 `Replace`。

### 2.3 生效范围开关

`FloodFiller` 是全局系统，必须限定在管网重建期间。用 `[ThreadStatic] int depth`：

- Prefix/Postfix 各 mod 的 `RegenPipeGrids()` / `RegenGrids()` / `RebuildPipeGrid(int)`：depth ±1。
- `FloodFill` Prefix：`depth == 0` 直接返回 true（原样放行）。

已核实的重建入口（public，可直接打补丁，也可被兼容层直接调用）：

| mod | 类型 | 入口 |
|---|---|---|
| DBH | `DubsBadHygiene.HygienePipeMapComp` | `RegenPipeGrids()` `060005B2`、`RebuildPipeGrid(Int32)` `060005BC`、`DirtyAllPipeGrids()` `060005B9` |
| DCH | `DubsCentralHeating.HygienePipeMapComp` | `RegenPipeGrids()` `060000E2`、`RebuildPipeGrid(Int32)` `060000EC`、`DirtyAllPipeGrids()` `060000E9` |
| Rimefeller | `Rimefeller.UniversalPipeMapComp` | `RegenPipeGrids()` `060001FA`、`RebuildPipeGrid(Int32)` `06000202`、`DirtyAllPipeGrids()` `060001FF` |
| Rimatomics | `Rimatomics.UniversalPipeMapComp` | `RegenGrids()` `06000365`、`RebuildPipeGrid(Int32)` `0600036B`、`DirtyAllPipeGrids()` `0600036A` |

### 2.4 增量重建必须被禁用（重要）

- DBH `PipeChanged`（`060005BA`）只 `changedPipes[mode].Add(pipe)` + 把
  `GenAdj.OccupiedRect(pipe.parent)` 的格塞进 `changedCells[mode]`。
- `RebuildChangedNets(int P)`（`060005BE`）只用 `SelfAndAround`（Self + 平面四邻，5 格）做种子。
→ 竖向桥永远不会被增量路径发现，**增量重建会把已并好的 net 切开**。

修法：Prefix 各 mod 的重建入口，把「本次要跑的 mode」的 `FullRebuild[i]` 置 true，
即只要有端子存在就只走全量 `RebuildPipeGrid`。全量代价：每 mode O(NumGridCells)，
在扩层后与每次管网变动各一次，属可接受（与 DBH 原始的 FullRebuild 语义相同）。

**注意 `FullRebuild` 并非四家都有（早期文档此处写错，已核实）：**

| mod | 有 `bool[] FullRebuild` | 说明 |
|---|---|---|
| DBH | 有 | `RegenPipeGrids` 里 `FullRebuild[i] ? RebuildPipeGrid(i) : RebuildChangedNets(i)` |
| DCH | 有 | 同上 |
| Rimefeller | 有 | 同上 |
| Rimatomics | **没有** | 只有 `DirtyPipe`；`RegenGrids()` = `for i: if DirtyPipe[i] RebuildPipeGrid(i)`，本身就没有增量路径 |

所以 `LayerPipePorts.Binding` 把 `FullRebuild` 存成 `FieldInfo?` 并 null 守卫；Rimatomics 走
「无增量路径」分支，不需要额外处理。

### 2.5 端子 def（XML）

约束：
1. 端子必须**自己就是该介质的管道**（占同一格会触发 `More than 1 pipe comp` /
   DBH 的 `CompPipe.PostSpawnSetup` 直接 Destroy 同格同 mode 的 Building_Pipe）。
2. 端子必须被 mod 原生注册（`RegisterPipe`），因此必须带该 mod 的
   `CompProperties_Pipe`。
3. RimLayers 不能编译期引用工坊程序集 → 新 comp 由 RimLayers 提供，mod 的 comp 用字符串类名写在 XML 里。

做法：新 def 继承各 mod 的抽象管道基类，父类提供 `thingClass`/贴图/`linkFlags`/
`placeWorkers`，自己补 `<mode>` 和标记 comp：

```xml
<ThingDef ParentName="DubsDirtyPipeBase">      <!-- 836308268 -->
  <defName>RL_DBHSewageRiser</defName>
  <label>vertical sewage riser</label>
  <comps>
    <li Class="DubsBadHygiene.CompProperties_Pipe"><mode>Sewage</mode></li>
    <li Class="RimLayers.Compatibility.CompProperties_LayerPort"/>
  </comps>
</ThingDef>
```

需要新增的 def（按各 mod 实际使用的介质枚举）：

| mod | 父模板（`1.7/Defs/ThingDefs_Buildings/`） | 需要端子 |
|---|---|---|
| DBH | `BuildingsA_Pipes.xml` → `DubsDirtyPipeBase` | `Sewage`、`Air`（`PipeType` 另有 `Heating`/`Water` 但全 Defs 里 0 次使用） |
| DCH | `BuildingsA_Pipes.xml` → `DCHPipeBase` | `Heating`、`Air` |
| Rimefeller | `BuildingsA_Pipes.xml` → `RimefellerPipeBase` | 只有 `Oil`（`Rimefeller.PipeType` 仅 1 个成员） |
| Rimatomics | `Buildings_A_Pipes.xml` → `RimatomicsPipeBase` | `Cooling`、`Steam`、`ColdWater`、`HighVoltage`、`Loom` |

注意：
- Rimatomics 的「电线」就是 `HighVoltage` 管网、「数据线」就是 `Loom` 管网，
  都走 `UniversalPipeMapComp`，**与原生 PowerNet 无关**，所以只能靠本方案跨层，
  `RL_PowerRiser` 对它们无效。
- 外观：实际实现没有新建 `graphicData`，而是在继承父模板贴图的基础上只覆盖 `<color>`
  （`(0.45,0.8,1)` 淡蓝），已验证 `<graphicData><color>` 会按字段合并、不影响父模板的
  `texPath`/`linkFlags`。跨层连边不会画错：`Graphic_LinkedPipe.ShouldLinkWith` 只收到
  cardinal 邻格，而 storage 坐标下层带偏移是 `±Stride`、永不相邻。
- 不要用 `CompProperties_Pipe.vertPipe` 做通用标记：只有 DBH 有该字段
  （`85b03536…:04000111:F`，且 `find_usages` 为 0，是死字段）；Rimefeller 与
  Rimatomics 的 `CompProperties_Pipe` 只有 `Type`/`mode`/`stuffed`，没有 `vertPipe`。

### 2.6 兼容层实际落地的文件

| 文件 | 作用 |
|---|---|
| `Compatibility/LayerPipePorts.cs` | `CompProperties_LayerPort` / `CompLayerPort` 纯标记；`FloodFill` Prefix + `Replace` 接管；`RebuildPrefix`/`RebuildFinalizer` 的 `depth` 门；`ExpansionCompleted(Map)` 扩层后强制 `FullRebuild` 或 `RebuildNow`；`LinkedLayers`/`CompInspectStringExtra` 给玩家看「连接楼层」 |
| `Patches/LayerPorts.xml` | 10 个端子 ThingDef，每个外层套 `PatchOperationConditional`（探 `sewagePipeStuff` / `HeatingPipe` / `OilPipeline` / `coolingPipe` 等对方 defName） |
| `Languages/ChineseSimplified/DefInjected/ThingDef/RimLayers_LayerPorts.xml` | 中文 label/description（英语没有 DefInjected 目录，label 直接写在 def 里） |
| `Generation/LiveMapExpansion.cs` | `Committed` 事件 + `NotifyCommitted`；`PrepareManaged` 的 `IsCellBuffer` 分支；`NestedGrids` 递归一层扩第三方 `GridLayer`/`OilGrid` |
| `Generation/GridCapacityRepair.cs` | `IsCellBuffer(array,cells)` 支持 rank-2；`GrowCells(old,oldCount,newCount)` 逐行拷 |
| `Generation/ExpansionBuffers.cs` | `GrowCells(owner,field,old,oldCount,newCount)` |
| `Compatibility/DubsCompatibility.cs` | 聚合安装器 + `Committed` 订阅 + `RepairDrawers`/`RepairLayer` + `ForEachOnUpperLayers` |
| `Compatibility/DubsWater.cs` | §4.1 地下水/深水/冰 |
| `Compatibility/RimefellerOil.cs` | §4.4 石油/深层石油 |
| `Compatibility/DubsSkylights.cs` | §4.5 天窗 |
| `Diagnostics/V1Dubs.cs` | `dubs` 场景的 40+ 项断言 |

启动时机：`[StaticConstructorOnStartup]` bootstrap（`Mod` 构造函数里 `AccessTools.TypeByName`
看不到其它 mod 的类型，`VVRaceCompatibility` 已有此教训），并照
`VanillaGravshipCompatibility` / `DubsMinimapCompatibility` 的
`ModLister.GetActiveModWithIdentifier(packageId, false) != null` 做门槛。

## 3. 核心方案 B：扩层收尾钩子

已核实：`Generation/LiveMapExpansion.cs` 的事务在结尾只调 `owner.FinalizeInit()`
（RimLayers 自己的 `LayerMapComponent`），**从不调用**
`MapComponentUtility.MapGenerated` / `MapComponentUtility.FinalizeInit`
（`d368493b…:0600148C/0600148D`）。`Generation/GridCapacityRepair.cs` 同理。

后果与对策（**已实现，下表是对当前代码的描述**）：

| 对象 | 扩层后会怎样 | 实际对策 |
|---|---|---|
| `PipeGrid`（4 mod 都是 `Int32[,]`） | 早期版本 `GridCapacityRepair` 只处理 `Rank==1`，长度停留在旧 `NumGridCells` | `GridCapacityRepair.IsCellBuffer(arr,cells)` 已扩为 `Rank==1 ? Length==cells : Rank==2 && GetLength(1)==cells`；`GrowCells` 对 rank-2 逐行 `Array.Copy`。`LayerPipePorts.ExpansionCompleted` 再 `DirtyAllPipeGrids()` 或 `RebuildNow` |
| `DirtyPipe`/`FullRebuild`（`bool[]`） | 自动扩容（前缀保留、尾部 false） | 同上，全部标脏 |
| `GridLayer.Grid`（`float[]`） | `map.components` 的直接字段能扩容，但 `GridLayer` 嵌在组件**下面一层**，旧代码到不了 | 新增 `LiveMapExpansion.NestedGrids(...)`（在 `changes.Apply()` 之内、`RegenerateEverythingNow()` 之前）递归一层到 `map.components` 的持有对象 |
| `GridLayer.DirtyFlags`（`bool[,]`，按 section 索引） | 末维是 section 数而不是 cell 数，**不**符合 `IsCellBuffer` | `NestedGrids` 里单独识别「维度 == 旧 section 矩形」的 `bool[,]` 并换成 `new bool[fullSections.x, fullSections.z]` |
| `GridLayer.Drawer`（`CellBoolDrawer`） | 私有 `mapSizeZ` 仍是旧值，网格画不全 | `NestedGrids` 设 `mapSizeZ = full.z` 且 `dirty = true` |
| `MapComp_Skylights.SkylightGrid/GardenGrid`（`bool[]`） | 自动扩容（尾部 false） | 够了；**不要**再替它跑 `RegenGrid()`（它会重建数组，使 DBH/DCH 缓存的 `Traverse<bool[]>` 失效） |
| `MapComp_Skylights.roofGridCopy`（`RoofDef[]`） | `IsCellBuffer` 已取消元素类型限制 → 现在会扩，尾部 null | 安全：`Patch_SectionLayer_LightingOverlay_Regenerate` 只在 `SkylightGrid[num]` 为真时回写，null 尾项永远读不到 |
| `PlumbingNet`/`PipelineNet`/`BasePipeNet` | 内部 `cells`/`PipedThings` 还是旧格 | 全量重建管网（`LayerPipePorts.ExpansionCompleted`） |
| `Rimefeller.OilGrid.SeedGrid`（`float[]`） | 同上，嵌在 `OilGrid` 里 | `NestedGrids` 扩；再由 `RimefellerOil.OnLayersChanged` 重跑 `RegenSeedGrids()` + `Clear()` + `Replant()` |
| `Verse.Area.innerGrid`（`BoolGrid`） | 构造时按当刻 `map.Size`（= 地面矩形）定尺寸，既不是 `oldSize` 也不是新尺寸 | `PrepareManaged` 的 `BoolGrid` 分支改为按 buffer 自身一致性判断（`Width==full.x && Height!=full.z`），拷贝长度取 `Min(arr.Length, Width*Height)`；**不要**用 `oldSize` 做前置条件 |
| **脱离 `areaManager.AllAreas` 的 `Area`** | DBH `RefreshAreas()` 三条 `RemoveAll` 都用 `x != cleanWater` 比较，把 `Area_WaterDirty`/`Area_WaterOcean` 每建即删；组件仍持有并写入它们，但 `AllAreas` 里查不到 → 上面的 `Area` 修复够不到 | `PrepareManaged` 收集 roots 时额外扫 `map.components` 的 `Area` 类型字段（`typeof(Area).IsAssignableFrom(f.FieldType)`），把孤儿 Area 也加入 root 集合 |

实际实现：钩子是 `Generation/LiveMapExpansion.cs` 的
`internal static event Action<Map, LayerLayout>? Committed;`，由 `NotifyCommitted(map, layout)` 触发：

- **一次**触发点：`EnsureElevation` 成功路径的 `changes.Commit(); owner.Underground.CommitGeneration();
  NotifyCommitted(map, layout); ResumePaths(paths);`。
- 同时由 `Generation/LayerGeneration.cs` 的 `FinalizeMapPatch` 在 `GridCapacityRepair.Fix` 之后触发，
  让生成路径与运行时扩层共用同一批订阅者。
- 两个提前返回（`elevation == 0` / 层已存在）与 catch 回滚路径**不**触发。
- 每个订阅者各自 try/catch（`Log.Error("A layer-expansion subscriber failed and was skipped")`），
  一个第三方适配器抛异常不能中止扩层。

> 为什么不能只靠「订阅者在 Commit 后自己扩」：`EnsureElevation` 里
> `map.mapDrawer.RegenerateEverythingNow()` 在 `NotifyCommitted` **之前**就跑了。
> 那段时 overlay `SectionLayer.Regenerate()` 已经用旧长度数组读过一遍，
> DBH `SectionLayer_Irrigation.Pork(int index) => grid.Grid[index]` 直接
> `IndexOutOfRangeException`。所以所有按 cell 索引的缓冲必须在**事务内**扩好，
> 也就是 `PrepareManaged` / `NestedGrids`，而不是 Commit 后。
> `Committed` 只用于「重算派生数据」（重灌管网、重播种、重建 drawer 尺寸）。
>
> **实测踩坑（2026-10-26）**：仅修 `areaManager.AllAreas` 里的 `Area` 后仍有 2846 条
> `BoolGrid.get_Item`。探针显示 `Area_WaterClean#6 listed=True` 而
> `Area_WaterDirty#7 listed=False`、`Area_WaterOcean#8 listed=False`：DBH 的
> `RefreshAreas()` 用 `areas.RemoveAll(x => x.GetType()==typeof(Area_WaterX) && x != cleanWater)`
> 三条**都**比 `cleanWater`，于是 Dirty/Ocean 的每个实例都被移除，而 `_dirtyWater`/`_oceanWater`
> 字段仍指向它们。这类 Area 只能通过组件字段发现，`AllAreas` 永远看不到。

## 4. 逐 mod 详细

### 4.1 Dubs Bad Hygiene（`Dubwise.DubsBadHygiene`）

**管网**：`DubsBadHygiene.HygienePipeMapComp`（`020000E1:T`）
- `int[,] PipeGrid = new int[Enum.GetValues(typeof(PipeType)).Length, NumGridCells]`（Rank==2）。
- `FloodNet(CompPipe root, int P, List<PlumbingNet> into, int firstNewID)`（`060005BD`，private）
  里 `map.floodFiller.FloodFill(root.parent.Position, passCheck, NoProcessor, int.MaxValue, false, null)`
  —— `extraRoots` 硬编码 `null`；passCheck 里写 `GridID`/`pipeNetRef`/`PipedThings`/`PipeGrid`/`cells`。
- `PlumbingNet.InitNet()`（`06000579`）遍历 `PipedThings` 按 comp 类型归类所有设备
  → **只要跨层格进了 `PipedThings`，两层设备自动并到同一 net，无需改 `InitNet`。**
- `RegisterPipe` 对同格重复会 `Log.Error("More than 1 pipe comp in the same cell at ...")`。

修法：§2 的竖向桥 + §2.4 强制全量 + §3 的 `PipeGrid` 重建。
端子 def：`RL_DBHSewageRiser`（ParentName `DubsDirtyPipeBase`，`<mode>Sewage</mode>`）、
`RL_DBHAirRiser`（`<mode>Air</mode>`）。

**地下水**：`MapComponent_Hygiene`（`020000xx`）
- `RegenWaterGrid()`（`06000375`）与 `RegenDeepWaterGrid()` 都是
  `foreach (var c in map.AllCells)`（两遍：地形 fertility/tag → `WaterGrid.AddRadial`；
  Perlin 噪声 → `SetAt`）。开头只挡真空/太空 Biome，**没有层判断**。
- `GridLayer`：`float[] Grid`、`AddRadial` 用 `GenGrid.InBounds`（含隔离带）。
- 放置：`PlaceWorker_WaterGrid.AllowsPlacing` / `PlaceWorker_DeepWaterGrid.AllowsPlacing`
  只看 `WaterGrid.ValueAt(loc) > 0`，上层有水就允许放井。

修法：Postfix `RegenWaterGrid`、`RegenDeepWaterGrid`、`RegenIceGrid`
（覆盖 `FinalizeInit`、`RerollWaterGrids`、以及我们自己的扩层重跑）：
逐个 storage 格，若 `!owner.TryDecode(c, out var l) || owner.Elevation(l.LayerId) > 0`
→ `GridLayer.Grid[index] = 0` 并 `Dirtify`。因为是清零而不是阻止生成，Perlin 的
地表/地下取值与现有一致，存档里已有的地面水不受影响。
副产品：位层井的 `AllowsPlacing` 自动被挡（`ValueAt == 0`）；建议再加一条
`PlacementReport`（照 `Compatibility/DeepResourceScope.cs` 的 `RL_DeepResourceGroundOnly`
范式，新增 key 如 `RL_GroundwaterUpperLayer`）给出明确拒绝理由，而不是只显示 "WellNeedsWater"。

**其它**：`HygienePipeMapComp.FinalizeInit` 里有跨 mod 反射
（找 `GetType().Name.Contains("MapComp_Skylights")`，再取私有字段 `SkylightGrid`）。
只要 §4.5 把被遮挡的天窗位清掉，这里自动正确。

### 4.2 Dubs Central Heating（`Dubwise.DubsCentralHeating`）

`DubsCentralHeating.HygienePipeMapComp` 是 DBH 那套的**独立克隆**（同名不同类型，
无继承关系），签名一致：`RegenPipeGrids()` `060000E2`、`RebuildPipeGrid(int)` `060000EC`、
`DirtyAllPipeGrids()` `060000E9`、`FloodNet(...)` `060000ED`（同样 `extraRoots = null`）。
差异仅两处，实现时要按类型分别处理：
- `PipeNets` 是 **private**（DBH 是 public）→ 读 net 列表只能用 `GetPipeNets()` `060000DC`。
- 多一个 public `Traverse<bool[]> skylights`（`04000089`）持有 Dubs Skylights 的网格引用。

介质：Defs 里 `<mode>` 统计为 9×`Heating`、3×`Air`。端子：`RL_DCHHeatingRiser`、`RL_DCHAirRiser`
（ParentName `DCHPipeBase`，位于 `1.7/Defs/ThingDefs_Buildings/BuildingsA_Pipes.xml`）。

天窗联动：`CompBoilerSolar.RoofedHeatOutputFactor` 遍历自身 `OccupiedRect` 数
`map.roofGrid.Roofed(cell)`，被 roof 且 `skylights[c] == true` 时视为无屋顶。
→ 一旦 §4.5 把被上层遮挡的天窗格清掉，这个系数自动正确，**不需要单独改 DCH**。

### 4.3 Dubs Rimatomics（`Dubwise.Rimatomics`）

- `Rimatomics.PipeType { HighVoltage, Cooling, Steam, ColdWater, Loom }`
  → 「电线」= `HighVoltage`，「数据线」= `Loom`，全部走 `UniversalPipeMapComp`。
- `Rimatomics.UniversalPipeMapComp`（`0200008C:T`）的 `RebuildPipeGrid(int P)`（`0600036B`）
  与 DBH 形态**不同**：没有 `FloodNet`，直接在 `RebuildPipeGrid` 内部
  `Dictionary<IntVec3,CompPipe> pipeDic`（`GenAdj.OccupiedRect` 每格）+
  `map.floodFiller.FloodFill(parent.Position, PassCheck, Processor, int.MaxValue, false, null)`，
  `PassCheck` 里写 `GridID`/`pipeNetRef`/`PipedThings`/`PipeGrid`。
  → §2.2 的包装 `passCheck` 方案对它同样成立（`original(对侧)` 命中 `pipeDic` 才记账）。
- `RebuildPipeGrid` 里同格重复会 `Log.Error($"More than 1 pipe comp in the same cell at ...")`。
- 另注意它自己也有 `HarmonyPatches.Harmony_TryConnectToAnyPowerNet`，与 RimLayers 的
  `LayerPowerConnectorQueryPatch` / `LayerPowerParentGuardPatch` 可能交叉，需要实机验证
  （两者都改连通性，但作用对象不同：一个是 PowerNet，一个是它自己的管网）。

端子：5 种介质各一个，ParentName `RimatomicsPipeBase`
（`1.7/Defs/ThingDefs_Buildings/Buildings_A_Pipes.xml`），comp 写
`<li Class="Rimatomics.CompProperties_Pipe"><mode>X</mode></li>`。
Rimatomics 的 `CompProperties_Pipe` **没有 `vertPipe`**（`02000008:T` 只有 `Type`/`mode`/`stuffed`）。

### 4.4 Rimefeller（`Dubwise.Rimefeller`）

**管网**：`Rimefeller.UniversalPipeMapComp`（`02000056:T`）与 DBH 同源克隆：
`RegenPipeGrids` `060001FA`、`RebuildPipeGrid` `06000202`、`FloodNet` `06000203`（`extraRoots=null`）、
`DirtyAllPipeGrids` `060001FF`。`Rimefeller.PipeType` 只有 `Oil`（`02000018:T` 单成员）
→ **只需 1 个端子** `RL_RimefellerOilRiser`（ParentName `RimefellerPipeBase`）。
注意 `Rimefeller.CompProperties_Pipe` 没有 `vertPipe`，且 Defs 里所有管道都不写 `<mode>`（走默认值）。

**石油**：`MapComponent_Rimefeller`（`02000047:T`）
- `OilGrid`/`DeepOilGrid` 各含 `float[] SeedGrid`（Rank==1，会被自动扩容）。
- `RegenSeedGrids()`（`060001AD`）：两个 `Perlin`（shallow 0.008/3 oct、deep 0.004/3 oct）
  `foreach (var c in map.AllCells)` 写 `SeedGrid[num]` → **覆盖所有层与隔离带**。
- `OilGrid.GenerateFields()`（`06000160`）：`for (i < SeedGrid.Length)` 收集
  `SeedGrid[i] > 0.01f` 的格，再用 `floodFiller.FloodFill` 做 4 邻连通域生成 `OilField`
  → 每个存储带各自长出独立油田。
- `RerollOilGrids()`（`060001AC`）= 重掷 `ShallowReservoirSeed`/`DeepReservoirSeed` +
  new 两个 OilGrid + `RegenSeedGrids()` + 两次 `GenerateFields()`。
- 放置：`PlaceWorker_WellHead`/`PlaceWorker_DeepWellHead` 用 `GetFieldAt(center)`，
  所以清掉上层 Fields 后上层井口自动放不下。

修法：
1. Postfix `RegenSeedGrids`：把 `SeedGrid[index]` 在
   `!owner.TryDecode(c, out var l) || owner.Elevation(l.LayerId) > 0` 处置 0。
2. Postfix `GenerateFields`：`Fields.RemoveAll(f => elevation(f.RootCell) > 0)`（以及
   剔除跨带残留格），确保同一次 `RerollOilGrids` 里也不会留下上层油田。
3. 扩层后**不要**调 `RerollOilGrids`（会重掷种子、破坏存档一致性），只调
   `RegenSeedGrids()` → 清零 → `OilGrid.GenerateFields()` / `DeepOilGrid.GenerateFields()`。

### 4.5 Dubs Skylights（`Dubwise.DubsSkylights`）

现状（全部已核实）：
- `MapComp_Skylights.RegenGrid()`（`06000039`）每次 `SkylightGrid = new bool[NumGridCells]`，
  然后对每个天窗把 `GenAdj.OccupiedRect(light).ExpandedBy(1)` 的格置 true。
  里面那个「`def.building.canPlaceOverWall` 才检查邻格屋顶/不可通行」的分支对
  `SkyLightA/B/C/D` **永不触发**（`canPlaceOverWall` 只出现在被整段注释掉的旧 `Window` def 里）。
- `Patch_GameGlowAt.Postfix(GlowGrid __instance, ref IntVec3 c, ref float __result)`（`0600003E`）：
  `if (__result < 1f && MapComp_Skylights.LightComps[map.uniqueID].SkylightGrid[idx]) __result = Mathf.Max(__result, map.skyManager.CurSkyGlow);`
  —— 无 `TryGetValue`（组件缺失会 `KeyNotFound`），且**不做任何层/遮挡判断**。
  它与 RimLayers 的 `Environment/SkyPatches.cs::LogicalSkyGlowPatch`（同方法 Prefix）
  叠加：RimLayers 先跑（可置 `ignoreSky` / `__result = 0`），Skylights 的 Postfix 后覆写。
- `Building_skyLight.TickRare()`（`06000002`）**整个方法体只有发热**：
  按 `AmbientTemperature`、`Map.mapTemperature.OutdoorTemp + 15`、纬度、
  `Map.skyManager.CurSkyGlow` 算 `num4 = 3f * n * num * CurSkyGlow * lat`，
  `if (num4 > 0.1f) GenTemperature.PushHeat(...)` —— 完全不看上方层。
- `BasedSkyLight` 的 `<terrainAffordanceNeeded></terrainAffordanceNeeded>` 是空的 →
  天窗无地形要求；`holdsRoof false`、`blockLight false`。
  RimLayers 的 `PlacementBoundaryPatch`（`GenConstruct.CanPlaceBlueprintAt` Prefix →
  `Structure.Validate`）仍会要求 `HasSurface(cell)`。

修法：
1. Postfix `MapComp_Skylights.RegenGrid()`：对每个被置 true 的 storage 格
   `if (!owner.Sky.IsExposed(cell)) SkylightGrid[index] = false;`
   —— `Environment/SkyExposure.cs::IsExposed` 已经实现了完整的
   「从最高层往下，任一层有屋顶或 `Structure.HasSurface` 即挡住」语义，
   正好等于用户要求的「上方没有其他层遮挡」。
2. Prefix `Building_skyLight.TickRare()`：`if (!owner.Sky.IsExposed(Position)) return false;`
   （该方法只做发热，整体跳过是安全的）。
3. 扩层后重跑 `RegenGrid()`（§3 钩子）。
4. 可选加固：Prefix `Patch_GameGlowAt.Postfix` 内层逻辑或自建一个前置判断，
   避免 `LightComps[uniqueID]` 的 `KeyNotFound`（非本 mod 引入的 bug，但兼容层最容易撞上）；
   至少兼容层不要依赖该字典项必然存在。

副产品：DCH 的 `CompBoilerSolar.RoofedHeatOutputFactor` 与 DBH 的 `skylights` 反射引用
都读这张网格，清位后自动一致。

## 5. 新增资产清单

| 文件 | 内容 |
|---|---|
| `Source/RimLayers/Compatibility/LayerPipePorts.cs` | `CompProperties_LayerPort`/`CompLayerPort`；`FloodFiller.FloodFill` 竖向桥（depth 门）；4 mod 重建入口的 `FullRebuild` 强化；端子放置/移除后的全量重建 |
| `Source/RimLayers/Compatibility/LayerExpansionHooks.cs` | 扩层收尾回调；`PipeGrid` 长度修复；`GridLayer.DirtyFlags` 重建 |
| `Source/RimLayers/Compatibility/DubsWater.cs` | `RegenWaterGrid`/`RegenDeepWaterGrid`/`RegenIceGrid` Postfix 清零 + 井位 `PlacementReport` |
| `Source/RimLayers/Compatibility/RimefellerOil.cs` | `RegenSeedGrids` Postfix 清零 + `GenerateFields` Postfix 剔除上层 `Fields` |
| `Source/RimLayers/Compatibility/DubsSkylights.cs` | `RegenGrid` Postfix（按 `Sky.IsExposed`）+ `TickRare` Prefix |
| `Defs/ThingDefs_Buildings/RimLayers_LayerPorts.xml` | 10 个端子 def（DBH 2 / DCH 2 / Rimefeller 1 / Rimatomics 5） |
| `Patches/LayerPorts.xml` | 用 `PatchOperationConditional` 以对方 defName 存在性为门槛（照 `Patches/VGE_PipeRisers.xml`） |
| `Source/RimLayers/RimLayersMod.cs` | 在 `Compatibility.AerocraftCompatibility.Install();` 之后、`int patchClasses=0;` 之前加 `Compatibility.DubsCompatibility.Install(harmony);`（见 §7） |
| `About/About.xml` | 往**现有唯一的** `<loadAfter>` 块内追加 5 个 packageId（不要另开块，见 §7） |
| `Languages/*/Keyed` | `RL_*` 端子名与拒绝理由 key |

## 6. 已知风险与验证矩阵

风险：
1. **全局 `FloodFiller` 补丁**：必须靠 depth 门严格限定；RimLayers 目前对 FloodFiller 无任何补丁
   （`grep -rn FloodFiller Source/` 只命中 `GridCapacityRepair` 注释与 `LiveMapExpansion` 的字段替换），
   冲突面小但需实测其它 mod（VEF、Combat Extended 等）的洪泛是否被影响。
2. **强制全量重建的性能**：管网变动从增量退化为全量，`NumGridCells × modes`；
   三层 250×250（stride 255）约 19 万格/层，5 个 mode 的全量约 100 万次字典查询，
   属一次性开销（玩家手动铺管时）。
3. **端子占格**：端子本身是管道，铺管时会被端子打断（玩家需在端子处接续），
   需要 UI 提示；这是「不能叠放」约束的唯一可行解。
4. **Rimatomics `Harmony_TryConnectToAnyPowerNet`** 与 RimLayers 电力补丁交叉，需实机验证。
5. **存档体积**：`MapSerializeUtility` 按全 storage 遍历；DBH 有 `WaterGrid`/`DeepWaterGrid`/
   `IceGrid`/`OilSpillGrid`(Rimefeller) 等多个 `ushort`/`float` 网格，扩层后按
   `Stride*LayerCount/Height` 线性增长（三层约 3×）。属现有代价，记录即可。

验证矩阵（实机，逐项）：

| # | 场景 | 期望 |
|---|---|---|
| 1 | 地表 + 上层各铺一段同介质管，中间放端子 | 两层同属一个 net（`InspectString`/设备共享） |
| 2 | 只在上层放端子、下层没管 | 不连通，无报错 |
| 3 | 端子被拆除 | 立即分成两个 net，无残留 `GridID` |
| 4 | 扩层后再铺管（旧层级边界上下） | 无 `IndexOutOfRange`，`PipeGrid` 长度等于新 `NumGridCells` |
| 5 | 新生成 3 层飞船地图 | 地上 2 层无地下水/石油，地表+地下层有；井口只能在地表/地下放 |
| 6 | 地下层天然山体 | 地下水/石油按地形标签正常生成 |
| 7 | 上层铺楼板压住地表天窗 | 天窗不再补光、不再发热；拆掉楼板后恢复 |
| 8 | 上层只是开放空气（无楼板无屋顶） | 地表天窗正常工作 |
| 9 | DCH 太阳能锅炉在楼板下 | `RoofedHeatOutputFactor` 输出为 0 |
| 10 | 存档保存/读取 | 端子连接关系与资源网格保持 |

### 6.1 自动化验收结果（`Tests/Run-V1.ps1 -Scenario dubs -Dubs -AllDlc`）

产物 `Tests/Artifacts/v1-dubs-20261026-085843`：**66 项断言 / 0 失败**，
`errors: []`，`IndexOutOfRangeException` = 0，`BoolGrid.get_Item` = 0，
`[RimLayers.Recovery]` 警告 = 0。日志仅剩 82 条与本兼容层无关的第三方 mod 元数据 WARN / 6 条 ERROR。

覆盖到的实机事实（均从结果 JSON 读取）：

- 层结构：`0,1,-1` → 再扩一层后 `0,1,-1,2`。
- 10 组端子（DBH sewage/air、DCH heating/air、Rimefeller oil、Rimatomics
  cooling/steam/coldWater/highVoltage/loom）全部 `net-crosses-the-band-boundary` 与
  `net-splits-without-the-riser`，且都是自己 mod 的真管道类（`CompProperties_Pipe,CompProperties_LayerPort`）。
- `dubs-pipe-grid-count-grew-with-the-map: 48960 -> 65280`，
  `dubs-pipe-grid-was-regrown-to-the-new-cell-count: rows=4, cols=65280`。
- 水资源：`upper=0, ground=10542`（`deepwater` `0 / 9323`，`ice` `0 / 0`）；井口在上层被拒、地表被准。
- 石油：`upper=0, ground=484`（`deep` `0 / 3628`），油田 `5` / `8` 个且 `RootCell` 均不在上层。
- 天窗：`open-column-is-lit: exposed=True`、`covered-column-is-not-lit: exposed=False`。

测试夹具本身的两个坑（已修，已记在本文档以免重蹈）：

1. `Tests/Run-V1.ps1` 固定 `-Layers 2..3`，所以场景必须把 `dubs` 加进
   `Generation/LayerGeneration.cs` 的 `V1Preallocated()` 排除表，否则 `EnsureElevation`
   全部early-return，早期四失败就是 `65280 -> 65280`。
2. Rimatomics 的 `HighVoltage`（x=56）/`controllerLoom`（x=60）组超出旧测试台
   `PrepareDubsDeck(30,30,24)` 的范围，导致 8 条 `reason=UnknownBuilding`；
   已改为 `(30,30,44)`。

## 7. 与并行 Aerocraft 兼容工作的协作约定

同期另一个会话（`01a17303`）在 `Mods/RimLayers` 上做 Aerocraft 兼容，已收工（Release 0 警告/0 错误，
验收 43/43，DLL 已装进 `Assemblies/RimLayers.dll`）。实际交接点如下，落地本方案时必须遵守。

### 7.1 它改了哪些文件（已完成，勿回退）

```
M  About/About.xml                       M  Source/RimLayers/RimLayersMod.cs
M  Assemblies/RimLayers.dll              M  Source/RimLayers/Diagnostics/V1Integration.cs
M  Docs/AEROCRAFT-COMPATIBILITY-PLAN.md   M  Tests/Run-V1.ps1
M  README.md
?? Docs/AEROCRAFT-COMPATIBILITY.md        ?? Source/RimLayers/Compatibility/AerocraftCompatibility.cs
?? Source/RimLayers/Compatibility/AerocraftLayers.cs
?? Source/RimLayers/Compatibility/AerocraftPatches.cs
?? Source/RimLayers/Diagnostics/V1Aerocraft.cs
```

### 7.2 `RimLayersMod.cs` 插入点（已确认）

当前形态（`Source/RimLayers/RimLayersMod.cs:24-28`）：

```csharp
        Compatibility.VVRaceCompatibility.Install();
        // Same ordering rule as VVRace: an optional adapter that answers Prepare() from its own
        // binding state must resolve before the class-processor loop reaches its patch classes.
        Compatibility.AerocraftCompatibility.Install();
        int patchClasses=0;
```

`DubsCompatibility.Install(harmony)` 插在 `AerocraftCompatibility.Install();` 之后、`int patchClasses=0;` 之前。
唯一硬约束是**必须在 class-processor 循环之前**（兼容层的 `Prepare()` 要读适配器自己的绑定状态）。
两者顺序本身不敏感；每个 `[HarmonyPatch]` 类都在独立 try/catch 里，绑定失败只跳过自己的类。

### 7.3 `About/About.xml` 的 `loadAfter`（已确认）

现有唯一一块在 `About/About.xml:16-23`（`MYDE.AerocraftFramework` / `vanillaexpanded.gravship{,2}` /
`Baal.VehicleFramework` / `SmashPhil.VehicleFramework` / `brrainz.harmony`）。
**往这一块内追加**，不要另开第二个 `<loadAfter>`（`ModMetaData` 里重复标签只有最后一个生效）。
追加内容：

```xml
    <li>Dubwise.DubsSkylights</li>
    <li>Dubwise.DubsCentralHeating</li>
    <li>Dubwise.DubsBadHygiene</li>
    <li>Dubwise.Rimefeller</li>
    <li>Dubwise.Rimatomics</li>
```

（按依赖顺序：Skylights 最底层，因为 DBH/DCH 会反射它的 `SkylightGrid`。）

### 7.4 `Generation/` 独占

`git status Source/RimLayers/Generation/` 为空 —— `LiveMapExpansion.cs` 与 `GridCapacityRepair.cs`
由本方案**独占**，没有并行冲突。

### 7.5 扩层钩子必须「只在提交成功后触发」

对方指出的两点直接改写了 §3 的设计：

1. `LiveMapExpansion.EnsureElevation` 有**两条提前返回**（`elevation == 0`、或该层
   `previous?.TryGetLayerAtElevation(...) == true`）返回零耗时结果；`EnsureLayerCount(map, 1)` 也直接返回。
   订阅者会把事件理解为「网格尺寸可能变了」，所以这些早退路径**不得触发**事件。
2. `EnsureElevation` 的 `catch` 分支会 `owner.ReplaceLayout(layout, previous)` + `changes.Restore()` +
   `map.info.Size = oldSize`。**如果订阅者在事件里扩了 `int[,]`，回滚时没有对应缩回钩子 → 事务失败后留下
   尺寸不匹配的网格。**

因此最终签名与触发点（不放 `Action<LayerLayout>` 到 `LayerMapComponent` 上做泛型广播）：

```csharp
// Generation/LiveMapExpansion.cs 内，唯一触发点。
// 位置：changes.Commit() 之后、ResumePaths(paths) 之前（此时所有持久状态与新布局都已可用，
// 且不再可能进入 catch 回滚分支）。
public static event Action<Map, LayerLayout>? Committed;
```

- **只在 `EnsureElevation` 成功路径的最后**触发一次，携带「已生效的新 layout」。
- **早退路径不触发**（尺寸没变）。
- **回滚分支不触发**（订阅者永远看不到需要撤销的状态）。
- 需要的额外信息（旧 `NumGridCells`）订阅者自己用 `map.cellIndices.NumGridCells` 之外的方式取：
  在事件里读 `map.cellIndices.NumGridCells`（已是新值）和自己缓存的旧值比较即可。

同理，`Generation/LayerGeneration.cs` 的 `FinalizeMapPatch`（新地图生成路径）在
`GridCapacityRepair.Fix` 之后触发同一个事件，这样「运行期扩层」与「生成时就是多层」两条路径共用一套订阅者。

可对照 `LayerMapComponent.FinalizeInit` 已有的对称写法：`Combat.RebuildFlights(); Power.Rebuild();
Air.Rebuild(); Sky.Rebuild(); Warnings.Rebuild();` —— 扩层钩子就是这组「重载后重建」的扩层版本。

### 7.6 构建锁

`Tests/Run-V1.ps1` 内含 `dotnet build`，会重编并覆盖 `Assemblies/RimLayers.dll`。
本方案跑任何构建/测试前先确认 `RimWorld.exe` 已退出（`taskkill //F //IM RimWorld.exe`，
该参数是 exe 锁），并确认另一方会话不在同一目录跑测试。

### 7.7 可直接复用的 Aerocraft 结构

- 复用 RimLayers 现成原语（`Structure.HasSurface`、`CombatService.OpenLaunchColumn`）而不是自造几何
  —— 本方案的 §4.1/§4.5 已经这么做（用 `SkyExposure.IsExposed` 与 `Structure.HasSurface`）。
- `Docs/AEROCRAFT-COMPATIBILITY.md` 与 `Source/RimLayers/Diagnostics/V1Aerocraft.cs` 的
  「验收场景挂在 `V1Integration` + `Run-V1.ps1 -Scenario X -Mod`」接线方式，本方案新增的 10 项
  验证矩阵可以直接照抄结构（新增 `Diagnostics/V1Dubs.cs` + 一个 scenario 名）。

### 7.8 范围提示

Aerocraft 是「单一 Building + 连续坐标」，Dubs 这套是真正的**管网/电网/热网拓扑问题**
（`FloodFiller` 图遍历 + 增量/全量重建语义 + 多介质枚举），体量高一个量级。落地建议按 §5 文件清单
分三批：

1. **批 1（可独立验收）**：`LayerPipePorts.cs` + 端子 XML + §2 的 FloodFiller 竖向桥。
   只依赖 §7.4 的独占目录，不依赖扩层钩子。
2. **批 2**：`LayerExpansionHooks.cs` + §7.5 的事件 + `PipeGrid` 长度修复。
3. **批 3（互不依赖，可并行）**：`DubsWater.cs` / `RimefellerOil.cs` / `DubsSkylights.cs`
   —— 三者互不相干，各自一个 Postfix 就能独立验证。
