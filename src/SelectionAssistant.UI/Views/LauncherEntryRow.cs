using Avalonia.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SelectionAssistant.Core.Launcher;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Row view-model for a single launcher entry shown in the settings "启动器"
/// card and the QuickTools launcher panel. Must be a public top-level type for
/// Avalonia compiled bindings (private nested types fail AVLN2000 and fall back
/// to reflection, which breaks NativeAOT 0-warnings).
/// <para>
/// The <see cref="RunCommand"/> is bound by the QuickTools launcher panel
/// (click = run this launcher entry). The settings card uses its own
/// edit/delete/move commands.
/// </para>
/// <para>
/// R43: implements <see cref="INotifyPropertyChanged"/> so the Spotlight panel
/// can data-bind the row's <c>Classes.Active</c> membership to
/// <see cref="IsSelected"/>. This is the only reliable way to drive per-row
/// selection visuals from an <c>ItemsControl</c> — its container is an
/// internal <c>ContentPresenter</c>, NOT the <c>Border</c> we put in the
/// <c>DataTemplate</c>, so manipulating container classes from code-behind
/// never reaches the styled element. Binding <c>IsSelected</c> →
/// <c>Classes.Active</c> does.
/// </para>
/// </summary>
public sealed class LauncherEntryRow : INotifyPropertyChanged
{
    /// <summary>Stable entry id (always "launcher-*").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name (e.g. "记事本", "Google").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Whether this entry starts a local app or opens a web URL.</summary>
    public LauncherKind Kind { get; init; }

    /// <summary>
    /// Target path (local exe) or URL (web). Displayed in the edit form;
    /// not shown in the row itself.
    /// </summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// Launch arguments with optional placeholders ({clip}, {sel}, {prompt:...}).
    /// </summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>
    /// Icon bitmap for the entry. Starts null; the App layer fills it
    /// asynchronously (e.g. via <c>WindowsIconExtractor</c>) to avoid
    /// blocking the UI thread during row construction.
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

    /// <summary>
    /// R43: Spotlight-only. True when this row is the keyboard-focused entry.
    /// Drives the <c>Active</c> class via <c>&lt;Classes.Active&gt;</c> binding
    /// in the Spotlight <c>DataTemplate</c>. Settings/QuickTools leave it false.
    /// </summary>
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

    /// <summary>QuickTools: click the row button to run this entry.</summary>
    public ICommand? RunCommand { get; set; }

    /// <summary>Settings: edit this entry's configuration.</summary>
    public ICommand? EditCommand { get; set; }

    /// <summary>Settings: delete this entry.</summary>
    public ICommand? DeleteCommand { get; set; }

    /// <summary>Settings: move this entry up in the list.</summary>
    public ICommand? MoveUpCommand { get; set; }

    /// <summary>Settings: move this entry down in the list.</summary>
    public ICommand? MoveDownCommand { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
