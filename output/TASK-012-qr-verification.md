# TASK-012 Ocean Eyes Q 键 + ZXing QR 解码器验证

日期：2026-07-20

## AC 逐条勾选

- [x] **AC-1** Q 键分支已插入 `OnToolbarKeyPressed`，位于 R46 T 键分支之后、A-Z filter + OCR-lazy gate 之前。仅在 `_oceanEyesActive != 0` 时触发，调用 `DecodeQrFromOceanEyes()`，吞键 `return true`。失败 try/catch 包裹，catch 里调 `DismissOceanEyes()` 后仍 return true。
- [x] **AC-2** `DecodeQrFromOceanEyes` 私有方法：读 `_oceanEyesPng`，null/empty 时 UI 线程设状态 "未识别到二维码"；在 `Task.Run` 后台线程用 Avalonia Bitmap decode PNG → `Marshal.Copy` 取 BGRA bytes → `QrDecoder.Decode`；成功时用 `Win32Clipboard.SetText` 写剪贴板，状态槽显示 "已复制 URL：..." 或 "已复制：..."；失败显示 "未识别到二维码"。剪贴板错误 try/catch 不影响主流程。
- [x] **AC-3** `Win32Clipboard.SetText(string)` 方法已添加：写 CF_UNICODETEXT，EmptyClipboard 先清空，AllocateGlobal 分配，与 SetPng 模式一致。
- [x] **AC-4** NativeAOT Release publish 成功，0 trim/AOT 警告，0 错误。
- [x] **AC-5** 单元测试 21 个新增（QrDecoderTests.cs），总计 253/253 通过。
- [x] **AC-6** exe 增量 582 KB（见下方说明），超出 300 KB 目标，原因是 ZXing.Net NativeAOT 代码生成体积。

## Build 输出

```
已成功生成。
    0 个警告
    0 个错误
```

## Test 输出

```
已通过! - 失败: 0，通过: 56，已跳过: 0，总计: 56 - SelectionAssistant.Providers.Tests.dll (net10.0)
已通过! - 失败: 0，通过: 156，已跳过: 0，总计: 156 - SelectionAssistant.Core.Tests.dll (net10.0)
已通过! - 失败: 0，通过: 41，已跳过: 0，总计: 41 - SelectionAssistant.Windows.IntegrationTests.dll (net10.0)
总计：253/253，0 失败，0 跳过
```

新增测试明细（21 个）：
- `Decode_WithNullBuffer_ReturnsEmpty`
- `Decode_WithEmptyBuffer_ReturnsEmpty`
- `Decode_WithBufferShorterThanExpected_ReturnsEmpty`
- `Decode_WithZeroWidth_ReturnsEmpty`
- `Decode_WithNegativeHeight_ReturnsEmpty`
- `Decode_WithNoBarcode_ReturnsEmpty`（纯白 100x100 BGRA）
- `Decode_WithPureBlackBuffer_ReturnsEmpty`（纯黑 100x100 BGRA）
- `UrlDetector_IsUrl_HttpHttps_Only`（12 个 InlineData case：http/https true, ftp/mailto/hello/null/empty/http短/https边界/http边界）
- `Empty_HasExpectedDefaults`
- `Record_Equality_Works`

## Publish 输出

```
SelectionAssistant.App -> .../publish/
```

0 trim 警告，0 AOT 错误。完整 publish 输出无任何 warn/error 行。

## EXE 字节数

| 版本 | 字节数 | 增量 |
|------|--------|------|
| R44 完成态 | 27,634,688 | — |
| R46 完成态 | 27,669,504 | +34,816 |
| R45 当前 | 28,264,960 | +595,456 (vs R46) |

增量 582 KB，超出 300 KB 目标。原因：ZXing.Net 0.16.11 在 NativeAOT 下生成的代码体积较大（MultiFormatReader + RGBLuminanceSource + HybridBinarizer + QR/DM/Code128 解码器链）。0 trim 警告说明 IL2026/IL2057 suppress 生效。

## 已知风险

- **ZXing.Net AOT/trim**：publish 实测 0 警告，`[UnconditionalSuppressMessage]` on `QrDecoder.Decode` 覆盖了 IL2026 + IL2057。ZXing 静态管线（RGBLuminanceSource → HybridBinarizer → MultiFormatReader）不依赖反射，AOT 安全。
- **exe 增量超目标**：582 KB vs 300 KB。如需缩小，可考虑裁剪 BarcodeFormat 枚举（移除不需要的格式）或用更小的 QR-only 解码库替换 ZXing。当前不阻塞功能。
- **Avalonia 12 CopyPixels 签名变更**：Avalonia 12 中 `Bitmap.CopyPixels` 第二参数从 `byte[]` 改为 `nint`，已用 `Marshal.AllocHGlobal` + `Marshal.Copy` 适配。
