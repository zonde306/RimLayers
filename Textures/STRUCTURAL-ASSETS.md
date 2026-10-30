# 电梯与屋顶素材

`Generate-StructuralTextures.py` 保存本项目原创几何贴图源，使用开发环境 Pillow 生成 128×128 PNG；游戏运行不依赖 Python。

- 蓝绿色双门为单层电梯；铜色双向标记为垂直电梯构件。
- 每张带方向箭头的贴图都有朝下的孪生版本：`LiftSingleDown` 由脚本用同一套几何重新绘制（箭头多边形换成 `DOWN_ARROW`），`StairsDown` 由脚本从手绘的 `Stairs.png` 派生——把米色箭头及其描边的像素在包围盒内上下镜像，再用同行最近的非箭头像素回填背景木板。`Stairs.png` 本身是手绘素材，脚本只读不写。
- 普通屋顶为连续横向接缝；飞船屋顶为带铆点金属板。两种屋顶均为不透明表面，由深度队列、实际屋顶及原生战争迷雾控制可见性。
- 原有薄雾素材和提示词仍在 [Atmosphere/README](Atmosphere/README.md)。
