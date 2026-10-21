# 0.1.4：征召右键与射击命令

## 故障与修复

玩家日志 `2026-10-18 21:53` 启动后，59 次右键菜单错误指向 `FloatMenuMakerMap.GetProviderOptions`。更早的启动检查在反射 `Rimiris.Dashboard.Net.ConsoleTap.savedLevels` 时发生 TypeLoadException：该字段的 `UnityEngine.StackTraceLogType[]` 不存在于当前 UnityStubs 中。Dashboard 的 packageId 为 `rimiris.dashboard`，工坊目录 `3800182954`；这是日志定位后的定向核对，不从模组名猜测。

当前原版 `PlayDataLoader.DoPlayLoad` 把以下操作放在同一个完成回调中：静态构造 → 开发模式资产检查 → 延迟材质解析 → `FloatMenuMakerMap.Init` → 图集烘焙。资产检查异常会跳过后续初始化，菜单 providers 留为 null；`GetProviderOptions` 的外层 foreach 因此在任意提供器执行前失败。不是移动 Job 被跨层寻路拒绝。

RimLayers 的 `Compatibility/DashboardStartupInspection.cs` 只在 Dashboard 激活时接入资产检查的 FieldInfo 谓词。仅对 **RimirisDashboard 程序集 / ConsoleTap.savedLevels / 缺失 StackTraceLogType 的 TypeLoadException** 跳过该非资产字段，输出带归属的一次警告；其余字段和异常沿用原版。没有重建菜单、代发 Goto、吞掉提供器异常或禁用整项开发检查。

此修复允许原版初始化继续。Dashboard 控制台的堆栈级别功能仍需其自身更新；不据此声称 Dashboard 整体兼容。RimLayers 的存档格式、可达性、完整路径、任务预订和战斗物理均未改变。必须退出并重启游戏才会生效，不需要新存档。

## 原生验证方法

`Tests/Run-V1.ps1 -Scenario command-input` 在独立新图中选择真实征召殖民者，装备原生步枪，关闭自由射击，指定一个真实敌对 Pawn。鼠标的屏幕位置和地图落点仅在诊断作用域替换，事件经原版 UIRoot、Selector 和 Targeter 分发；射击从 Pawn.GetGizmos 的真实 Command_VerbTarget 开始。检查原版 Goto / AttackStatic 与真实 Thing 身份，再运行正常 Tick，核实同层移动、1→2→3 到达及实际 LastShotTick 增长。测试不直接创建这些移动或攻击 Job。

`-Dashboard` 加入玩家实际使用的 `rimiris.rimiris.beta` 和 Dashboard；后者不完整声明其软依赖，不应以仅加载 Dashboard 的错误组合代替该输入。`-PlayerMods -AllDlc` 只复制玩家启用列表到本场景的独立配置，仍使用新建测试地图。JSON 记录实际 activeMods 和游戏 MVID。启动和玩法错误原样记录，不设忽略名单。

当前基准：1.7.4981 rev136 / Waylith / .NET 10 / Harmony 3；游戏 MVID `f1e6382600a84191bbc6d8a184fd6b57`。目标 Dashboard MVID `ecac7f0afdff42bab210bdca707f7326`。原版证据成员：DoPlayLoad 0600093B、ReportProbablyMissingAttributes 0600074D、FloatMenuMakerMap.Init 0600C065、GetProviderOptions 0600C067、Selector.HandleMapClicks 0600C417、Command_VerbTarget.ProcessInput 06003077、Targeter.OrderPawnForceTarget 0600C451。

## 运行记录

| Tests/Artifacts 目录 | 组合 | 完整 UIRoot 输入检查 |
|---|---|---|
| [v1-command-input-20261018-223722](../Tests/Artifacts/v1-command-input-20261018-223722/generate-command-input-results.json) | Core + Harmony + RimLayers | 18 项零失败，errors 为空 |
| [v1-command-input-20261018-223746](../Tests/Artifacts/v1-command-input-20261018-223746/generate-command-input-results.json) | 全五 DLC + Harmony + RimLayers + RimIris Beta + Dashboard | 19 项零失败，errors 为空 |

