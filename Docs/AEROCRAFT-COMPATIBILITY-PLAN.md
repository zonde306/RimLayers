# Aerocraft Framework 楼层兼容：修复实施方案（调研稿）

基准：本机 1.7 程序集与工坊目录（2026-10-25 复核）。

- 游戏：`Assembly-CSharp.dll`（15,632,896 B，MD5 `35242F1A2F872909BB394181C5658CAC`）MVID `3b4968d3bd524047bc7507c1b0386895`
- Aerocraft Framework：`G:/SteamLibrary/steamapps/workshop/content/294100/3611555958/1.7/Assemblies/AerocraftFramework.dll`（150,016 B）MVID `5c27baf286b2b74c88247b73975870bf`，packageId `MYDE.AerocraftFramework`
- RimLayers：`Mods/RimLayers/Assemblies/RimLayers.dll` MVID `c53774d03e65a0408f68ddfa1f16f4e0`

Aerocraft 1.7 只有 DLL、没有源码；下文所有 `Building_Aerocraft_*.cs` 行号指 ilspycmd 反编译产物（`.tmp/aero17/MYDE_AerocraftFramework/`），
结论以该 1.7 DLL 为准，`AerocraftFramework1.6update/` 的 1.5 时代源码只用于命名参考。

**实施状态（2026-10-25）：§1 §2 §3 §4 §5 §7 §9 已按本文实现并通过 Release 构建（0 警告 / 0 错误），并通过 `Run-V1.ps1 -Scenario aerocraft -Aerocraft` 在真实游戏里验收 34/34。**
实现落在 `Source/RimLayers/Compatibility/AerocraftCompatibility.cs`（绑定）、`AerocraftLayers.cs`（分层记录）、`AerocraftPatches.cs`（全部 Harmony 挂点）。运行记录与已知限制见 [Aerocraft 兼容](AEROCRAFT-COMPATIBILITY.md)。

本文其余部分保留为设计依据；与实现有偏差的地方在对应小节标注“实现”。

---

## 0. 结论摘要

| # | 现象 | 根因（已核实） | 修法 | 规模 |
|---|---|---|---|---|
| 1 | 飞行器只能在最底层（band 0）飞，跨层后立刻被拉回 | `Change_Position` 每 10 tick 用 `Position = new IntVec3((int)RealCurrentPosition.x, Position.y, (int)RealCurrentPosition.y)` 覆盖存储格，`y` 原样保留、`z` 直接取逻辑 z（`Building_Aerocraft_AsBaseThing.cs:1140-1145`） | 不改它的写法，改**约定**：`RealCurrentPosition.y` 始终保存**存储 z**（`layer*Stride + 逻辑 z`），换层时整体 `+= (newLayer-oldLayer)*Stride`。参照 `Vehicles/SteelFlightAdapter.cs` 的 `Commit`/`ConstrainGround` 做法 | M |
| 2 | 起飞/降落没有任何“层”的概念，`If_UpOrDown` 只是二值 | Aerocraft 全库无逻辑高度字段：`DrawPos.y` 恒为 `def.Altitude`（`Building_Aerocraft_Base.cs:88`），起降只切 `Move_WarmUpTick`（`AsBaseThing.cs:172/174`） | 新增 `AerocraftLayerState`（独立 `MapComponent`）保存每架机的 `ProxyLayer`、`Datum`（该层地板高度）、`HomeLayer`；换层只在“垂直段”做，一次一个 band | M |
| 3 | 低油/低电/被 EMP 时滑翔降落会穿到上层楼板底下或卡在隔离带 | 落点扫描只查 `roofGrid` 与 `map.Size.z`（`AsBaseThing.cs:527/555/710/779/805`），而 `map.Size.z == Layout.StorageHeight`（`Layout/LayerLayout.cs:90`、`Generation/GenerationScope.FullSize`），band 之间的隔离行完全不在它的视野里 | 用 RimLayers 已有语义重做落点解析：`SkyfallService.Trace` 的“自上而下找第一个 `HasSurface` 层” + `CombatService.OpenLaunchColumn` 的“垂直柱无遮挡” | M |
| 4 | 停在上层/地下停机坪的飞行器**永远没人来加油** | Aerocraft 自己的 Prefix 要求 `Map.areaManager.Home[b.Position] && b.Is_Static`（`MYDE_Harmony_AerocraftFramework_WorkGiver_Refuel_Turret.cs`）；`Area` 索引用存储格（`Verse.Area.this[IntVec3]`），玩家圈地几乎都只在地表 band 0 | 用 `[HarmonyPriority(Priority.First)]` Prefix 抢先于它的 Prefix：按 (X,Z) 列判定（该层 `HasSurface` + 该列任一层在 Home 内），`Is_Static` 换成“在当前层已落地” | S |
| 5 | 上层空腔（`RL_OpenAir`）能直接手放飞行器，起飞就穿模 | `PlaceWorker_NotUnderRoof` 只看原生 `roofGrid.Roofed`（`RimWorld.PlaceWorker_NotUnderRoof` 全文），不看 RimLayers 的 `Structure.HasSurface`；而 Aerocraft 父模板又挂了 `PlaceWorker_NotUnderRoof`（`1.7/Defs/SomeThing/Base.xml`） | Prefix 该 `AllowsPlacing`：Aerocraft 整机时改成“本层需 `HasSurface` 且垂直柱需 `OpenLaunchColumn`”；其余 `return true` | S |
| 6 | 换层之后点“返回基地”会穿层飞 | `TakeOffVPos_A`/`TakeOffVPos_G` 存的是 `DrawPos` 派生值且被 Scribe（`AsBaseThing.cs:278/279`、`:1522`、`:1656`），换层后它们仍指旧 band | 换层时同步 rebase 这两个点（它们与 `RealCurrentPosition` 同一坐标空间，差值不变即视觉不变） | S |
| 7 | 跨图抵达后总是出现在最底层的角落，飞向“地图中心”会落到中间某一层 | `Arrived()` 的四个候选是 `(0,0,0)+(15,0,15)`、`(Size.x,0,0)+(-15,0,15)`、`(0,0,Size.z)+(15,0,-15)`、`(Size.x,0,Size.z)+(-15,0,-15)`，`Size.z == StorageHeight` → 两个候选在 band 0、两个在最上层 band（`WorldObject_CrossMapThing_Flying.cs:510-514`）；随后 `linkToAerocraft.Set_TargetVPos(TargetMap.Center.ToVector3Shifted())`，`Map.Center.z = StorageHeight/2`（`Verse.Map.Center`）落在中间层 | Postfix `Arrived`：spawn 之后按“该 (X,Z) 列自上而下第一个 `HasSurface` 层”重设 `Position` 与 `RealCurrentPosition`，并把 `TargetVPos` 重投影到同层 | S |
| 8 | 飞行器飞越隔离带时整机消失/抖动 | `Presentation/ProjectileCullPatch.cs` 与原生 `DynamicDrawManager.ComputeCulledThings` 都按 `thing.Position`（非 Pawn）取 `cellIndices` 与 `fogGrid`；band 之间是 `RL_Isolation` 行，`LayerMapComponent.FinalizeInit` 还给它们加了 light blocker | 由 #1 的“不做跨 band 直飞、只做 rebase”天然规避；不要在 `RealCurrentPosition` 上做跨带插值 | — |
| 9 | 扩层（`LiveMapExpansion`）窗口内飞行器的边界判定会错 | 扩层期间 `map.info.Size` 在 `GroundSize` 与 `FullSize` 之间切换（`Generation/LayerGeneration.cs` 的 `ConstructComponentsPatch`/`FinalizeMapPatch`），而 `Change_Position` 的边界与 `GenGrid.InNoBuildEdgeArea` 都读 `Map.Size` | 换层/落点服务统一加 `RecoveryCompatibility.TransactionActive` 与 `GenerationScope.Active` 闸门，命中就 `DeferFor(thing, 30)` 本 tick 不动作 | S |
| 10 | `Change_Position` 每次写 `Position` 的代价在分层地图上是原来的 ~3 倍 | `Verse.Thing.Position` setter 是完整 `DeregisterInRegions`/`thingGrid.Deregister`/`Register`/`DirtyMapMesh`，且 `Map.Area == Size.x * Size.z == Width*StorageHeight` | 不改语义；只把 `ChangPosTickMax`（默认 10）作为可调项记录在案，跑 `Tests/Run-VehicleRegression.ps1` 时观测 | S |

