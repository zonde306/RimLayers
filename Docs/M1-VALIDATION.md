# M1：工程与地图基础实现及验证

日期：2026-10-16。结论：**第一阶段完成；M2–M5 未实现**。范围对应 [PLAN 的 M1](PLAN.md#5-实施里程碑与交付门槛)，不是完整可玩多层 mod 的验收。

## 交付与入口

- `About/About.xml`：RimWorld 1.7 元数据、Harmony 依赖及开发版状态。
- `Source/RimLayers/RimLayers.csproj`：net10.0 单程序集，使用本地游戏/Harmony 引用，禁止复制依赖 DLL。
- `Layout/LayerLayout.cs`：不可变布局、严格坐标/隔离区校验及溢出检查。
- `Core/LayerMapComponent.cs`：布局唯一所有者、列查询、失效版本/事件、查看层、Scribe 版本与加载校验。
- `Generation/`：全尺寸分配、地面尺寸生成、恢复完整尺寸、临时网格修复、河流数组重排；异常时恢复尺寸/上下文。
- `Presentation/LayerView.cs`：普通视角切层、相机与视口限制、绘制/拾取过滤、命令边界、选择与跳转 Pawn。
- `Core/IsolationPatches.cs`：不支持楼面的建造拒绝、隔离区视线/气体/挡光边界、地面出口网格、地形/屋顶失效通知。
- `Defs/` 和 `Languages/`：独立空域/隔离地形与英文、简中 UI 文本。
- `Tests/`：独立布局检查与真实游戏进程验收；运行数据只写入当前 mod 的 Artifacts 子目录。
- 生成/网格修复的 MIT 来源与许可保留在 [ThirdPartyNotices](../ThirdPartyNotices.md)。

默认只扩容新生成的玩家地表地图。旧图、NPC 图、Pocket、地下、太空和 step debugger 不转换。上层未铺楼面，普通蓝图包括 god mode 也不能绕过当前承载限制。后续楼板/楼梯阶段将接入正式结构服务，不依赖临时开放整个上层。

## 构建与环境

| 项目 | 本次证据 |
|---|---|
| 游戏 | 1.7.4980 rev60，Waylith |
| SDK / 工程 | .NET SDK 10.0.402；net10.0 |
| 游戏程序集 SHA-256 | `EFF7D97949F919FB2B151EBFB906E9BE749B63410B7DBDA78EEEBB7972B245C3` |
| 游戏 MVID | `8e962ecc7ac742d28f3b1b16cd24d2cd` |
| 构建 | Release 成功，0 警告、0 错误 |
| RimLayers.dll SHA-256 | `7AA9399A097589DAC3D2ABBBAE557374B66176497BFFB0B6087877B4D2EA0461` |
| 输出 | RimLayers.dll、RimLayers.pdb、RimLayers.deps.json；无游戏、运行时或 Harmony DLL |
| 激活组合 | Harmony、Core、Royalty、Ideology、Biotech、Anomaly、Odyssey、RimLayers |
| 游戏显示 | 本次截图 1920×1080，英文；简中 XML 已解析，简中画面尚未单独验证 |

## 可复现命令与原始结果

从游戏根目录运行：

```powershell
dotnet build Mods/RimLayers/Source/RimLayers/RimLayers.csproj -c Release --nologo
dotnet run --project Mods/RimLayers/Tests/LayoutTests/LayoutTests.csproj -c Release
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-M1.ps1 -River
```

脚本创建新的时间戳数据目录，用两个独立进程分别生成/保存和重启/加载。它不会用成功日志代替断言：缺结果、超时或任一失败都会返回失败。-mod 会自动加入安装的官方 DLC；本次没有做无 DLC 声明。

最终运行目录：`Tests/Artifacts/m1-20261016-211733`。

| 检查 | 结果 | 原始证据 |
|---|---|---|
| 布局独立检查 | 862,803 项通过 | [测试代码](../Tests/LayoutTests/Program.cs)；脚本标准输出 |
| 生成进程 | 38 项，0 失败 | [generate-results.json](../Tests/Artifacts/m1-20261016-211733/generate-results.json) |
| 新进程读档 | 40 项，0 失败 | [load-results.json](../Tests/Artifacts/m1-20261016-211733/load-results.json) |
| 多地图恢复 | 玩家三层图 + 普通 60×60 NPC 图 | [load-maps.json](../Tests/Artifacts/m1-20261016-211733/load-maps.json) |
| 持久事实对照 | Pawn 身份、物品/建筑/植物等事实及非压缩定义实体 ID 一致 | [baseline.json](../Tests/Artifacts/m1-20261016-211733/baseline.json)、[load-things.json](../Tests/Artifacts/m1-20261016-211733/load-things.json) |
| 地面显示 | 已看图核对 | [生成地面](../Tests/Artifacts/m1-20261016-211733/generate-ground.png)、[读档地面](../Tests/Artifacts/m1-20261016-211733/load-ground.png) |
| 上层显示 | 空域，无地面实体透出；楼层控件与教学提示分离 | [生成上层](../Tests/Artifacts/m1-20261016-211733/generate-upper.png)、[读档上层](../Tests/Artifacts/m1-20261016-211733/load-upper.png) |

这些 Artifacts 文件属于本机验收证据，已在 .gitignore 中排除，未来不随 mod 发布。

## M1 门槛逐项对应

| 门槛 | 验证范围 |
|---|---|
| 三层生成、地面内容 | 120×120 地面、stride 136、真实 Map 120×408；检查所有上层/间隔格的地形、不可行走、无顶、无物体和揭雾 |
| 逻辑坐标 | 多组尺寸逐格往返；负数、越界、错误 y、隔离行、错误 stride 和溢出均拒绝 |
| 空域与隔离 | 28,800 个上层逻辑格、5,760 个隔离格；气体和快速视线拒绝隔离格，普通建造拒绝上层和间隔 |
| 生成兼容入口 | 真实有河流与道路的地块；完整流向数组长度正确，上层/间隔段流向为零，地面存在实际流向 |
| 显示/命令边界 | 三层切换、相机越界位置与极端缩放、非空视口、屏幕中心解码、其他层拾取拒绝 |
| 不改 Pawn / Job | 切层前后位置与当前 Job 引用一致；跳转和选择地面 Pawn 会回到目标层 |
| 普通地图与多图 | 生成实际 60×60 NPC 地图且保持不受管；往返切图和新进程加载后仍保持尺寸与独立查看状态 |
| 世界出口网格 | 实际调用已补丁的重建器，检查上层/间隔全无出口、地面有逻辑出口；不改变家园启用出口网格的原版政策 |
| 存档与重启 | 保存查看层 3；新进程加载后布局、原玩家地图 ID、查看层、Pawn 身份及持久实体事实一致 |
| 事件与光照基础 | 地形/屋顶变化有统一失效通知；FinalizeInit 重建固定隔离区挡光位，普通移除不能撤销；完整领域传播仍在 M5 |

相机的边界/缩放测试通过实际 CameraDriver 接口施加输入结果，并执行原版应用流程；未声称穷尽所有鼠标、屏幕比例和第三方 UI 组合。右侧控件已看图，按钮路径复用已测试的 Switch；后续可继续补输入设备自动化。

## 验收中查明的原版行为

- CurrentViewRect 按 frame 缓存。手动移动相机或同帧切层必须让缓存失效，否则可能返回上一层的空交集。本次已在相机应用时失效缓存，并要求视口非空。
- 原版 MapFileCompressor 会重建可压缩物体的 ID，不能用全部 ThingID 完全相等判断丢物。测试对可压缩定义比较类型、位置、数量、生命值，对其他持久实体另保留 ID；Pawn 始终比较 ID。
- Map.FinalizeLoading 以 FullRefund 重新生成物体；GenSpawn.SpawningWipes 会让建筑及部分阻挡物品清除重叠污渍。此行为已用原版方法体和实际销毁调用栈确认。baseline 单独记录 nativeLoadWipes；只对这些明确的污渍清理作归一化，不过滤物品、建筑、Pawn 或植物差异。
- NPC Settlement 没有驻留玩家 Pawn 等保留条件时可被原版卸载。多图测试使用专用玩家访客保持该测试地图，不改原版卸载规则。
- 家园的 ExitMapGrid.Grid / IsExitCell 可因 MapUsesExitGrid 为 false 而返回 null / false。验收直接调用重建器读取其存储网格，不把“公共 getter 全 false”误判成隔离正确。

测试中曾因这些错误假设出现失败；相关旧运行目录保留，未将失败结果改写为通过。最终上述两份结果才是本次交付依据。

启动日志还包含本机既有重复 packageId、Vulkan layer 路径和退出时 Waylith 持久分配提示；未修改其他 mod 或系统配置来消除它们。最终测试期间捕获的 Verse.Log.Error 为零，并逐项检查本 mod 的安装/生成/加载过程；不宣称整份原生日志没有任何警告。

## 存档、兼容与后续边界

已验证的是三层新图原型及上述本机组合。尚未运行长期模拟、250×250 或更大地图压力矩阵、无 DLC 组合、任意第三方生成步骤和全部 UI 尺寸；不由格子数量或本次通过结果推导性能上限。

生成期网格修复含版本相关反射，不能保证修复任意第三方私有网格。光照隔离是固定边界基础，不代表跨层照明、热/气体交换、电网或天气列规则已经完成。正式跨层通行后的家园离图流程在 M2 的路径/边界接入中继续验证；M1 上层不可站立。

已有地图不会自动迁移；读取不受管布局保持普通地图。带 RimLayers 布局的地图不能在卸载 mod 后继续安全使用。没有修改原版 Data、游戏 DLL、参考项目、玩家配置、玩家存档或玩家启用列表。

下一阶段为 M2：楼梯/平台、完整端点 Dijkstra、动态断路与连续工作查询。保留 [PLAN](PLAN.md) 中后续阶段的全部目标和不变量。
