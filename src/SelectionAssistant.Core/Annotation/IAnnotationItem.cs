namespace SelectionAssistant.Core.Annotation;

/// <summary>
/// Common interface for all annotation items (badges + shapes).
/// Enables a unified undo stack and burn-in dispatch.
/// All implementations are sealed records for AOT safety (no reflection needed).
/// </summary>
public interface IAnnotationItem { }

/// <summary>R48: wraps a numbered badge (from R47) as an annotation item.</summary>
public sealed record NumberedBadgeAnnotation(int Number, double X, double Y) : IAnnotationItem;

/// <summary>R48: axis-aligned rectangle annotation (top-left corner + size).</summary>
public sealed record RectangleAnnotation(double Left, double Top, double Width, double Height) : IAnnotationItem;

/// <summary>R48: axis-aligned ellipse annotation (bounding box).</summary>
public sealed record EllipseAnnotation(double Left, double Top, double Width, double Height) : IAnnotationItem;

/// <summary>R48: directional arrow annotation (start/end points).</summary>
public sealed record ArrowAnnotation(double StartX, double StartY, double EndX, double EndY) : IAnnotationItem;

/// <summary>R48: freehand pen stroke (sequence of connected points).</summary>
public sealed record PenStrokeAnnotation(IReadOnlyList<(double X, double Y)> Points) : IAnnotationItem;

/// <summary>R48: highlight stroke (same as pen but semi-transparent yellow, 8px wide).</summary>
public sealed record HighlightStrokeAnnotation(IReadOnlyList<(double X, double Y)> Points) : IAnnotationItem;
