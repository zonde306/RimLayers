# Aerocraft Framework 兼容

状态：**v2 已实现（“空中飞行器跟随视角所在层”模型）。** 编译 0 警告 / 0 错误；游戏内自动化验收见下方
`已实测`，每次运行的产物在 `Tests/Artifacts/v1-aerocraft-*`。

验收命令（游戏根目录，需先能编译整个 `RimLayers.csproj`）：

```powershell
& './Mods/RimLayers/Tests/Run-V1.ps1' -Scenario aerocraft -Aerocraft -TimeoutSeconds 420
```

`-Aerocraft` 只在这个独立测试 profile 里启用 `MYDE.AerocraftFramework`，不读取也不修改玩家配置。

基准：RimWorld 1.7 本机程序集 + `MYDE.AerocraftFramework` 1.7 程序集。
设计依据与逐条根因见 [调研稿](AEROCRAFT-COMPATIBILITY-PLAN.md)，其中 §12 记录了两轮实现各自推翻的方案。

---

## 为什么需要适配

Aerocraft Framework（packageId `MYDE.AerocraftFramework`，工坊 `3611555958`）用
`Building` + 连续 `Vector2 RealCurrentPosition` 飞，不是 Pawn：没有 `Vehicles` 程序集、没有 pather、
**没有任何高度字段**（`DrawPos.y` 恒为 `def.Altitude`），起降只是翻 `Move_WarmUpTick`。

在分层地图上它有三处直接失效：

1. `Change_Position` 每 10 tick 写
   `new IntVec3((int)RealCurrentPosition.x, Position.y, (int)RealCurrentPosition.y)`，
   把逻辑 z 直接当成存储 z —— 于是飞行器被钉死在存储 band 0（或更糟，钉在 band 之间的隔离行上，
   那里 `TryDecode` 失败，所有 RimLayers 服务都会跳过它）。
2. 边界判定用 `Map.Size.z`，而分层地图上它是整个存储高度（`Stride * LayerCount`），
   所以机体会把上面的层当成“地图边缘之外”，也会飞进 `RL_Isolation` 保留行。
3. 原生落点扫描（缺油/缺电/EMP 强制降落、滑翔降落、跨图抵达）同样只看 `roofGrid` 与 `Map.Size.z`，
   完全不知道“哪一层有地板”。

## 适配契约（v2）

**空中的飞行器不属于任何一层——它在天上。** RimLayers 加的唯一规则是：

> 一架在飞的飞行器，跟随你正在看的那一层。

因此它的存储带（`Thing.Position` 的 z）始终等于 `ViewedLayer` 的 band，于是**所有**原生子系统
——右键目标、选中与拾取、verb 射程、预约、搭载、加油、伤害——都自动作用在你看着的那一层，
不需要任何额外挂点。停放的飞行器不同：它落在真实地板上，并留在那一层，直到再次起飞。

这条规则的唯一实现点是 `SetViewedLayer` 的 Postfix。切层的一切入口
（`LayerView.Switch`、`LayerTransition` 的过渡动画三条路径、`JumpLayerPatch`、飞船降落）
最终都会走到它，所以一次挂钩覆盖全部入口。

### 保留在 base 层的东西

`TakeOffVPos_A` / `TakeOffVPos_G` **不平移**：它们是“返回基地”的目标，而基地是一个真实的楼层，
不是“镜头碰巧停在哪一层”。返航前先把基地层显示出来（`ShowFloor`），`Follow` 就会把在飞的机体
带进那个 band，随后原生瞄准才解析到正确的点。

### 地下

`Follow` 在 `Elevation(ViewedLayer) < 0` 时直接返回：从地下看不到天空，把机体留在它原来的
地面以上 band 既正确也本来不可见（选区与拾取只认当前 band）。这就是“地下不能看见/选中飞行器”的实现，
不需要额外代码。

## 实现的挂点

全部在 `Source/RimLayers/Compatibility/`：

| 文件 | 内容 |
|---|---|
| `AerocraftCompatibility.cs` | 惰性反射绑定、字段/属性访问器、`Follow`/`RebaseTo`/`BandOf`/`ShowFloor`、`SelectionKeepers` 注册 |
| `AerocraftLayers.cs` | `AerocraftLayers : MapComponent`，按机记录 `HomeLayer`，`Settle()` 把停放的机体吸附到真实地板 |
| `AerocraftPatches.cs` | 全部 Harmony 挂点、`AerocraftLanding` 几何服务、`AerocraftBootstrap` |

