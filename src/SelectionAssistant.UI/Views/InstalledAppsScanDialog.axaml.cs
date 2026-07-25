using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Row view-model for a single detected app shown in the scan dialog.
/// Must be public for Avalonia compiled bindings. Implements
/// <see cref="INotifyPropertyChanged"/> so that
/// <c>IsChecked="{Binding IsSelected}"</c> works two-way.
/// </summary>
public sealed class ScanAppRow : INotifyPropertyChanged
{
    /// <summary>Display name of the detected app.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Full filesystem path to the executable.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Whether the user has checked this row for import.</summary>
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

    /// <summary>
    /// Icon bitmap for the app. Starts null; the dialog fills it
    /// asynchronously via the iconExtractor callback provided to
    /// <see cref="InstalledAppsScanDialog.ShowAsync"/>.
    /// </summary>
    public Bitmap? Icon
    {
        get => _icon;
        set
        {
            if (!ReferenceEquals(_icon, value))
            {
                _icon = value;
                OnPropertyChanged();
            }
        }
    }
    private Bitmap? _icon;

    /// <summary>Back-reference to the source model.</summary>
    public DetectedApp Source { get; init; }

    public ScanAppRow(DetectedApp source)
    {
        Name = source.Name;
        Path = source.ExecutablePath;
        Source = source;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Non-modal dialog that shows a list of <see cref="DetectedApp"/> items
/// for the user to select and import. Follows the same window lifecycle
/// pattern as <see cref="LauncherEntryEditWindow"/>: uses Show() +
/// Activate(), returns data via <see cref="TaskCompletionSource{TResult}"/>,
/// closes via Close().
/// </summary>
public partial class InstalledAppsScanDialog : Window
{
    private readonly List<ScanAppRow> _allRows = [];
    private readonly TaskCompletionSource<List<DetectedApp>> _tcs = new();

    /// <summary>
    /// Shows the scan dialog as a non-modal window and returns the list of
    /// <see cref="DetectedApp"/> entries the user selected (or an empty list
    /// if cancelled / closed / Esc).
    /// </summary>
    /// <param name="owner">The owner window.</param>
    /// <param name="apps">All detected apps to display.</param>
    /// <param name="iconExtractor">
    /// Optional callback that, given an executable path, returns PNG bytes
    /// for the app icon, or null if extraction fails. If provided, icon
    /// loading runs in the background and results are marshalled to the UI
    /// thread.
    /// </param>
    /// <returns>
    /// The list of <see cref="DetectedApp"/> instances the user selected.
    /// Never null (empty list if cancelled).
    /// </returns>
    public static Task<List<DetectedApp>> ShowAsync(
        Window owner,
        IReadOnlyList<DetectedApp> apps,
        Func<string, byte[]?>? iconExtractor = null)
    {
        var dialog = new InstalledAppsScanDialog();
        dialog.PopulateRows(apps);

        dialog.Show(owner);
        dialog.Activate();

        if (iconExtractor is not null)
        {
            dialog.StartIconLoading(iconExtractor);
        }

        return dialog._tcs.Task;
    }

    public InstalledAppsScanDialog()
    {
        InitializeComponent();
        UpdateCountText();
    }

    private void PopulateRows(IReadOnlyList<DetectedApp> apps)
    {
        _allRows.Clear();
        foreach (var app in apps)
        {
            _allRows.Add(new ScanAppRow(app));
        }
        ApplyFilter();
    }

    /// <summary>
    /// Exposes visible rows so external code can extract icons.
    /// </summary>
    public IEnumerable<ScanAppRow> GetVisibleRows() =>
        _allRows.Where(r => ResultsList.ItemsSource is IEnumerable<ScanAppRow> visible
                            && visible.Contains(r));

    private void StartIconLoading(Func<string, byte[]?> iconExtractor)
    {
        Task.Run(async () =>
        {
            foreach (var row in _allRows)
            {
                try
                {
                    var pngBytes = iconExtractor(row.Path);
                    if (pngBytes is not null && pngBytes.Length > 0)
                    {
                        using var ms = new MemoryStream(pngBytes);
                        var bitmap = new Bitmap(ms);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            row.Icon = bitmap;
                        }, DispatcherPriority.Background);
                    }
                }
                catch
                {
                    // Silently skip icons that fail to load.
                }
            }
        });
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var searchText = SearchInput.Text?.Trim() ?? string.Empty;
        IEnumerable<ScanAppRow> visible;

        if (string.IsNullOrEmpty(searchText))
        {
            visible = _allRows;
        }
        else
        {
            visible = _allRows.Where(r =>
                r.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        ResultsList.ItemsSource = new ObservableCollection<ScanAppRow>(visible);
        SyncSelectAllCheckBox();
        UpdateCountText();
    }

    private void OnSelectAllChanged(object? sender, RoutedEventArgs e)
    {
        if (ResultsList.ItemsSource is not IEnumerable<ScanAppRow> visible)
        {
            return;
        }

        bool isChecked = SelectAllCheckBox.IsChecked == true;
        foreach (var row in visible)
        {
            row.IsSelected = isChecked;
        }
        UpdateCountText();
    }

    private void SyncSelectAllCheckBox()
    {
        if (ResultsList.ItemsSource is not IEnumerable<ScanAppRow> visible)
        {
            SelectAllCheckBox.IsChecked = false;
            return;
        }

        var list = visible.ToList();
        if (list.Count == 0)
        {
            SelectAllCheckBox.IsChecked = false;
        }
        else if (list.All(r => r.IsSelected))
        {
            SelectAllCheckBox.IsChecked = true;
        }
        else if (list.All(r => !r.IsSelected))
        {
            SelectAllCheckBox.IsChecked = false;
        }
        else
        {
            SelectAllCheckBox.IsChecked = null; // indeterminate
        }
    }

    private void UpdateCountText()
    {
        var visible = ResultsList.ItemsSource is IEnumerable<ScanAppRow> rows
            ? rows.ToList()
            : [];

        int total = _allRows.Count;
        int visibleCount = visible.Count;
        int selectedCount = visible.Count(r => r.IsSelected);

        CountText.Text = $"共 {total} 个，已选 {selectedCount} 个";
    }

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var selected = _allRows
            .Where(r => r.IsSelected)
            .Select(r => r.Source)
            .ToList();

        _tcs.TrySetResult(selected);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult([]);
        Close();
    }

    /// <summary>
    /// Esc closes the dialog with an empty result.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            _tcs.TrySetResult([]);
            Close();
        }
    }

    /// <summary>
    /// If the user closes via system chrome / Alt+F4, ensure the task
    /// completes so the caller doesn't hang.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _tcs.TrySetResult([]);
        base.OnClosed(e);
    }
}
