namespace SelectionAssistant.Core.Capture;

/// <summary>
/// R24 track B: configuration for the screenshot→cloud-OCR capture tier.
/// <para><c>VisionProviderId</c> + <c>VisionModel</c> select which OpenAI-compatible
/// provider entry (from <c>providers.json</c>) and model id perform OCR. Keeping
/// the OCR model separate from the translation model lets users keep DeepSeek-V4
/// for translation while using <c>Qwen/Qwen3.5-4B</c> for capture.</para>
/// </summary>
public sealed record VisionCaptureSettings
{
    /// <summary>
    /// Master switch. When false, the vision OCR tier is skipped entirely (UIA +
    /// clipboard only). Default true per R24 design (free model, user-confirmed
    /// 2026-07-17); rationale for toggling off is latency/privacy, not cost.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Id of the provider entry in <c>providers.json</c> to use for OCR. Defaults
    /// to <c>"siliconflow"</c>. Must resolve to a vision-capable endpoint.
    /// </summary>
    public string ProviderId { get; init; } = "siliconflow";

    /// <summary>
    /// Model id for OCR, e.g. <c>Qwen/Qwen3.5-4B</c> (default),
    /// <c>PaddlePaddle/PaddleOCR-VL-1.5</c>, or a general vision model.
    /// Overrides the provider entry's <c>defaultModel</c>.
    /// </summary>
    public string Model { get; init; } = "Qwen/Qwen3.5-4B";

    /// <summary>
    /// OCR instruction prompt. Defaults to DeepSeek-OCR's official
    /// <c>"Free OCR."</c>. For PaddleOCR-VL use <c>"document parsing."</c>.
    /// </summary>
    public string OcrPrompt { get; init; } = "Free OCR.";

    /// <summary>
    /// When true, region capture tries UI Automation before OCR (both for the
    /// prefill box that follows the cursor AND for scanning text inside the
    /// drawn box). When false (default), region capture is pure OCR — captures
    /// exactly the drawn rectangle and is trustworthy on every kind of content.
    /// <para>
    /// Why default off: UIA's ancestor-walk can return text from outside the
    /// drawn box on apps whose UIA container structure doesn't match the visual
    /// layout (the "box doesn't match what gets read" problem). OCR's guarantee
    /// — "frame is frame" — is more important than UIA's speed/cleanliness on
    /// the apps where it does work. Users who want the UIA path on simple
    /// desktop apps can opt in; UIA-empty regions still fall back to OCR.
    /// </para>
    /// </summary>
    public bool UiaPrefillEnabled { get; init; } = false;

    /// <summary>
    /// When true, sends <c>enable_thinking: false</c> in the OCR request body.
    /// Required for hybrid reasoning vision models (Qwen3.x, DeepSeek-VL) that
    /// otherwise spend seconds generating reasoning_content before the actual
    /// OCR text — visible as latency even though reasoning_content is discarded.
    /// Pure OCR models (DeepSeek-OCR, PaddleOCR-VL) reject this param with
    /// HTTP 400, so turn it off when using those. Default true because the
    /// default Qwen3.5 model otherwise spends seconds producing reasoning.
    /// </summary>
    public bool DisableThinking { get; init; } = true;

    public static VisionCaptureSettings Default { get; } = new();
}
