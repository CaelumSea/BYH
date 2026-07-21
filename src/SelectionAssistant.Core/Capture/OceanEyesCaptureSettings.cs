namespace SelectionAssistant.Core.Capture;

/// <summary>
/// R40: persistent settings for Ocean Eyes' screenshot + region-select
/// behavior. Independent of <see cref="VisionCaptureSettings"/> (which
/// controls the OCR provider/model). The user's answers:
/// <list type="bullet">
///   <item>Default save: file <b>and</b> clipboard (both can be turned off).</item>
///   <item>Save path is configurable; default = <c>%USERPROFILE%\Pictures\Ocean Eyes</c>.</item>
///   <item>UIA "assisted boxing" (live element-tracking while hovering) is on
///   by default; toggleable.</item>
/// </list>
/// </summary>
public sealed record OceanEyesCaptureSettings
{
    private static readonly string DefaultSavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "Ocean Eyes");

    /// <summary>Folder where <c>ocean-eyes-yyyyMMdd-HHmmss.png</c> files land.</summary>
    public string SavePath { get; init; } = DefaultSavePath;

    /// <summary>
    /// When true, pressing Enter while the Ocean Eyes toolbar is up writes the
    /// captured region to <see cref="SavePath"/>. When false, Enter only copies
    /// to the clipboard (if <see cref="CopyToClipboardEnabled"/> is on).
    /// </summary>
    public bool AutoSaveEnabled { get; init; } = true;

    /// <summary>
    /// When true, the captured PNG is also placed on the clipboard regardless
    /// of <see cref="AutoSaveEnabled"/>. Default true so the user can always
    /// paste the shot into any app even when file-saving is off.
    /// </summary>
    public bool CopyToClipboardEnabled { get; init; } = true;

    /// <summary>
    /// R40 Ocean Eyes: when true, hovering the cursor over desktop elements
    /// during region-select snaps the preselection box to the element's
    /// bounding box (UIA live tracking). Off = pure free-draw. Default true.
    /// The user-touched latch in the overlay always wins: once the user draws
    /// or resizes, tracking stops for that session.
    /// </summary>
    public bool UiaAssistEnabled { get; init; } = true;

    // ── R51 Beautify (B key) ─────────────────────────────────────────
    // These mirror the defaults in <see cref="BeautifyOptions"/>. They're
    // persisted so users can hand-edit ocean-eyes-capture.json without a
    // schema bump (v1 settings files missing these fields fall back to
    // the in-code defaults). No settings UI in v1 — leave defaults for
    // 99% of users.

    /// <summary>Padding added around the screenshot by the B-key beautify, in px.</summary>
    public int BeautifyPadding { get; init; } = 32;

    /// <summary>Corner radius applied by the B-key beautify, in px. 0 = sharp.</summary>
    public int BeautifyCornerRadius { get; init; } = 8;

    /// <summary>Background hex string (#RGB / #RRGGBB / #AARRGGBB) for the B-key beautify.</summary>
    public string BeautifyBackgroundHex { get; init; } = "#FFFCF7EA";

    /// <summary>Drop shadow X offset, px. Positive = right.</summary>
    public int BeautifyShadowOffsetX { get; init; } = 4;

    /// <summary>Drop shadow Y offset, px. Positive = down.</summary>
    public int BeautifyShadowOffsetY { get; init; } = 4;

    /// <summary>Drop shadow blur radius, px. 0 = hard-edged silhouette.</summary>
    public int BeautifyShadowBlurRadius { get; init; } = 16;

    /// <summary>Drop shadow opacity in [0, 1]. 0 disables shadow.</summary>
    public double BeautifyShadowOpacity { get; init; } = 0.5;

    public static OceanEyesCaptureSettings Default { get; } = new();

    /// <summary>
    /// Normalizes the path: trims whitespace, strips a trailing directory
    /// separator, expands <c>%VAR%</c> environment tokens. Empty → default.
    /// </summary>
    public OceanEyesCaptureSettings Normalize() => this with
    {
        SavePath = NormalizePath(SavePath),
    };

    public void Validate()
    {
        string normalized = NormalizePath(SavePath);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("截图保存路径不能为空。", nameof(SavePath));
        }

        // Reject characters Windows forbids in paths. Path.GetFullPath will
        // throw on most of them already, but we want a clean message.
        char[] invalid = Path.GetInvalidPathChars();
        if (normalized.IndexOfAny(invalid) >= 0)
        {
            throw new ArgumentException("截图保存路径包含非法字符。", nameof(SavePath));
        }

        try
        {
            // Fully resolves "." / ".." segments and catches malformed roots.
            _ = Path.GetFullPath(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or System.IO.PathTooLongException)
        {
            throw new ArgumentException("截图保存路径无法解析。", nameof(SavePath), exception);
        }

        // R51 beautify numeric ranges. Hex format check is deferred to the
        // beautifier (which falls back to the champagne default on any
        // malformed input) — validating here would force a hard schema rule
        // on a free-form user-editable string.
        if (BeautifyPadding < 0 || BeautifyPadding > 256)
        {
            throw new ArgumentException("BeautifyPadding 必须在 0-256 之间。", nameof(BeautifyPadding));
        }
        if (BeautifyCornerRadius < 0 || BeautifyCornerRadius > 128)
        {
            throw new ArgumentException("BeautifyCornerRadius 必须在 0-128 之间。", nameof(BeautifyCornerRadius));
        }
        if (BeautifyShadowOffsetX < -64 || BeautifyShadowOffsetX > 64)
        {
            throw new ArgumentException("BeautifyShadowOffsetX 必须在 -64 到 64 之间。", nameof(BeautifyShadowOffsetX));
        }
        if (BeautifyShadowOffsetY < -64 || BeautifyShadowOffsetY > 64)
        {
            throw new ArgumentException("BeautifyShadowOffsetY 必须在 -64 到 64 之间。", nameof(BeautifyShadowOffsetY));
        }
        if (BeautifyShadowBlurRadius < 0 || BeautifyShadowBlurRadius > 64)
        {
            throw new ArgumentException("BeautifyShadowBlurRadius 必须在 0-64 之间。", nameof(BeautifyShadowBlurRadius));
        }
        if (BeautifyShadowOpacity < 0.0 || BeautifyShadowOpacity > 1.0)
        {
            throw new ArgumentException("BeautifyShadowOpacity 必须在 0.0 到 1.0 之间。", nameof(BeautifyShadowOpacity));
        }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DefaultSavePath;
        }

        string trimmed = path.Trim();
        string expanded = Environment.ExpandEnvironmentVariables(trimmed);
        // Trim ONE trailing separator (keep root "C:\" intact).
        if (expanded.Length > 1 &&
            (expanded[^1] == Path.DirectorySeparatorChar ||
             expanded[^1] == Path.AltDirectorySeparatorChar))
        {
            expanded = expanded[..^1];
        }
        return string.IsNullOrEmpty(expanded) ? DefaultSavePath : expanded;
    }
}
