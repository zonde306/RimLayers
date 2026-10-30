# Smart Combat（Immersive Warfare）兼容

状态：**已实现（“扩层时丢弃路况网格、由 Smart Combat 自己重建”模型）。** 编译 0 警告 / 0 错误；
游戏内自动化验收 23 断言 0 失败（`Tests/Artifacts/v1-smartcombat-20261030-171022`）。

验收命令（游戏根目录，需先能编译整个 `RimLayers.csproj`）：

```powershell
& './Mods/RimLayers/Tests/Run-V1.ps1' -Scenario smartcombat -TimeoutSeconds 420
# 整个工程暂时编译不过时（例如别的会话正在改别的文件），加 -SkipBuild 复用
# Assemblies/RimLayers.dll，只跳过 `dotnet build` 那一步：
& './Mods/RimLayers/Tests/Run-V1.ps1' -Scenario smartcombat -SkipBuild -TimeoutSeconds 420
```

`-Scenario smartcombat` 只在这个独立测试 profile 里启用 `lordfelix.SmartCombat`（也可单独用
`-SmartCombat` 把它加到别的场景），不读取也不修改玩家配置。

基准：RimWorld 1.7 本机程序集 + `Immersive Warfare` 1.7 程序集
（`294100/3815290781/1.7/Assemblies/SmartCombat.dll`，packageId `lordfelix.SmartCombat`）。

---

## 为什么需要适配

Smart Combat 的 `SmartCombat.OrdinaryRoadMap : MapComponent` 维护一张“离路附加代价”网格，
用来让殖民者走路时偏离道路多花 6 点路径代价（`RoadPreferencePolicy.OffRoadPenalty = 6`）。它是
**按格的原生缓冲**：

```csharp
private NativeArray<ushort> offsets;   // 长度 == map.cellIndices.NumGridCells
private bool[] roadCells;              // 每条路格的标记
private int roadCount;                 // roadCells 中 true 的个数
private readonly HashSet<int> dirty;   // 待增量重算的索引
```

关键点：**它是懒分配的**。`offsets` 只有在一个殖民者开始做“普通工作”
（`OrdinaryRoadPreference.Applies`：非征召、非战斗、非精神崩溃，且 `CurJob.def.defName` 命中
`Goto/HaulToCell/DoBill/ConstructDeliverResources/Clean/Sow/Harvest/…` 白名单）时，才会在
`PathFinder.ComputeWorkThisTick` 的 `OrdinaryRoadBatchPatch` 里由 `PrepareSafeBatch` 建出来。

于是在分层地图上出现了一个**只有玩过一会儿才会踩到**的硬失败：

```
[RimLayers] Stair-triggered map expansion failed on map 0:
System.NotSupportedException: Unmapped native per-cell buffer: SmartCombat.OrdinaryRoadMap.offsets.
Expansion was not applied.
```

`LiveMapExpansion.PrepareManaged` 遍历 `map.components` 的字段时，看到的是一个
`Unity.Collections` 命名空间、长度恰好等于旧格数（`oldCount`）的 `NativeArray<ushort>`，
但没有人声明过它 —— 于是它按设计**安全拒绝**整次扩层并整体回滚。楼梯因此永远建不出楼层，
而且 `StairLayerBootstrap.Prepare` 会把该格记进 `BlockedStairs`，之后连自动重试都停了。

这是“声明缺失”，不是数值错误：`offsets` 的内容是地形、屋顶与工作状态的纯派生值。

## 适配契约

> **不迁移前缀，直接在事务内丢弃；由 Smart Combat 自己的下一次批量重建。**

这条规则不是省事，而是唯一正确：

1. **Smart Combat 已经内置了这条退化路径。** `PrepareSafeBatch` 的第一行就是
   `if (offsets.IsCreated && offsets.Length != numGridCells) DisposeGrid();`，
   然后 `if (!offsets.IsCreated)` 全量 `UpdateCell(i)` 并置 `changed = true`，
   上层 `ApplySafeBatch` 见到 `changed` 就反射调 `PathFinder.RecycleGridJobData` 丢掉原生路径网格缓存。
   也就是说：**只要扩层把它变回“未创建”，下一次批量就会自己长出正确的网格。**
2. **迁移前缀会留下错值。** `LiveMapExpansion` 只追加坐标行、宽度不变，所以
   `CellIndices` 索引的缓冲确实是前缀；但 `offsets` 里存的是**旧地图**的附加代价。
   新增的存储带是空地形/隔离行，本来就必须全量重算，保留旧前缀没有任何价值。
3. **迁移前缀会让 `roadCount` 失配。** `roadCount` 是与 `roadCells` 一起维护的增量计数
   （`roadCount += preferred ? 1 : -1`），而 `PrepareSafeBatch` 只有在 `roadCount > 0` 时才返回
   customizer。前缀拷贝后新行没被 `UpdateCell` 过，`roadCount` 与真实路格数就不一致了。
4. **不能只是“检查一下 `IsCreated` 然后留着旧数组”。** 长度会与新的
   `NumGridCells` 不同，任何按新尺寸 `CellToIndex` 写入（`MarkDirty`）都会越界。

