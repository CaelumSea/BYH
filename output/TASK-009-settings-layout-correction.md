# TASK-009 · 设置页上下窗格纠正验证

日期：2026-07-20

## 结果

用户指出原布局仍把导航做成贯穿全高的独立列。现已按参考图修正为：

- 产品概览：左侧跨上下两排；
- 导航塔：仅位于上排；
- 中央设置与 Current setup：位于上排；
- `SYSTEM OVERVIEW`：位于下排，并横跨导航与中央设置两列；
- `Window controls`：保持右下独立窗格。

底部共享窗格采用“上方标题带 + 下方三组内容”的纵向结构，保留运行模式、主题预览、配置路径和两个目录按钮。五个导航入口及其 Click 事件不变。

## 验证

- Release build：0 warning / 0 error
- Tests：232/232（Core 156、Providers 35、Windows 41）
- NativeAOT：0 warning，0 PDB
- `BYH.exe`：27,674,112 bytes
- 默认尺寸：`artifacts/qa/ivory-jade-settings-v7-corrected-nativeaot.png`
- 175% DPI 最小尺寸（1240×680 logical）：`artifacts/qa/ivory-jade-settings-v7-corrected-minimum-nativeaot.png`

## 永久布局约束

不要把导航栏恢复为 `Grid.RowSpan="2"`。下方共享窗格必须保持 `Grid.Column="1" Grid.ColumnSpan="2" Grid.Row="1"`，从导航下方延伸至中央工作区下方。
