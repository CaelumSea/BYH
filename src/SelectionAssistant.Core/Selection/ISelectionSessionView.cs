using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Core.Selection;

/// <summary>UI operations used by the session manager.</summary>
public interface ISelectionSessionView
{
    void ShowToolbar(SelectionGesture gesture);

    void HideToolbar();

    void SetCaptureResult(CaptureResult result);
}

/// <summary>Dispatches selection UI work to the framework's UI thread.</summary>
public interface ISelectionUiDispatcher
{
    Task InvokeAsync(Action action);
}