S≈0.5 天，M≈1–2 天（含游戏内验证）。

**架构结论（最重要的一条）**：Aerocraft 是 `Building` + 连续 `Vector2` 运动学，**不能**走 `FrameworkVehicleAdapter`/`IVehicleAdapter` 那条 Pawn 路线（它没有 `Vehicles` 程序集、没有 `PatherType`），也不需要 `VehicleFlight`/`VehicleAirspace` 的规划器。
它的正确兼容形态是“**把连续坐标整体驻留在一个存储 band 内，换层时整体平移**”，这与 `SteelFlightAdapter` 处理 Steel Horizon 的手法是同一个套路（`Commit` 里 `position.Set(comp, position.Get(comp)+new Vector2(0,shift))`，`shift=(layer-oldLayer)*Stride`），只是对象从 Pawn 换成 Building、从 3D-track 换成 2D-track。

---

## 1. 坐标契约：把 `RealCurrentPosition` 固定在一个 band 内

### 证据

`Building_Aerocraft_AsBaseThing.cs:1132-1145`（`Change_Position`，每 tick 调用，内部 10 tick 才真正写一次）：

```csharp
ChangPosTick++;
if (ChangPosTick < ChangPosTickMax) return;      // ChangPosTickMax 默认 10（Building_Aerocraft_Base.cs:82-84）
ChangPosTick = 0;
IntVec3 val = new IntVec3((int)RealCurrentPosition.x, ((Thing)this).Position.y, (int)RealCurrentPosition.y);
if (val.x < 1 + def.size.x || val.x > Map.Size.x - def.size.x - 1 ||
    val.z < 1 + def.size.z || val.z > Map.Size.z - def.size.z - 1) return;
((Thing)this).Position = val;
```

- `y` 取的是**旧 Position.y**，`z` 直接是 `RealCurrentPosition.y` → 在分层地图上就是 `z = 逻辑 z`，永远是 band 0。
- `Map.Size.z` 在受管地图上等于 `Layout.StorageHeight`（`Layout/LayerLayout.cs:90` `StorageHeight => Stride * LayerCount`；`Generation/LayerGeneration.cs` 的 `GenerationScope.FullSize => new(Layout.Width, 1, Layout.StorageHeight)`，`FinalizeMapPatch` 把它写回 `info.Size`）。所以这条边界检查对 z 方向**过度宽松**：允许飞进隔离带、允许飞过最上层 band。
- 同一方法末尾还有 `GenGrid.InNoBuildEdgeArea(Position, Map)`（`Verse.GenGrid.CloseToEdge(map, 10)` → `c.z >= size.z - 10`），在 band 0 的低 10 行与最上层 band 的高 10 行会触发折返。

`DrawPos`（`Building_Aerocraft_Base.cs:88`）：

```csharp
public override Vector3 DrawPos => new Vector3(RealCurrentPosition.x, def.Altitude, RealCurrentPosition.y);
```

x/z 全来自 `RealCurrentPosition`，**没有任何独立修正**。绘制链上另外三处 y 都各自写死为道具自身的 `AltitudeLayer`（`Draw_BaseThing` 的 `def.Altitude + Draw_Base_ExtraAltitudeLayerNum`、`Draw_Decoration`/`Draw_Shadow` 的 `Altitudes.AltitudeFor(Props.Draw_Decoration_BaseAltitude) + extra`），x/z 都从 `DrawPos` 派生 —— 也就是说**只要 `RealCurrentPosition.y` 正确，四个绘制点全对，不需要 patch 任何一个绘制函数**。

### 方案

约定：**`RealCurrentPosition`（以及 `TargetVPos`、`TakeOffVPos_A`、`TakeOffVPos_G`、`Shadow_Pos`）一律存“存储坐标”**，即

```
RealCurrentPosition.y = proxyLayer * Layout.Stride + 逻辑 z        (0 <= 逻辑 z < Layout.Height)
```

`ProxyLayer` / `Datum` 放在新 `MapComponent` 里：

```csharp
namespace RimLayers.Compatibility;

public sealed class AerocraftLayers : MapComponent
{
    public sealed class Flight : IExposable
    {
        public int ThingId;
        public int ProxyLayer;   // 当前驻留的存储 band
        public int HomeLayer;    // "返回基地"用的层
        public void ExposeData() { Scribe_Values.Look(ref ThingId,"aerocraftThing",0);
                                   Scribe_Values.Look(ref ProxyLayer,"aerocraftLayer",0);
                                   Scribe_Values.Look(ref HomeLayer,"aerocraftHome",0); }
    }
    private readonly Dictionary<int, Flight> flights = new();
    public Flight? Of(Thing t) => flights.TryGetValue(t.thingIDNumber, out var f) ? f : null;
    public override void ExposeData() { Scribe_Collections.Look(ref flights, "aerocraftLayers", LookMode.Value, LookMode.Deep); }
}
```

挂点零成本：`Verse.Map` 的 ctor 会对 `MapComponent.AllSubclassesNonAbstract()` 里每个非 `CustomMapComponent` 自动 `Activator.CreateInstance(type, this)` 并加入 `components`（`Verse.Map` ctor），`MapComponentUtility.MapComponentTick` 自动 tick，`Map.ExposeData` 用 `Scribe_Collections.Look(ref components,"components",LookMode.Deep,this)` 存档。RimLayers 自己没有 MapComponent 的 Def 注册点，这里也不需要。

换层原语（对齐 `Vehicles/SteelFlightAdapter.cs` 的 `Commit`）：

```csharp
internal static void Rebase(AerocraftLayers.Flight f, Thing t, LayerMapComponent owner, int layer)
{
    if (layer == f.ProxyLayer) return;
    int shift = (layer - f.ProxyLayer) * owner.Layout!.Stride;
    Instance(t).RealCurrentPosition.y += shift;   // RealCurrentPosition
    Instance(t).TargetVPos.y        += shift;
    Instance(t).TakeOffVPos_A.y     += shift;
    Instance(t).TakeOffVPos_G.y     += shift;
    Instance(t).Shadow_Pos.z        += shift;
    f.ProxyLayer = layer;
}
```

