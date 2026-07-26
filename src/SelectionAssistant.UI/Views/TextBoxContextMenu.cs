using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SelectionAssistant.Core.I18n;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Builds the standard Copy / Cut / Paste / Select-all context menu for a
/// <see cref="TextBox" />. The menu enables/disables items based on whether
/// there is a selection and (for paste) whether the clipboard has text.
/// </summary>
/// <remarks>
/// Cut/Paste are hidden (not just disabled) when the TextBox is read-only, so
/// the result pane doesn't show unusable greyed-out actions. The copy/paste
/// calls go through <c>ClipboardExtensions</c> (Avalonia 12.1 moved
/// GetText/SetText off <c>IClipboard</c> into extension methods).
/// </remarks>
internal static class TextBoxContextMenu
{
    /// <summary>Attaches a fresh Copy/Cut/Paste/Select-all menu to the textbox.</summary>
    public static void Attach(TextBox textBox)
    {
        var menu = new ContextMenu();

        var copy = new MenuItem { Header = Strings.Common_Copy };
        var cut = new MenuItem { Header = Strings.Common_Cut };
        var paste = new MenuItem { Header = Strings.Common_Paste };
        var selectAll = new MenuItem { Header = Strings.Common_SelectAll };

        copy.Click += async (_, _) =>
        {
            string? selected = textBox.SelectedText;
            if (!string.IsNullOrEmpty(selected))
            {
                var clipboard = TopLevel.GetTopLevel(textBox)?.Clipboard;
                if (clipboard is not null)
                {
                    await ClipboardExtensions.SetTextAsync(clipboard, selected);
                }
            }
        };

        cut.Click += (_, _) =>
        {
            if (textBox.IsReadOnly) return;
            // Cut = remove selection + copy. TextBox.Cut() handles both.
            textBox.Cut();
        };

        paste.Click += (_, _) =>
        {
            if (textBox.IsReadOnly) return;
            textBox.Paste();
        };

        selectAll.Click += (_, _) => textBox.SelectAll();

        menu.Items.Add(copy);
        menu.Items.Add(cut);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);

        // Refresh item enablement every time the menu opens, based on current
        // selection / focus state. Opening fires before the menu is shown.
        menu.Opening += async (_, _) =>
        {
            bool hasSelection = !string.IsNullOrEmpty(textBox.SelectedText);
            copy.IsEnabled = hasSelection;
            cut.IsEnabled = hasSelection && !textBox.IsReadOnly;
            selectAll.IsEnabled = !string.IsNullOrEmpty(textBox.Text);

            // Paste enablement depends on clipboard contents.
            var clipboard = TopLevel.GetTopLevel(textBox)?.Clipboard;
            if (clipboard is not null && !textBox.IsReadOnly)
            {
                string? clipText = await ClipboardExtensions.TryGetTextAsync(clipboard);
                paste.IsEnabled = !string.IsNullOrEmpty(clipText);
            }
            else
            {
                paste.IsEnabled = false;
            }

            // Hide cut/paste entirely on read-only boxes (result/source views).
            cut.IsVisible = !textBox.IsReadOnly;
            paste.IsVisible = !textBox.IsReadOnly;
        };

        textBox.ContextMenu = menu;
    }
}

