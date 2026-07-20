using Avalonia.Threading;
using SelectionAssistant.Core.Selection;
using SelectionAssistant.Core.Translation;

namespace SelectionAssistant.App;

internal sealed class AvaloniaSelectionUiDispatcher : ISelectionUiDispatcher, ITranslationUiDispatcher
{
    public async Task InvokeAsync(Action action)
    {
        await Dispatcher.UIThread.InvokeAsync(action);
    }
}
