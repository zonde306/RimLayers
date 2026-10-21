# 换层保留建筑放置与重新安装

## 使用与修复

选中建筑，点击“重新安装”，再用 F7/F8 或楼层面板切换楼层，继续放置重装蓝图。普通建造工具也保留；换层只结束上一层的拖拽，不把旧范围带到新层。右键／取消和放置完成后的原版工具退出仍生效。

根因有两处：`LayerView.Switch` 无条件取消 Designator，同时取消所有非 Pawn 的选择。原版 `Designator_Install` 每次通过 `SingleSelectedThing` 查找待安装的打包物或待重新安装的建筑，因此必须一起保留工具和源对象。

生产修改仅在 `Source/RimLayers/Presentation/LayerView.cs`：不再取消当前 Designator；安装工具激活时额外保留其源对象。沿用原版放置检查、蓝图引用、搬运／安装工作、材料和旋转状态。普通非 Pawn 选择仍按原规则清除，战斗 Targeter 和 Pawn 选择逻辑不变。未新增存档字段或更改存档格式；无 XML／Def 修改。

## 回归入口

```powershell
& ./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario reinstall -TimeoutSeconds 240
```

`V1Reinstall.cs` 在独立测试地图中调用真实安装 Gizmo、UIRoot 输入和原版工作调度，覆盖：

- 普通建造工具换层保留，旧拖拽结束，非安装建筑选择清除。
- 打包物安装向下换层时保留源对象；右键取消仍有效。
- 实体建筑上楼／下楼重新安装：F8/F7、目标层原版点击生成蓝图，关联同一源对象。
- 动画切层的输入快进提交保留工具与源对象。
- 真实工人自动完成上楼／下楼搬移，原建筑身份保持、蓝图消失，无重复实体。

测试开关仅在专用测试进程启用，鼠标输入注入只在测试事件期间安装；工人由原版 AI 和 Job/Toil 执行，驱动通过原生 tick 入口加速。

## 验证记录（2026-10-21）

基准：RimWorld `1.7.4984 rev1242`，根 Assembly-CSharp MVID `484efdc5765b463ca9cc54a6131e38f0`，net10.0、Harmony 3。Release 编译通过，0 警告、0 错误。

初始夹具失败保留：
- `Tests/Artifacts/v1-reinstall-20261021-183100`：MousePositionOnUI 是属性，测试误用方法挂钩；已改为 PropertyGetter。
- `Tests/Artifacts/v1-reinstall-20261021-183142`：夹具换层后鼠标还在旧层、直接在异层选源对象，以及对 MinifiedThing 本体设置阵营；已更新测试鼠标坐标、在选择前定位源层、对 InnerThing 设置阵营。这两轮不能作为功能通过证据。

最终通过目录：[v1-reinstall-20261021-183247](../Tests/Artifacts/v1-reinstall-20261021-183247/)。

- `generate-reinstall-results.json`：21 项断言全部通过，场景错误列表为空；游戏退出码 0。
- 实际加载组合：Core、Harmony、RimLayers。生产 DLL 已输出到 `Assemblies/RimLayers.dll`。
- 同一建筑 `Stool9569` 上楼用 437 tick、下楼用 361 tick；两次原版放置均创建引用原对象的 Blueprint_Install，最终蓝图消失且原建筑位于目标格。
- 已查看 `reinstall-blueprint-0.png`、`reinstall-blueprint-1.png` 和 `reinstall-completed.png`：上／下层目标蓝图可见；完成图停留在搬离后的上层，最终楼下位置由实体断言确认。
- `generate-stdout.log` 启动期仍有本机 Bandicam/SocialClub Vulkan 清单缺失、未启用 mod 的重复 packageId 扫描错误；未出现本次功能相关运行异常。另有现有快捷键冲突、资源静态字段和退出分配警告，未改动这些独立问题。
- 本轮未做完整玩家 mod 组合或存读档矩阵；修复不改变序列化。动画路径验证到输入快进提交，未将本轮结果扩称为全部动画画面回归。

重新启动游戏即可加载修复，无需修改存档。
