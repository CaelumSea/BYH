namespace SelectionAssistant.Core.Annotation;

/// <summary>
/// R48: pure-function geometry calculations for annotation shape rendering.
/// All methods are static and side-effect-free for unit-testability.
/// </summary>
public static class AnnotationShapeGeometry
{
    /// <summary>Gold stroke color BGRA components: #FFD9C28A.</summary>
    public const byte GoldB = 0xAA, GoldG = 0xC2, GoldR = 0xD9, GoldA = 0xFF;

    /// <summary>Highlight color BGRA components: #80FFEB3B (alpha=0x80).</summary>
    public const byte HighlightB = 0x3B, HighlightG = 0xEB, HighlightR = 0xFF, HighlightA = 0x80;

    /// <summary>Default stroke thickness for shapes (2px at 100% DPI).</summary>
    public const double StrokeThicknessDip = 2;

    /// <summary>Highlight stroke thickness (8px at 100% DPI).</summary>
    public const double HighlightThicknessDip = 8;

    /// <summary>Arrow head length in DIP.</summary>
    public const double ArrowHeadLengthDip = 12;

    /// <summary>
    /// Constrains a rectangle to a square when shift is held.
    /// Uses the smaller dimension (min of width/height).
    /// Returns the constrained rect (same Left/Top origin).
    /// </summary>
    public static RectangleAnnotation ApplyShiftConstraint(RectangleAnnotation rect, bool shift)
    {
        if (!shift) return rect;

        double side = Math.Min(rect.Width, rect.Height);
        return rect with { Width = side, Height = side };
    }

    /// <summary>
    /// Constrains an ellipse to a circle when shift is held.
    /// Uses the smaller dimension (min of width/height).
    /// Returns the constrained ellipse (same Left/Top origin).
    /// </summary>
    public static EllipseAnnotation ApplyShiftConstraint(EllipseAnnotation ellipse, bool shift)
    {
        if (!shift) return ellipse;

        double side = Math.Min(ellipse.Width, ellipse.Height);
        return ellipse with { Width = side, Height = side };
    }

    /// <summary>
    /// Computes the three points of an arrow head (triangle tip + two barbs).
    /// The arrow head is at the end point, pointing back toward the start.
    /// </summary>
    /// <param name="startX">Arrow line start X.</param>
    /// <param name="startY">Arrow line start Y.</param>
    /// <param name="endX">Arrow line end X (where the head tip is).</param>
    /// <param name="endY">Arrow line end Y (where the head tip is).</param>
    /// <param name="headLength">Length of the arrow head in DIP (default 12).</param>
    /// <param name="headAngleDeg">Half-angle of the arrow head cone in degrees (default 25).</param>
    /// <returns>(TipX, TipY, Barb1X, Barb1Y, Barb2X, Barb2Y).</returns>
    public static (double TipX, double TipY, double Barb1X, double Barb1Y, double Barb2X, double Barb2Y)
        ComputeArrowHead(double startX, double startY, double endX, double endY,
            double headLength = ArrowHeadLengthDip, double headAngleDeg = 25)
    {
        double dx = endX - startX;
        double dy = endY - startY;
        double len = Math.Sqrt(dx * dx + dy * dy);

        // Degenerate arrow (zero length): point barbs at the end itself.
        if (len < 0.001)
        {
            return (endX, endY, endX, endY, endX, endY);
        }

        // Unit vector from start to end (direction of arrow).
        double ux = dx / len;
        double uy = dy / len;

        double angleRad = headAngleDeg * Math.PI / 180.0;
        double cosA = Math.Cos(angleRad);
        double sinA = Math.Sin(angleRad);

        // Barb1: rotate direction vector by +angle, scale by headLength.
        double b1x = endX - headLength * (ux * cosA + uy * sinA);
        double b1y = endY - headLength * (-ux * sinA + uy * cosA);

        // Barb2: rotate direction vector by -angle, scale by headLength.
        double b2x = endX - headLength * (ux * cosA - uy * sinA);
        double b2y = endY - headLength * (ux * sinA + uy * cosA);

        return (endX, endY, b1x, b1y, b2x, b2y);
    }

    /// <summary>
    /// Normalizes a drag rectangle so that Left/Top is the true top-left
    /// (handles dragging from bottom-right to top-left).
    /// </summary>
    public static RectangleAnnotation NormalizeRect(double x1, double y1, double x2, double y2)
    {
        double left = Math.Min(x1, x2);
        double top = Math.Min(y1, y2);
        double w = Math.Abs(x2 - x1);
        double h = Math.Abs(y2 - y1);
        return new RectangleAnnotation(left, top, w, h);
    }

    /// <summary>
    /// Normalizes a drag ellipse (bounding box) so that Left/Top is the
    /// true top-left corner.
    /// </summary>
    public static EllipseAnnotation NormalizeEllipse(double x1, double y1, double x2, double y2)
    {
        double left = Math.Min(x1, x2);
        double top = Math.Min(y1, y2);
        double w = Math.Abs(x2 - x1);
        double h = Math.Abs(y2 - y1);
        return new EllipseAnnotation(left, top, w, h);
    }
}
