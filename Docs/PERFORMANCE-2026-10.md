# 层数/规模性能优化（2026-10-24）

RimWorld **1.7 / Waylith / .NET 10**；RimLayers 0.1.5。
目标：层数增多、Pawn 增多时，mod 自身的增量逻辑不应把「一个格子变了」放大成「全图重建」。

## 结论

同规模（250×250、3 层、60 人、全 DLC、加载后 12000 tick）对照。

| 指标 | 优化前 `172821`（load） | 优化后 `061352`（最终代码，load） | 变化 |
|---|---:|---:|---|
| tick 均值 | 1.698 ms | **0.921 ms** | −46% |
| tick p95 | 4.483 ms | **1.754 ms** | −61% |
| tick p99 | 26.523 ms | **2.748 ms** | **−90%** |
| tick max | 85.638 ms | 154.8 ms | 不作为成果，见下 |
| path 均值 / p95 | 2.247 / 2.353 ms | 2.226 / 2.472 ms | +持平 |
| load | 3083.6 ms | 3330.3 ms | 持平量级 |

同一 run 的 generate 阶段同口径对照（供参考，不作为主结论）：均值 0.906 ms、
p95 1.728 ms、**p99 3.520 ms**。generate 的 p99 高于 load，故 p99 的稳健描述是
**2.75–3.52 ms（约 −87% … −90%）**，而不是单一数字。

不是同 seed 控制实验，两组都是原生 Lord 队伍真实行走，用作代表性开销，不当作 FPS 承诺。
`maxMs` 是阶段切换（`LayerView.Switch` + 相机跳转 + 一次存档）的瞬时成本：
154.8（load） / 141.2（generate）。它取决于阶段切换落在哪一 tick，不是稳定态指标；
基线记录的 85.6 ms 反而更低，故本项不作为优化成果。

`path` 均值基本持平（2.247 → 2.226 ms），p95 略升（2.353 → 2.472 ms）。
原因是路由现在会先跑一条同层直连腿（用于可证最优性判定），
命中 `routeDirectShortCircuits` 时总寻路次数大幅下降，但**单次查询**多了一条必跑的腿。
实测 `legPathfindsPerRequest` 从改造前的量级（每个请求对每个连接器端点各跑一次，
典型 40+）降到 **1.75（`043929`）/ 1.86（`061352`）**。

## 本轮计数器（`Tests/Artifacts/v1-scale-20261024-061352/load-scale-metrics.json`）

| 计数器 | 值 | 说明 |
|---|---:|---|
| `forCacheHitRatio` | 0.99999976 | 24,600,370 次 `For()` 仅 6 次真正扫描 `Map.components` |
| `routeRequests` | 3144 | 跨层/同层路径请求 |
| `routeLegPathfinds` | 5858 | 实际 `FindPathNow`，**1.86/请求** |
| `routeDirectShortCircuits` | 1837 | 58% 请求被「直连腿可证最优」短路 |
| `heatExchanges` / `heatRebuilds` | 100 / 5 | 95% 的交换复用增量边表 |
| `heatColumnEvaluations` | 312,500 | 仅脏列，而非每次 191,250 格全扫 |
| `environmentTopologyBuilds` | 5 | 固定在 `Tick()` 单点，不在 stat getter 里惰性触发 |

## 逐项改动

1. **结构失效按列限定**（`Structure/StructuralSupport.cs`、`Mountains/VerticalMining.cs`）
   原来任何地形/屋顶写入都无条件 `Connectors.Invalidate()`，而 `Version` 同时是热、气、环境拓扑的
   缓存键，于是一次无关写入引爆全图重建。现在只有**真正含连接器的列**（`ConnectorGraph.TouchesColumn`）
   或真的有上层格子受影响时才失效。连接器可用性只依赖它自己那一列：`Position` 与 `UpperCell`
   共享 `(x,z)`，竖向电梯也在同一列（`ExtensionExit` = `Adjacent(Position, direction)`）。

2. **`LayerMapComponent.For()` 备忘**（`Core/LayerMapComponent.cs`）
   原生 `Map.GetComponent<T>` 是线性扫 `components`，而 `For()` 在逐格路径
   （`GenSight.LineOfSight`、`GasCanMoveTo`、`SkyQueries.Roofed`）和逐帧 UI 上被调用。
   加 `[ThreadStatic]` 单条备忘。**关键**：`Configure` / `ReplaceLayout` / `RestoreLayout`
   都必须清备忘——加载时 `For()` 可能已经缓存了「此图未分层」的否定结果。