## 实现的挂点

只有一个文件、一个挂点：

| 文件 | 内容 |
|---|---|
| `Source/RimLayers/Compatibility/SmartCombatCompatibility.cs` | 惰性反射绑定、`RegisterBeforeResize` 注册、`SmartCombatBootstrap` 重试 |

| 挂点 | 目标 | 作用 |
|---|---|---|
| `LayerCellBufferCompatibility.RegisterBeforeResize` | `SmartCombat.OrdinaryRoadMap` | 在 `PrepareManaged` 扫描字段**之前**调用 `DisposeGrid()`，让未登记的原生网格不再存在 |

`DisposeGrid()` 在这里是安全的，三条理由都可核对：

- 它不需要 `map`，只靠 `NativeArray.IsCreated` 判断，未创建时幂等；
- `EnsureElevation` 在碰任何缓冲之前已经
  `AccessTools.Method(typeof(PathFinder),"ForceCompleteScheduledJobs").Invoke(oldFinder,null)`，
  所以扩层时没有在跑的 path job 持有原生视图；
- 它同时清掉 `roadCells` / `current` / `roadCount` / `dirty` / `terrainTraits`，
  回到“刚建地图”的干净状态。

**没有**登记 `RegisterNativeCellBuffer`。这不是遗漏：登记它意味着“请替我按前缀扩容”，
而上面的第 2、3 条说明那样做会留下错值。副作用是拒绝归因少了一级 —— 但
`ModOrigin.NameOf(Type)` 在 `DeclaringMod` 为空时会回退到 `PackOf(type.Assembly)`，
按 `LoadedModManager.RunningModsListForReading[].assemblies.loadedAssemblies` 反查，
所以万一别的 mod 也抛出未登记原生缓冲，弹窗里仍然写得出 `Immersive Warfare [lordfelix.SmartCombat]`。

### 核心侧的一处配套修正（不是本 mod 专属）

`NativeArray<T>` 被 `Dispose()` 之后，字段仍是那个 struct，`IsCreated` 变 `false`
但 **`Length` 保留旧值**。`LiveMapExpansion.PrepareManaged` 与 `GridCapacityRepair.Fix` 原来只看
`Length`，于是“适配器刚把缓冲丢掉”反而会被当成“一个长度恰好是旧格数的未登记缓冲”，继续抛
`Unmapped native per-cell buffer`。两处都改成把 `IsCreated` 当作存活判据：

```csharp
// LiveMapExpansion.cs, 未登记原生缓冲分支
bool live = value.GetType().GetProperty("IsCreated")?.GetValue(value) is not bool created || created;
var length = live ? value.GetType().GetProperty("Length")?.GetValue(value) : null;
if (length is int n && n >= oldCount && n <= oldCount + 63) throw new NotSupportedException(...);
```

已释放 / 从未分配的原生缓冲不持有任何格，既没有前缀可搬，也不可能被追加带索引到越界写入，
把它当作“不存在”是语义正确的。`NativeBitArray` / `UnsafeBitArray` / `Vector` 这类容器本来就有
`IsCreated`；没有这个属性的类型走原来的 `Length` 路径，行为不变。

## 降级策略

`Install()` 先判断 Smart Combat 是否真的在加载：

```csharp
if (ModLister.GetActiveModWithIdentifier(PackageId, false) == null)
    return LoadedModManager.RunningModsListForReading
        .Any(pack => pack.PackageId.Equals(PackageId, StringComparison.OrdinalIgnoreCase));
```

不能只查 `ModLister`：1.7 的 `ModsConfig.RecacheActiveMods()` 把 `ModsConfig.xml` 里的字符串
**原样**塞进 `activeModsHashSet`，而 `ModsConfig.IsActive(id)` 是拿 `id.ToLower()` 去查。
所以如果玩家/脚本把 packageId 写成 `<li>lordfelix.SmartCombat</li>`（保留 `About.xml` 的驼峰），
DLL 照样加载、`AccessTools.TypeByName` 也找得到类型，但 `ModMetaData.Active` 恒为 `false`、
`GetActiveModWithIdentifier` 恒返回 `null`，适配器就会静默不装。`AerocraftCompatibility.Active()`
早就是同一个双路判定（它的 `PackageId` 常量因此写全小写）；`RunningModsListForReading` 与大小写无关，
是可靠的兜底。

没装就什么都不做。装了但 `offsets` 字段或 `DisposeGrid()` 方法找不到时：

```
[RimLayers] Smart Combat OrdinaryRoadMap layout changed; layered map expansion stays
disabled on those maps until the adapter is updated.
```

—— 即恢复原来那种“安全拒绝 + 可读归因”的行为，而不是去丢一个自己已经认不出的缓冲。
Smart Combat 是可选第三方包，**不允许**它的适配失败连累 RimLayers 的其它部分：
`Install()` 全程不抛，`SmartCombatBootstrap` 外面还有一层 `try/catch → Log.Error`。

