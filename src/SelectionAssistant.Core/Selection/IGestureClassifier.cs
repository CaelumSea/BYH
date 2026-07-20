using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Core.Selection;

/// <summary>Consumes pointer events and emits only selection-like gestures.</summary>
public interface IGestureClassifier
{
    SelectionGesture? Process(MouseEventData mouseEvent, nint rootWindowHandle, uint processId);

    void Reset();
}