3. **`LayerLayout` 预计算表**（`Layout/LayerLayout.cs`）
   `OrderedLayers` / `Above` / `Below` 原本每次访问都 `Enumerable.Range().OrderBy()` 分配+排序，
   而 `Above` 在 `HasSurface`、`OpenLaunchColumn`、`SkyExposure.IsExposed` 等逐格路径里。
   `LayerLayout` 不可变，故构造时算好 `ordered[]` / `above[][]` / `below[][]`，
   并把 `Array.AsReadOnly` 视图也缓存（它本身每次调用都分配）。

4. **`ConnectorGraph` 缓存**（`Navigation/ConnectorGraph.cs`）
   `MovementEdges` 每次重建+排序+电梯 O(S²) 嵌套扫描；`HasTransferEdge` 每次 O(S)+闭包分配，
   却被 `VolumeLinks.Open` 逐格调用。现在按 `Version` 缓存 `orderedStairs[]`、`shaftLifts[]`、
   `shaftLiftAt` 位置索引和 `transferFlags` 位掩码表。结构前置条件仍然动态判定。

5. **`LayerRouter` 可证剪枝 + O(1) 节点索引**（`Navigation/LayerRouter.cs`、`Navigation/RouteBounds.cs`）
   原实现对起点做急切展开，对**每一个**同层端点各跑一次同步 `FindPathNow`。现在：
   - 节点用字典索引，取代 `nodes.Skip(2).Contains` / `FindIndex` 的 O(E·N) 扫描；
   - 同层时先跑一条直连腿。离开本层再回到目标层**至少跨 2 个连接器**，
     所以 `directCost <= 2 × cheapestEdgeTicks` 时直连腿**必为最优**，可完全跳过连接器定价；
   - 否则用该直连腿代价作为 Dijkstra 上界，`EndpointSearch.Find(..., upperBound)` 提前收敛。

   **注意距离剪枝不可采纳**：原版 `PathGrid.CalculatedCostAt` 里地形可出现 `pathCost=0`
   （原版数据中 13 处），所以 `TotalCost >= 直线距离` 不成立。只有上述「跨越次数」下界可证明。

6. **热交换列级增量**（`Environment/HeatLinks.cs`、`Environment/IncrementalEdgeSum.cs`）
   竖向导热是**列局部**的：任意两边都来自同一个 `(x,z)` 列的两层配对，所以整张边表
   = 各列贡献之和。单列变更只需 O(1) 更新，而不是每 120 tick 全图重扫。
   房间合并/拆分走 `MarkFullRebuild`（`Room.Notify_RoomShapeChanged` 已接）。

   `OutdoorCoverage` 按房间缓存**已回退**（原计划键为 `Sky.Revision`）。它的结果依赖房间
   单元格集合、每格的楼面与屋顶、`Environment.Enabled` 以及 `Room.CellCount`，无法对
   「房间合并/拆分」和「开关切换」证明缓存键完备，故保持直扫；真正的收益在增量边表。

7. **`SkyExposure` 房间级失效 —— 已回退**（`Environment/SkyExposure.cs`）

   计划是「只失效包含该列的房间」。实现后发现**按房间即时失效并不安全**：
   `InvalidateRoomsOf` 需要 `GetRoom()`，而它在 `RoofGrid.SetRoof` / `TerrainGrid.SetTerrain` 的
   Postfix 里运行，会强制 `RegionAndRoomUpdater` 在结构变更中途重建区域，
   产生 `RegionLink.Register` 错误 —— 地图生成期和正常结构编辑期都会刷屏。
   已回到原有的全局 `revision` 失效（语义与改动前一致），只保留两项安全的部分：
   暴露 `Revision`（供 `HeatLinks` 一致性判断）和用 `epoch` 比较房间缓存（避免重复重算）。
   经验：**不要在栅格 Postfix 里做寻路/房间查询**。

8. **飞船服务反向索引**（`Ships/ShipStructureService.cs`）
   `OwnerAt` 原本对每个飞船做 LINQ 扫描，而它在 `RoofSurfaces.Build` 里**逐格**被调用。
   改为维护 `cellOwners` 反向字典（歧义船不登记，与旧语义一致）。
   `Refresh` 的 `map.AllCells` 全扫改为按 `LayerRect` 逐层且跳过地下层。

