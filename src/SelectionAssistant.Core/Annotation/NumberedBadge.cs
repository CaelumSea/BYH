namespace SelectionAssistant.Core.Annotation;

/// <summary>
/// R47: a single numbered badge placed during annotation mode.
/// <see cref="X"/> and <see cref="Y"/> are in overlay DIP coordinates
/// (device-independent pixels, matching Avalonia Canvas layout).
/// </summary>
public sealed record NumberedBadge(int Number, double X, double Y);
