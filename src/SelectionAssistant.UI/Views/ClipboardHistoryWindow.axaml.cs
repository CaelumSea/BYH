using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Infrastructure.Configuration;
using System.Collections.ObjectModel;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// R54 clipboard-history popup (Maccy-style). Summoned by its own global hotkey
/// (default <c>Ctrl+Alt+V</c>). A top search box filters entries via
/// <see cref="PinyinSearchHelper"/> (substring + initials + pinyin, same engine
/// as <see cref="SpotlightWindow"/>); ↑↓ moves the selection; Enter pastes the
/// highlighted entry; Ctrl+P toggles pin; Delete removes; clicking a masked
/// sensitive row reveals its text. Auto-hides on focus loss.
/// </summary>
/// <remarks>
/// The window holds no Win32/store references — it operates purely on the
/// <see cref="ClipboardEntry"/> list pushed by App via <see cref="SetEntries"/>
/// and raises events (<see cref="PasteRequested"/>, <see cref="PinToggled"/>,
/// <see cref="DeleteRequested"/>) that App forwards to
/// <c>ClipboardHistoryService</c>. This keeps the UI layer free of the App layer.
/// </remarks>
public partial class ClipboardHistoryWindow : Window
{
    // Full set of rows (display order: pinned first, then newest). App pushes a
    // fresh snapshot whenever history changes; we rebuild _allRows from it.
    private readonly ObservableCollection<ClipboardHistoryEntryRow> _allRows = [];
    private readonly ObservableCollection<ClipboardHistoryEntryRow> _filteredRows = [];

    private int _selectedIndex;
    private bool _allowClose;
    private bool _maskSensitive = true;

    // Ids whose preview the user has revealed (clicked) despite being sensitive.
    private readonly HashSet<Guid> _revealed = [];

    public ClipboardHistoryWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _filteredRows;

        Opened += (_, _) =>
        {
            SearchInput.Text = string.Empty;
            SearchInput.Focus();
        };

        Deactivated += (_, _) => Hide();
        Closing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    /// <summary>Replaces the displayed entries from a service snapshot. Call on
    /// the UI thread. <paramref name="maskSensitive"/> controls whether
    /// sensitive rows show ●●●● until clicked.</summary>
    public void SetEntries(IReadOnlyList<ClipboardEntry> entries, bool maskSensitive)
    {
        _maskSensitive = maskSensitive;
        _allRows.Clear();
        foreach (ClipboardEntry entry in entries)
        {
            _allRows.Add(ToRow(entry));
        }
        ReapplyFilter();
    }

    /// <summary>Paste the given entry (App delegates to the service). Arg = id.</summary>
    public event Action<Guid>? PasteRequested;

    /// <summary>Toggle the pinned flag. Arg = id. App re-pushes the snapshot.</summary>
    public event Action<Guid>? PinToggled;

    /// <summary>Delete a single entry. Arg = id. App re-pushes the snapshot.</summary>
    public event Action<Guid>? DeleteRequested;

    /// <summary>Footer "设置" clicked — open the clipboard-history settings.</summary>
    public event Action? SettingsRequested;

    public void PrepareForShutdown() => _allowClose = true;

    private ClipboardHistoryEntryRow ToRow(ClipboardEntry entry)
    {
        bool reveal = _revealed.Contains(entry.Id);
        string preview = reveal
            ? ClipboardHistoryStore.BuildPreview(entry.Text, isSensitive: false, maskSensitive: false)
            : ClipboardHistoryStore.BuildPreview(entry.Text, entry.IsSensitive, _maskSensitive);
        return new ClipboardHistoryEntryRow
        {
            Id = entry.Id,
            Text = entry.Text,
            Preview = preview,
            GroupLabel = GroupToLabel(entry.Group),
            SourceLabel = entry.SourceProcessName ?? string.Empty,
            TimeLabel = FormatTime(entry.CapturedAt),
            IsSensitive = entry.IsSensitive,
            IsPinned = entry.IsPinned,
        };
    }

    private static string GroupToLabel(ClipboardGroup group) => group switch
    {
        ClipboardGroup.Sensitive => "敏感",
        ClipboardGroup.Link => "链接",
        ClipboardGroup.Json => "JSON",
        ClipboardGroup.Code => "代码",
        ClipboardGroup.Shell => "命令",
        ClipboardGroup.Contact => "联系人",
        ClipboardGroup.Number => "数字",
        _ => string.Empty, // Text → no badge
    };