因为所有点同时平移同一个量，**相对几何完全不变**（`MoveToTarget` 用 `TargetVPos - DrawPos`，`Check_GoToTarget` 用 `ToIntVec3(DrawPos) - ToIntVec3(TargetVPos)`，`GetVector3_By_AngleFlat(DrawPos, ...)` 全部是差值），所以换层对 Aerocraft 自身逻辑是“透明”的，视觉上就是“整机换了一个楼层”。

`Datum`（该层地板米高）只在需要与 RimLayers 服务互操作时用：`owner.Elevation(layer) * 3`（与 `Vehicles/VehicleSpace.Pose`、`VehicleFlight.Ensure` 的约定一致）。

注意：**不要**去 patch `DrawPos` 做 `(ViewedLayer-layerId)*Stride` 之类的投影。那样虽然能显示，但 `Position` 会留在错误 band，于是 `fogGrid`/`Area.Home`/`thingGrid`/`Comp_AutoFindTarget`/`RefuelWorkGiverUtility` 全部看错层，还要再补一个 `ProjectileCullPatch` 式的 transpiler。RimLayers 自己的 `LowerFloorBackdrop.Project` 只用于“装饰性预览”，不是实体定位手段（该文件注释明确 "no entity position, map state, AI or collision is changed"）。

### 需要游戏内复核的点

- 落地状态（`Is_Static`）下换层是否会造成一次可见抖动（`Position` setter 会 `DirtyMapMesh`）。
- 存档往返：`RealCurrentPosition` 由 Aerocraft 自己 Scribe、`ProxyLayer` 由我们 Scribe，两者在 `ExposeData` 顺序不同步时是否可能不一致 → 需在 `MapComponent.FinalizeInit` 里做一次校正（`Math.Clamp` 到 `[0, LayerCount)`，并用 `RealCurrentPosition.y / Stride` 反推）。

---

## 2. 谁决定“目标层”：起降的层语义

### 证据

- 右键选点走 `MYDE_Harmony_Selector_HandleMapClicks`：`IntVec3 val = IntVec3.FromVector3(UI.MouseMapPosition())`，`UI.MouseMapPosition() => UIToMapPosition(MousePositionOnUI)`，而 `UI.UIToMapPosition` 是 `Find.Camera.ScreenPointToRay(screenLoc*Prefs.UIScale)` 的 `ray.origin` 取 `(x, 0, z)` —— **y 恒 0，z 直接是相机 rootPos.z**，而 `Presentation/LayerView.cs:33 Switch` 已经给 `camera.rootPos.z` 加了 `(layer-old)*Stride`。
  → **右键点到的格天然就是当前查看层的存储格**，`Set_TargetVPos(val.ToVector3Shifted())` 里的 `TargetVPos.z` 已经在目标 band 内。这一条不需要改。
- 滑翔起飞/降落用 `Find.Targeter.BeginTargeting(new TargetingParameters{ canTargetLocations=true, validator = target => target.IsValid && GenGrid.InBounds(target.Cell, Map) })`（`AsBaseThing.cs:1531`/`:1749`）→ 同样是当前查看层的格；但 validator 只查 `GenGrid.InBounds`，**不查该层有没有地板、垂直柱是否通畅**。
- 起飞基准点：`Change_Up()` 末尾 `TakeOffVPos_A = DrawPos`（`AsBaseThing.cs:1522`）；`Change_Up_GlidingTakeOff()` 的 `TakeOffVPos_G` 由 `GetVector3_By_AngleFlat` 算出（`:1656`）。两个都被 Scribe（`:278/279`）。`Set_GoBack()` 用它们作为返航点（`:1274`）。
- 飞行器是 `Building` 不是 Pawn：`Presentation/LayerView.cs:33` 切层时会 `Deselect` 所有非 Pawn 选中对象（`if(!Find.Targeter.IsTargeting)` 才做，注释见文件）。所以“选中飞行器 → 点起降 gizmo → targeting 期间切层 → 点落点”这条路径是可行的，而“选中飞行器后先切层再点右键”会丢失选中。

### 方案

三层语义，按优先级：

1. **起飞**：`Change_Up` / `Change_Up_GlidingTakeOff` 成功后，`ProxyLayer` 保持不变，只记 `Datum = owner.Elevation(ProxyLayer)*3`；`HomeLayer = ProxyLayer`。
   旋翼机（`If_NeedGlidingWhenTakeOff == false`，见 `RotorCraft.xml`）垂直起降 → 不换层。
   固定翼（`If_NeedGlidingWhenTakeOff == true`，`FixedWingCraft.xml:102`，`Gliding_Range=20`/`30`）需要一个 ≥`Gliding_Range` 的空旷直线，**这一段必须在目标层的 band 内**，所以滑翔起飞应该在“选点”时就 rebase 到落点所在层（见 3 的合法性判定）。
2. **巡航**：`TargetVPos` 所在 band 就是目标层。若它与 `ProxyLayer` 不同，则在**起飞完成的那一刻**（`Is_Flying` 首次为 true，即 `Move_WarmUpTick > Move_WarmUpTickMax`，`AsBaseThing.cs:172`）调用一次 `Rebase`，让整段巡航都在目标层内进行。绝不在飞行途中跨越隔离带。
3. **降落**：`Change_Down` / `Set_TargetVPos_Down_GlidingTakeOff` 把 `If_UpOrDown=false`；落地（`Is_Static` 再次为 true，`:174`）后 `ProxyLayer` 就是最终楼层，`HomeLayer` 同步更新。

`Set_GoBack` 需要的只是“返航点与当前机同属一个 band”，这由 #1 的 rebase 保证（`TakeOffVPos_A/G` 一起平移）。

### 需要游戏内复核的点

- 跨层起飞时相机不跟着走（玩家看到的还是旧层），是否需要 `LayerView.Switch(owner, targetLayer)`。参考 `SteelFlightAdapter.Commit` 里 `if(shift!=0 && manual(comp)) LayerView.Switch(owner,layer);` —— 只在“手动驾驶”时切，Aerocraft 没有手动驾驶，建议在 git 里做成可配置（默认不切，避免抢镜头）。
- 固定翼滑翔的 20/30 格跑道在 `LayerLayout.Height` 较小（`CalculationStride` 最小 17 行 gap）时是否放得下。

---

## 3. 起降点合法性：接 RimLayers 的“垂直柱”语义

### 证据

RimLayers 已有两段完全对口的判定：

`Structure/SkyfallService.cs:18-49`：

```csharp
public bool Trace(Skyfaller faller, IntVec3 requested, Rot4 rotation, out IntVec3 hit)   // 自上而下找第一个有地板的层
    => foreach (int layer in owner.Layout!.OrderedLayers.Reverse().Where(l => owner.Elevation(l) >= 0))
           if (owner.Structure.HasSurface(owner.ToStorage(new LayerCell(layer, local.X, local.Z)))) { hit = ...; return true; }

public bool CanArrive(IntVec3 c, bool punch)   // 该格要有地板，且 {本层}.Concat(Above) 上没有表面/屋顶
```

