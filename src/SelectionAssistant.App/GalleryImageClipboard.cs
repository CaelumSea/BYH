using SelectionAssistant.Platform.Windows.Clipboard;

namespace SelectionAssistant.App;

/// <summary>
/// Puts an Ocean Eyes screenshot on the clipboard in a form every Windows app
/// can actually paste.
/// </summary>
/// <remarks>
/// <b>Why this exists:</b> the gallery "copy" button and the pinned-window copy
/// used to call <see cref="Win32Clipboard.SetPng"/> alone, which writes only the
/// registered <c>"PNG"</c> format. Word, Paint, Explorer, chat clients and
/// everything else that pastes images reads <c>CF_DIB</c> (format 8) — so the
/// write "succeeded", the log said "copied", and pasting anywhere produced
/// nothing. <see cref="Win32Clipboard.SetImageDibAndPng"/> writes both formats in
/// one open/empty/set sequence, which is what the save path already uses.
/// </remarks>
public static class GalleryImageClipboard
{
    /// <summary>
    /// Writes <paramref name="png"/> as <c>CF_DIB</c> (plus <c>CF_PNG</c> when it
    /// fits) and returns whether anything was actually transferred. A DIB encode
    /// failure degrades to PNG-only rather than throwing — a partially-useful
    /// clipboard beats none.
    /// </summary>
    public static bool CopyPng(byte[] png, Win32Clipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(clipboard);
        if (png.Length == 0)
        {
            return false;
        }
        byte[]? dib = PngToDibConverter.ConvertPngToDib(png);
        return clipboard.SetImageDibAndPng(png, dib);
    }

    /// <summary>Reads the PNG from disk and copies it; false when the file is
    /// gone or nothing could be placed on the clipboard.</summary>
    public static bool CopyFile(string filePath, Win32Clipboard clipboard)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }
        return CopyPng(File.ReadAllBytes(filePath), clipboard);
    }
}
