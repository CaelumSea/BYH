namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// R54 v2: discriminates text vs image clipboard entries. Text entries (v1)
/// always carry <see cref="ClipboardEntry.Text"/>; image entries (v2) leave
/// <see cref="ClipboardEntry.Text"/> empty and carry
/// <see cref="ClipboardEntry.ImageFileName"/> (a PNG written to
/// <c>ClipboardImagesDirectory</c>). Persisted as the <c>kind</c> JSON field
/// (schema v2); legacy v1 entries without the field decode as <see cref="Text"/>.
/// </summary>
public enum ClipboardEntryKind
{
    /// <summary>A text capture (links/code/json/shell/plain — the only kind in
    /// R54 v1). <see cref="ClipboardEntry.Text"/> is the payload.</summary>
    Text = 0,

    /// <summary>An image capture (R54 v2). <see cref="ClipboardEntry.Text"/> is
    /// empty; <see cref="ClipboardEntry.ImageFileName"/> names the PNG on disk.
    /// Images are not Smart-auto-grouped (always <see cref="ClipboardGroup.Text"/>)
    /// and never sensitive.</summary>
    Image = 1,
}
