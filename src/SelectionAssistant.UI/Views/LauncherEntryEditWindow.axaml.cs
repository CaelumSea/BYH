using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SelectionAssistant.Core.Launcher;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Modal editor for a single launcher entry. Supports both "new" and "edit"
/// modes. The type radio buttons toggle visibility of the working-directory
/// panel and the browse button. Saving raises <see cref="EntryCreated"/> or
/// <see cref="EntrySaved"/> depending on mode.
/// </summary>
public partial class LauncherEntryEditWindow : Window
{
    private string _id = string.Empty;
    private bool _isNew;

    public LauncherEntryEditWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised on save (new mode). Args = (name, kind, target, args, workDir).
    /// </summary>
    public event Action<string, LauncherKind, string, string, string>? EntryCreated;  // (name, kind, target, args, workDir)

    /// <summary>
    /// Raised when the user saves an existing entry. Args include the (possibly
    /// renamed) name: (id, name, kind, target, args, workDir).
    /// </summary>
    public event Action<string, string, LauncherKind, string, string, string>? EntrySaved;

    /// <summary>
    /// Seeds the editor for creating a NEW launcher entry. The name input is
    /// visible and editable; the save button raises <see cref="EntryCreated"/>.
    /// </summary>
    public void ShowForNew()
    {
        _isNew = true;
        Title = "BYH · 新增启动项";
        TitleText.Text = "新增启动项";
        SubtitleText.Text = "配置目标和参数，保存后即可在 Spotlight 搜索面板使用。";

        LocalAppRadio.IsChecked = true;
        NameInput.Text = string.Empty;
        TargetInput.Text = string.Empty;
        ArgumentsInput.Text = string.Empty;
        WorkingDirectoryInput.Text = string.Empty;
        IconPreview.Source = null;
        IconPreviewPanel.IsVisible = false;

        Show();
        Activate();
        NameInput.Focus();
    }

    /// <summary>
    /// Seeds the editor for the given entry (edit mode). Fills the form with
    /// the existing values; the save button raises <see cref="EntrySaved"/>.
    /// </summary>
    public void ShowFor(string id, LauncherEntry existing)
    {
        _isNew = false;
        _id = id;

        Title = "BYH · 编辑启动项";
        TitleText.Text = $"编辑「{existing.Name}」";
        SubtitleText.Text = "修改后立即生效。";

        LocalAppRadio.IsChecked = existing.Kind == LauncherKind.LocalApp;
        WebUrlRadio.IsChecked = existing.Kind == LauncherKind.WebUrl;
        NameInput.Text = existing.Name;
        TargetInput.Text = existing.Target;
        ArgumentsInput.Text = existing.Arguments;
        WorkingDirectoryInput.Text = existing.WorkingDirectory;
        IconPreview.Source = null;
        IconPreviewPanel.IsVisible = false;

        Show();
        Activate();
        NameInput.Focus();
    }

    private void OnKindCheckedChanged(object? sender, RoutedEventArgs e)
    {
        bool isLocal = LocalAppRadio.IsChecked == true;
        WorkingDirectoryPanel.IsVisible = isLocal;
        BrowseTargetButton.IsVisible = isLocal;
        TargetInput.PlaceholderText = isLocal
            ? "程序路径（.exe）"
            : "网页地址（https://...）";
    }

    private async void OnBrowseTargetClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择程序",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("可执行文件") { Patterns = ["*.exe"] },
                new FilePickerFileType("所有文件") { Patterns = ["*.*"] },
            ],
        });

        if (files.Count > 0)
        {
            TargetInput.Text = files[0].TryGetLocalPath() ?? files[0].Path.ToString();
        }
    }

    private async void OnBrowseWorkDirClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工作目录",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            WorkingDirectoryInput.Text = folders[0].TryGetLocalPath() ?? folders[0].Path.ToString();
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        string name = NameInput.Text?.Trim() ?? string.Empty;
        string target = TargetInput.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(name))
        {
            NameInput.Focus();
            return;
        }
        if (string.IsNullOrEmpty(target))
        {
            TargetInput.Focus();
            return;
        }

        LauncherKind kind = LocalAppRadio.IsChecked == true
            ? LauncherKind.LocalApp
            : LauncherKind.WebUrl;
        string args = ArgumentsInput.Text?.Trim() ?? string.Empty;
        string workDir = WorkingDirectoryInput.Text?.Trim() ?? string.Empty;

        if (_isNew)
        {
            EntryCreated?.Invoke(name, kind, target, args, workDir);
        }
        else
        {
            EntrySaved?.Invoke(_id, name, kind, target, args, workDir);
        }

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// R38: Esc cancels the editor. Reuses OnCancelClick's close path. This
    /// dialog is created fresh each time, so Close() is correct (not Hide()).
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Close();
        }
    }
}
