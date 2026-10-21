# 起降动画在高层查看时保持可见

2026-10-19，RimWorld 1.7.4982 rev1280 / Waylith / .NET 10；本地0.1.4开发候选。

## 使用

重启游戏后，正常起飞或降落。可以保留二、三层查看，也可以在动画中切层；船体快照及特效跟随当前查看坐标带。不再把起飞时强制切到核心甲板作为显示前提。

动画继续使用原版核心甲板二维快照，并非多甲板三维起降。实体、船体归属、载荷、消耗和最终落点不因观看层改变。

## 修复入口

- 原版 `MapDrawer.DrawMapMesh`、`DynamicDrawManager.ComputeCulledThings` 会把实际捕获范围加入绘制范围，但 RimLayers 的 `SectionViewPatch` 又按所选层拒绝了其他层的 section。现在允许当前地图中与原生捕获范围相交的 section，并在原生捕获期间跳过下层投影/薄雾，避免把观察背景烘进船体快照。
- 原版 BeginTerrainRender 没有像船体捕获那样更新公共 `GravshipCaptureBounds`；在托管地图捕获目的地表时补上实际范围，避免沿用上一次核心甲板的捕获坐标。
- 原版 `GravshipRenderer` 按真实起降坐标绘制，镜头却可能位于其他坐标带。`ShipAnimationView` 在每次 UpdateTakeoff / UpdateLanding 内临时平移展示中心与引擎特效位置，finalizer 恢复原值；下一帧按新的查看层重新求偏移，不积累位置误差。
- 镜头 PanToMapLocAndSize 目标和原版降落幕布/掩码使用相同展示偏移；不更改捕获中心、原版载荷、Pawn 或物品坐标。幕布提交作用域结束时恢复。

源码：`Ships/ShipCutsceneView.cs`、`Presentation/LayerView.cs`、`LowerFloorBackdrop.cs`、`BackgroundPass.cs`。测试：`Diagnostics/V1ShipCutscene.cs`，仅由显式 `-ShipVisual` 启用。

## 验证方法与保留的失败

真实 GUI Repaint 阶段发起起飞，先让新建网格呈现；捕获期间查看非核心层，在动画进度约0.16和0.35后分别查看二、三层，保存实际 PNG 并检查绘制坐标带和捕获有效像素。随后继续原版飞行/落地/保存重启/返航，检查实体身份、甲板、装修、舱室和原版所有权。

早期 `172354`、`172506` 捕获为0个有效像素；同入口一层对照 `capture-control-172757` 也为空，说明混入了测试从 Game.UpdatePlay 发起且新网格尚未呈现的时序问题。这几组保留为失败，不能据此断定全部空像素均由楼层过滤造成。修正为 GUI 阶段后 `172933` 首次起飞捕获为1170144/1477440个 alpha>127 的像素，二/三层起飞与降落截图可见；生成51项、加载26项通过。其返航夹具仍从 UpdatePlay 发起，17项中捕获断言失败1项；返航现也统一为 GUI 发起。早期第三层坐标断言还受测试切层在展示补丁之后执行的一帧顺序影响，测试切层改为优先执行；未放宽坐标或像素断言。

## 复现

```powershell
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario gravship -NativeShip -ShipVisual -AllDlc -EngineLayer 2 -ExistingMap -Space -TimeoutSeconds 480
```

另有 `173300` GUI夹具失败：等待GUI启动的几帧仍推进模拟，未驻留目的地图被原版回收，确认出发时空引用。保留该日志并结束其自身测试进程；修正为等待GUI动作期间不推进测试tick，并在GUI夹具中记录异常后结束测试，没有改地图回收或正式起降逻辑。


## 真实游戏结果

| 独立目录（20261019） | 通过断言 | 覆盖 |
|---|---|---|
| `v1-gravship-173514` | 56 / 27 / 18 | 三层核心，捕获时查看二层；动画中切二、三、一层；既有太空目的地、旋转、飞行和落地重启、返航 |
| `cutscene-load-final-173843` | 28 | 同一三层核心飞行检查点回放，最终地表范围补丁，原版降落完成和原实体/装修/舱室保持 |
| `v1-command-input-174409` | 18 | Core无Odyssey，真实右键移动/跨层/攻击和射击图标输入 |

上述结果 `failures=0`、`errors=[]`。三层核心初次捕获有效像素1170144/1477440；返航捕获725698/921240，阈值均为alpha>127。已查看真实起飞/降落截图，船体可见，不是仅检查Job或动画结束标记。

- [三层查看时起飞](../Tests/Artifacts/v1-gravship-20261019-173514/generate-takeoff-layer-3.png)
- [最终三层查看时降落](../Tests/Artifacts/cutscene-load-final-20261019-173843/load-landing-layer-3.png)
- [实际船体捕获图](../Tests/Artifacts/v1-gravship-20261019-173514/generate-ship-capture.png)

程序集已重新构建，需重启游戏使用。捕获修复不重新生成此前已保存的旧贴图；重新起飞会按新规则捕获。


Core附加回归 `173929` 的10项失败，日志指向合成点击被原版 MainButtonWorker/GUI.ButtonImpl 消费；`174409` 仅增加按钮矩形/事件坐标诊断，未修改生产输入规则，18项通过，errors为空。前一次失败保留，不用后一次通过声称已修复所有输入夹具稳定性问题。
