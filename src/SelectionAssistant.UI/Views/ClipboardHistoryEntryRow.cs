using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using SelectionAssistant.Core.Clipboard;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Row view-model for a single clipboard-history entry in
/// <see cref="ClipboardHistoryWindow"/>. Mirrors <see cref="LauncherEntryRow"/>:
/// must be a public top-level type for Avalonia compiled bindings (private
/// nested types fail AVLN2000 and fall back to reflection → NativeAOT warnings).
/// Implements <see cref="INotifyPropertyChanged"/> so the DataTemplate can
/// data-bind <c>Classes.Active</c> to <see cref="IsSelected"/> (same machinery
/// as the Spotlight panel — see <see cref="LauncherEntryRow"/> docs).
/// </summary>
public sealed class ClipboardHistoryEntryRow : INotifyPropertyChanged
{
    /// <summary>Stable id (matches <see cref="ClipboardEntry.Id"/>).</summary>
    public Guid Id { get; init; }

    /// <summary>R54 v2: text vs image. Image entries show a thumbnail instead of
    /// the preview text, are never sensitive, and paste via SetPng.</summary>
    public ClipboardEntryKind Kind { get; init; } = ClipboardEntryKind.Text;

    /// <summary>Convenience: <see cref="Kind"/> == <see cref="ClipboardEntryKind.Image"/>.
    /// Bound from AXAML to toggle thumbnail vs text visibility.</summary>
    public bool IsImage => Kind == ClipboardEntryKind.Image;

    /// <summary>Convenience: <see cref="Kind"/> == <see cref="ClipboardEntryKind.Text"/>.
    /// Bound from AXAML to show/hide the text preview + expanded TextBlocks
    /// (image rows render a thumbnail instead and never expand).</summary>
    public bool IsTextRow => Kind == ClipboardEntryKind.Text;

    /// <summary>R54 v2: the full path to the image's PNG on disk (built by the
    /// window from <see cref="ClipboardEntry.ImageFileName"/> + the images dir).
    /// Loaded off-thread into <see cref="Thumbnail"/> via Bitmap.DecodeToWidth.</summary>
    public string? ImagePath { get; init; }

