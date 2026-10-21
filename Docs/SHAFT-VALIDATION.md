# 垂直电梯双向扩井验收

2026-10-21；RimWorld **1.7.4982 rev1280 / Waylith / .NET 10**；RimLayers 0.1.5。使用独立测试配置和存档，每次加载均重启游戏进程。

## 同一座地面站同时服务上层与地下

已实现“向上扩井／向下开挖”，两个方向各自保存施工状态。扩井每次创建相邻一层；出口与相邻施工位的真实岩体由原版采矿伤害流程清理，再放置普通站点蓝图。两个新站各消耗钢铁 160、塑钢 30、零部件 4，原版供料和施工实际完成；不免费生成站点。

新站完工前通过起点站慢速进出，基准耗时 210 tick，使用应急电梯倍率。完工即切换为原来的连续井道连接。关闭本站停靠、禁用站点或缺少中间构件仍按原规则断开相应路线；不增加空气、热、火焰或电网连接。

使用步骤见 [电梯指南](DECKS-AND-ELEVATORS.md#同一座地面站向上向下扩井)。DLL 已输出到 `Assemblies/RimLayers.dll`，需重启游戏生效。

## 最终候选的真实游戏结果

| 测试记录 | 通过断言 | 决定性覆盖 |
|---|---:|---|
| `v1-shaft-extension-20261021-014854`，全五 DLC、中文 | 生成 14／施工中加载 19／完工后加载 13 | 原未扩容地面站同时向上下请求；冰盖、海冰、浅水和无屋顶拒绝；两个方向请求、真实岩石 HP 和原 Job 读档保持；出口及施工位实际采矿；原版自动搬运、蓝图→Frame→站点；两站材料精确消耗；地面→上层→B1→地面的同 Job 往返；中间停靠关闭后仍可穿井；B1 发起 B2 并取消；部分框架钢铁 17 只返还一次；完工整井再次重启读档后仍双向通行 |
| `v1-elevators-20261021-015110`，Core | 生成 20／加载 6 | 原有单层电梯和手动建造三层井道；真实发电机供电、独立倍率与实际通行耗时；断电应急；拆中间段后原 Job 改道；停靠和电闸保存；材料延迟返还 |

上述各结果均 `failures=0`、`errors=[]`。基础双向流程另有 Core 阶段验证 `v1-shaft-extension-20261021-014557`（14／15 项通过）；最终候选增加精确成本、取消退款和完工后重载检查，并保留原 ConnectorEdge 构造与解构入口。

`shaft-construction-probes.json` 记录两个出口蓝图的原版邻格、可达性、CanConstruct 均为 true，且原版供料 WorkGiver 返回 HaulToContainer。生成场景临时关闭洞穴以保证固定测试坐标确实有两格岩体；正常地下洞穴、矿物、虫巢仍由已有设置和生成机制处理，其证据沿用 [地下验收](UNDERGROUND-VALIDATION.md)。

实际中文画面已检查：[双向施工中的地面站](../Tests/Artifacts/v1-shaft-extension-20261021-014854/ground-dual-extension.png)、[完工后的地面站](../Tests/Artifacts/v1-shaft-extension-20261021-014854/ground-completed-dual-shaft.png)。两个按钮的标签、施工状态与停靠操作均可读。

## 构建与静态检查

- Release 构建：0 警告、0 错误；输出仍仅 mod 的 DLL、PDB 与 deps.json。
- 41 个 mod XML 文件解析通过；英文、简中各 187 个 Keyed 键，无重复。
- 既有单层连接器仍使用原入口、成本、完工准备和竖向采矿路径；新施工意图仅用于垂直电梯站，旧档缺省为空，不自动开井。

## 保留的失败与修复

`v1-shaft-extension-20261021-014225`：生成 14 项通过，施工中加载 6 项中供料施工超时。只有出口格可走，但原版 GenConstruct.CanTouchTargetFromValidCell 要求可达邻格，建筑工不接受孤立一格站点蓝图。

修复为保留真实相邻施工位，地下该位置也逐块采矿，人工上层检查已有下层屋顶；未修改全局原版施工邻格规则。后续 Core 14／15 项和最终全 DLC 14／19／13 项完整通过，包含原版供料及精确材料消耗。

## 复现

```powershell
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario shaft-extension -Language ChineseSimplified -AllDlc -TimeoutSeconds 480
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario elevators -TimeoutSeconds 480
```

shaft-extension 依次运行新图、采矿中存档加载、完工整井加载三个独立进程。测试生成和修改的配置、存档均位于该次 `Tests/Artifacts` 目录。