| 挂点 | 目标 | 作用 |
|---|---|---|
| `AerocraftViewPatch` | `LayerMapComponent.SetViewedLayer` | **整个跨层机制**：切层后调用 `Follow`，把每架在飞的机体整条轨迹平移进新 band |
| `AerocraftPositionBoundsPatch` | `AsBaseThing.Change_Position` | 空中时把轨道夹在**本 band 的局部高度**内（不是 `Map.Size`）；停放时放行原生方法体，保住电网重连 |
| `AerocraftTakeOffPatch` | `AsBaseThing.Change_Up` | 起飞记录 base 层；原生因缺油缺电拒绝时（`If_UpOrDown` 仍为 false）不记录 |
| `AerocraftTouchdownPatch` | `AsBaseThing.Change_Down` | 落地把**当前所在层**记为 base |
| `AerocraftForcedDescentPatch` | `Set_TargetVPos_Down_GlidingTakeOff` | 5 处强制降落（缺油/缺电/EMP/撞毁/自毁）的唯一 choke point：不合法落点换成同层最近合法格，且把**存储 z 原样**写回 |
| `AerocraftPlacementPatch` | `PlaceWorker_NotUnderRoof.AllowsPlacing` | 放置改为“本层有 `HasSurface` 且垂直柱通畅” |
| `AerocraftRefuelPatch` | `WorkGiver_Refuel_Turret.CanRefuelThing` | `Priority.First` 抢在 Aerocraft 自己的 `Area.Home[Position]` 判定之前，改按 (x,z) **列**判定 |
| `AerocraftGoBackPatch` | `AsBaseThing.Set_GoBack` | 在飞且基地层 ≠ 当前视角层时，先把基地层显示出来，让 `Follow` 把机体带回正确 band |
| `AerocraftArrivalPatch` | `WorldObject_CrossMapThing_Flying.Arrived` | 跨图抵达后落到同层合法地板，而不是随机的 `Map.Size` 角落 |

落点/放置合法性复用 RimLayers 现成语义，不另造第三套“这里有地板吗”：
`StructuralSupport.HasSurface` + `CombatService.OpenLaunchColumn`。

## 与切层选中行为的交互

`LayerView.Switch` 会 Deselect 所有非 Pawn，避免一次放错层的建造工具继续作用在你刚离开的层上。
在飞的飞行器不属于任何一层，丢选中就等于没法跨层指挥它，所以 `LayerView` 增加了一个
`internal static readonly List<Func<object,bool>> SelectionKeepers`，兼容模块往里注册谓词，
而不是让 `LayerView` 认识第三方类型：

```csharp
private static bool KeepSelected(object selected) =>
    selected is Thing thing && IsAerocraft(thing) && IsFlying(thing);
```

## 降级策略

绑定失败（Aerocraft 改了字段布局，或未启用）时 `AerocraftCompatibility.Bound == false`，
每个挂点的 `Prepare()` 返回 false，Harmony 不安装它们，飞行器行为与没装 RimLayers 时完全一致。
Aerocraft 是可选第三方包，**不允许**它的适配失败连累 RimLayers 的其它补丁。

`About/About.xml` 的 `loadAfter` 已加入 `MYDE.AerocraftFramework`；`modDependencies` 未加（可选兼容）。

`Install()` 不是一次性守卫：`Mod` 构造时其它 mod 的程序集还没加载完，
`AccessTools.TypeByName` 看不到 Aerocraft 的类型，所以失败的首次尝试必须由
`[StaticConstructorOnStartup] AerocraftBootstrap` 重试。守卫只在成功绑定之后才生效。

## 已实测

见下方“验收矩阵”。断言读的是 RimLayers 状态与原生 `Thing.Position`，不是维护的镜像值。

测试机型 `MYDE_AF_RotorCraft_TwinPropeller_Base`（“Patrol” RT-63，旋翼垂直起降，300 电池，
**不用 `CompRefuelable`**，出生为 0 必须先充电），以及 `MYDE_AF_FWCraft_Bombardment`
（固定翼，`If_NeedGlidingWhenTakeOff=true`，必须走跑道）。

| 组 | 断言 |
|---|---|
| 绑定 | 适配器已绑定；包已启用（大小写不敏感）；Def 可加载 |
| 地表停机坪 | `HasSurface`；垂直柱通畅；`GenConstruct.CanPlaceBlueprintAt` 接受 |
| 停放 | 停放时机体 band = 机位 band；`BandOf` 与存储格解码一致 |
| **加油** | 无任何层在 Home 内 → 拒绝；Home 只画在上层 → 接受（列判定）；Home 在同层 → 接受 |
| 起飞 | 通过真实 gizmo 入口 `Change_UpOrDown`；`If_UpOrDown` 生效；base 层被记录 |
| 空中 | 存储格在本 band，且与轨道 z 一致 |
| **切层跟随** | 视角切到 1 层：机体存储带变成 1，且整条轨迹（`pos.z` 与 `RealCurrentPosition.y`）仍在同一 band；切到 2 层再切回 0 层同理 |
| **选中保持** | 切层前选中在飞机体，切层后仍选中；`SelectionKeepers` 非空 |
| **地下不可见** | 视角切到地下层：机体 band 不变（不会被搬进地下） |
| **落地到当前层** | 视角在 1 层时降落：base 层 = 1；`Is_Static` 为真；`Settle()` 后机体停在真实地板上 |
| **返航** | 二次起飞后视角在 2 层，`Set_GoBack` 把画面带回 base 层（`Follow` 同步把机体带回该 band） |
| 落点几何 | 隔离保留行既不解码也不被接受；上层 `RL_OpenAir` 空腔被拒；地表停机坪被接受 |
| **强制降落** | 在本 band 内挖出一个无地板的探针格，`Set_TargetVPos_Down_GlidingTakeOff` 被改到**同 band**的合法地板格 |
| 固定翼 | def 确为滑翔型；26×26 无顶跑道每格通过 `CanLand`；滑翔起飞被接受且保持本层 |
| 存档 / 重载 | 记录随存档保存；重载后适配器重新绑定，存储格可解码且与轨道 band 一致，base 层有效 |

