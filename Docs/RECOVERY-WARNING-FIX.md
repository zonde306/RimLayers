# 楼板施工恢复误报修复

2026-10-21；RimWorld **1.7.4984 rev1242 / Waylith / .NET 10**；RimLayers 0.1.5。

## 原因与修复

玩家日志在正常铺设楼板时反复把 `Blueprint_RL_StructuralFloor` 和 `Frame_RL_StructuralFloor` 报为 `UnknownBuilding`。恢复巡检错误地要求未完成楼板已经提供可走表面；同时，未知建筑告警和待恢复实体共用了累计计数及“打开待恢复列表”提示，但建筑根本不进入该列表。

- 正常结构楼板和飞船结构楼板的蓝图／框架改用既有施工规划校验，继续检查下层屋顶、逻辑层和飞船归属等条件；不要求规划中的格子已有可走楼面或邻接施工已完成。
- 未知建筑／规划异常、巡检失败和真正待恢复实体分别提示。未知建筑仍保留日志中的 ID、Def 和位置，不自动搬移。
- 延迟合并提示按对象去重，发送前检查现状；已恢复、已移除或已重新获得承托的对象不遗留提示计数。
- 不改变实体暂存、原对象身份、通行规则、材料、坍塌或存档格式。更新后启动游戏即可生效，无需清理恢复队列或修改设置。

实现：`Source/RimLayers/Recovery/EntityRecoveryService.cs`、`RecoveryCompatibility.cs`；中英文提示在 `Languages/*/Keyed/RimLayers_Settings.xml`。使用说明见 [SETTINGS.md](SETTINGS.md)。

## 验证结果

命令：

```powershell
./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario settings-recovery -AllDlc -Language ChineseSimplified -TimeoutSeconds 300
```

独立测试目录：[v1-settings-recovery-20261021-173623](../Tests/Artifacts/v1-settings-recovery-20261021-173623)。

- Release 构建成功，零警告、零错误；已输出 `Assemblies/RimLayers.dll`。
- MOD XML 可解析；中英文设置翻译各 95 个唯一键，键集合一致。
- 原版隐藏测试进程 generate **56/56**、load **18/18** 通过；两进程正常退出（0）。实际加载 Core、全部五个 DLC、Harmony、RimLayers；两阶段报告对应同一 MOD 程序集 MVID。
- 新增 11 项检查覆盖原生规划入口生成楼板蓝图、带材料框架、重复通知、正常施工无提示、失去屋顶仍告警、承托修复撤销延迟提示、未知墙与墙蓝图仍单独告警、真实暂存仍提示、通知前已恢复不再提示。测试捕获实际 `Messages.Message` 调用并检查本地化文本。
- 既有恢复回归覆盖 Pawn／物品／尸体、装备／背包／携带物、真空暂存与休眠、存读档、地图移除、跨图确认、循环纠正、逐实体故障隔离及重试。
- 报告 `errors` 均为空。启动日志保留本机已有的两个 Vulkan 覆盖层路径错误、三个重复 packageId 清单错误及 Steam Fossilize 警告；读档阶段有用例主动注入的恢复异常，随后重试成功。未发现本修复的其他运行错误。

完整结果、stdout/stderr、独立配置和存档在该目录，读档阶段全局日志另归档为 `load-player.log`。未使用或修改玩家存档、设置和启用列表。

### 保留的失败记录

`v1-settings-recovery-20261021-173500` 的全部业务断言通过，但运行错误检查失败：测试给 `Blueprint_Wall` 的 MakeThing 调用传入 Steel，而蓝图自身不是 MadeFromStuff。已移除错误的夹具参数；上述最终轮次通过，生产建造条件没有放宽。

故障玩家日志及修改前文件保留于 `Tests/Artifacts/recovery-warning-fix-20261021/`。此次验证针对施工误报与恢复链，未重跑无关性能／五页 UI 全矩阵；未验证任意第三方 MOD 组合。
