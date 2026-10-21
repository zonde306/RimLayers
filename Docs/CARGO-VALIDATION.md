# 吊篮验收记录

2026-10-21，RimWorld 1.7.4984 rev1242 / Waylith / .NET 10。保留包 ID `rimlayers.core`，输出 `Assemblies/RimLayers.dll`。

## 真实游戏结果

以下运行使用 `Tests/Run-V1.ps1` 创建的独立数据目录。generate、load、landed-load 各自启动新游戏进程；JSON 保存逐项结果，日志和存档保存在同一目录。

| 场景 | 证据目录（Tests/Artifacts 下） | 通过数 |
|---|---|---:|
| 完整吊篮闭环、运行中存档 | v1-cargo-20261021-194013 | generate 66 / load 56 |
| 装入过程中存档 | v1-cargo-20261021-193616 | generate 22 / load 20 |
| 双请求排队中存档 | v1-cargo-20261021-193913 | generate 22 / load 20 |
| 飞船货物及井道保全 | v1-gravship-20261021-194140 | generate 53 / load 26 / landed-load 8 |
| 楼梯及载人电梯回归 | v1-elevators-20261021-194354 | generate 20 / load 6 |
| 原版小货架搬运回归 | v1-work-haul-20261021-194458 | generate 16 |

这些结果均为 0 失败，测试捕获的运行时 errors 数组为空。

### 核心存取与原版任务

- 原版 `HaulAIUtility.HaulToStorageJob` 选中吊篮，Pawn 搬入实际 20 钢铁；另一层 Pawn 取出 10，篮中剩余 10。没有人员连接时仍能取用，Pawn 不换层。
- 空篮不出现虚构库存；较近的本地货物优先于明显更慢的吊篮。禁用本层端口或堵住全部操作位后，不再提供远端库存。
- 原版做饭工单分两次取 3 土豆＋7 大米；制造短弓使用带未完成品的原生工单；建筑供料、自动进食、穿戴和装备均完成。检查真实材料消耗、剩余数量和产物，而不是仅检查任务生成。
- 手动／电动竖井跨三层；手动征召中断后保持位置并续行；电动断电保持进度，取消请求后仍完成已开始行程。本层停靠时允许无电存取。
- 同篮 3 个原版堆栈；修改筛选保留原物品；断井、修复、主机意外摧毁均检查关联或实物身份。
- 在正常游戏 tick 中，篮内食物腐烂进度增加；停靠温度等于该层篮子位置的温度。

### 排队、预留与存档

- 在真实游戏进程内，两个 Pawn 的有效任务分别请求容量和货物：容量只承诺 50＋25，实际 75 件货物也只承诺 50＋25；顺序为 FIFO，取消立即释放预留。这组检查直接调用访问服务验证竞争算术。
- 排队存档场景运行实际搬运任务＋另一 Pawn 的呼叫任务，保存时恰有两个有效请求。重启后恢复请求、原任务、唯一物品及数量；取消排队者立即只剩一个请求。
- 装入、升降中分别保存后退出重启，校验同一 Job ID、物品身份、篮子机械位置、处理进度和货物数量，再完成取货。

排队回归曾发现原版预留发生于 `pawn.CurJob` 赋值之前，产生空 Job 的额外容量承诺。已改为绑定 `JobDriver.job`，修复后 `193913` 的 generate/load 全部通过。

### 飞船

- 三层完整井道内的 17 黄金在起飞、飞行中重启、旋转降落及落地重启后，保持同一物品 ID、唯一容器、三端引用与暂停的升降进度。
- 一侧属于飞船、另一侧属于普通楼层的井道：预览不改动货物；实际跨界拆分提交后，两端拆除，11 黄金原物品仍存在且只出现一次。
- 既有飞船降落规则已允许散落物品，因此旧碰撞夹具改用钢墙，继续验证建筑阻挡，不以旧的“散落钢铁阻挡”规则作为预期。

### 普通货架与人员交通

原版小货架场景使用实际 `Building_Storage`，无吊篮端口：跨层搬运拆出 10、向原有 5 合并为 15，来源余 65；总量、原 Job/Driver、征召／取消释放均通过。既有电梯场景验证原生电源、路径成本、停站、断电及重启恢复，楼梯仍使用原人员连接。

### 视觉

已查看实际中文游戏截图：
[吊篮与建造蓝图](../Tests/Artifacts/v1-cargo-20261021-194013/generate-cargo.png)。

检查了木架绞盘、金属电机、停靠货物、离站封闭口、电动建造蓝图、储存／内容物页签、楼层名称、容量和排队数。中文与英文文本均已配置。

## 静态与构建检查

- Release 构建：0 警告、0 错误，保持 `net10.0` 与现有输出路径。
- 7 个相关 XML 文件可解析；10 个建筑／下拉组 defName 唯一；英中 Keyed 各 23 键一致。
- `Tests/CargoTests`：10 项精确时序检查通过，覆盖手动 180 tick、电动启动及逐层耗时、暂停、负楼层、同层零升降和大 delta 不越站。

## 复现

在 mod 根目录运行：

```powershell
pwsh -NoProfile -File Tests/Run-V1.ps1 -Scenario cargo -Language ChineseSimplified
pwsh -NoProfile -File Tests/Run-V1.ps1 -Scenario cargo -CargoCheckpoint handling
pwsh -NoProfile -File Tests/Run-V1.ps1 -Scenario cargo -CargoCheckpoint queue
pwsh -NoProfile -File Tests/Run-V1.ps1 -Scenario gravship -TimeoutSeconds 300
pwsh -NoProfile -File Tests/Run-V1.ps1 -Scenario elevators
pwsh -NoProfile -File Tests/Run-V1.ps1 -Scenario work-haul -Shelf
dotnet run --project Tests/CargoTests/CargoTests.csproj -c Release
```

核心吊篮场景加载 Core、Harmony、RimLayers；飞船场景另加载 Odyssey。未把玩家当前全量 mod 组合或长期殖民地压力测试计入这些结果。