最终事件同时设置 MouseDown 的 type / rawType；屏幕鼠标位置与地图落点同步，地图点击位于逻辑 UI 宽度中点、高度四分之一处，避开原版 Gizmo 控件区。每次输入记录 UIRoot、窗口、Designator、Targeter、Selector 的事件状态与 Event.Use 栈，避免只看生成 Job 就认定上游 UI 通过。验证的攻击来自原生按钮/菜单，测试关闭自由射击，实际发射时间戳须在各次命令之后增长。

**完整 165 模组和玩家原档：尚未通过。** `v1-command-input-20261018-221936` 复制原启用列表后，新建世界在 Ideology 描述生成的 GrammarResolver.AddRule 遇到 null key，未进入地图；随后达到脚本 240 秒限额，只有本场景的隐藏子进程被结束。另有 LightWingRimUnityShader 的缺类型、XML 与资源错误，均未由本次补丁吞掉。该结果不能证明原档命令失败或成功。玩家应重启 0.1.4 读原档回验右键和射击；若射击仍无效，下一步定位该实际 Pawn / 武器 / 目标与命令入口，不扩大启动兼容补丁。

### 已保留的失败和中间结果

- `command-input-before-0.1.4/player-20261018-2153.log`：原玩家故障日志及 0.1.3 DLL/PDB 备份。
- `v1-command-input-20261018-221251`：最初 Core 探针右键移动、跨三层移动、右键攻击和目标所在层射击通过；背景目标位于完整楼板下，按规则不能拾取。修正夹具到无遮盖边缘；旧 LastShotTick 检查可能受自由射击干扰，随后关闭自由射击并比较每次提交前后的时间戳。
- `v1-command-input-20261018-221350`：未加兼容补丁，仅 Dashboard 的复现；providers=null，右键移动/攻击失败，射击图标能实际开火。这说明启动异常可解释右键失效，但不能直接解释全部射击反馈。
- `v1-command-input-20261018-221514`：初版定向补丁成功隔离 StackTraceLogType，但测试缺少 RimIris Beta，另一个 RimIrisPerfSample 缺失仍中断检查。没有为此扩大异常吞噬范围；补齐真实软依赖后再测。
- `v1-command-input-20261018-221630`：Core + Harmony + RimLayers + RimIris Beta + Dashboard，19 项零失败、无运行错误。此阶段通过私有 Selector.HandleMapClicks / Targeter.ProcessInputEvents，后续把分发提升到 UIRoot，单独记录最终证据。
- `v1-command-input-20261018-221739`：全 DLC + HAR + Nanami + RimIris Beta + Dashboard。Nanami 自身年龄/需求初始化失败，角色未生成，探针无法取得该角色；保留失败，不记其命令或原装近战武器的射击通过。既有 Nanami 独立异常见显示验收及可用性验收。

- `v1-command-input-20261018-222350`、`222828`：完整 UIRoot 探针的事件在 MapInterfaceOnGUI_AfterMainTabs 之前/之中已被消费，右键及射击提交失败；providers 初始化与 Targeter 目标身份检查通过。早期事件只设置 type，rawType 仍是 Event 默认 Repaint，初版还未同步屏幕鼠标坐标。没有把这两次失败改为通过，也不据此归因为生产战斗缺陷。
- `222606`：同步屏幕坐标后的 Core + RimIris Beta + Dashboard 19 项通过；`223109`：增加 Event.Use 栈后的全 DLC 同组合 19 项通过。这些中间记录未用于替代最终 type/rawType 一致的 223314。
- `223314` 全 DLC 19 项通过，但其后的 Core `223419` 在同候选上 10 项失败，不能据前次通过宣称输入夹具稳定。Event.Use 栈明确指向 **GizmoGridDrawer.DrawGizmoGrid → GenUI.AbsorbClicksInRect**：固定屏幕点击 (512,320) 落在控件区，被原版正常消费；早期仅按 MapInterface 阶段归因不充分。最终改用随逻辑 UI 尺寸选择的地图空白点，保留正常控件吸收逻辑，223722/223746 通过。生产补丁未随这些夹具调整而改变。
- `223052`：诊断栈使用 Environment.StackTrace 与 RimLayers.Environment 命名空间冲突，构建失败；改为 System.Environment 后构建零警告零错误。生产修复未因此改变。

玩家完整旧档中的射击按钮问题尚未建立同一原因，不能用右键修复替代该项验收。