`Install()` 不是一次性守卫：`Mod` 构造时其它 mod 的程序集还没加载完，
`AccessTools.TypeByName` 看不到 Smart Combat 的类型，所以失败的首次尝试必须由
`[StaticConstructorOnStartup] SmartCombatBootstrap` 重试。注册本身按 `Type` 幂等，重复调用无害。

`About/About.xml` 的 `loadAfter` 已加入 `lordfelix.SmartCombat`；`modDependencies` 未加（可选兼容）。

## 已实测

场景 `smartcombat`（`Diagnostics/V1SmartCombat.cs`）在一个**未扩容的外来单层地图**上建楼梯，
先用 Smart Combat 自己的 `PrepareSafeBatch` 把网格懒分配出来，再让完成的楼梯帧跑真实的扩层路径。

| 组 | 断言 |
|---|---|
| 绑定 | 适配器已安装；`OrdinaryRoadMap` 已挂在夹具地图上 |
| 前置状态 | 未扩层；网格**未创建**（`IsCreated == false`）；未扩层的楼梯被接受 |
| 懒分配 | 让 Smart Combat 自己跑 `PrepareSafeBatch`：网格被创建，且长度 == `NumGridCells` |
| **扩层不再被拦** | 完成楼梯 → 第二层楼层出现，`NumGridCells` 变大，`BlockedStairs` 为空（没有“扩层被拒”记录） |
| **网格被正确丢弃** | 扩层后 `IsCreated == false`，且**不是**那种“长度不对但仍活着”的坏缓冲 |
| 上层可用 | 楼梯的落点在新的第 1 层有地板 |
| **自愈** | 下一次 `PrepareSafeBatch` 重建网格：长度 == 新 `NumGridCells`、`changed == true`（真的重算了，不是空白分配）、代价值不全为 0 |
| 二次扩层 | 网格再次活着 → `EnsureElevation(map, 2)` 加第三层 → 网格再次被丢弃 |

### 验收脚本的注意事项（踩过的坑）

- **不要手写 `offsets` 私有字段**。反射塞一个 `NativeArray<ushort>` 进去看起来更直接，
  但那样 `roadCells` / `roadCount` / `dirty` 仍然是空的，`PrepareSafeBatch` 返回 null 且不算“真的分配过”。
  测试调用 Smart Combat 自己的 `PrepareSafeBatch`，走的就是殖民者开始普通工作的同一条路径。
- `NativeArray<T>` 是 struct，泛型约束不能从 `object` 直接取，所以断言用反射读
  `IsCreated` / `Length` / `Item` 索引器（`TryReadNative` / `ReadNativeUShorts`）。
- 扩层是**同步**完成的，所以“网格已被丢弃”可以在递交楼梯后立刻检查，不需要等 tick。
- 第二次扩层用 `LiveMapExpansion.EnsureElevation(map, 2)` 直接驱动（与 Dubs 兼容场景同法），
  因为再摆一根楼梯不会触发第二次扩层。

## 已知限制

- 网格被丢弃到下一次批量重建之间，地图上**没有**离路附加代价 —— 也就是那段时间殖民者会像没装
  Smart Combat 一样走直线。这在语义上是正确的（旧代价已失效），且下一次 `ComputeWorkThisTick`
  就会重建；但如果那段时间没有殖民者行走，网格会一直保持未创建状态，属于纯惰性。
- 扩层后**不**保留任何旧代价。玩家的观感是“扩完层的第一个寻路会有一点额外开销”，
  因为要全量 `UpdateCell` 一遍 `NumGridCells` 格。
- 只覆盖 `OrdinaryRoadMap.offsets`。如果 Smart Combat 以后再加别的按格原生缓冲
  （例如弹道、掩护网格），必须照此文件新增声明，否则会重新变成“安全拒绝”。
  `GridCapacityRepair` 在生成期也会为这类字段打一条 `Unresized native per-cell buffer ...`
  警告，那是发现新缓冲最快的入口。
- 用 `ModLister.GetActiveModWithIdentifier` 判定“是否装了 Smart Combat”，所以**只认已启用的包**。
  装了但未启用时适配器不注册，也不会误报。

## 复核清单（拿到启用 Smart Combat 的存档后）

1. 玩到一个殖民者在走普通工作（搬东西、装修、清洁）→ 日志出现
   `[RimLayers] Smart Combat ordinary-road cost grid is released before layered map expansion.`
2. 建楼梯 → 楼上出现，日志**没有** `Stair-triggered map expansion failed` 与
   `Unmapped native per-cell buffer`。
3. 扩层后的第一支殖民地队伍照常寻路，没有一条路走进隔离保留行。
4. 连建第二段楼梯（再加一层）仍然成功。
5. 存读档往返：楼层、楼梯与路况系统都还在，殖民者仍能跨层行走。
6. 卸载 Smart Combat 后 RimLayers 正常加载，日志无适配器行，无 Harmony 报错。

日志入口：`[RimLayers] Smart Combat ordinary-road cost grid is released before layered map expansion.`
（或 `Log.Warning` 的降级说明）。
