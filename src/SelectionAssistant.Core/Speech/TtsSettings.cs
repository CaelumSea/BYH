namespace SelectionAssistant.Core.Speech;

/// <summary>Where the active Speak credential is resolved from.</summary>
public enum TtsCredentialSource
{
    None,
    ByhSecret,
    MmxConfig,
}

/// <summary>
/// 朗读 (text-to-speech) 功能配置。选中文字 → 调 MiniMax T2A 合成 mp3 → 后台播放。
/// 复用 <c>~/.mmx/config.json</c> 已登录的 MiniMax 密钥（<see cref="ApiKeyReference"/>
/// 为空或对应密钥未设置时自动回退到 mmx 的配置）。镜像 <c>VisionCaptureSettings</c>
/// 的 record 惯例：<see cref="Default"/> / <see cref="Normalize"/> / <see cref="Validate"/>。
/// </summary>
public sealed record TtsSettings
{
    /// <summary>
    /// Master switch. When false, the 朗读 button on the toolbar does nothing
    /// (still visible so the layout is stable). Default true.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// DPAPI secret reference for a BYH-managed MiniMax API key, e.g.
    /// <c>"secret://tts/minimax"</c>. When the referenced secret is absent,
    /// <c>MmxConfigReader</c> falls back to <c>~/.mmx/config.json</c>'s
    /// <c>api_key</c> / <c>oauth.access_token</c>. Null/empty forces the mmx
    /// fallback (useful when the user already ran <c>mmx auth login</c>).
    /// </summary>
    public string? ApiKeyReference { get; init; } = "secret://tts/minimax";

    /// <summary>"global" → https://api.minimax.io ; "cn" → https://api.minimaxi.com
    /// (note: cn host is minimax<b>i</b>.com). Default "global".</summary>
    public string Region { get; init; } = "global";

    /// <summary>MiniMax T2A model id. Default <c>speech-2.8-hd</c>.</summary>
    public string Model { get; init; } = "speech-2.8-hd";

    /// <summary>
    /// Voice used when content is pure English (no CJK ideographs). Default
    /// <c>Spanish_CaptivatingStoryteller</c> — a cross-lingual voice (user-curated
    /// 2026-07-30): speaks whatever language the text is in while keeping a
    /// consistent storyteller timbre, and the English rendition was confirmed
    /// good. See ~/tts-output/voice-pickup.md.
    /// </summary>
    public string EnglishVoice { get; init; } = "Spanish_CaptivatingStoryteller";

    /// <summary>
    /// Voice used when content is pure Chinese (has CJK ideographs but no Latin
    /// letters). Default <c>Chinese (Mandarin)_Warm_Bestie</c> — 暖心闺蜜
    /// (alias "bestie"), user-curated pick (2026-07-30) for 日常 vlog、推荐、
    /// 生活贴士. See ~/tts-output/voice-pickup.md.
    /// </summary>
    public string ChineseVoice { get; init; } = "Chinese (Mandarin)_Warm_Bestie";

    /// <summary>
    /// Voice used when content mixes Chinese and Latin scripts (has both CJK
    /// ideographs and Latin letters, e.g. "今天用了 iPhone 感觉很 nice").
    /// Default <c>Japanese_CalmLady</c> (alias "jp-calm") — one of two
    /// cross-lingual treasures in the curated collection (user-curated
    /// 2026-07-30): speaks whatever language the text is in while keeping a
    /// consistent calm timbre, confirmed good across JP/EN/CN. Mixed content is
    /// exactly where a cross-lingual voice shines. See ~/tts-output/voice-pickup.md.
    /// </summary>
    public string MixedVoice { get; init; } = "Japanese_CalmLady";

    /// <summary>
    /// Speed multiplier passed to MiniMax voice_setting.speed. 0.5–2.0. Default
    /// 1.1 — matches the speed at which the curated voice collection was selected
    /// (per ~/tts-output/voice-pickup.md), so playback lands on the intended timbre.
    /// </summary>
    public double Speed { get; init; } = 1.1;

    /// <summary>
    /// Cap on input text length sent to TTS. MiniMax accepts up to 10k chars but
    /// synthesis latency + token cost grow steeply; 2000 chars (~minutes of audio)
    /// is a sane default for a selection-toolbar feature.
    /// </summary>
    public int MaxCharacters { get; init; } = 2000;

    public static TtsSettings Default { get; } = new();

    /// <summary>
    /// Returns a copy with null/whitespace string fields restored to their
    /// defaults and out-of-range numerics clamped. Mirrors the Normalize
    /// convention used by every other settings record.
    /// </summary>
    public TtsSettings Normalize() => this with
    {
        Region = string.IsNullOrWhiteSpace(Region) ? Default.Region : Region.Trim(),
        Model = string.IsNullOrWhiteSpace(Model) ? Default.Model : Model.Trim(),
        EnglishVoice = string.IsNullOrWhiteSpace(EnglishVoice) ? Default.EnglishVoice : EnglishVoice.Trim(),
        ChineseVoice = string.IsNullOrWhiteSpace(ChineseVoice) ? Default.ChineseVoice : ChineseVoice.Trim(),
        MixedVoice = string.IsNullOrWhiteSpace(MixedVoice) ? Default.MixedVoice : MixedVoice.Trim(),
        Speed = Speed <= 0 ? Default.Speed : Math.Min(Math.Max(Speed, 0.5), 2.0),
        MaxCharacters = MaxCharacters <= 0 ? Default.MaxCharacters : Math.Min(MaxCharacters, 10000),
    };

    /// <summary>Asserts the post-Normalize invariants.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new ArgumentException("TTS Model must not be empty.", nameof(Model));
        }
        if (string.IsNullOrWhiteSpace(EnglishVoice))
        {
            throw new ArgumentException("TTS EnglishVoice must not be empty.", nameof(EnglishVoice));
        }
        if (string.IsNullOrWhiteSpace(ChineseVoice))
        {
            throw new ArgumentException("TTS ChineseVoice must not be empty.", nameof(ChineseVoice));
        }
        if (string.IsNullOrWhiteSpace(MixedVoice))
        {
            throw new ArgumentException("TTS MixedVoice must not be empty.", nameof(MixedVoice));
        }
    }
}