9. **环境拓扑移出 stat getter**（`Environment/LayerEnvironment.cs`）
   `Concentration()` 从 `NoxiousHaze`、植物生长率、极光想法等**逐植物/逐 Pawn/逐 stat** 路径进入，
   原本会就地触发全图房间拓扑重建。改为 `Tick()` 开头单点重建；`Concentration()` 只在
   从未构建时兜底一次。

10. **搬运/工作候选**（`Work/LayerWorkQueries.cs`、`Work/StorageQueries.cs`）
    `LayerCandidates.Closest` 对同一容器的每个持有物重复寻路 → 按父位置缓存腿代价。
    `StorageCellQueryPatch` 的 `TraverseParms.For(carrier)` 从循环内提到循环外。

## 游戏内运行才发现的两个缺陷

单元测试无法覆盖（需要 Verse/Unity 类型），只有真实运行才暴露：

1. **`ConditionalWeakTable.Add` 重复键**：`FindPathNow` 返回的是**池化** `PawnPath`，
   同一对象可能再次从池里出来。改用 `AddOrUpdate`，并且**不再把池化对象当归档结果返回**。
2. **`For()` 否定结果泄漏**：加载期 `For()` 缓存了「未分层」，`RestoreLayout` 设好 `Layout` 后
   没清备忘，导致 load 阶段 2 项失败。已在三处布局变更点统一清除。

## 自动化测试

```powershell
./Mods/RimLayers/Tests/Run-UnitTests.ps1        # 13 个游戏外套件
```

新增/扩展：

- `Tests/HeatLinksTests`（新）：`IncrementalEdgeSum` 与「每次全量重算」的参照实现
  **逐项等价**，含 400 轮随机变更序列 + 脏列计数断言。153,528 项。
- `Tests/NavigationTests`（扩展）：`EndpointSearch` 带上界后的最优性（不劣化、边界、并列、不可达），
  以及 `RouteBounds.DirectIsOptimal` 的可采纳性边界（含 0 / 负数 / NaN / ∞）。58 项。
- `Tests/LayoutTests`（扩展）：`Above`/`Below` 对所有分配顺序与旧 LINQ 投影**完全一致**，
  且不重复分配。862,996 项。

游戏内：

```powershell
./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario navigation -AllDlc
./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario scale -AllDlc -MapSize 250 -PawnCount 60 -Layers 3 -TimeoutSeconds 900
```

- `navigation` 20261024-042511（优化后）：generate 12/0，load 4/0；
  基线对照 20261024-043547（stash 回退本轮全部改动）：generate 12/0，load 4/0。
  两者一致，说明跨层寻路与读档续走行为未因优化改变。
- `scale` 20261024-061352：generate 13/0，load 12/0；12000 tick、四次全程往返、
  Lord/队伍/全楼层身份通过。计数器见上表。
- `Tests/Run-PerfRegression.ps1`：air / sky / reachability / structure / work-haul /
  combat-shot / **minimap** / ship-structure 串行回归，8/8 全过。
- `ship-structure` 20261024-055600：generate 55/0（`InvalidateRoomsOf` 移除后；
  修复前 `053016`/`054707` 分别有 174/107 次调用并失败）。

## 未验证

- 玩家完整模组组合（本机 227 个）未测；本轮为 Core + 全 5 官方 DLC + Harmony + RimLayers。
- 单次 `maxMs=193` 的构成未逐段拆分，只确认它出现在阶段切换。
- 未做同 seed 对照，故表中「变化」是代表性开销对比，不是受控实验结论。
- `minimap` 场景已补跑（`20261024-063340`，16/0）。Dubs Mint Minimap 实际**已安装**
  （`dubwise.dubsmintminimap`，Workshop 1662119905）；之前跑不起来是测试脚本漏了 `-Minimap`
  开关，属于工具链缺陷而非环境缺失，已修正并纳入 `Run-PerfRegression.ps1`。

## heat 的 `top-ceiling-retains-native-outdoor-loss`：已修复（改夹具预期）

该断言从 **2026-10-22** 起一直失败。现已修复并全部通过（`v1-heat-20261024-081755`，generate 20/0、load 6/0）。

### 它曾经是过的

`Docs/V1-VALIDATION.md` / `WORK-LOG.md` 记录：`v1-heat-20261018-132512`（Core）**20 / 6 零失败**。

### 根因：夹具预期锁定了旧行为，不是生产代码错

