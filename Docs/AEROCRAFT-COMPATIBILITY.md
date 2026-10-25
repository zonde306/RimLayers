# Aerocraft Framework 兼容

状态：**已实现，并在真实游戏中通过自动化验收 43/43（generate 37 + load 6），Release 构建 0 警告 / 0 错误。**

验收命令（游戏根目录）：

```powershell
& './Mods/RimLayers/Tests/Run-V1.ps1' -Scenario aerocraft -Aerocraft -TimeoutSeconds 420
```

`-Aerocraft` 只在这个独立测试 profile 里启用 `MYDE.AerocraftFramework`，不读取也不修改玩家配置。
最近一次通过：`Tests/Artifacts/v1-aerocraft-20261026-014750`（generate 37 项、load 6 项，0 失败、0 运行期错误）。

基准：RimWorld 1.7 本机程序集。
设计依据与逐条根因见 [调研稿](AEROCRAFT-COMPATIBILITY-PLAN.md)，其中 §12 记录实现时推翻的方案。

---

## 为什么需要适配

Aerocraft Framework（packageId `MYDE.AerocraftFramework`，工坊 `3611555958`）用
`Building` + 连续 `Vector2 RealCurrentPosition` 飞，不是 Pawn：没有 `Vehicles` 程序集、没有 pather、
**没有任何高度字段**（`DrawPos.y` 恒为 `def.Altitude`），起降只是翻 `Move_WarmUpTick`。

在分层地图上，它的 `Change_Position` 每 10 tick 写
`new IntVec3((int)RealCurrentPosition.x, Position.y, (int)RealCurrentPosition.y)`，
于是飞行器被钉死在存储 band 0；而 `Map.Size.z` 是整个存储高度（含 band 之间的隔离行），
所以它自带的落点扫描与边界判定都跨不了层。

## 适配契约

与 `Vehicles/SteelFlightAdapter.cs` 对 Steel Horizon 的做法同构：**连续轨迹整体驻留在一个存储 band 内，
换层时整条轨迹平移** `(新层 - 旧层) * Stride`。

`RealCurrentPosition.y` 与 `Shadow_Pos.z` 一律保存**存储 z**。Aerocraft 自身的几何全由
`TargetVPos - DrawPos` 与 `GetVector3_By_AngleFlat(DrawPos, ...)` 派生，因此同量平移对它的
仿真与渲染完全透明（底图、炮塔、装饰、阴影的 x/z 都取自 `DrawPos`/`Shadow_Pos`）。

`TargetVPos` 与 `TakeOffVPos_A/G` **不平移**：前者是目标层坐标（换层判据的来源），
后者是 home 层的返航点。返航靠“`Set_GoBack` 把目标设回旧 band → 下一次目标 Postfix 自动 rebase”完成。

## 实现的挂点

全部在 `Source/RimLayers/Compatibility/`：

| 文件 | 内容 |
|---|---|
| `AerocraftCompatibility.cs` | 惰性反射绑定（`AccessTools.TypeByName` + `VehicleReflection` 表达式编译），字段/属性访问器，`Rebase`，`BandOf` |
| `AerocraftLayers.cs` | `AerocraftLayers : MapComponent`，按 `Scribe_References` 存每架机的 `ProxyLayer` / `HomeLayer` |
| `AerocraftPatches.cs` | 全部 Harmony 挂点、`AerocraftLanding` 几何服务、`AerocraftBootstrap`、`AircraftTrace` 日志 |

