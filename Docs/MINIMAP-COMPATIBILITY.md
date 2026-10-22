# Dubs Mint Minimap 兼容

启用 `dubwise.dubsmintminimap` 时自动生效；未安装时不安装任何补丁，也不产生程序集依赖。改动只在 RimLayers 内，不修改 minimap 程序集、`Map.Size`、原版 `Data/` 或玩家存档。

## 冲突原因

Dubs Mint Minimap 把 `Map.Size` 与 `Map.AllCells` 当作“整张可玩地图”。RimLayers 把每一层放进同一坐标带的独立存储行（`LayerLayout.Stride` 为 17 的倍数，`StorageHeight = Stride * LayerCount`），`Map.Size.z` 因此等于存储高度而不是地面高度。120×120、3 层、stride 136 的地图实际是 `(120, 1, 408)`，可玩地面只占 z ∈ [0,120)。

minimap 的每个几何假设都会因此失效：

| minimap 代码 | 原假设 | 分层后的后果 |
| --- | --- | --- |
| `UpdateMapTex` | `MapTexture.Reinitialize(Size.x, Size.z)` | 纹理 120×408，宽高比 3.4:1 |
| `BaseMapRectSize` | 宽高比 = `Size.z / Size.x` | 小地图被画成竖条，定位失真 |
| `Minimap` / `DrawCam` / `DrawAllPawns` / `DrawFires` / `DrawPings` / `DrawProjectiles` / `DrawThingLocators` | `LerpDoubleClamped(0, Size.z, …, DrawPos.z)` | 所有标记挤在顶部或底边 |
| `DrawGrid` | `Size.z / 10` 条线 | 网格密度错误 |
| `Harmony_TryUpdate.Prefix`（补 `Section.TryUpdate`） | `dirtyGirls[cellIndices.CellToIndex(cell)]` | 存储带格子索引越过 `dirtyGirls` 末尾 |

最后一条是用户报告的“警告”的来源：它**不是**警告而是每帧异常。`Section.TryUpdate` 的原版实现只对 `Regenerate()` 做了 try/catch，不含 Prefix；`dirtyGirls` 只在为 `null` 时按 `AllCells` 重新分配，因此扩层后仍保持旧的 120×120=14400 长度，而新坐标带 z=136 的格子索引为 `136*120 = 16320`，直接 `IndexOutOfRangeException`。异常沿 `MapUpdate → Game.UpdatePlay → Root_Play.Update` 冒泡并被捕获，VisualExceptions 将其记录成补丁链报告（因此报告里会混入 GUI Performance Patch 等无关补丁）。Prefix 末尾的 `Refresh = true` 永远执行不到，`UpdateMapTex` 不再被调用去扩容数组，于是**每帧都抛，直到关闭小地图**。

## 实现

新增 `Source/RimLayers/Compatibility/DubsMinimapCompatibility.cs`，全部按 `ModLister.GetActiveModWithIdentifier` 条件启用。核心是线程作用域的翻译窗口：只在 minimap 自己的调用栈内把几何读成“当前查看层”的本地坐标，不改变全局语义。

- **`Enter`/`Exit`**：`[ThreadStatic]` 深度计数；记录 `ViewedLayer` 与 `Stride`。
- **几何重定向**（transpiler，覆盖上述 minimap 方法）：`Map.get_Size → Size`、`Map.get_AllCells → Cells`、`Map.get_Area → CellCount`、`Thing.get_DrawPos → LocalDrawPos`、`CameraDriver.rootPos` 字段读 → `LocalRootPos`；`map.cellIndices.CellToIndex(c)` 连同其 `cellIndices` 字段载入一起替换为 `LocalIndex(map, localCell)`。
- **`procCell` 单点回译**：枚举出来的都是本地格，只在进入 `procCell` 时用 `ToStorage` 还原一次存储坐标，之后所有原生网格查询（地形、建筑、植物、迷雾）读到的仍是真实世界。
- **`Harmony_TryUpdate.Prefix` 守卫**：只对完全落在当前查看层矩形内的 `Section` 放行。存储带里的 section 没有可玩格，既消除了越界写入，也让脏标记只覆盖实际绘制的那一层。
- **相机**：`CurrentRealPosition` 的 postfix 减去层偏移（`MapPosition` 由它派生，故不重复补丁）；`JumpToCurrentMapLoc` 的两个重载各自在 prefix 加回偏移，使点击跳转落到当前层而非存储带。
- **切层刷新**：minimap 只在 `Map.Size` 变化时重建纹理，而存储尺寸恒定，切层后画面会停留在上一层。检测到查看层变化后强制调用一次 `UpdateMapTex(map, true)`。
- **占位地形配色**：`RL_OpenAir` / `RL_Isolation` 声明了 `dontRender`，`PostLoad` 不为其建 graphic，`Blitter` 会采到引擎的缺失纹理（洋红）。为这两个 RimLayers 自有地形返回扁平中性色，其余地形仍走 minimap 自己的采样。

