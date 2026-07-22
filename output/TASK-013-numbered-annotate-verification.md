# TASK-013 Ocean Eyes A 键 + 数字标注 Canvas + Skia 烧入验证

日期：2026-07-20

## AC 逐条勾选

- [x] **AC-1** Ocean Eyes 框选确认后按 A 进入数字标注模式：工具栏状态槽显示"标注模式 · 点击放序号 · Ctrl+Z 撤销 · A/Esc 退出"；鼠标左键不再触发 R41 重画（被标注模式吞键吞点击）。A 键分支位于 `OnToolbarKeyPressed` 的 Q 键分支之后、A-Z filter + OCR-lazy gate 之前。`_oceanEyesAnnotating` Volatile flag 控制模式状态。
- [x] **AC-2** 标注模式下，每次鼠标左键点击在点击位置放一个圆形 badge：直径 28 DIP，gold accent（#FFD9C28A）填充 + 1px 古金描边（#FFB8956A）+ 居中数字（白色 Bold 字号 14）；数字从 1 自增。Badge 在 `RegionSelectOverlay.AnnotationCanvas` 上渲染为 Ellipse + TextBlock 组合。
- [x] **AC-3** Ctrl+Z 撤销最近一个 badge；连续 Ctrl+Z 多次可逐个回退至空。通过 `GetKeyState(VK_CONTROL)` 检测组合键，`NumberedAnnotationSession.Undo()` + `RegionSelectOverlay.RemoveLastBadge()` 同步。
- [x] **AC-4** 再次按 A 或按 Esc 退出标注模式，badge 仍保留在 overlay 上；Enter 保存截图时，当前所有 badge 烧入 PNG（`BurnBadgesIntoPng` 用 BGRA 像素操作 + 内置 5x7 位图字体）；Esc 退出整个 Ocean Eyes 时不保存（`DismissOceanEyes` 清空 session + canvas）。
- [x] **AC-5** 标注 layer 是 Avalonia Canvas（`AnnotationCanvas`），挂在 `RegionSelectOverlay.RootCanvas` 内，仅 Ocean Eyes + 标注模式激活期间存在；无新增原生依赖；常驻内存增量 = 0（Canvas 在 overlay 关闭时随 Window 一起销毁）。
- [x] **AC-6** NativeAOT Release publish 0 警告 0 错误；Debug build 0 警告 0 错误；测试套件全过（280/280，含新增 27 个 NumberedAnnotationSession / NumberedBadgeGeometry 纯函数测试）。

## Build 输出

```
已成功生成。
    0 个警告
    0 个错误
```

## Test 输出

```
已通过! - 失败: 0，通过: 56，已跳过: 0，总计: 56 - SelectionAssistant.Providers.Tests.dll (net10.0)
已通过! - 失败: 0，通过: 183，已跳过: 0，总计: 183 - SelectionAssistant.Core.Tests.dll (net10.0)
已通过! - 失败: 0，通过: 41，已跳过: 0，总计: 41 - SelectionAssistant.Windows.IntegrationTests.dll (net10.0)
总计：280/280，0 失败，0 跳过
```

新增测试明细（27 个）：

**NumberedAnnotationSessionTests (11):**
- `Push_IncrementsNumberFromOne`
- `Push_StoresCoordinates`
- `Push_IncrementsCount`
- `Undo_RemovesLastBadge`
- `Undo_MultipleTimes_RemovesInReverseOrder`
- `Undo_ToEmpty_ReturnsTrueEachTime`
- `Undo_OnEmptySession_ReturnsFalse`
- `Undo_AfterClear_ReturnsFalse`
- `Clear_RemovesAllBadges`
- `Push_AfterUndo_AppendsWithCorrectNumber`
- `Badges_ReturnsReadOnlySnapshot`

**NumberedBadgeGeometryTests (16):**
- `GetRadius_At100Percent_Returns14`
- `GetDiameter_At100Percent_Returns28`
- `GetRadius_At175Percent_ScalesCorrectly`
- `GetDiameter_At200Percent_Returns56`
- `GetFontSize_At100Percent_Returns14`
- `GetFontSize_At150Percent_ScalesCorrectly`
- `GetStrokeThickness_At100Percent_Returns1`
- `GetStrokeThickness_At200Percent_Returns2`
- `GetStrokeThickness_At50Percent_ClampsToMinimum1`
- `GetPhysicalCenter_At100Percent_ReturnsSameCoordinates`
- `GetPhysicalCenter_At150Percent_ScalesCoordinates`
- `GetPhysicalCenter_At200Percent_DoublesCoordinates`
- `GetPhysicalBounds_At100Percent_CenteredOnBadge`
- `GetPhysicalBounds_At200Percent_ScalesAllDimensions`
- `Constants_HaveExpectedValues`
- `NumberedBadge_RecordEquality`

## Publish 输出

```
SelectionAssistant.App -> .../publish/
```

0 trim 警告，0 AOT 错误。

## EXE 字节数

| 版本 | 字节数 | 增量 |
|------|--------|------|
| R45 完成态 | 28,264,960 | — |
| R47 当前 | 28,283,392 | +18,432 |

增量 18 KB，远低于 100 KB 目标。无新增依赖（badge 烧入使用内置 5x7 位图字体 + Avalonia Bitmap decode，不引入 SkiaSharp）。

## 设计说明

### 简化方案
Badge 烧入 PNG 未使用 SkiaSharp（项目中无此依赖），而是：
1. 用 Avalonia `Bitmap.CopyPixels` 解码 PNG 到 BGRA 像素缓冲
2. 用纯 C# 圆形光栅化 + alpha 混合绘制填充和描边
3. 用内置 5x7 位图字体（scale=2）绘制白色居中数字
4. 用已有 `PngEncoder.Encode` 重新编码 PNG

这个方案避免了引入新依赖，exe 增量仅 18 KB。如果未来需要更高质量的文本渲染（如抗锯齿、非等宽字体），可引入 SkiaSharp 或用 Avalonia `RenderTargetBitmap` 替代。

### R48 预留抽象
- `NumberedAnnotationSession` 的 undo stack 抽象可直接被 R48 的矩形/椭圆/箭头/画笔工具复用
- `AnnotationCanvas` 的 add/remove/clear API 对任何 Canvas 子元素通用
- `NumberedBadgeGeometry` 纯函数模式可扩展为 `AnnotationToolGeometry` 基类