| 挂点 | 目标 | 作用 |
|---|---|---|
| `AerocraftPositionBoundsPatch` | `AsBaseThing.Change_Position` | 把轨道夹在本 band 内（band 0 保持原生）；落地状态不跳过原生方法体，保住电网重连 |
| `AerocraftTakeOffPatch` | `AsBaseThing.Change_Up` | 滑翔起飞按目标 band 平移整条轨迹；垂直起飞不换层 |
| `AerocraftTargetPatch` | `Set_TargetVPos` / `_AndDestroy` / `_Droping` / `Set_Target_FollowTargetThing` | 空中改目标时换层（比较前后 `TargetVPos`，被原生拒绝的命令不动轨迹） |
| `AerocraftTouchdownPatch` | `AsBaseThing.Change_Down` | 落地即把当前 band 记为 home |
| `AerocraftForcedDescentPatch` | `Set_TargetVPos_Down_GlidingTakeOff` | 5 处强制降落（缺油/缺电/EMP/撞毁/自毁）的唯一 choke point：不合法落点换成同层最近合法格 |
| `AerocraftPlacementPatch` | `PlaceWorker_NotUnderRoof.AllowsPlacing` | 放置改为“本层有 `HasSurface` 且垂直柱通畅” |
| `AerocraftRefuelPatch` | `WorkGiver_Refuel_Turret.CanRefuelThing` | `Priority.First` 抢在 Aerocraft 自己的 `Area.Home[Position]` 判定之前，改按 (x,z) 列判定 |
| `AerocraftArrivalPatch` | `WorldObject_CrossMapThing_Flying.Arrived` | 跨图抵达后落到“该列最上面有地板的层”，而不是随机 band |

落点/放置合法性复用 RimLayers 现成语义，不另造一套：`StructuralSupport.HasSurface` +
`CombatService.OpenLaunchColumn`；选层复用 `SkyfallService.Trace` 的“自上而下第一个有地表层”。

## 降级策略

绑定失败（Aerocraft 改了字段布局、或未启用）时 `AerocraftCompatibility.Bound == false`，
每个挂点的 `Prepare()` 返回 false，Harmony 不安装它们，飞行器行为与没装 RimLayers 时完全一致。
Aerocraft 是可选第三方包，**不允许**它的适配失败连累 RimLayers 的其它补丁。

`About/About.xml` 的 `loadAfter` 已加入 `MYDE.AerocraftFramework`；`modDependencies` 未加（可选兼容）。

## 已实测（自动化验收，43/43）

测试机型 `MYDE_AF_RotorCraft_TwinPropeller_Base`（“Patrol” RT-63，1×1，旋翼垂直起降，300 电池），
在真实分层地图（250×250、3 层、stride 255）上通过 Aerocraft 自己的 tick 与 gizmo 入口驱动：

| 组 | 断言 |
|---|---|
| 绑定 | 适配器已绑定；包已启用（大小写不敏感）；Def 可加载 |
| 上层停机坪 | 有 `HasSurface`；`GenConstruct.CanPlaceBlueprintAt` 接受；实际 spawn 落在 `(66,0,202)` 即 band 1 |
| 分带推导 | `ProxyLayer` 由轨道几何推导为 1，`HomeLayer` 一致 |
| **上层加油** | 原生 `Area.Home[上层格]` 为 false，但 `CanRefuelThing` 为 true（按列判定）；无任何层在 Home 内时为 false；同层在 Home 内时为 true |
| 动力 | 机体有 300 电池（RT-63 不用 `CompRefuelable`，用 `CompProperties_Battery`，出生为 0，必须先充电） |
| 起飞 | 垂直起飞保持 band 1；`If_UpOrDown` 访问器与反射读值一致（交叉验证绑定本身） |
| 空中 | `Position` 留在 band 内且与轨道一致（`pos.z=202` / `track.z=202.5`） |
| **跨层改目标** | 空中右键地表 `(70,70)`：整条轨道 rebase 到 band 0；`Position` 随后由原生 `Change_Position` 提交为 `(67,0,66)`，与轨道 `66.7` 一致 |
| 降落 | 落地把当前 band 记为 home；`Is_Static` 为真 |
| 落点判定 | 该列最上面有地表的是 band 1；`RL_OpenAir` 空腔被拒；`RL_Isolation` 保留行既不解码也不被接受 |
| **强制降落** | 探针放在 band 1 的 `(30,166)`（飞机在 band 0）：原生 `Set_TargetVPos_Down_GlidingTakeOff` 记录的 `TargetVPos` 被改到 band 0 的 `(30,30)`，证明跨带落点被拦下并改到本层 |
| **固定翼滑翔起飞** | `MYDE_AF_FWCraft_Bombardment`（3×3、`Gliding_Range=20`）落在 band 0；def 确为滑翔型；26×26 无顶跑道每格通过 `CanLand`；滑翔起飞被接受且**保持本层**（覆盖 `AerocraftTakeOffPatch` 的 `If_TuringByGlidingTakeOffOrDownNow` 分支） |
| 存档 | band 记录随存档保存 |
| 重载 | 适配器重新绑定；机体存活；记录的 band 与几何推导一致（0 == 0）；位置可解码；home 有效 |