`Combat/CombatService.cs:40-50`：

```csharp
public bool OpenLaunchColumn(IntVec3 source)   // 从本层向上的垂直柱：无 roof、无上层 HasSurface
{
    if (!owner.TryDecode(source, out var c)) return false;
    foreach (int layer in new[]{c.LayerId}.Concat(owner.Layout!.Above(c.LayerId)))
    {
        IntVec3 at = owner.ToStorage(new LayerCell(layer, c.X, c.Z));
        if (owner.map.roofGrid.RoofAt(at) != null ||
            owner.Elevation(layer) > owner.Elevation(c.LayerId) && owner.Structure.HasSurface(at)) return false;
    }
    return true;
}
```

Aerocraft 侧的落点扫描（`AsBaseThing.cs:527` 等 5 处）只有：

```csharp
if ((GridsUtility.GetRoof(val, map) != null && GetRoof(val, map).isThickRoof) ||
    val.x >= map.Size.x - 5 || val.x <= 5 || val.z >= map.Size.z - 5 || val.z <= 5) break;
```

`val` 是存储格（`GetVector3_By_AngleFlat(drawPos, range, angle)` 的结果），`map.Size.z == StorageHeight` → 这个 `±5` 只在最底层与最上层生效，中间任何一层都判不出来；而 `GetRoof` 只查原生屋顶，**查不到 RimLayers 上层楼板**。结果：滑翔降落可能穿过上层楼板、或停在 `RL_Isolation` 行上（那里 `TryDecode` 失败）。

### 方案

新增 `AerocraftLanding` 服务（放在 `Compatibility/AerocraftLayers.cs` 同文件或独立文件）：

```csharp
internal static bool CanLand(LayerMapComponent owner, IntVec3 storage, IntVec2 size)
{
    if (!owner.TryDecode(storage, out var c)) return false;               // 落在隔离带 → 拒绝
    foreach (IntVec3 cell in GenAdj.OccupiedRect(storage, Rot4.North, size))
        if (!owner.TryDecode(cell, out var l) || l.LayerId != c.LayerId ||
            !owner.Structure.HasSurface(cell) ||
            !owner.Combat.OpenLaunchColumn(cell)) return false;           // 地板 + 垂直柱通畅
    return true;
}
```

接线点（按收益排序）：

1. `Change_Down_GlidingTakeOff`（`:1749`）与 `Change_Down`：若目标落点不合法，改用 `SkyfallService.Trace` 式扫描在**同一层内**找最近的合法格；找不到 `MoteMaker.ThrowText(..., "RL_NoSurface")`。
2. 低油/低电/EMP 的三处强制降落扫描（`:527`/`:555`/`:710`/`:779`/`:805`）：Prefix 替换扫描循环，把 `GetRoof(...).isThickRoof || 边缘 ±5` 换成 `!CanLand(...)`。
3. 滑翔起飞/降落的 `Targeter` validator：`GenGrid.InBounds` 之外再加 `CanLand`，让玩家在选点阶段就得到拒绝（红圈）。
4. 起飞的“跑道”合法性：起飞方向上的 `Gliding_Range` 格逐格 `CanLand`。

因为 `HasSurface` 已经包含“第 0 层天然地面 / `StructuralCells` + 必需屋顶 / `VehicleFacilitySpace.HasDeck`”三种来源（`Structure/StructuralSupport.cs:27`），这里不需要区分地表、楼板、飞船甲板与载具甲板。

### 需要游戏内复核的点

- `OpenLaunchColumn` 对“本层上方是同层屋顶”的判定是否会把室内停机坪（自己盖了屋顶）也拒掉 —— 若是，起飞时改用 `SkyPayload`/`punch` 的宽松口径。
- 固定翼的 20 格跑道在多层建筑里的可用率。

---

## 4. 跨层加油

### 证据

Aerocraft 自己的 Prefix（`MYDE_Harmony_AerocraftFramework_WorkGiver_Refuel_Turret.cs` 全文）：

```csharp
[HarmonyPatch(typeof(WorkGiver_Refuel_Turret), "CanRefuelThing")]
internal class MYDE_Harmony_AerocraftFramework_WorkGiver_Refuel_Turret
{
    [HarmonyPrefix] private static bool Prefix(Thing t)
    {
        bool result = true;
        if (t is Building_Aerocraft_Base)
        {
            var b = t as Building_Aerocraft_Base;
            if (!((Area)b.Map.areaManager.Home)[b.Position]) result = false;
            else if (!b.Is_Static) result = false;
        }
        return result;
    }
}
```

- `Verse.Area.this[IntVec3 c] => innerGrid[Map.cellIndices.CellToIndex(c)]` —— **存储格索引**。RimLayers 的 `Generation/GridCapacityRepair.cs` 会把 `areaManager.AllAreas` 的 `innerGrid` 扩到 `StorageHeight`（不会崩），但玩家圈的 Home 仍然只有地表那一片 → 上层/地下停机坪的 `Home[Position]` 恒 false → **不生成加油 Job**。
- 原生 `WorkGiver_Refuel_Turret : WorkGiver_Refuel`（`CanRefuelThing(t) => t is Building_Turret`），`WorkGiver_Refuel.PotentialWorkThingRequest = ThingRequest.ForGroup(ThingRequestGroup.Refuelable)`、`PathEndMode.Touch`、`HasJobOnThing => CanRefuelThing(t) && RefuelWorkGiverUtility.CanRefuel(pawn,t,forced)`。
- `RefuelWorkGiverUtility.CanRefuel` 全文顺序：`CompRefuelable`/`Fogged()`/`IsFull`/`allowAutoRefuel`/`allowRefuelIfNotEmpty`/`ShouldAutoRefuelNow`/`CanReserve`/`Faction`/`CompInteractable 冷却`/`FindBestFuel(pawn,t)==null` → `"NoFuelToRefuel"`。
- `FindBestFuel` = `GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, filter.BestThingRequest, PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f, Validator)` —— 这条路径**已被 RimLayers 接管**（`Work/LayerWorkQueries.cs` 的 `ReachableCandidatePatch` Prefix + `Cargo/CargoQueries.cs:50` 的 Postfix），但守卫里有 `!owner.Connectors.HasAny` → **没有楼梯/升降机的地图仍然退回单层搜索**。
- `FindAllFuel`（`RefuelWorkGiverUtility.FindEnoughReservableThings`）走原生 `RegionTraverser.BreadthFirstTraverse(region, EntryCondition, RegionProcessor, 99999)` + `ReachabilityWithinRegion.ThingFromRegionListerReachable`，**RimLayers 未接管**（全仓 grep `CompRefuelable` 只在 `Diagnostics/V1*.cs` 出现，无 `WorkGiver_Refuel`/`RefuelWorkGiverUtility`/`JobDriver_Refuel` 补丁）。但 Aerocraft 的 5 处 `CompProperties_Refuelable`（`FixedWingCraft.xml` 3 处、`RotorCraft.xml` 2 处）**都没有写 `atomicFueling`** → 默认 false → 走逐件搬运，不触发原子路径。
- 跨图界面加油（`Building_Aerocraft_AsBaseThing.DoSomething_Refuel`，`:580+`，从 `CompTransporter.innerContainer` 取 `Comp_CanCrossMap.Props.DefaultThing_ToRefuel` 直接填 `CompRefuelable`）**不经 WorkGiver**，与楼层无关。

