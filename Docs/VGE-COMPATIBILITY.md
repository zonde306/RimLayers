# Vanilla Gravship Expanded 兼容

启用 Chapter 1（vanillaexpanded.gravship）时自动启用适配；Chapter 2（vanillaexpanded.gravship2）按实际启用状态追加规则。不安装 VGE 时继续走原生路径，不增加程序集或 modDependencies 硬依赖。

## 安装与使用

加载顺序：Harmony → Odyssey / Vanilla Expanded Framework → VGE Chapter 1 → Chapter 2（如使用）→ RimLayers。现有加载排序由 loadAfter 提示；VGE 未加入 modDependencies。更新 DLL 后重启游戏。

在普通结构平台上建原生逆重基架，再放对应引擎；上甲板仍用 RimLayers 飞船结构楼板。Gravjumper 使用 pilot cockpit，gravhulk 使用 pilot bridge，普通引擎使用 pilot console，适配不会解除 VGE 的驾驶设施型号匹配。

燃料罐和推进器可随上层甲板运输、链接到其他层的本船引擎，但实际燃料管道仍需连通；楼层投影不会连接两根不同层的管道。多层船的着陆保持完整净空检查，不通过 VGE 撞毁障碍式降落清空殖民地楼板或建筑。

## 已确定规则

- 按用户确认，VGE 下保留其全局单活动引擎规则；纯原生仍允许多船，船体归属不能合并。
- 支持原生引擎及 VGE 的 gravjumper / gravhulk 引擎。敌方特殊引擎不自动视作玩家多层船。
- 保留燃料管网、推进器缺燃料、维护、研究、引力异常、Chapter 2 干扰与远程锁定。管网仍遵循 VGE 的实际连接，不凭楼层投影免费连通。
- 普通楼板与飞船楼板继续按格独立归属；高层建造、起飞分离、跨界建筑确认拆除、停靠高度与全存档楼层配额沿用 RimLayers 现有规则。
- VGE subscaffold 仍随船运输但不占承载容量；不能因适配而变成可放建筑的普通船板。
- 真空屏障顶颜色按船内层坐标保存，旋转/高层降落后还原到对应楼层。
- VGE 仪式直接起飞入口必须在扣燃料前完成分离确认与目标配额预留。

## 验证

Release 构建通过，0 警告 / 0 错误。以下均为独立配置的真实游戏运行，结果 JSON 中 errors=[]：

| 配置与场景 | 结果 | Artifacts 目录 |
|---|---|---|
| 无 VGE / VEF，原生高层引擎完整起飞、降落、返航 | 生成 49、飞行重载 26、落地重载 10 项通过 | v1-gravship-20261021-024545 |
| 仅 Chapter 1，三种引擎双层转运 | 生成 43、重载 5 项通过 | v1-ship-vge-20261021-025241 |
| Chapter 1 + 2，含仪式选择、远程锁与干扰 | 生成 47、重载 5 项通过 | v1-ship-vge-20261021-025425 |

画面复核：v1-ship-vge-visual-20261021-025605 复用最终飞行存档，重载 5 项通过、errors=[]；实看第 3 层的旋转驾驶台、白银、subscaffold 与洋红色真空屏障顶。初始截图停在世界地图，修正测试视图后取得实际甲板画面：

![VGE 飞船上层甲板重载后](../Tests/Artifacts/v1-ship-vge-visual-20261021-025605/vge-landed-upper-deck.png)

目录均在 Tests/Artifacts 下。Chapter 1 与原生证据在后续不影响这些路径的改动中继续有效；最后的仪式窗口修正由两章场景专项验证。

VGE 三种引擎逐一测试：本船跨两层 98 格、匹配驾驶设施跨层连接、subscaffold 只计 97 格承载、原生载荷捕获、普通停机坪保留、旋转高层落地、原货物身份、再次起飞无重复。最终原生型号的 VGE 双层世界飞行对象保存、重载、落地，保持上甲板颜色与 37 个白银原堆叠。