`Position` 的提交由机体自己的 `Change_Position` 在 10 tick 节奏上完成，且 `Is_Static` 时直接返回，
所以验收脚本按**真实状态**推进（等 `Is_Flying`、等存储提交），不按固定 tick 号。

## 已知限制

- 尚未覆盖：跨图抵达（`Arrived`，需要真实世界地图旅行）、扩层窗口内的起降。
  这两条代码路径已实现并通过静态核对，但没有独立场景。
- 强制降落已覆盖，但通过**直接调用** `Set_TargetVPos_Down_GlidingTakeOff` 驱动，而不是真的把电池抽干
  等原生触发器跑一遍。断言读的是机体自己的 `TargetVPos`；**不能**通过反射读回被 Prefix 改写的参数——
  `MethodInfo.Invoke` 把实参复制到自己的缓冲区，Harmony 的改写对调用方不可见（真实游戏里是直接调用，
  改写生效）。
- 验收只覆盖一种旋翼机。固定翼（`If_NeedGlidingWhenTakeOff=true`、跑道 20/30 格）需要单独的
  机场夹具。
- 选点不合法时**不会**在光标上拒绝（原生的 validator 是编译闭包，无法定点 Patch；为它去 Postfix
  全局 `GenGrid.InBounds` 风险大于收益）。表现是“点了照样接受，但落点被自动改到同层最近合法格”。
- 换层一次一个 band，表现为垂直升降后换层，不是连续 3D 飞行；不跨 band 直飞（跨 band 会经过
  `RL_Isolation` 保留行，`DynamicDrawManager` 与 `fogGrid` 都会看错）。
- `Area` / `Zone` / `Plan` 没有分层语义。上层停机坪的 Home 判定是“该 (x,z) 列任一层被圈进 Home”的近似。
- 原子加油（`atomicFueling=true`，Aerocraft 默认不开）跨层仍不支持 —— RimLayers 侧
  `RefuelWorkGiverUtility.FindEnoughReservableThings` 尚未分层。
- `Change_Position` 每次写 `Position` 的代价在分层地图上约为单层的 3 倍（`Thing.Position` setter
  要重挂 regions/thingGrid/coverGrid 并 dirty mesh）。未改语义，仅记录。

## 复核清单（拿到启用 Aerocraft 的存档后）

1. 地表起飞 → 上层 `RL_StructuralFloor` 降落：`Position` 落在目标 band，`AerocraftLayers.ProxyLayer` 一致，无抖动。
2. 上层停放 → 殖民者自动加油：生成 `RearmTurret` Job（`CanRefuelThing` 为 true）。
3. 固定翼低油滑翔降落：落点在同层有地板处，不穿上层楼板。
4. 存读档往返：`ProxyLayer` 与 `RealCurrentPosition` 一致；“返回基地”能回到起飞层。
5. 跨图抵达：落到有地板的层，不是随机 band。
6. 无 Aerocraft 环境：RimLayers 正常加载，日志无 `[RimLayers aerocraft]` 绑定行，无 Harmony 报错。

日志入口：`AircraftTrace`（最多 40 条，前缀 `[RimLayers aerocraft]`）、
绑定结果 `[RimLayers] Aerocraft Framework layered adapter bound.`（或 `Log.Warning` 的降级说明）。
