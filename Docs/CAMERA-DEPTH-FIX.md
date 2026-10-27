# 高层背景黑屏：相机远裁剪面不足

2026-10-27；RimWorld **1.7.4990 rev1069 / Waylith / .NET 10**；RimLayers 0.1.5。

## 现象

- 站在高层（空域）时，拉远视角到最大缩放，整片下层背景、屋顶、薄雾全部消失，只剩引擎底色。
- 读档后底图内容会变化，容易误判为「脏 buffer」；实际每次缓存切换后重新提交，裁剪结果都不一样，看起来才像画面在变。
- 逆重飞船起飞／降落的过场动画被同样内容盖住，看不到飞船。

## 原因：原生远裁剪面按单层地图设计

`CameraDriver.Awake` 只设置一次 `MyCamera.farClipPlane = 65.5f`，此后不再更新；而 `ApplyPositionToGameObject` 每帧写入的相机高度是

```text
rootPos.y = 15 + (RootSize - min) / (max - min) * 50      // 最大缩放 => 65
```

正交俯视相机的可见列底部因此是 `y = 65 - 65.5 = -0.5`。原生地图所有内容都在 `y >= 0`，够用。

RimLayers 把下层投影到 `-depth * 3`（`LowerFloorBackdrop` 与 `RoofSurfaces`／`DriftingMist` 共用同一偏移）。从顶层看一层楼下，背景面位于 `y = -3`，已经在裁剪面之外；两层楼下为 `-6`，更深。镜头拉近时相机高度低，勉强能看到；拉到最大缩放时相机升到 65，背景整片被 GPU 裁掉。

逆重飞船相关代码（`WorldComponent_GravshipController.DrawGravship`）在过场开始时直接 `SetRootSize` 朝 `capture.maxCameraSize`（= `config.sizeRange.max`）插值，等于立刻触发上面的极限高度，所以飞船动画同样被「背景消失 + 引擎底色」盖住。

## 为什么只在大型地图上看得见

`CameraClampPatch`（`LayerView.cs`）把缩放上限压到能完整纳入当前层宽度：`maxSize = min((Width-4)/(2*aspect), (Height-4)/2)`。120 地图约 32.6，相机最高只到 `y≈37`，原生 65.5 在这个高度下反倒够用（能看到 `-28.4`），所以小图无法复现。

在 250 地图上该上限为 60（等于原生 `sizeRange.max`），相机才真正升到 65 并收窄到 `-0.5`。因此 presentation 场景默认改为 250；用户上报的存档尺寸就在这个范围内。

## 修复

新增 [CameraDepthMath.cs](../Source/RimLayers/Presentation/CameraDepthMath.cs)（纯数学，可脱离游戏测试）与 [CameraDepth.cs](../Source/RimLayers/Presentation/CameraDepth.cs)：

- `Far(cameraHeight, floorsBelow)`：下方没有楼层时**原样返回原生 65.5**，单层地图行为完全不变；有投影楼层时取 `max(65.5, cameraHeight + floorsBelow * 3 + 40)`。
- `CameraDepthPatch`：`CameraDriver.ApplyPositionToGameObject` 的 Postfix 按当前相机高度重算远期裁剪面。该方法是高度与裁剪面唯一的写入点，因此两者不会失步。
- 只放宽、从不收紧：`far` 永不小于 65.5，且不改变 near、投影矩阵、可见内容或任何游戏状态。

## 验证

- 纯数学：`Tests/CameraDepthTests`（804 条断言）覆盖零楼层保持原生值、最大缩放与深度 1–9 的全组合、单调性，以及「原生 65.5 确实会裁掉背景」这一回归断言。`Tests/Run-UnitTests.ps1` 全部 15 个游戏外工程通过。
- 游戏内：`Run-V1.ps1 -Scenario presentation -AllDlc`（默认 250 地图）新增四条断言：分层地图确有投影楼层；修正后的 `far` 包含每一个投影面；`far` 永不小于原生 65.5（只放宽不收紧）；当相机确实升到原生天花板（`y>=64`）时，对照断言原生 65.5 **会**裁掉背景。另截图 `presentation-maxzoom-a/b.png` 供人工确认。
- 该修复不涉及存档格式，读档无需迁移。

## 边界

- 逆重飞船过场还依赖原版 `FakeTerrain`／`TerrainCurtain` 幕布与掩码绘制；本次只保证这些面不再被裁剪，未改动其材质或时序。
- 只调整 `Find.Camera`。`GravshipCapturer` 的子相机由 `Fit(bounds, 15f)` 固定在 `y=15`，原生 far 65.5 已能看到 `-50`，因此不需要放宽；WaterDepth 子相机的 far 也刻意不动，避免改变水面深度采样。
- 若第三方光影 mod（如 RimIris）接管了后处理或自建渲染目标，其自身的画面状态仍需自行处理；本修复只保证几何不再被视锥裁掉。
