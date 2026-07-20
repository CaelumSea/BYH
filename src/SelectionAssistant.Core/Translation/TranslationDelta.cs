namespace SelectionAssistant.Core.Translation;

/// <summary>
/// A single incremental chunk emitted by a streaming provider. Content should
/// be appended to the in-progress result text. Empty deltas are permitted and
/// the consumer may skip them.
/// </summary>
public sealed record TranslationDelta(string Content);
