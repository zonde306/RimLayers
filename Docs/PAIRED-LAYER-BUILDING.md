# 上下层对位建造

有些建筑只有上下两层同一格都建成才起作用：电力端子、竖直管道、电梯站点、吊篮两端。过去玩家要在两个楼层各画一次蓝图，靠肉眼对齐格子。现在选中一端，点一个按钮，另一端就在正上／正下方、同一格位置上排进蓝图队列。

## 按钮在哪

选中建筑本身时多出两个按钮：

| 按钮 | 含义 |
|---|---|
| **在上层建造对应端** | 目标层 = 当前层 +1 |
| **在下层建造对应端** | 目标层 = 当前层 -1 |

蓝图和框架上也有同样的按钮，所以在两端都还没完工之前就能一起排进队列。

## 它到底做什么

按的是原版规则：

1. 目标层必须**已经存在**。按钮不会替你开辟楼层。
2. 目标格必须有**承重楼面**（或原版允许的地面／船体甲板）。空中格不行。
3. 那一格不能已经有同一个建筑或同一个蓝图。
4. 最后整条原版放置检查都要通过：地图边缘、迷雾、地形承载、占地冲突、交互位、以及本 mod 自己的摆放规则。

全部通过才会放下一个**普通蓝图**——它照常消耗材料、被搬运、需要施工。按钮不会凭空生成成品，也不会减免费用。任何一条不通过，按钮会灰掉，把鼠标移到上面就能看到原因。

**唯一例外是吊篮的存取口**：它是本 mod 的结构件，主机在造价里已经付了两端，所以输入按钮直接补一个成品存取口（和主机自己完工时自动生成的行为一致），不会多一分钱。

## 每种建筑的对位规则

| 建筑 | 对位对象 | 方向 |
|---|---|---|
| 电力端子 | 它自己 | 上、下 |
| 竖直管道（Dubs 系 / Vanilla Expanded 系） | 它自己 | 上、下 |
| 垂直电梯站（`RL_LiftVertical`） | 它自己 | 上、下 |
| 吊篮端（手动／电动，向上／向下） | 吊篮存取口 | 只有造价里那一侧 |
| 单层电梯、楼梯 | 无对位按钮 | — |

单层电梯和楼梯本身就是「跨到相邻层」的那一件东西，两端不是两个建筑，所以没有这个按钮；它们原来的「向上／向下扩展竖井」按钮各管各的。

## 层不存在怎么办

按钮会显示「B1 还不存在」之类的提示并灰掉。先造通往那一层的楼梯或竖井——那是这个 mod 打开楼层的正规入口，也是它扣材料、挖岩石、铺落脚楼面的地方。开了层之后按钮自然可用。

## 开发者快速完成

打开开发者模式并开启上帝模式后（`DebugSettings.ShowDevGizmos` = 开发者模式 ∧ 上帝模式），相关东西上会多出几个调试按钮：

| 按钮 | 出现条件 | 作用 |
|---|---|---|
| `DEV: finish now` | 选中蓝图或框架 | 相当于一个建造工把它做完（框架走原版 `Frame.CompleteConstruction`） |
| `DEV: clear the way` | 这个连接件上游还挡着岩石 | 打掉那块岩石（含正常矿石掉落），然后推进竖井 |
| `DEV: finish the shaft` | 电梯站正在扩井 | 清岩石 → 开落脚楼面 → 放原生站点蓝图 → 直接把蓝图做成成品 |
| `DEV: matching end above/below, built` | 该方向有对位建筑 | 放对应端并立刻完成 |

这些按钮都走 mod 自己的提交路径（原版 `Frame.CompleteConstruction`、`TickExtensions`、`LayerPairing.Check`），不直接改状态，所以不会造出正常玩法会被拒绝的建筑。方便的就是「立即打通楼层」：电梯站上按一次 `DEV: finish the shaft`，岩石、落脚楼面和上层站点全部到位。

关掉上帝模式（或退出开发者模式）后按钮立即消失，存档里不会留下半套状态。

## 相关文件

- `Source/RimLayers/Structure/LayerPairing.cs` —— 规则、按钮、开发者按钮、运行时派生登记
- `Source/RimLayers/Structure/LayerPairMath.cs` —— 楼层带内的坐标换算（纯整数，可单测）
- `Tests/PairingTests/` —— 坐标换算的纯逻辑测试
- `Defs/ThingDefs_Buildings/RimLayers_Elevators.xml` —— `RL_LiftVertical` 的 `LayerPairProperties`