### 方案

Aerocraft 适配层用一个 `[HarmonyPriority(Priority.First)]` Prefix 抢在它的 Prefix 之前（Harmony 前缀按 priority 降序执行，`Priority.First = 800`；先返回 false 的一方的 `__result` 生效、原方法与后续前缀都被跳过）：

```csharp
[HarmonyPatch(typeof(WorkGiver_Refuel_Turret), "CanRefuelThing")]
internal static class AerocraftRefuelPatch
{
    [HarmonyPriority(Priority.First)]
    [HarmonyPrefix]
    private static bool Prefix(Thing t, ref bool __result)
    {
        if (t is not Building_Aerocraft_Base) return true;               // 其它原版炮塔交回
        if (!AerocraftCompatibility.Bound) return true;                  // 适配器未就绪
        var owner = LayerMapComponent.For(t.Map);
        if (owner == null || !owner.TryDecode(t.Position, out var at)) { __result = true; return false; }
        // 该层要有地板（不是悬空/隔离带），且该 (X,Z) 列在任意一层被圈进 Home
        bool grounded = owner.Structure.HasSurface(t.Position);
        bool home = owner.Column(at.X, at.Z).Any(owner.map.areaManager.Home);   // IEnumerable<IntVec3> Column(x,z)
        __result = grounded && home && AerocraftLayers.Landed(t);
        return false;
    }
}
```

`Landed(t)` 用 `Is_Static`（`Move_WarmUpTick <= 0`）而不是原始 `b.Is_Static`，语义相同但可以顺带放宽“正在暖机”的机体。

RimLayers 侧的通用缺口（可选、与 Aerocraft 无关但会被这个需求暴露）：

1. `ReachableCandidatePatch` 的 `!owner.Connectors.HasAny` 守卫：没有楼梯的纯楼板地图（用 `RL_StructuralFloor` 或飞船甲板搭出来的）应当也走分层候选，否则加油/搬运在无楼梯时仍然单层搜索。
2. `RefuelWorkGiverUtility.FindEnoughReservableThings`（原子加油）没有分层版本。目前 Aerocraft 不用，但任何第三方如果开了 `atomicFueling=true` 就会中招。建议另记一条 TODO，不在本次 Aerocraft 兼容里做。
3. `Home` 区本身没有分层语义（`Area` 是 `BoolGrid`）。Aerocraft 这条用“按列判定”绕过去；通用的做法需要 RimLayers 提供 `Area` 的分层查询，属于更大的设计改动。

### 需要游戏内复核的点

- `owner.Column(at.X, at.Z)` 的枚举开销（`LayerMapComponent.Column` 返回 `IEnumerable<IntVec3>`，可能是 LINQ）。这条路径每 tick 每个 WorkGiver 都会调，需实测；必要时改成 `Layout.OrderedLayers` 上直接 `ToStorage` 的两点判断。
- 燃料（Chemfuel）放在上层储物区时，`FindBestFuel` 的 `GenClosest` 是否真能跨层找到（依赖 #4 的 `Connectors.HasAny`）。

---

## 5. 放置：`PlaceWorker_NotUnderRoof` 与 `terrainAffordanceNeeded`

### 证据

原生（ilspycmd 全文）：

```csharp
public class PlaceWorker_NotUnderRoof : PlaceWorker
{
    public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
    {
        if (checkingDef.Size.x == 1 && checkingDef.Size.z == 1) { if (map.roofGrid.Roofed(loc)) return ... }
        else foreach (IntVec3 item in GenAdj.OccupiedRect(loc, rot, checkingDef.Size)) if (map.roofGrid.Roofed(item)) return ...;
        return true;
    }
}
```

Aerocraft 父模板挂了它（`1.7/Defs/SomeThing/Base.xml` 的 `placeWorkers` = `PlaceWorker_DrawAerocraft` + `PlaceWorker_NotUnderRoof`），并声明 `terrainAffordanceNeeded=Light`。

RimLayers 地形：`RimLayers_Structure.xml:14`（结构楼板）、`RimLayers_ShipFloor.xml:8`（飞船甲板）、`RimLayers_VehicleDeck.xml:8`（载具甲板）都有 `<li>Light</li>`；`RimLayers_Terrains.xml:26` 的 `RL_OpenAir` **只有 `RL_FloorSupport`**；`RL_Isolation` 的 `<affordances />` 为空。
→ 手感结果：上层空腔（`RL_OpenAir`，一层的天花板）**能放**、隔离带不能放；下层有屋顶的地方不能放（与原生一致）。这与 RimLayers 自己的 `SkyfallService.CanArrive` 口径（`HasSurface` **且** 上层无屋顶）不一致。

### 方案

Prefix `PlaceWorker_NotUnderRoof.AllowsPlacing`：只对 `checkingDef.thingClass` 派生自 `Building_Aerocraft_Base` 的生效；`AcceptanceReport` 是 struct（`Verse.AcceptanceReport`），Prefix 用 `ref AcceptanceReport __result` 写值并 `return false`：

```csharp
[HarmonyPatch(typeof(PlaceWorker_NotUnderRoof), nameof(PlaceWorker_NotUnderRoof.AllowsPlacing))]
internal static class AerocraftPlacePatch
{
    [HarmonyPrefix]
    private static bool Prefix(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, ref AcceptanceReport __result)
    {
        if (!AerocraftCompatibility.IsAircraft(checkingDef.thingClass)) return true;
        var owner = LayerMapComponent.For(map);
        if (owner == null || !owner.TryDecode(loc, out var at)) { __result = "RL_NoSurface".Translate(); return false; }
        __result = AerocraftLanding.CanLand(owner, loc, checkingDef.Size)
            ? AcceptanceReport.WasAccepted
            : new AcceptanceReport("MustPlaceUnroofed".Translate());
        return false;
    }
}
```

`CanLand` 已经包含 `HasSurface` + 垂直柱通畅，正好同时修掉“上层空腔可放”和“必须无屋顶”的两处口径差异；`terrainAffordanceNeeded=Light` 由 RimLayers 的 `Structure.Validate`/地形 affordance 自然处理（`RL_OpenAir` 没有 Light，本来就会拒）。

### 需要游戏内复核的点

- `GenConstruct.CanPlaceBlueprintAt` 已被 RimLayers 的 `Core/IsolationPatches.cs` `PlacementBoundaryPatch` 接管（`component.Structure.Validate(entDef,center,rot,planning:true)`），两个 Prefix 的执行顺序需要用 `HarmonyPriority` 明确（建议 RimLayers 的保持默认，本 patch 用 `Priority.Last`，让 RimLayers 先做地形/结构判定）。
- 已经有飞行器停在下层时，在上层同列再放一架是否会被 `PlaceWorker_InBaseThing`（Aerocraft 自己的重叠检查，`PlaceWorker_InBaseThing.cs:33` 用 `def.Size.z/2`）拦住。

---

## 6. 剔除、雾、以及为什么不能只改 `DrawPos`

