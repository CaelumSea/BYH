using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SelectionAssistant.Core.Launcher;
using SelectionAssistant.Core.Translation;
using System.Collections.ObjectModel;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// R32 standalone launcher-search panel. Triggered by its own global hotkey
/// (default <c>Ctrl+Alt+Space</c>, configured separately from QuickTools).
/// Provides a Spotlight/PowerToys-Run-style flow: a top search box filters the
/// user's launcher entries by name; arrow keys move the selection; Enter starts
/// the highlighted entry; Ctrl+Enter opens the settings editor for it; Esc
/// closes the panel.
/// </summary>
/// <remarks>
/// The panel shares the same <see cref="LauncherEntry"/> source as QuickTools
/// and Settings — App pushes the entries via <see cref="SetLauncherEntries"/>
/// and asynchronously pushes icons via <see cref="UpdateLauncherIcon"/>. The
/// panel owns its own filtered view (<see cref="_filteredRows"/>) plus a
/// single <see cref="_selectedIndex"/> that arrow keys move (clamped, no wrap).
/// </remarks>
public partial class SpotlightWindow : Window
{
    // Full set of rows currently known to the panel (one per LauncherEntry).
    private readonly ObservableCollection<LauncherEntryRow> _allRows = [];
    // Subset currently shown after applying the search filter. Indices in this
    // list are what _selectedIndex refers to.
    private readonly ObservableCollection<LauncherEntryRow> _filteredRows = [];

    private int _selectedIndex;

    public SpotlightWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _filteredRows;

        // Focus the search box as soon as the window is in the visual tree.
        // Doing it in the constructor is too early (window not shown yet); in
        // Window.Opened works but fires on every re-show, which is what we want.
        Opened += (_, _) =>
        {
            SearchInput.Text = string.Empty;
            SearchInput.Focus();
        };

        // Hide on focus loss — same UX as QuickTools. The user hit the global
        // hotkey to summon us; clicking elsewhere should dismiss.
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

    private bool _allowClose;

    /// <summary>Pushes the full set of launcher entries from the runtime.</summary>
    public void SetLauncherEntries(IReadOnlyList<LauncherEntry> entries)
    {
        _allRows.Clear();
        foreach (LauncherEntry entry in entries)
        {
            string entryId = entry.Id;
            _allRows.Add(new LauncherEntryRow
            {
                Id = entryId,
                Name = entry.Name,
                Kind = entry.Kind,
                Target = entry.Target,
                Arguments = entry.Arguments,
            });
        }
        ReapplyFilter();
    }

    /// <summary>
    /// Updates the icon for an entry by id. Posted to the UI thread so the
    /// background icon-loading task can call it from any thread.
    /// </summary>
    public void UpdateLauncherIcon(string entryId, Bitmap? icon)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (LauncherEntryRow row in _allRows)
            {
                if (row.Id == entryId)
                {
                    row.Icon = icon;
                    break;
                }
            }
        });
    }

    /// <summary>Runs the highlighted entry. Args = (entryId, selectedText, clipText).</summary>
    public event Action<string, string?, string?>? LauncherRunRequested;

    /// <summary>Edit the highlighted entry in the settings window. Arg = entryId.</summary>
    public event Action<string>? LauncherEditRequested;

    /// <summary>Footer "设置" clicked — open the launcher settings section.</summary>
    public event Action? SettingsRequested;

    public void PrepareForShutdown() => _allowClose = true;

    // ── Search filter ──

    private void OnSearchInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        ReapplyFilter();
    }

    private void ReapplyFilter()
    {
        string query = (SearchInput.Text ?? string.Empty).Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _allRows.ToList()
            : _allRows.Where(r => MatchesQuery(r.Name, query)).ToList();

        _filteredRows.Clear();
        foreach (var row in matches)
        {
            _filteredRows.Add(row);
        }
        _selectedIndex = _filteredRows.Count > 0 ? 0 : -1;
        SyncRowSelection();
    }

    // ── Search matching ──
    //
    // Three-tier matching (substring → initials scan → pinyin initials) is
    // centralized in <see cref="PinyinSearchHelper"/>, shared with
    // ClipboardHistoryWindow (R54).

    /// <summary>Returns true if <paramref name="name"/> matches <paramref name="query"/>.</summary>
    private static bool MatchesQuery(string name, string query) =>
        PinyinSearchHelper.MatchesQuery(name, query);

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
                e.Handled = MoveSelection(delta: +1);
                return;

            case Key.Up:
                e.Handled = MoveSelection(delta: -1);
                return;

            case Key.Enter:
                e.Handled = true;
                LauncherEntryRow? row = CurrentSelectedRow;
                if (row is null)
                {
                    return;
                }
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    LauncherEditRequested?.Invoke(row.Id);
                }
                else
                {
                    _ = LaunchCurrentAsync();
                }
                return;
        }
    }

    /// <summary>
    /// Moves the selection by delta (+1 down, -1 up). Clamped to [0, count-1];
    /// no wrap-around (matches the reference UI and avoids surprising users).
    /// Returns true if the selection moved (so the caller can mark the key
    /// event handled), false at the edges.
    /// </summary>
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

    private LauncherEntryRow? CurrentSelectedRow =>
        _selectedIndex >= 0 && _selectedIndex < _filteredRows.Count
            ? _filteredRows[_selectedIndex]
            : null;

    private async Task LaunchCurrentAsync()
    {
        LauncherEntryRow? row = CurrentSelectedRow;
        if (row is null)
        {
            return;
        }
        string? selection = null;     // Spotlight doesn't capture selection context
        string? clip = null;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            clip = await clipboard.TryGetTextAsync();
        }
        Hide();
        LauncherRunRequested?.Invoke(row.Id, selection, clip);
    }

    // ── Visual selection state ──
    //
    // R43: previous approach walked the ItemsControl's containers and toggled
    // an "Active" class on them. That never worked, because ItemsControl wraps
    // each item in an internal ContentPresenter — NOT the Border inside our
    // DataTemplate — so the class never reached the styled element and the
    // selection highlight was invisible.
    //
    // Now we drive selection purely through the row model: each LauncherEntryRow
    // has an IsSelected flag (INotifyPropertyChanged), and the DataTemplate
    // binds "Classes.Active" to it via the Avalonia "Classes.<name>={Binding}"
    // syntax. Toggling IsSelected flips the class on the Border that actually
    // owns the SpotlightRow style. No container realization races possible.

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
        if (sender is not Border border || border.DataContext is not LauncherEntryRow row)
        {
            return;
        }
        // Click-to-launch (mouse users shouldn't need Enter).
        int index = IndexOfRow(row);
        if (index >= 0)
        {
            _selectedIndex = index;
            SyncRowSelection();
        }
        if (e.Pointer.IsPrimary)
        {
            _ = LaunchRowAsync(row);
        }
    }

    private async Task LaunchRowAsync(LauncherEntryRow row)
    {
        string? clip = null;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            clip = await clipboard.TryGetTextAsync();
        }
        Hide();
        LauncherRunRequested?.Invoke(row.Id, null, clip);
    }

    private int IndexOfRow(LauncherEntryRow row)
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
