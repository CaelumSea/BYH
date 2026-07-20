namespace SelectionAssistant.UI.Views;

/// <summary>
/// R24 track B: common OCR model ids offered in the settings dropdown. Public +
/// top-level so compiled bindings/AOT stay happy. The dropdown is also editable,
/// so users can type any model id their provider exposes.
/// </summary>
public static class VisionModelPresets
{
    /// <summary>
    /// Ordered list: the real-machine-verified default first, then alternative
    /// OCR/vision models. Availability and prices are provider-controlled.
    /// </summary>
    public static readonly string[] All =
    [
        "Qwen/Qwen3.5-4B",                  // default, 关思考后 <1s，桌面文字实测稳定
        "PaddlePaddle/PaddleOCR-VL-1.5",     // 免费, 中文文档/票据强
        "PaddlePaddle/PaddleOCR-VL-1.6",     // OmniDocBench SOTA, 升级版
        "Qwen/Qwen3.5-27B",                  // 通用视觉高精度, 付费
        "moonshot/kimi-k2.6",                // 手写最强, 付费
        "deepseek-ai/DeepSeek-OCR",          // 保留兼容；桌面截图曾出现严重幻觉
    ];
}
