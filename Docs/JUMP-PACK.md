# 跳跃背包与蝗虫装甲：跨层跳跃

征召装备跳跃背包或蝗虫装甲的人物，点击原版“跳跃”，再用 F7/F8 切到目标层并选择落点。无需楼梯、供电电梯或可步行路线。

更新 DLL 后先重启游戏。同层跳跃继续使用原版流程。

## 从平台边缘上下跳，不穿楼板

- 可以从开放空间跃上平台，或从平台边缘跳到下层。
- 轨迹上的楼板、飞船甲板、天然楼面、薄顶、厚顶和墙体都会阻挡。检查整条弧线及人物净空，不只检查起终点。
- 落点必须已揭示、可站立并有实际楼面承托，不能选空域。
- 当前地表是完整实体面，不能跳穿地表进入地下。楼梯、电梯连接不是飞行洞口。
- 从上方落到楼面是合法着陆；从下面穿过同一楼面不合法。
- 本功能只增加原版 `Verb_Jump` 的跨层能力。能力跳跃、第三方特殊飞行与 CE 未在本次接入。

高度差计入射程，不使用枪械的高低差倍率：

```text
距离² = 水平 Δx² + 水平 Δz² + (3 × 海拔差)²
```

普通背包仍读取装备的 JumpRange，原版默认 23.9；同层射程不变。

## 结构变化时保留人物与携带物

预热结束后，在扣跳跃次数前复核轨迹。新出现的天花板或失效落点会阻止起跳，不扣次数。

已经起飞后，每次模拟回调扫过完整前进区间。遇到新障碍或落点坍塌，先尝试障碍物这一侧、几何通畅且有承托的附近落点；不能借应急着陆穿板或传送到另一侧。

没有合法落点时，人物与携带物留在原生飞行容器中，暂停移动并定期重试。选中人物可使用“重试安全着陆”；清理落地区域后也会自动恢复。已经成功起飞的跳跃不重复扣次数。

落地恢复原人物、原携带物、征召/自由开火状态及排队指令，不创建人物副本。

## 实现入口

基准：RimWorld **1.7.4984 rev1242**、Waylith / .NET 10、Harmony 3；RimLayers 0.1.5。

- `Source/RimLayers/Navigation/JumpGeometry.cs`：逻辑二次曲线与人物包围体的解析扫掠；楼板/屋顶平面和建筑高度来自已有 `IShotScene`。
- `JumpService.cs`：选点、起跳前复核、三维射程与合法落点。坐标先解码，通过 `Elevation(layerId)` 换算物理高度。
- `JumpPatches.cs`：原版跳跃 Verb 的选点、下单、预览及启动；短作用域保护携带物交接。
- `LayerJumpFlyer.cs`：继承原版 `PawnFlyer`，保留原生人物/货物持有与落地流程，接管逻辑位置、逐 tick 扫掠、应急下降与附加存档。
- `Combat/CombatPatches.cs`：为跳跃的原版预热补齐 `TryFindShootLineFromTo` 分支，不把跳跃误判成不支持的非投射物 Verb。
- `Defs/ThingDefs_Misc/RimLayers_Jump.xml`：独立 `RL_LayerJumpFlyer`，不替换原版 PawnFlyer Def。

曲线脚点离楼面 0.03，升弧参数为 `max(1, (abs(高度差)+2)/4)`，保证从上方接近落点；人物高度沿用战斗系统的体型映射，水平半宽为 `clamp(0.22 × sqrt(BodySize), 0.15, 0.48)`。预览与实际扫掠使用同一曲线。位置由模拟 tick 更新，绘制不推进模拟。

原版落地先恢复队列、又立即进行一次作业覆盖查询，会将已恢复的 Goto 覆盖为 Wait_Combat。本载体把队列交还放到原生落地查询之后，只恢复一次。作用域结束后，普通人物的作业、携带与其他飞行器规则不变。

## 存档与复现

现有楼层布局不迁移。新载体保存逻辑起终点、升弧、人物净空、已扫掠进度与等待状态；人物、货物、剩余飞行时间和捕获队列仍由原生 PawnFlyer 保存。含有在途 `RL_LayerJumpFlyer` 的存档需要保留本次更新的 DLL。

在游戏根目录运行：

```powershell
dotnet run --project Mods/RimLayers/Tests/JumpTests/JumpTests.csproj -c Release
pwsh -NoProfile -File Mods/RimLayers/Tests/Run-V1.ps1 -Scenario jump -AllDlc -Language ChineseSimplified
```

真实游戏测试使用 `Tests/Artifacts/<run>` 独立配置。测试代码只在显式传入 `-rimlayersv1test` 时运行；不修改玩家存档、偏好或启用列表。

验收记录见 [JUMP-VALIDATION.md](JUMP-VALIDATION.md)。
