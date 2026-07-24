using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    /// <summary>Full entry text (used by App to paste). Never masked here —
    /// masking only affects <see cref="Preview"/>.</summary>
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

    /// <summary>Auto-classified group badge text (Link/Code/Json/Shell/…).</summary>
    public string GroupLabel { get; init; } = string.Empty;

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