### 验收脚本的注意事项（踩过的坑）

- **不要**通过反射读回被 Prefix 改写的参数：`MethodInfo.Invoke` 把实参复制到自己的缓冲区，
  Harmony 的改写对调用方不可见（真实游戏里是直接调用，改写生效）。断言要读机体自己的状态
  （`TargetVPos` 等）。
- `Position` 由原生 `Change_Position` 在 10 tick 节奏上提交，且 `Is_Static` 时直接返回，
  所以脚本按**真实状态**推进（等 `Is_Flying`、等存储提交），不按固定 tick 号。
- `Set_GoBack` 在 `Is_Flying == false` 时是 no-op，所以“返航”必须是独立的、由 tick 驱动的阶段，
  不能和起飞写在同一个调用栈里。
- `SetViewedLayer` 的 Postfix 是切层跟随的唯一来源；测试直接调 `LayerView.Switch`，走的是
  玩家同一条路径。

## 已知限制

- 跨图抵达（`Arrived`）已实现并通过静态核对，但没有独立场景——需要真实的世界地图旅行。
- 强制降落通过**直接调用** `Set_TargetVPos_Down_GlidingTakeOff` 驱动，而不是真的把电池抽干
  让原生触发器跑一遍。
- 选点不合法时**不会**在光标上拒绝（原生 validator 是编译闭包，无法定点 Patch；为它去 Postfix
  全局 `GenGrid.InBounds` 风险大于收益）。表现是“点了照样接受，但落点被自动改到同层最近合法格”。
- 换层表现为离散的“视角切层时整条轨迹平移一个 band”，不是连续 3D 升降。Aerocraft 本身没有高度变量
  （`If_UpOrDown` 是 bool），做连续高度等于替它重写一套飞行模型。
- 两条在飞的飞机**不会**跨层相撞：`Follow` 把每一架在飞的飞机都搬进同一个 viewed band，
  所以它们永远同 band，原生 `Check_Collide`（同格 `GetThingList`）照常生效。
  真正没有的是"跨层同列"的碰撞——而那在物理上也说不通（不同层高度不同）。
- 停放（`Is_Static`）的飞机不跟随视角，它属于自己那一层。所以在飞的飞机与停在别层的飞机
  不会互相阻挡；这也是正确语义。
- `Area` / `Zone` / `Plan` 没有分层语义。加油的 Home 判定是“该 (x,z) 列任一层被圈进 Home”的近似。
- 原子加油（`atomicFueling=true`，Aerocraft 默认不开）跨层仍不支持 ——
  `RefuelWorkGiverUtility.FindEnoughReservableThings` 尚未分层。
- 殖民者到上层机位加油需要 `ConnectorGraph` 有通路（楼梯/升降机）。这是**玩家侧前提**，
  不是适配器的缺陷：没有通路时 `Toils_Goto.GotoThing(A, PathEndMode.Touch)` 本来就走不到。
- `Change_Position` 每次写 `Position` 的代价在分层地图上约为单层的 3 倍（`Thing.Position` setter
  要重挂 regions/thingGrid/coverGrid 并 dirty mesh）。未改语义，仅记录。

## 复核清单（拿到启用 Aerocraft 的存档后）

1. 地表起飞 → 视角切到上层 → 右键上层地面：飞机会飞到上层，不在地表层盘旋。
2. 上层降落 → 殖民者自动加油：生成 `RearmTurret` Job（且上层有楼梯/升降机通路）。
3. 切层时已选中的在飞飞机仍在选区内。
4. 视角切到地下：飞机看不见、选不到，切回来后位置没变。
5. 固定翼低油滑翔降落：落点在同层有地板处，不穿上层楼板。
6. “返回基地”：画面与飞机一起回到起飞层。
7. 存读档往返：飞机所在层与 `RealCurrentPosition` 一致。
8. 无 Aerocraft 环境：RimLayers 正常加载，日志无绑定行，无 Harmony 报错。

日志入口：绑定结果 `[RimLayers] Aerocraft Framework layered adapter bound.`
（或 `Log.Warning` 的降级说明）。