`git log -L` 定位到 `3e1cc1b`（feat(vehicle): Vehicle Compatibility, 2026-10-22）修改了
`HeatLinks.OutdoorCoverage`：

```csharp
// init (fefaa4f) —— 132512 通过的那个版本
if(!owner.TryDecode(at,out var c) || owner.Elevation(c.LayerId)!=owner.Layout!.HighestElevation) continue;

// 3e1cc1b 之后
if(!owner.TryDecode(at,out var c) ||
   !(owner.Environment.Enabled?owner.Sky.IsExposedAbove(at)
                             :owner.Elevation(c.LayerId)==owner.Layout!.HighestElevation)) continue;
```

同时 `HeatCeiling.Calculate` 在 `Environment.Enabled` 时走 `LocalOutside`：

```csharp
float outside=owner.Environment.Enabled?owner.Environment.LocalOutside(room):room.Map.mapTemperature.OutdoorTemp;
```

而 `LocalOutside` = `OutdoorTemp - Elevation*AltitudeLapse`。**`AltitudeLapse` 是用户可配的正式功能**
（`EnvironmentSettings.Parameters`，`"°C/F"`，Advanced），所以海拔递减是**预期行为**。
夹具仍按原始 `OutdoorTemp` 预期，等于钉死了 10-22 之前的语义。

### 修法：只改预期，生产代码一行未动

```csharp
float topOutside = owner.Environment.Enabled
    ? owner.Environment.LocalOutside(rooms[2])
    : map.mapTemperature.OutdoorTemp;
float topExpected = (topOutside - rooms[2].Temperature) * 0.006f;
```

`HeatLinks.OutdoorCoverage` 的语义保持 `3e1cc1b` 之后的样子。

### 验证闭环

`v1-heat-20261024-081755`：

```text
actual=-0.109699, expected=-0.109699
outdoor=1.756832, outside=1.716832     ← 差 0.04 = 2 × AltitudeLapse(0.02)
coverage=1, enabled=True
```

`actual` 与 `expected` 完全相等；`outside` 正好是 `outdoor - 2×0.02`。
**generate 20/0、load 6/0 全部通过**，与 10-18 的基线持平。

### 为何确认不是本轮引入

- `git stash` 回退本轮全部改动后仍失败（`v1-heat-20261024-051248`，`detail` 为空，
  证明跑的是未加诊断的原始 `V1Heat.cs`）；
- 四次 heat run（`044750`/`045838`/`050623`/`051248`）全部 1 项失败；
- 不带 `-AllDlc`（Core）重跑（`072542`）同样 1 项失败，排除 DLC 差异；
- `HeatPatches`/`HeatCeiling` 本体逻辑自 `fefaa4f` 起未变。

### 注意事项

本条修法与另一会话（地下生态）提前对齐过：heat 语义归属同一分支，避免两边都改。

### 顺带发现的工具缺陷（未修）

各场景 `*-results.json` 里的 `assemblyMvid` 取自 `typeof(Pawn).Assembly`，即**游戏本体
`Assembly-CSharp.dll`**，不是 RimLayers，因此**无法区分基线 run 与改动 run**。
若需严格的「同一 DLL」证据，应把 RimLayers 自己的 MVID 写进结果。

## 二层视角的渲染成本（2026-10-26 实测，配对 A/B）

之前所有 scale 运行都在初始化时 `LayerView.Switch(owner, scaleLayers-1)`，即全程停在顶层，
因此从来没有「同一世界、不同视角」的对照。本节补上了这块。

### 方法

`V1Scale.ScaleStep()` 每 `ScaleViewDwell=600` tick 切换视角
（`LayerView.Switch(owner, owner.ViewedLayer==scaleLayers-1?0:scaleLayers-1)`），
并在 `ScaleDrawEnded` 里按 `owner.ViewedLayer` 二次分桶（key = `Method@viewN`），
跳过切换后前 60 tick 以排除 section 重建的一次性开销。

命令：`Tests/Run-V1.ps1 -Scenario scale -AllDlc -MapSize 250 -PawnCount 60 -Layers 2 -TimeoutSeconds 900`
（2 层即可产生「有下层 / 无下层」两种状态；3 层只会让两者都非空，失去对照价值。）

### 结果（`v1-scale-20261026-142716`，2 层，things=36,328，pawns=131）