### 证据

`Verse.DynamicDrawManager.ComputeCulledThings(NativeArray<ThingCullDetails>)`：

```csharp
ThingCullDetails d = new ThingCullDetails {
    cell = (thing is Pawn) ? thing.DrawPos.ToIntVec3() : thing.Position,   // 非 Pawn 用存储格
    coarseBounds = thing.OccupiedDrawRect(), seeThroughFog = thing.def.seeThroughFog,
    hideAtSnowOrSandDepth = ..., hasSunShadows = ..., drawOffscreen = thing.def.drawOffscreen };
// CullJob：indices.Contains(cell) / fogGrid.IsSet(cell) / depthGrid[cell] 全部按这个 cell
```

Aerocraft 父模板 `seeThroughFog=true`、`drawOffscreen=true` → 雾与视野裁剪对它基本放行，但 `cellIndices` / `depthGrid` / `fogGrid` 的索引仍是**存储格**。RimLayers 的 `Presentation/ProjectileCullPatch.cs` 就是为这个问题给 `Projectile` 打的 transpiler（把 `Thing.Position` getter 换成 `DrawCell`、`GenAdj.OccupiedDrawRect` 换成 `DrawBounds`，各需恰好 1 处否则抛 `$"RimLayers projectile cull anchors changed: {cells}/{rects}"`）。

### 方案

不新增 cull patch。#1 的“坐标驻留在 band 内”让 `Position` 与 `DrawPos` 的 z 天然一致，`ComputeCulledThings`、`fogGrid`、`thingGrid`、`RoofGrid`、`Area.Home` 全部自然正确。

这也是排除“只 Postfix `DrawPos`”方案的决定性理由：那样必须再补 cull、fog、`thingGrid`、`Home`、`GenClosest` 五条链，工作量与风险都远高于 #1。

另外两个 **不需要改**的绘制点，记录下来避免以后误伤：

- `Building_Aerocraft_Base.Draw_BaseThing`：`drawPos.y = def.Altitude + Draw_Base_ExtraAltitudeLayerNum`，x/z 来自 `DrawPos`。
- `Comp_BaseDraw_DecorationAndShadow.Draw_Decoration` / `Draw_Shadow`：`v.y = Altitudes.AltitudeFor(Props.Draw_Decoration_BaseAltitude) + extra`，x/z 来自 `GetVector3_By_AngleFlat(...DrawPos.../...Shadow_Pos...)`。`Shadow_Pos` 是 `Vector3` 且被 Scribe（`Building_Aerocraft_Base.cs:869` 写入），所以它也必须参与 #1 的 rebase（见 §1 代码）。

---

## 7. 跨图抵达

### 证据

`WorldObject_CrossMapThing_Flying.cs:505-530`：

```csharp
int num = 15;
var list = new List<IntVec3>{
    (0,0,0)+(15,0,15), (TargetMap.Size.x,0,0)+(-15,0,15),
    (0,0,TargetMap.Size.z)+(15,0,-15), (TargetMap.Size.x,0,TargetMap.Size.z)+(-15,0,-15) };
IntVec3 val2 = list.RandomElement();
...
GenSpawn.Spawn(LinkToAerocraft, val2, TargetMap, WipeMode.Vanish);
linkToAerocraft.Set_TargetVPos(TargetMap.Center.ToVector3Shifted());
if (!LinkToAerocraft.Is_Flying) LinkToAerocraft.Set_Flying();
CameraJumper.TryJump(val2, TargetMap, MovementMode.Cut); Find.Selector.Select(LinkToAerocraft, true, true);
```

- `TargetMap.Size.z == Layout.StorageHeight` → 第 1、4 个候选在 band 0，第 2、3 个在**最上层 band**（z ≈ `StorageHeight-15`）。
- `Verse.Map.Center => new IntVec3(Size.x/2, 0, Size.z/2)` → `StorageHeight/2`，落在中间某个 band（例：`Height=250 → Stride=255, LayerCount=3 → StorageHeight=765`，center.z=382 → band 1）。
- `CameraJumper.TryJump(val2, ...)` 会被 RimLayers 的 `JumpLayerPatch`（`Presentation/LayerView.cs`）自动切到 `val2` 所在层，所以“跳过去看到的是对的层”，但那个层是随机的。

`Comp_CanCrossMap.cs:284`：离场 `GenSpawn.Spawn(skyfaller_Aerocraft_Leaving, parent.Position, map, WipeMode.Vanish)` 用存储 `Position`；该 Skyfaller 是 `reversed=true`（`FixedWingCraft.xml:289/761`），`SkyfallService.Incoming` 对 reversed 返回 false → **不会被 RimLayers 的重力落点逻辑接管**，无需处理。

### 方案

Postfix `WorldObject_CrossMapThing_Flying.Arrived`：spawn 之后做一次“按列选层”：

```csharp
int layer = owner.Layout!.OrderedLayers.Reverse()
    .Where(l => owner.Elevation(l) >= 0)
    .FirstOrDefault(l => owner.Structure.HasSurface(owner.ToStorage(new LayerCell(l, at.X, at.Z))), 0);
```

与 `SkyfallService.Trace` 完全同构（自上而下第一个有地板的层），然后：

- `Position` 重设为 `owner.ToStorage(new LayerCell(layer, at.X, at.Z))`（无地板时退回 `val2` 原值）；
- `RealCurrentPosition` 设为 `(Position.x+0.5f, Position.z+0.5f)`（与 `Building_Aerocraft_Base.SpawnSetup:203` 一致）；
- `TargetVPos` 重投影到同一 band（把它的 z 换成 `layer*Stride + 逻辑 z`）；
- `ProxyLayer = layer; HomeLayer = layer`。

更彻底的做法是 Prefix 改 `val2`，但那需要自己复刻四个角落的语义；Postfix 更省。

### 需要游戏内复核的点

- 抵达时目标地图如果是**尚未生成**的（`GetOrGenerateMapUtility.GetOrGenerateMap`），`LayerMapComponent.Layout` 可能还没建好 → Postfix 需要 `owner?.Managed == true` 守卫，否则跳过（此时飞行器留在 `val2`，下一次 `Change_Position` 会根据 band 0 的逻辑 z 把它放好）。

---

## 8. 边界与扩层窗口

### 证据

- `Verse.GenGrid.CloseToEdge(map, 10)`：`c.z >= size.z - edgeDist`，`size.z == StorageHeight`。
- `Change_Position` 的四个边界比较全部用 `Map.Size`（`:1141`）。
- `Generation/LayerGeneration.cs`：`ConstructComponentsPatch.Postfix` 里 `info.Size = GroundSize`，`FinalizeMapPatch`（`[HarmonyPriority(Priority.First)]`）恢复 `info.Size = FullSize`；`Generation/LiveMapExpansion.cs` 的 `EnsureElevation` 在成功时 `map.info.Size = full`、失败回滚 `map.info.Size = oldSize`。
- `Recovery/RecoveryCompatibility.cs`：`TransactionActive => GenerationScope.Active != null || MapGenerator.mapBeingGenerated != null || ShipLayers.Capture != null || ShipLayers.Placing != null || SkyPayload.Current != null || WorldComponent_GravshipController.CutsceneInProgress`；`DeferFor(thing, ticks)` 是“暂停第三方驱动”的标准手段（`SteelFlightAdapter.BeforeTick` 每 tick 调 `DeferFor(pawn, 3)`）。

