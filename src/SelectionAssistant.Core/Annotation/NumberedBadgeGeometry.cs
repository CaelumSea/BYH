namespace SelectionAssistant.Core.Annotation;

/// <summary>
/// R47: pure-function geometry calculations for numbered badge rendering.
/// Separated from UI concerns so the logic is unit-testable.
/// All outputs are in physical pixels (overlay DIP × DPI scale factor).
/// </summary>
public static class NumberedBadgeGeometry
{
    /// <summary>Badge diameter in DIP (device-independent pixels).</summary>
    public const double DiameterDip = 28;

    /// <summary>Badge radius in DIP.</summary>
    public const double RadiusDip = DiameterDip / 2;

    /// <summary>Stroke thickness in DIP.</summary>
    public const double StrokeThicknessDip = 1;

    /// <summary>Font size in DIP for the centered digit.</summary>
    public const double FontSizeDip = 14;

    /// <summary>
    /// Computes the physical-pixel radius for badge rendering at the given DPI scale.
    /// </summary>
    /// <param name="dpiScale">DPI scale factor (1.0 = 100%, 1.5 = 150%, 2.0 = 200%).</param>
    public static double GetRadius(double dpiScale) => RadiusDip * dpiScale;

    /// <summary>
    /// Computes the physical-pixel diameter for badge rendering at the given DPI scale.
    /// </summary>
    public static double GetDiameter(double dpiScale) => DiameterDip * dpiScale;

    /// <summary>
    /// Computes the physical-pixel stroke thickness at the given DPI scale.
    /// Minimum 1 physical pixel.
    /// </summary>
    public static double GetStrokeThickness(double dpiScale) =>
        Math.Max(1, StrokeThicknessDip * dpiScale);

    /// <summary>
    /// Computes the physical-pixel font size for the centered digit at the given DPI scale.
    /// </summary>
    public static double GetFontSize(double dpiScale) => FontSizeDip * dpiScale;

    /// <summary>
    /// Converts a badge's DIP coordinates to physical-pixel center coordinates.
    /// </summary>
    /// <param name="badge">The badge with DIP coordinates.</param>
    /// <param name="dpiScale">DPI scale factor.</param>
    /// <returns>(CenterX, CenterY) in physical pixels.</returns>
    public static (double CenterX, double CenterY) GetPhysicalCenter(
        NumberedBadge badge, double dpiScale)
    {
        return (badge.X * dpiScale, badge.Y * dpiScale);
    }

    /// <summary>
    /// Computes the bounding box (in physical pixels) for a badge,
    /// suitable for pixel-level rendering (Skia or raw BGRA buffer).
    /// Returns (Left, Top, Width, Height) where Left/Top is the top-left
    /// corner of the bounding square enclosing the circle.
    /// </summary>
    public static (double Left, double Top, double Width, double Height) GetPhysicalBounds(
        NumberedBadge badge, double dpiScale)
    {
        double r = GetRadius(dpiScale);
        double cx = badge.X * dpiScale;
        double cy = badge.Y * dpiScale;
        return (cx - r, cy - r, r * 2, r * 2);
    }
}