| 方法 | view0（一层） | view1（二层） | 差 |
|---|---|---|---|
| `LowerFloorBackdrop` | 0.004 | **0.405** | **+0.401（×101）** |
| `MapDrawer` | 0.312 | 0.246 | −0.066 |
| `MapInterface` | 0.588 | 0.604 | +0.016 |
| `DynamicDrawManager` mean | 0.647 | 0.648 | **0.000** |
| `DynamicDrawManager` p50 | 0.535 | 0.110 | −0.425 |
| `DynamicDrawManager` p99 | 2.616 | **10.069** | **+7.45** |
| `RoofSurfaces` (view1) | — | 0.012 | — |
| `DriftingMist` (view1) | — | 0.013 | — |

`LowerFloorBackdrop.Draw` 的门是 `Below(viewed)` 非空，所以一层是零成本 early-return、
二层才第一次真正执行。`+0.401 ms` 是唯一随视角层变化的 mod 侧成本，
占 60fps 的 16.7 ms 预算约 2.4%。

`DynamicDrawManager` 的均值**完全不动**而 p99 从 2.6 跳到 10.1：长尾来自渲染顺序变化
（每帧多了一批背景层 `Graphics.DrawMesh` 提交），不是工作量增加。优化应针对
`LowerFloorBackdrop` 自身，而不是去动原生管理器。

### 0.325 ms 的三段拆分（`v1-scale-20261026-143312`）

用 `Stopwatch.GetTimestamp()` 在 `Draw()` 内三段打点（计数器被 view0 零值帧对半稀释，数值×2 还原）：

| 阶段 | 占比 | 内容 |
|---|---|---|
| section 网格提交 | 36%（0.116 ms） | ~140 个 section，5 种图层，`Graphics.DrawMesh` |
| 动态 thing 筛选 | **54%（0.176 ms）** | 逐 thing 过谓词链 + `DrawNowAt` |
| roof + mist 尾部 | 7%（0.024 ms） | `RoofSurfaces.Draw` + `DriftingMist.Draw` |

逐帧漏斗（×2 还原）：`scanned≈364` → `floorMatch≈350` → `viewReject≈296` → `dynamic≈8`。
即 350 个候选里 296 个**仅因不在视野**被丢弃，只有约 54 个进入 `Exposed()`。

### 已提交的两处修改（`e0d86e3`）

1. 视野包含测试提到 `Exposed()`/`Fogged()` 之前 → 删掉 ~85% 的 `Exposed()` 调用。
   依据：两者都是纯读谓词，提前 `continue` 不改变任何存活的 drawable。
2. `Exposed()` 去掉每次调用两次 `new[]{...}.Concat(...).Where(...)`，
   直接用预计算的 `Above()` 数组；`target.LayerId` 的屋顶检查提到循环外
   （此时 `Elevation(target) < viewed` 已成立，谓词恒真）。两个谓词无副作用且任一命中即 `false`，
   所以逐层交错检查等价于原两遍扫描。

### 被实测推翻的先前判断（勿重复）

| 先前说法 | 实测 |
|---|---|
| 存档每帧扫描 13,381 个 thing | `DrawThings` 实为 ~168 个；13,045 是 `listerThings`，多数 `drawerType=MapMeshOnly` |
| 掉帧主因是下层渲染 | 仅 0.35-0.40 ms/帧 |
| `RoofSurfaces` 字符串比较 + 全 key 扫描是热点 | 0.010-0.012 ms/帧 |
| `DriftingMist` 字典无淘汰是隐患 | 0.012-0.013 ms/帧 |
| `Exposed()` 零分配能显著改善长尾 | 动态阶段每帧只覆盖 2-8 个 thing；长尾在原生 `DynamicDrawManager` |
| 测试夹具测不到热点 | 夹具是真实的（~168 DrawThings 与玩家存档同量级） |

`RoofSurfaces`/`DriftingMist` 比其余两项便宜 3 个数量级，**不是优化对象**。

### 仍未量化

- 层数 ≥3 时 `Below` 长度 >1，`LowerFloorBackdrop` 的 cost 是否严格线性（预期是，
  但未实测；3 层下 0.602 vs 2 层 0.434/0.372 的对比混入了 thing 数差异，不可直接比）。
- 原生 `DynamicDrawManager` 的 p99 尖峰内部构成（`ComputeCulledThings` 的 job 调度
  vs 后续提交），需 Unity Profiler 级别的采样，本仓库的 Stopwatch 基建测不到。