燃料测试使用真实 VGE 油罐、管道、推进器：空网禁用，注入 200 后启用，原生启航资格通过；VGE provider 按 25% 航程比例实际从 200 扣至 192.5。非法目的地从 VGE 仪式执行入口拒绝，扣费与过场均未发生。第二章远程锁保持原拒绝文本；地面干扰器阻止同逻辑位置上层着陆，移除后恢复允许。可取消仪式选址动作保持未包装，额度在执行起飞入口处理。

尚未单独跑完 VGE 的完整仪式 UI、整场舰战、突袭/轨道任务及每种特殊敌舰流程；本次验证的是玩家多层船的转运、设施、资源与起降约束。保留 VGE 对这些玩法的原实现。

复现命令（游戏根目录，PowerShell 7）：

    ./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-vge -Vge 1 -AllDlc -TimeoutSeconds 480
    ./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario ship-vge -Vge 2 -AllDlc -TimeoutSeconds 480
    ./Mods/RimLayers/Tests/Run-V1.ps1 -Scenario gravship -NativeShip -AllDlc -EngineLayer 1 -TimeoutSeconds 480

### 发现并修复的问题

- 023908：VEF 在 RimLayers 替换 RoofAt 后找不到阴影补丁位置；已明确补丁顺序。初始测试误给两种特殊引擎放普通 console，改用其定义允许的 cockpit / bridge。
- 024104：型号改正后跨层仍未连接，定位到另一处同层设施筛选仍只认原生 GravEngine，已与候选筛选一并修复。
- 024330：测试推进器尾焰朝向船内，原生正确拒绝 CanLink；将测试推进器朝外后燃料功能通过，没有放宽游戏净空规则。
- 024457：测试把第二章静态 Instance 字段当属性读取，修正测试反射入口。
- 024844：生成 46 项通过，重载发现测试只保存临时 Game.Gravship 引用；改成原生世界对象持有后 025128 的 46/5 项通过。未改生产序列化来迁就测试。
- 构建曾与自己的原生重载进程争用 DLL，等待该进程完整结束后构建成功，没有终止玩家进程。
- 025425：补充 VGE 先选目的地再开仪式的可取消边界，47/5 项通过。

源码修改前副本：Tests/Artifacts/vge-before-20261020-193610。

本机依据：2026-10-21 两个指定目录的 1.7 DLL、XML。Chapter 1 MVID 57be97aa5bc146fbafd0a60f764bf91e；Chapter 2 MVID 05f048ed829d4ceca8a79bd49c74b77c。定向核实 GravshipHelper、GetConnectedSubstructure、ExtraGravshipDataComp、ExecuteGravshipLaunch 与 Chapter 2 的 CanLaunch / IsValidCell 补丁。

## 实现入口

- Compatibility/VanillaGravshipCompatibility.cs：按 packageId 绑定反射接口，不引用 VGE/VEF DLL；引擎白名单、subscaffold 容量、全甲板发电负荷、颜色编码和 VGE 仪式扣费前确认；选址时的可取消仪式窗口不预占额度。
- 两处原生设施筛选同时识别 VGE 引擎，保留其 linkableFacilities 与 CanLink；型号不匹配、尾焰阻塞和缺燃料照常拒绝。
- VGE 的 subscaffold 2D 洪泛由本船归属与本层格替换；原生多层总容量按付费甲板数检查。
- VEF 屋顶阴影 transpiler 先运行，RimLayers 再替换实际屋顶查询，避免互相找不到指令。
- Chapter 2 干扰器只修正空间距离：同一逻辑 x/z 的不同层不会因存储带间距逃出干扰。供电、眩晕、范围与远程锁定继续由 VGE 判断。
- 原生与 VGE 的撤离后地图保留政策分别由各自原流程处理；适配不删改 VGE 世界战斗控制器。