### 方案

所有新增服务入口（换层、落点解析、加油判定、抵达落点）统一加：

```csharp
if (RecoveryCompatibility.TransactionActive || !owner.Managed) { RecoveryCompatibility.DeferFor(t, 30); return; }
```

并且把带内 z 的边界改成 **band 内**：

```csharp
// val.z 已在 proxyLayer band 内；只检查 band 内下标
int localZ = val.z - f.ProxyLayer * owner.Layout!.Stride;
if (val.x < 1 + def.size.x || val.x > owner.Layout.Width - def.size.x - 1 ||
    localZ   < 1 + def.size.z || localZ   > owner.Layout.Height - def.size.z - 1) return;
```

最小实现可以只 Prefix `Change_Position` 的这一个判断（原方法体其余部分——禁建区折返、`BestTransmitterForConnector`——保持原样），或者干脆不 patch，接受“隔离带可飞、最上层可越界”，把它记为已知限制（`Change_Position` 的早退会让机体原地不动，不会崩）。

### 需要游戏内复核的点

- `GenGrid.InNoBuildEdgeArea` 的折返（`If_CheckMapBoundary` 默认 true，`MYDE_AerocraftFramework_Setting.cs:7`）在 band ≥ 1 时不会误触发（`c.z >= 10` 恒真），但在 band 0 的低 10 行会——与原生一致，不改。
- 扩层期间 `map.info.Size` 短暂为 `GroundSize` 时的 `Change_Position` 早退是否会造成飞行器“卡住一帧”。

---

## 9. 加载顺序、绑定与失败策略

### 挂点

`RimLayersMod` ctor 的顺序（`Source/RimLayers/RimLayersMod.cs`）已经是现成的三段式：

1. `LongEventHandler.ExecuteWhenFinished(SettingsDefinitions.Apply)`
2. `new Harmony(HarmonyId)`（`HarmonyId = "rimlayers.core"`）
3. `Compatibility.VVRaceCompatibility.Install()` —— 注释明确“可选适配器注册必须在 patch 循环之前”
4. 反射遍历程序集里所有带 `[HarmonyPatch]` 的类型逐个 `CreateClassProcessor(type).Patch()`，每个类型独立 try/catch（失败 `Log.Error($"[RimLayers] Patch class {type.Name} failed and was skipped; ...")`）
5. `Compatibility.VanillaGravshipCompatibility.Install(harmony)` / `M1Integration.Configure()` / `V1Integration.Configure()`

Aerocraft 适配器放在第 3 步旁边：`Compatibility.AerocraftCompatibility.Install()`。它内部：

- `ModsConfig.IsActive("myde.aerocraftframework")` / `LoadedModManager.RunningModsListForReading` 判存在（对齐 `VehicleCompatibility.Bind` 的 `Enabled(id)` 写法与 `VVRaceCompatibility.PackageId` 常量写法）；
- 用**惰性 Resolve** 拿 Aerocraft 类型（`AccessTools.TypeByName("MYDE_AerocraftFramework.Building_Aerocraft_AsBaseThing")` 等），**不要在 `[StaticConstructorOnStartup]` 静态字段里查**——`VVRaceCompatibility.cs` 的注释已经点明：“a Mod constructor runs before other mods' assemblies are reachable through TypeByName”；
- 字段/方法绑定复用 `Vehicles/VehicleReflection.cs` 的表达式编译（`Call<T>(MethodInfo)` / `Field<T>(Type,name)`），**不要每 tick 反射**；
- 绑定失败按 `VVRaceCompatibility` 的口径降级：`Log.Warning("[RimLayers] Aerocraft Framework layout changed; layered support disabled until the adapter is updated.")`，并让所有 patch 立即 `return true`（完全交回原生，等价于没装兼容）。不要用 `VanillaGravshipCompatibility.Required()` 那种“失败即抛”的风格——Aerocraft 是第三方可选包，抛异常会连累整个 RimLayers。

### `About/About.xml`

`loadAfter` 需补 `<li>MYDE.AerocraftFramework</li>`（现有：`vanillaexpanded.gravship` / `vanillaexpanded.gravship2` / `Baal.VehicleFramework` / `SmashPhil.VehicleFramework` / `brrainz.harmony`），保证 RimLayers 的 patch 在 Aerocraft 的 patch **之后**打上；否则 #4 需要的 `Priority.First` 抢先关系虽然仍成立（Harmony 按 priority 而非加载顺序），但 #1/#5 的判定会在 Aerocraft 自身状态未初始化时执行。

`modDependencies` 不要加 Aerocraft（可选兼容，不能强制）。

### 需要的字段/方法清单（绑定目标）

| 类型 | 成员 | 用途 |
|---|---|---|
| `Building_Aerocraft_Base` | `Vector2 RealCurrentPosition`、`override Vector3 DrawPos`、`CompRefuelable RefuelableComp` | #1 坐标、#4 加油 |
| `Building_Aerocraft_AsBaseThing` | `Vector3 TargetVPos`、`TakeOffVPos_A`、`TakeOffVPos_G`、`Shadow_Pos`（在 Base 上）、`bool If_UpOrDown`、`int Move_WarmUpTick/Move_WarmUpTickMax`、`bool Is_Flying/Is_Static`、`int ChangPosTick/Max`、`Set_TargetVPos`、`Set_GoBack`、`Change_Up/Change_Down` | #1 #2 #3 #6 |
| `Building_Aerocraft_AsBaseThing.Change_Position` | 方法（Prefix/Postfix） | #1 #8 |
| `Building_Aerocraft_AsBaseThing.Change_Down_GlidingTakeOff` | 方法 | #3 |
| `WorldObject_CrossMapThing_Flying.Arrived` | 方法（Postfix） | #7 |
| `MYDE_AerocraftFramework_Setting` | 静态字段（只读，用于诊断） | 诊断输出 |

---

## 10. 实施顺序与验收

按“每步都能单独跑起来”排序：

1. **S**：`AerocraftCompatibility`（绑定 + 开关 + 诊断行）+ `AerocraftLayers`（MapComponent + Scribe + `Rebase` 原语）。此时**行为完全不变**（没有任何 patch 生效），可先合入。
2. **S**：#5 放置判定（纯拒绝逻辑，最容易验证，不会污染存档）。
3. **M**：#1 坐标契约（起飞/落地时 rebase + `Change_Position` 的 band 内边界）。这是核心，其余项依赖它。
4. **M**：#3 落点合法性（`CanLand` 接进 5 处扫描 + `Targeter` validator）。
5. **S**：#4 加油 Prefix（`Priority.First` 抢先 Aerocraft 的 Home/Is_Static 限制）。
6. **S**：#6 返航点 rebase（若 #1 里已经一起平移，则只是验证项）。
7. **S**：#7 跨图抵达落点。
8. **S**：#9 扩层窗口闸门。
9. 文档：新增 `Docs/AEROCRAFT-COMPATIBILITY.md`（对齐 `Docs/VGE-COMPATIBILITY.md` 的“已支持 / 已知限制 / 诊断”格式），并在 `Docs/DEVELOPMENT.md`、`Docs/PLAN.md`、`Docs/DECKS-AND-ELEVATORS.md:117` 的“具体第三方框架仍留待适配”列表里把 Aerocraft 移入已适配。