### 两处实现陷阱（已修，勿回退）

1. **`__instance` 对静态目标无效**。`DubsMinimap.Harmony_TryUpdate.Prefix` 是静态方法，用 `Section __instance` 会绑到 null 实例而不是第一个参数，`__instance.map` 立即 NRE。改为按位置绑定 `Section __0`。
2. **Harmony 按名字逐个目标绑定补丁参数**。一个补丁类里写两个 `Prefix` 重载去覆盖 `JumpToCurrentMapLoc(Vector3)` 与 `(IntVec3)` 会抛 `Parameter "cell" not found in ... (Vector3 loc)`，并且因为发生在 `RimLayersMod` 构造函数里，会连带整个 mod 实例化失败。每个重载必须各自拥有独立的补丁类。

## 验证

`Run-V1.ps1 -Scenario minimap -Minimap`，独立测试数据目录，120×120、3 层。Release 构建 0 警告 / 0 错误。

最终 `v1-minimap-20261023-053834`：**16 项断言通过、failures=0**、`errors=[]`。

覆盖内容：

| 断言 | 结果 |
| --- | --- |
| 测试地图确实是多层且每层独占一条存储带 | `size=(120,1,408), stride=136, layers=3` |
| 兼容层确实介入 | `redirectedCalls=9651` |
| 纹理为一层而非存储带 | `120x120`（存储 `(120,1,408)`） |
| `pixels` 为一层 | `120x120` |
| `dirtyGirls` 按逻辑面积而非原生面积 | `14400`，原生 `48960` |
| 正方形地面得到 1:1 宽高比 | 逻辑 1.0，原生 3.4 |
| 通过回译读到真实地面地形 | `Concrete` |
| 相机位置落在查看层内 | `localZ=60.5 / height=120` |
| 切层后纹理重建为当前层 | 上层 `WoodPlankFloor` |
| 点击跳转落在查看层而非存储带 | 通过 |
| `Section.TryUpdate` 前缀不再越界 | 通过 |
| 空域是扁平中性色而非缺失纹理洋红 | 通过 |
| 分层 section 更新未产生新错误 | 通过 |

画面复核：`generate-minimap-upper.png` 中 120×120 小地图为正方形，空域呈暗色中性，相机框位于中央并跟随当前层；未出现洋红。

回归：`-Scenario navigation`（不加 `-Minimap`）generate 12 项 / load 4 项全部通过，确认未安装 minimap 时兼容层完全惰性、不影响既有多层行为。

## 范围与限制

- 只适配 Dubs Mint Minimap 1.7 版本中实际存在的成员（`MainTabWindow_MiniMap`、`Harmony_TryUpdate`）。上游若重命名这些方法，`TargetMethods` 会静默跳过并回到未适配状态；`V1Minimap` 的断言会失败。
- 未验证：其他小地图/雷达类 mod、minimap 的 Rimatomics 雷达与多人 ping 路径（这些路径复用同一几何入口，但未单独断言）、大于 120 的实测地图、4 层以上、`ShowFog=true` 时的雾区配色。
- `indexMisses` 仅统计落在绘制层之外、被折返到 0 的索引请求，用于诊断；非零不代表故障。
