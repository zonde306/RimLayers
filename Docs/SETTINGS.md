# 设置、扩层与异常实体恢复

入口：**模组设置 → RimLayers**。五页设置全局共用；恢复队列、已生成楼层和运输预留属于各自存档。

## 先选生效时机

| 页面 / 设置 | 范围；默认 | 生效时机 |
|---|---|---|
| 显示：换层过渡动画 | 开启 | 即时；关闭恢复瞬切 |
| 显示：楼层面板、快捷键提示、楼梯标记、返层按钮、规划边界 | 各自开关；全开 | 即时 |
| 显示：下层背景与拾取、漂浮雾效、战斗信息框、轨迹预览 | 各自开关；全开 | 即时 |
| 楼层：单图地上 / 地下上限 | 分别 0–9；各 2 | 仅限制新增 |
| 楼层：全局额外楼层上限 | 0–100；20 | 限制跨地图新增，含运输预留 |
| 楼层：允许太空地图扩层 | 开启 | 约束建造、安装、直接生成连接器及飞船接收新增层 |
| 玩法：楼梯、通电电梯、应急电梯耗时 | 各自 0.25–4 倍；1 | 即时，同时改变选路和实走 |
| 玩法：直射高差射程效果 | 0–2 倍；1 | 即时，射程、AI 搜索和预览共用；曲射不变 |
| 玩法：建造材料、建造工时、电梯耗电 | 各自 0.25–4 倍；1 | 重启后，只改 RimLayers 自有定义 |
| 地下生成：矿物数量 | 0–3 倍；1 | 仅新生成地下层 |
| 地下生成：洞穴、初始洞穴虫巢 | 各自开关；开启 | 仅新层；虫巢仍受难度和剧本限制 |
| 兼容与恢复：自动恢复异常空域实体 | 开启 | 停止开关只影响新接管，不清空已有队列 |
| 兼容与恢复：恢复方式 | 先下落，再安全传送 | 可改为优先安全复位；不改变正常坍塌 |

恢复默认需要确认，不删除已有层或暂存内容。重启类设置改动后显示提示；已有蓝图需求和拆解返还使用当前定义，不保存历史造价。

换层动画用轻微缩放、上下位移和交叉淡入表达升降，持续 320 毫秒，不受暂停和游戏倍速影响。点击地图下令、拖动或缩放会立即结束动画；自动定位保持瞬切。楼层面板、工具栏和窗口不参与动画。

楼梯标记和返层按钮可分别开关，均默认开启并即时生效。升级时，两个选项都继承原合并开关的状态；之后独立保存。

隐藏楼层面板后，F7/F8 等楼层快捷键仍有效；山体迁移入口保留在“兼容与恢复”。关闭下层背景也关闭不可见下层目标拾取。电梯停站、供电和禁用操作仍在对应建筑的原有控件中。太空不显示大气雾效。

## 配额如何计算

地表是海拔 0，不占额外层额度。单图上下各设为 5 时，共 11 层；硬容量为 −9…+9。布局保留原有存储带和海拔表，追加顺序不移动旧层。

全局占用是当前存档所有已加载地图的 `LayerCount - 1` 之和。空层也占额度；切换地图、拆楼梯或飞船离开都不释放。只有地图真正移除后才扣除其占用。楼层页显示已生成、已预留、上限、每张地图占用和额外存储格数。

每次新增同时检查单图上下限、全局额度和硬容量。批量追加检查整个增量；预览不分配，实际追加前再次检查。降低上限、恢复默认或载入超额旧档都保留已有楼层、通行和维修，只阻止继续新增。原版基础地图仍可正常创建。

太空以地图的原生 `isSpace` 标记识别。关闭太空扩层不妨碍在足够的已有层上使用连接器和降落，但不能因此新建太空层；打开该开关也不允许太空地下开挖。

### 飞船先预留，抵达后分配

出发前、原版扣燃料和拆离载荷之前，为目的地所需的额外层预留额度。已有目的地只按缺少的增量预留，新目的地按完整需求预留。预留随运输存档，分配后转为真实占用；取消或失败释放未分配部分，重复回调不重复扣除。

已经出发或持有有效预留的运输可以完成承诺范围内的接收，即使随后降低上限或关闭太空扩层。旧档在途船也会保全。改选更高落点所需的额外层必须重新检查。载荷最多跨 6 层地表以上甲板，含中间空层；最高点不能超过海拔 +9。地下飞船和第三方飞船框架适配不在此功能内。

配额用于控制资源占用，不保证固定帧率。楼层越多，存储格、地形和相关原版网格开销越高。

## 如何恢复异常实体