对位属性也可以用在你自己写的 ThingDef 上：

```xml
<modExtensions>
  <li Class="RimLayers.Structure.LayerPairProperties">
    <partner>某个ThingDef</partner>   <!-- 省略则配对自身 -->
    <directions><li>1</li><li>-1</li></directions>
    <companion>false</companion>      <!-- true = 本 mod 拥有的助手件，直接给成品 -->
  </li>
</modExtensions>
```

不写这个扩展也能用：带 `CompProperties_PowerRiser`、`CompProperties_LayerPort`、`CompProperties_ResourcePort` 的 def 会自动登记为上下自配对；带 `CargoProperties` 且 `direction ≠ 0`、`shaft=false`、`station=false` 的 def 会自动配对 `RL_CargoPort` 作为成品助手件。这套派生在 `[StaticConstructorOnStartup]` 里跑一次，日志会写 `[RimLayers] N def(s) registered as paired-layer buildings.`。

## 验证

- `Tests/PairingTests` —— 127 项坐标换算断言，`Run-UnitTests.ps1` 里跑。
- `Run-V1.ps1 -Scenario pairing` —— 游戏内场景：三个楼层、按钮可用性、原生蓝图、无楼面／无楼层拒绝、电梯对位、吊篮存取口补回、开发者按钮显隐与成品化。

## 实现要点（给改代码的人）

`CargoNetwork.Ports` **包含主机自己**：`Building_CargoHoist` 继承自 `Building_CargoPort`，它的 `Master = this`。想列出"某台主机的存取口"必须 `p != master && p.Master == master`，否则会把主机一起当成端口——测试里第一次就是这么写的，结果把主机自己 `Destroy` 掉了，报 `Tried to destroy already-destroyed thing RL_CargoPort`。

对位关系来自三处，优先级从高到低：

1. def 自己在 XML 里声明的 `LayerPairProperties`（`RL_LiftVertical` 走这条）。
2. `LayerPairingBootstrap` 的运行时派生：带 `CompPowerRiser` / `CompLayerPort` / `CompResourcePort` 的 def 上下自配对；带 `CargoProperties{direction≠0, shaft:false, station:false}` 的 def 配对 `RL_CargoPort` 且 `companion=true`。
3. 都没有 → 没有按钮。

派生只在 `[StaticConstructorOnStartup]` 跑一次，不写存档。**注意 `ThingDef.HasComp(Type)` 比的是 `comps[i].compClass`（`Comp` 类型），不是 `CompProperties` 类型**——写错只会静默少登记，不会报错。

`kind` 字段只是诊断标签，不参与逻辑；XML 声明的那条没有赋值，所以断言别去校验 `kind`。

## 验收证据

| 项目 | 结果 | 位置 |
|---|---|---|
| 纯逻辑坐标换算 | 127 断言通过 | `Tests/PairingTests`（含在 `Run-UnitTests.ps1` 的 16 套件内） |
| 全量纯逻辑回归 | 16/16 套件通过 | `Run-UnitTests.ps1` |
| 游戏内场景 | **34 断言，0 失败，0 运行时错误** | `Tests/Artifacts/v1-pairing-20261029-184424` |

游戏内场景实际走过的路径（不是只查显隐）：

- 电力端子按钮 → 上层出现 `Blueprint_RL_PowerRiser`，**不是**成品；再按一次被 `RL_PairBlueprintExists` 拒绝。
- 无楼面格 → `Floor 2 has open air here, with no floor to build on.`，且什么都没放下。
- 缺层 → `B1 does not exist yet. Build the stairs or shaft that reach it first.`
- 电梯站按钮 → 上层 `Blueprint_RL_LiftVertical`。
- 吊篮：先删掉自动生成的存取口，按按钮后**成品** `RL_CargoPort` 回到上层同格，且不留下蓝图或框架。
- 开发者按钮：无上帝模式时全部不可见；`DEV: finish now` 把 `Frame` 经 `Frame.CompleteConstruction` 变成成品；`DEV: matching end above, built` 直接产出成品。
- **`DEV: finish the shaft`**：真的用 `StartExtension(-1)` 开了向下扩展（出口是 `Granite` 岩石），按一次后岩石被清掉、下一层出现完成状态的 `RL_LiftVertical` 站点、扩展状态清除。

`About/About.xml` 与 `RimLayers.csproj` 的版本号仍是 0.1.5（本次未提升）。