验收矩阵（沿用现有脚本，`Tests/` 下需先关闭其它 RimWorld 实例）：

| 场景 | 观测点 | 脚本 |
|---|---|---|
| 地表放置 → 起飞 → 在上层 `RL_StructuralFloor` 降落 | `Position` 落在目标 band；`AerocraftLayers.ProxyLayer` 一致；无抖动 | `Tests/Run-VehicleGround.ps1` |
| 上层停放 → WorkGiver 加油 | 生成 `RearmTurret` Job；`CanRefuelThing` 为 true | `Tests/Run-VehicleBindings.ps1` |
| 低油滑翔降落（固定翼） | 落点在**同一层**有地板处，不穿上层楼板 | `Tests/Run-VehicleRegression.ps1` |
| 存读档往返 | `ProxyLayer`/`RealCurrentPosition` 一致；`TakeOffVPos_*` 可返航 | `Tests/Run-V1.ps1` |
| 扩层窗口内起降 | 不崩、不写非法坐标 | `Tests/Run-VehicleRegression.ps1` |
| 跨图抵达 | 落到有地板的层，而不是随机 band | 手动 |
| 无 Aerocraft 环境 | `RimLayers` 正常加载、无 Harmony 报错、诊断为空 | `Tests/Run-V1.ps1` |

---

## 11. 已知限制（写进用户文档）

- 同一 tick 内不做跨多层瞬移：换层一次一个 band，视觉上表现为“垂直升降后换层”，不是连续 3D 飞行。
- `Highlight`/`GenDraw.DrawRadiusRing(this.Position, range)`（`Building_Aerocraft_Base.cs:554`）画在当前 band，切层后环会跟着层走 —— 这是正确的，但与“只看地表”的原版习惯不同。
- `Area`/`Zone`/`Plan` 没有分层语义；上层停机坪的 Home 判定靠“按列”近似。若是多列不规则停机坪，需要玩家在任意一层把该列圈进 Home。
- `Comp_AutoFindTarget`（`FindTarget()` 里比较 `map.Size.z`）在受管地图上 `Size.z == StorageHeight`，因此**不会**误判上层目标（已核实），无需修改；但它的目标判定仍按存储格距离，跨层目标会被当成“很远”，属可接受行为。
- 原子加油（`atomicFueling=true`）跨层仍不支持（Aerocraft 默认不开）。
- 三个 `Move_ByWrap_*` 瞬移（`AsBaseThing.cs:1326/1374/1426`）在默认配置下**不执行**（`If_CanWrap` 默认 false，`CompProperties_MoveToTargetAndHover.cs:51`，XML 全库无此键），因此不在本次范围内；若以后有人打开它，这三处需要一起 rebase。

---

## 12. 实现偏差（实现时推翻的设计）

以下是实现阶段发现方案有误、或者代价高于收益的地方。维护时**不要**按前文改回去。

### 12.1 `Rebase` 只平移 `RealCurrentPosition` 与 `Shadow_Pos`

前文 §1 的代码示例把 `TargetVPos`、`TakeOffVPos_A/G` 一起 `+= shift`。这是错的，三个点分属三种坐标系：

| 字段 | 真实语义 | 是否属于“当前层轨道” |
|---|---|---|
| `RealCurrentPosition` | 机体当前连续位置（存储坐标） | **是**，要平移 |
| `Shadow_Pos` | 阴影投影点，由 `DrawPos` 派生 | **是**，要平移 |
| `TargetVPos` | **目标层**的落点。它正是 `TargetBand()` 推导 band 的来源 | **否**。平移它会把目的地再推一个 band |
| `TakeOffVPos_A` / `_G` | **起飞层**的返航点，`Set_GoBack` 原样复用 | **否**。平移它会让“返回基地”变成原地不动 |

`Set_GoBack` 的行为因此变成：把 `TargetVPos` 设回仍留在旧 band 的 `TakeOffVPos_*`，
下一次 `Set_TargetVPos` 的 Postfix 看到目标 band ≠ 当前 band，自动 rebase 回 home。返航是通的。

### 12.2 落点合法性只挂一个 choke point，不挂 5 处扫描

前文 §3 说 Prefix 掉 5 处落点扫描循环。实际做法是只 Prefix
`Set_TargetVPos_Down_GlidingTakeOff`（`AerocraftForcedDescentPatch`）——5 处扫描（低油、低电、EMP、
撞击坠落、自毁）**全部**汇聚到这一个方法，且它是 `public void Set_TargetVPos_Down_GlidingTakeOff(Vector3)`，
签名稳定。在入口处把不合法落点替换成同层最近合法格，一处覆盖五个调用点，
也不用碰 `GetVector3_By_AngleFlat` 与 `isThickRoof` 那套内联算术。

### 12.3 放弃 `GenGrid.InBounds` 后补方案

前文 §3 第 3 点打算 Postfix `GenGrid.InBounds` 给滑翔选点加红圈。实现时**放弃**了：
`GenGrid.InBounds` 是全局热路径（每个 UI 帧、每个 AI 查询都会走），而原生的
`TargetingParameters.validator` 是编译生成的闭包，无法定点 Patch。为了一个选点光标去 Postfix 全局函数
风险远大于收益。改为在消费点（12.2）兜底：玩家点了不合法落点，飞行器自己改到最近合法格并打一条
`forced-descent-redirected` 日志。**表现是“点了不会拒绝，但会落到合理的格子上”**，不是红圈。

### 12.4 垂直起飞不换层

`Change_Up` 既被垂直起飞 gizmo 调用，也被滑翔起飞尾部调用。垂直起飞时 `TargetVPos` 可能还留着
上一次命令的旧值，照着它换层会让“原地起飞”突然跨层。因此 `TargetBand()` 先读
`If_TuringByGlidingTakeOffOrDownNow`：为 false（垂直起飞）就直接返回当前 band，不换层。
只有滑翔命令（先算跑道端点、再调 `Change_Up`）才按 `TargetVPos` 换层。

### 12.5 `Change_Position` 的闸门只对空中生效

前文 §8 说命中 `TransactionActive` 就整帧跳过 `Change_Position`。实现改了：**落地状态必须继续跑原生方法体**，
因为该方法末尾的 `BestTransmitterForConnector` / `Notify_ConnectorWantsConnect` 是电力重连的唯一入口，
跳过它会让停在地上的飞行器永久脱离电网。现在只在 `!IsStatic` 时 `DeferFor`。

### 12.6 未实现的两项

- §9 的“诊断开关与 `Docs/AEROCRAFT-COMPATIBILITY.md` 的自动化验证矩阵”只做到日志埋点（`AircraftTrace`，上限 40 条），
  没有新增测试场景。原因见下：本机没有启用 Aerocraft，写不出能跑的验收。
- 前文 §0 表 #9（扩层窗口）与 #10（`Change_Position` 写入代价）只加了闸门与非回归观测，没有独立场景。