自动恢复针对 RimLayers 地图上的 Pawn、普通物品和尸体，处理无承托空域、存储隔离带或非法地图坐标。单纯不可寻路不算空域异常；床上的 Pawn、被携带者和容器内容不由通用恢复搬出。飞行、跳跃载体、弹丸、特效、空投和正常运输由各自流程处理。无承托的未知建筑、蓝图和框架只告警，不自动搬动或拆开其容器。正常的结构楼板／飞船结构楼板蓝图和框架按施工规划规则检查，不要求未完工楼板已经形成可走表面。

默认顺序：

1. 从可靠原海拔向下，寻找最近承托面；不穿过楼板、屋顶或岩体。第一接触层堵塞时，不继续向更低层穿透。
2. 尝试对象仍合法的最近有效位置。
3. 尝试原层附近，再查找同地图其他安全位置。敌对 Pawn 不会被默认吸引到殖民者身边。
4. 无合法落点时，将原对象放入持久化恢复容器并合并告警。

非法坐标无法确定海拔时跳过下落，不取模猜层。“优先安全复位”直接从第 2 步开始。所有落点都检查真实层、完整占地、承托、阻挡和原版即时危险；不把不耐真空的 Pawn 放入暴露太空，不驱散、覆盖或删除其他实体。恢复不自动扩层、造地板或跨地图传送。

保留原对象身份、派系、装备、背包、携带物、物品属性和尸体内容，不销毁重建。兼容纠错不附加伤害；正常结构坍塌保持每下降一层 8 点钝伤，只结算一次。

### 待恢复列表

“待恢复”提示对应仍在队列中的实体；通知发出前已经恢复的对象不再计入。建筑／施工规划异常及巡检失败使用单独提示，详情见日志，不要求到待恢复列表处理。

打开 **兼容与恢复 → 待恢复列表**：

- **定位**：查看仍存在且坐标可识别的来源位置。
- **重试**：重新尝试自动查找合法位置；反复被其他模组移走的对象会暂停自动纠正，可手动检查后重试。
- **指定落点**：选择对象、地图和楼层，输入该层逻辑 X/Z 坐标，再选择“恢复至此格”。非法位置会被拒绝。

自动重试仅使用原地图。原地图已移除时，可明确选择其他地图并确认跨图恢复；普通未扩层地图也可作为目标，不会因此创建楼层。暂存使用游戏级 `ThingOwner` 保存真实对象，移除来源地图不会删除它。暂存期间对象自身按原版休眠语义暂停，不治疗、复活或复制；世界事件和任务计时照常进行。关闭自动恢复不删除暂存对象，已有队列仍可重试和手动释放。

## 兼容补丁接口

恢复排除项填写 `ThingDef.defName`，用逗号分隔；只影响恢复，不改变地形、寻路和结构规则。临时运输事务可使用公开接口：

```csharp
using RimLayers.Recovery;

using (RecoveryCompatibility.BeginHandling(thing))
{
    // 由本模组独占的同步移动事务。
    MoveOriginalThing(thing);
}

// 异步流程应在持有实体期间维护自己的租约，或按明确时长延后检查。
RecoveryCompatibility.DeferFor(thing, ticks: 120);
```

`BeginHandling` 返回的租约必须在事务结束时释放；若异步流程跨帧持续，应保留并在完成/失败时 `Dispose`。`DeferFor` 按游戏 tick 延后，不能代替长期所有权。RimLayers 分批轮转检查 Pawn 和物品，所有地图共享预算；没有每 tick 全地图格子扫描。

## 存档与边界

**包含超出旧版 ±2 上限楼层的存档不能回退旧版 DLL 读取。** 更新前备份存档并另存新槽。使用楼层或暂存自有对象的存档持续依赖 RimLayers；更新 DLL/Harmony 后必须重启。

本设置功能不提供物理层高、地图尺寸、存储间隔修改，不删除已有层，不重生成已开发层，也不关闭核心多层补丁。太空地下生成、地下飞船及第三方框架专用适配未包含。CE、其他多层实现和自定义运输/悬浮机制应按各自兼容说明处理。

本次构建、原版游戏、存读档、性能与视觉检查结果见 [验收记录](SETTINGS-VALIDATION.md)。

---

# Settings, expansion quotas and recovery

Open **Mod settings → RimLayers**. Display, Floors, Gameplay, Underground and Recovery settings are global; generated floors, transport reservations and recovery contents belong to each save.

## Defaults and when changes apply

