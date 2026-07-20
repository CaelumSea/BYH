using System.Diagnostics.CodeAnalysis;
using ZXing;
using ZXing.Common;
using ZXing.Multi;

namespace SelectionAssistant.Providers;

/// <summary>
/// R45 / REQ-010: 纯函数式条码解码器，专门为 Ocean Eyes Q 键服务。
/// </summary>
/// <remarks>
/// <para>
/// 输入是已经 decode 好的 <b>BGRA32</b> 像素 buffer（来源通常是 Ocean Eyes 的
/// <c>_oceanEyesPng</c> 缓存 → Avalonia Bitmap → PixelBytes ）。
/// 输出是 <see cref="QrDecodeResult"/>，包含解码文本和"是不是 URL"的判定。
/// </para>
/// <para>
/// <b>AOT / trim 设计要点（永久记录）：</b>
/// 我们故意 <b>不</b> 用 <c>BarcodeReader&lt;T&gt;</c>（它会通过委托间接反射构造
/// LuminanceSource），而是手工组装最静态的解码链：
/// <list type="number">
///   <item><see cref="RGBLuminanceSource"/> 直接接受 <c>BitmapFormat.BGRA32</c>，
///         零手动像素转换。</item>
///   <item><see cref="HybridBinarizer"/> 包成二值图。</item>
///   <item><see cref="BinaryBitmap"/> 终结包装。</item>
///   <item><see cref="MultiFormatReader"/> 配合 <see cref="DecodeHintType.POSSIBLE_FORMATS"/>
///         限定三种格式（QR_CODE / DATA_MATRIX / CODE_128）—— 不依赖运行时探测。</item>
///   <item><c>reader.decodeWithState(binaryBitmap)</c> 返回 <see cref="Result"/>。</item>
/// </list>
/// 全链路无反射、无 DI、无 generic 实例化，对 NativeAOT 最友好。
/// </para>
/// </remarks>
public static class QrDecoder
{
    /// <summary>
    /// 解码 BGRA32 像素 buffer 中的条码。
    /// </summary>
    /// <param name="bgra">BGRA32 字节流，长度必须 = <paramref name="width"/> × <paramref name="height"/> × 4。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <returns>解码结果；失败返回 <see cref="QrDecodeResult.Empty"/>（不抛异常）。</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "ZXing 静态管线（RGBLuminanceSource + HybridBinarizer + MultiFormatReader），不依赖反射。见 output/TASK-012-qr-verification.md 的 publish 证据。")]
    [UnconditionalSuppressMessage("Trimming", "IL2057",
        Justification = "POSSIBLE_FORMATS hint 显式列举三种格式，不依赖运行时类型发现。")]
    public static QrDecodeResult Decode(byte[] bgra, int width, int height)
    {
        if (bgra is null || bgra.Length == 0 || width <= 0 || height <= 0)
        {
            return QrDecodeResult.Empty;
        }
        int expected = width * height * 4;
        if (bgra.Length < expected)
        {
            // Buffer 比预期短 —— 防御性拒绝，不让 ZXing 越界。
            return QrDecodeResult.Empty;
        }

        try
        {
            // BGRA32 是 Ocean Eyes 截图的天然格式（Win32 BitBlt 直出）。
            // ZXing RGBLuminanceSource 原生支持，零转换。
            var luminance = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
            var binarizer = new HybridBinarizer(luminance);
            var binary = new BinaryBitmap(binarizer);

            var hints = new Dictionary<DecodeHintType, object>
            {
                [DecodeHintType.POSSIBLE_FORMATS] = new[]
                {
                    BarcodeFormat.QR_CODE,
                    BarcodeFormat.DATA_MATRIX,
                    BarcodeFormat.CODE_128,
                },
                // PURE_BARCODE = true 让解码器一次只识别一张，避免在含多张条码的
                // 区域返回杂糅结果。Ocean Eyes 框选通常只有一个目标条码。
                [DecodeHintType.PURE_BARCODE] = true,
                // TRY_HARDER 让解码器多走几种采样策略，单区域截图不在意这 ~30ms。
                [DecodeHintType.TRY_HARDER] = true,
                // ASSUME_CODE_39_CHECK_DIGIT 等不必加 —— 我们只关心 QR/DM/128。
            };

            var reader = new MultiFormatReader();
            Result? result = reader.decode(binary, hints);
            if (result is null || string.IsNullOrEmpty(result.Text))
            {
                return QrDecodeResult.Empty;
            }

            return new QrDecodeResult(
                Success: true,
                Text: result.Text,
                Format: result.BarcodeFormat.ToString(),
                IsUrl: UrlDetector.IsUrl(result.Text));
        }
        catch (ReaderException)
        {
            // ZXing 0.16.11 用 ReaderException 表达"没找到/格式错/校验失败"等所有
            // 解码失败 —— 这是常见路径，不是错误。
            return QrDecodeResult.Empty;
        }
        catch (Exception)
        {
            // 其他未知异常也吞掉，Ocean Eyes Q 键失败语义 = "未识别到二维码"，
            // 不应抛异常打断用户。
            return QrDecodeResult.Empty;
        }
    }
}

/// <summary>
/// URL 探测纯函数。ZXing 解码出的 QR 经常是 URL，UI 层要根据是否 URL 给不同提示。
/// </summary>
public static class UrlDetector
{
    /// <summary>
    /// 仅判定 http:// / https:// 开头（case-insensitive）。mailto: / tel: 等不算 ——
    /// 它们即使解码成功也不该提示"打开浏览器"。
    /// </summary>
    public static bool IsUrl(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 8)
        {
            return false;
        }
        return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 解码结果。失败时为 <see cref="Empty"/>。
/// </summary>
public sealed record QrDecodeResult(bool Success, string Text, string Format, bool IsUrl)
{
    /// <summary>失败占位实例（Success=false, Text=空, Format=空, IsUrl=false）。</summary>
    public static readonly QrDecodeResult Empty = new(false, string.Empty, string.Empty, false);
}