    private static string FormatTime(DateTimeOffset capturedAt)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan age = now - capturedAt.ToLocalTime();
        if (age.TotalMinutes < 1) return "刚刚";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}分钟前";
        if (age.TotalDays < 1) return capturedAt.ToLocalTime().ToString("HH:mm");
        if (age.TotalDays < 7) return $"{(int)age.TotalDays}天前";
        return capturedAt.ToLocalTime().ToString("MM-dd");
    }

    // ── Search filter ──

    private void OnSearchInputTextChanged(object? sender, TextChangedEventArgs e) => ReapplyFilter();

    private void ReapplyFilter()
    {
        string query = (SearchInput.Text ?? string.Empty).Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _allRows.ToList()
            : _allRows.Where(r => PinyinSearchHelper.MatchesQuery(r.Text, query)).ToList();

        _filteredRows.Clear();
        foreach (var row in matches)
        {
            _filteredRows.Add(row);
        }
        _selectedIndex = _filteredRows.Count > 0 ? 0 : -1;
        SyncRowSelection();
    }

    // ── Keyboard navigation ──

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Hide();
                return;

            case Key.Down:
                e.Handled = MoveSelection(+1);
                return;

            case Key.Up:
                e.Handled = MoveSelection(-1);
                return;

            case Key.Enter:
                e.Handled = true;
                if (CurrentSelectedRow is { } row)
                {
                    PasteAndHide(row);
                }
                return;

            case Key.P when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                if (CurrentSelectedRow is { } pinRow)
                {
                    PinToggled?.Invoke(pinRow.Id);
                }
                return;

            case Key.Delete:
                e.Handled = true;
                if (CurrentSelectedRow is { } delRow)
                {
                    DeleteRequested?.Invoke(delRow.Id);
                }
                return;
        }
    }

    private bool MoveSelection(int delta)
    {
        if (_filteredRows.Count == 0)
        {
            return false;
        }
        int newIndex = Math.Clamp(_selectedIndex + delta, 0, _filteredRows.Count - 1);
        if (newIndex == _selectedIndex)
        {
            return false;
        }
        _selectedIndex = newIndex;
        SyncRowSelection();
        ScrollSelectedIntoView();
        return true;
    }

    private ClipboardHistoryEntryRow? CurrentSelectedRow =>
        _selectedIndex >= 0 && _selectedIndex < _filteredRows.Count
            ? _filteredRows[_selectedIndex]
            : null;

    private void PasteAndHide(ClipboardHistoryEntryRow row)
    {
        Hide();
        PasteRequested?.Invoke(row.Id);
    }

    private void SyncRowSelection()
    {
        for (int i = 0; i < _filteredRows.Count; i++)
        {
            _filteredRows[i].IsSelected = i == _selectedIndex;
        }
    }

    private void ScrollSelectedIntoView()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _filteredRows.Count)
        {
            ResultsList.ScrollIntoView(_filteredRows[_selectedIndex]);
        }
    }

    // ── Mouse interactions ──

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ClipboardHistoryEntryRow row)
        {
            return;
        }

        int index = IndexOfRow(row);
        if (index >= 0)
        {
            _selectedIndex = index;
            SyncRowSelection();
        }

        if (!e.Pointer.IsPrimary)
        {
            return;
        }

        // Sensitive rows: first click reveals, second click pastes. Non-sensitive
        // rows: click pastes immediately.
        if (row.IsSensitive && row.Preview.Contains('●'))
        {
            Reveal(row);
        }
        else
        {
            PasteAndHide(row);
        }
    }

    private void Reveal(ClipboardHistoryEntryRow row)
    {
        _revealed.Add(row.Id);
        row.Preview = ClipboardHistoryStore.BuildPreview(row.Text, isSensitive: false, maskSensitive: false);
    }

    private int IndexOfRow(ClipboardHistoryEntryRow row)
    {
        for (int i = 0; i < _filteredRows.Count; i++)
        {
            if (ReferenceEquals(_filteredRows[i], row))
            {
                return i;
            }
        }
        return -1;
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke();
}