- **Display:** ten independent toggles, all on. Changes are immediate. Stair markers and return buttons are separate toggles; existing settings migrate both from the former combined switch, then save them independently. Floor transition animation uses a 320 ms zoom/vertical crossfade in real time; UI stays fixed. Map commands, dragging and camera input finish it immediately. Automatic camera jumps remain instant. Disable this toggle for instant manual switches. Hiding the floor panel keeps hotkeys active; mountain migration remains on the Recovery page. Hiding the lower background also disables picking invisible lower-floor targets. Space has no atmospheric mist.
- **Floors:** upper/lower limits each range from 0–9, default 2. The save-wide extra-floor limit is 0–100, default 20. Space expansion is on by default. These restrict new floors only.
- **Gameplay:** stair, powered-lift and emergency-lift times are independent 0.25–4 multipliers, default 1, applied to both routing and actual travel. The direct-fire height effect ranges 0–2, default 1; range, AI search and previews share it. Indirect fire is unchanged.
- **Restart required:** material cost, construction work and lift power are independent 0.25–4 multipliers, default 1. They affect RimLayers definitions only, once per startup. Existing blueprints and demolition refunds use current definitions, not historical prices.
- **New underground floors only:** mineral quantity 0–3, default 1; caves and initial cave hives on. Native difficulty/scenario restrictions still apply. Developed floors are never regenerated.
- **Recovery:** automatic recovery is on; the default order is fall, then safe reset. The alternative skips the fall attempt. Restoring defaults asks for confirmation and retains existing floors and recovery contents.

## New floors consume shared capacity

The surface is elevation 0 and does not consume an extra-floor slot. Limits of five above/five below allow eleven floors in one map; hard elevation bounds are −9…+9. Storage bands and existing coordinates remain stable when appending in different orders.

Usage is the sum of `LayerCount - 1` across all loaded maps. Empty floors count. Changing maps, removing stairs or departing in a ship does not release capacity; actual map removal does. The Floors page shows generated and reserved capacity, the limit, each map's usage and extra storage cells. Lower limits and over-limit old saves preserve existing access and repairs; only new allocation is denied. Batch additions check the full increment again at commit, and previews allocate nothing. Native base maps remain creatable.

Space uses native `isSpace`. Disabling expansion blocks additional space floors through construction, installation, connector spawning and ship reception, while permitting use/landing when existing floors suffice. It never enables underground excavation in space.

Ship destinations reserve their missing floors before native fuel use and payload detachment. Reservations persist in flight, convert to occupancy on allocation, and release unused capacity on cancellation/failure. Existing promises and old in-flight saves can complete their committed reception despite later setting changes; choosing a higher landing needs a new quota check. Payloads span at most six above-ground decks including hollow intermediate decks, with their highest elevation no greater than +9. Quotas protect resources rather than guarantee frame rate.

## Recovery preserves the original entity

Eligible Pawns, ordinary items and corpses in unsupported airspace, storage gaps or illegal coordinates enter a deferred recovery queue. Unreachability alone is not an anomaly. Beds, carried/container contents, native flight, jumping carriers, projectiles, effects and normal drop/ship transport retain their own handling. Unsupported unknown buildings, blueprints and frames produce warnings instead of being moved or unpacked. Valid structural-floor and ship-floor blueprints/frames use construction planning rules: the unfinished floor need not already be walkable.

The default search stops at the first physical contact plane, without penetrating a solid floor, roof or rock. Congestion does not allow continuing the fall through that plane. It then tries the last valid position, nearby original-floor cells and suitable cells elsewhere on the same map. Invalid coordinates never imply an altitude through modulo arithmetic. Hostile Pawns are not attracted toward colonists. All placements check support, full footprint, obstacles, occupancy and native immediate hazards including vacuum. Nothing is displaced, wiped or recreated, and recovery does not add floors.

If no legal destination exists, the original entity waits in a persistent game-level suspended holder. Identity, faction, equipment, inventory, carried objects, quality, durability and corpse contents are preserved. Its own simulation pauses under native suspension semantics; world events and quest timers do not. Compatibility correction adds no injury. Physical collapse still applies eight blunt damage per fallen floor once.

Pending-recovery notifications count entities still in the queue, excluding those resolved before the notification. Unsupported building/planning issues and inspection failures have separate messages with details in the log.

Open **Recovery → Pending recovery** to locate the source, retry, or select a map/floor and logical X/Z destination. Automatic retries stay on the original map. If that map is gone, choosing another map requires explicit confirmation. Ordinary unexpanded maps are valid manual destinations without allocating floors. Disabling automatic recovery stops new capture but retains existing contents and manual/automatic retries. Repeated external relocation pauses automatic correction for manual review.

Def exclusions use comma-separated `ThingDef.defName` values and affect recovery only. Compatibility code can hold `RecoveryCompatibility.BeginHandling(thing)` through its movement transaction and dispose it on completion/failure, or use `DeferFor(thing, ticks)` for a bounded delay. These do not change structural or pathing rules.

**Saves beyond the old ±2 floor limits cannot be read by an older DLL.** Back up and save to a new slot; restart after DLL/Harmony updates. Physical layer height, map size and storage spacing are not configurable. Existing floors cannot be deleted or regenerated, and core multi-layer patches cannot be disabled here. Space underground generation, underground ships and dedicated third-party framework adapters are outside this feature. See [the validation report](SETTINGS-VALIDATION.md) for native-game results, performance measurements and compatibility limits.