    /// <summary>R54 v2: the decoded thumbnail bitmap (64px wide, decoded via
    /// DecodeToWidth so only the needed resolution is read). Posted from a worker
    /// thread once decode completes. Used for the collapsed 64×64 preview ONLY —
    /// the expanded large view and the full-screen popup use <see cref="FullBitmap"/>
    /// (full-resolution decode) so they stay sharp instead of upscaling a 64px
    /// bitmap (which was the "why is the image so blurry" bug).</summary>
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }
    }
    private Bitmap? _thumbnail;

    /// <summary>R54 v2: the full-resolution bitmap, lazily decoded from the PNG
    /// on disk when the row is first expanded (or the "View image…" popup opens).
    /// Null until then. Separate from <see cref="Thumbnail"/> so the collapsed
    /// thumbnail stays cheap (64px decode) while the expanded view is crisp.
    /// Disposed by the window on snapshot refresh.</summary>
    public Bitmap? FullBitmap
    {
        get => _fullBitmap;
        set
        {
            if (!ReferenceEquals(_fullBitmap, value))
            {
                _fullBitmap = value;
                OnPropertyChanged();
            }
        }
    }
    private Bitmap? _fullBitmap;

    /// <summary>Full entry text (used by App to paste). Never masked here —
    /// masking only affects <see cref="Preview"/>. Empty for image entries.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Display preview: ●●●● for sensitive (masked), otherwise a
    /// single-line truncated copy. Updated when the user clicks a sensitive row
    /// to reveal the real text.</summary>
    public string Preview
    {
        get => _preview;
        set
        {
            if (_preview != value)
            {
                _preview = value;
                OnPropertyChanged();
            }
        }
    }
    private string _preview = string.Empty;

    /// <summary>Effective group badge text (Link/Code/Json/Shell/…). Reflects
    /// the user's override when set, else the auto classification — i.e. this is
    /// already the <em>effective</em> label. Bound to the meta-line badge so the
    /// badge follows the user's correction. Set once per snapshot refresh.</summary>
    public string GroupLabel { get; init; } = string.Empty;

    /// <summary>R54 v2: the original auto-classified group (from
    /// <see cref="ClipboardEntry.Group"/>), ignoring any user override. Used by
    /// the "Move to…" submenu to show the Auto target and to allow reverting an
    /// override back to this value. Set once per snapshot refresh.</summary>
    public ClipboardGroup AutoGroup { get; init; } = ClipboardGroup.Text;

    /// <summary>R54 v2: the user's manual group correction, or null when the
    /// entry is still on automatic classification. Mirrors
    /// <see cref="ClipboardEntry.GroupOverride"/>. Used by the "Move to…" submenu
    /// to mark the active target with a checkmark and to detect the "revert to
    /// auto" case. Set once per snapshot refresh.</summary>
    public ClipboardGroup? GroupOverride { get; init; }

    /// <summary>R54 v2: the group that wins for filtering/badging — the override
    /// when set, else <see cref="AutoGroup"/>. Pure; used by ReapplyFilter and
    /// the badge label.</summary>
    public ClipboardGroup EffectiveGroup => GroupOverride ?? AutoGroup;

    /// <summary>Source process name (e.g. "chrome"), or empty. Shown muted.</summary>
    public string SourceLabel { get; init; } = string.Empty;

    /// <summary>Relative/short timestamp label (e.g. "12:34", "昨天").</summary>
    public string TimeLabel { get; init; } = string.Empty;

    /// <summary>True when <see cref="ClipboardEntry.IsSensitive"/>. Drives the
    /// mask state — clicking a sensitive row toggles <see cref="Preview"/>.</summary>
    public bool IsSensitive { get; init; }

    /// <summary>True when pinned (Ctrl+P). Shows a ★ marker; pinned entries sort
    /// first and survive LRU eviction.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned != value)
            {
                _isPinned = value;
                OnPropertyChanged();
            }
        }
    }
    private bool _isPinned;

    /// <summary>R54 v1.1: True when the entry carries the ❤收藏 tag. Independent
    /// of <see cref="IsPinned"/> — an entry can be favorited without being
    /// pinned. Drives the ❤ marker and the 收藏 nav tab.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite != value)
            {
                _isFavorite = value;
                OnPropertyChanged();
            }
        }
    }
    private bool _isFavorite;

    /// <summary>R54 v1.1: Custom tag names assigned to this entry (display
    /// independent of the built-in group/pin/favorite flags). Pushed by App
    /// from <c>ClipboardHistoryService.Tags</c>. Drives the "移动到…" submenu
    /// checkmarks and the custom-tag nav tab membership.</summary>
    public IReadOnlyList<string> CustomTags
    {
        get => _customTags;
        set
        {
            _customTags = value;
            OnPropertyChanged();
        }
    }
    private IReadOnlyList<string> _customTags = [];

    /// <summary>R54 v2: per-entry annotation tags shown inline as badges on the
    /// row's meta line (e.g. "AWS", "Stripe") so a glance tells you which key/
    /// snippet this is. Independent of <see cref="CustomTags"/> — these never
    /// become nav tabs, never appear in "Move to…", and are added via a separate
    /// "Add tag…" right-click entry. Mirrors <see cref="ClipboardEntry.EntryTags"/>;
    /// pushed by App on snapshot refresh. Empty list = no badges (the bound
    /// ItemsControl renders no children).</summary>
    public IReadOnlyList<string> EntryTags
    {
        get => _entryTags;
        set
        {
            _entryTags = value;
            OnPropertyChanged();
        }
    }
    private IReadOnlyList<string> _entryTags = [];

    /// <summary>R103: true when this row came from the monthly archive (evicted
    /// from the live window by LRU), false when it's a live entry. Drives the
    /// "Archived" badge, dimmed rendering, and the restricted context menu
    /// (archived rows only support copy/paste — pin/favorite/tag/move/delete
    /// don't apply to historical entries). Set once during <c>ToRow</c>; never
    /// changes after. Pure UI-layer flag — the underlying <see cref="ClipboardEntry"/>
    /// has no such field (live and archived entries are structurally identical;
    /// only their storage location differs).</summary>
    public bool IsArchived { get; init; }

    /// <summary>True when this entry is assigned the given custom tag name
    /// (case-sensitive, ordinal). Convenience over <see cref="CustomTags"/>.</summary>
    public bool HasCustomTag(string tagName) =>
        CustomTags.Contains(tagName, StringComparer.Ordinal);

    /// <summary>R54 v1.2: full text for the expanded view. Single-click on a
    /// row toggles <see cref="IsExpanded"/>; when expanded, the row swaps the
    /// single-line <see cref="Preview"/> for this wrapped multi-line text.
    /// Sensitive rows still mask here (●●●●) until the user explicitly reveals
    /// via the right-click menu — expanding never bypasses the mask. Settable so
    /// <c>Reveal</c> can refresh it in place.</summary>
    public string ExpandedText
    {
        get => _expandedText;
        set
        {
            if (_expandedText != value)
            {
                _expandedText = value;
                OnPropertyChanged();
            }
        }
    }
    private string _expandedText = string.Empty;

    /// <summary>R54 v1.2: true when the row is expanded to show the full text.
    /// Toggled by single-click; double-click still pastes (the second click of a
    /// double lands within the 400 ms window, so the paste follows the toggle).
    /// Drives a <c>Classes.Expanded</c> binding on the row border.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }
    private bool _isExpanded;

    /// <summary>True when this row is the keyboard-focused entry. Drives the
    /// <c>Active</c> class via <c>Classes.Active="{Binding IsSelected}"</c>.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
