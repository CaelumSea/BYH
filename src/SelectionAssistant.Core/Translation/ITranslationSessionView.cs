namespace SelectionAssistant.Core.Translation;

public interface ITranslationSessionView
{
    void ShowLoading(TranslationRequest request, string providerName);

    void ShowResult(TranslationResult result);

    /// <summary>
    /// Appends an incremental chunk from a streaming provider. The first call
    /// for a given request replaces the loading placeholder; subsequent calls
    /// append. This is always called from the UI thread by the session manager
    /// and is generation-guarded, so stale streams never reach the view.
    /// </summary>
    void AppendPartialResult(string chunk);

    void ShowError(string userMessage);

    void Hide();
}

public interface ITranslationUiDispatcher
{
    Task InvokeAsync(Action action);
}
