namespace SelectionAssistant.Core.Annotation;

/// <summary>
/// R48: static BGRA pixel-buffer drawing primitives for burning annotations
/// into PNG screenshots. All methods write directly into a BGRA byte array
/// (no SkiaSharp dependency). Uses Bresenham line/ellipse algorithms + alpha
/// blending. 1px jaggedness is accepted (no anti-aliasing).
/// </summary>
public static class BurnInHelpers
{
    /// <summary>
    /// Draws a line segment on a BGRA pixel buffer using Bresenham's algorithm.
    /// </summary>
    public static void DrawLineOnBgra(
        byte[] bgra, int imgW, int imgH,
        int x0, int y0, int x1, int y1,
        int thickness,
        byte b, byte g, byte r, byte a)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        int halfThick = thickness / 2;

        while (true)
        {
            // Draw a filled square of thickness around the current point.
            for (int oy = -halfThick; oy <= halfThick; oy++)
            {
                for (int ox = -halfThick; ox <= halfThick; ox++)
                {
                    SetPixelAlphaBlend(bgra, imgW, imgH, x0 + ox, y0 + oy, b, g, r, a);
                }
            }

            if (x0 == x1 && y0 == y1) break;

            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// Draws a rectangle stroke (outline) on a BGRA pixel buffer.
    /// Draws four lines forming the rectangle border.
    /// </summary>
    public static void DrawRectangleStrokeOnBgra(
        byte[] bgra, int imgW, int imgH,
        int left, int top, int right, int bottom,
        int thickness,
        byte b, byte g, byte r, byte a)
    {
        // Top edge
        DrawLineOnBgra(bgra, imgW, imgH, left, top, right, top, thickness, b, g, r, a);
        // Right edge
        DrawLineOnBgra(bgra, imgW, imgH, right, top, right, bottom, thickness, b, g, r, a);
        // Bottom edge
        DrawLineOnBgra(bgra, imgW, imgH, right, bottom, left, bottom, thickness, b, g, r, a);
        // Left edge
        DrawLineOnBgra(bgra, imgW, imgH, left, bottom, left, top, thickness, b, g, r, a);
    }

    /// <summary>
    /// Draws an ellipse stroke on a BGRA pixel buffer using the midpoint
    /// ellipse algorithm.
    /// </summary>
    public static void DrawEllipseStrokeOnBgra(
        byte[] bgra, int imgW, int imgH,
        int cx, int cy, int rx, int ry,
        int thickness,
        byte b, byte g, byte r, byte a)
    {
        if (rx <= 0 || ry <= 0) return;

        int halfThick = thickness / 2;

        // Midpoint ellipse algorithm: plot 4 symmetric points per step.
        void PlotSymmetric(int px, int py)
        {
            for (int oy = -halfThick; oy <= halfThick; oy++)
            {
                for (int ox = -halfThick; ox <= halfThick; ox++)
                {
                    SetPixelAlphaBlend(bgra, imgW, imgH, cx + px + ox, cy + py + oy, b, g, r, a);
                    SetPixelAlphaBlend(bgra, imgW, imgH, cx - px + ox, cy + py + oy, b, g, r, a);
                    SetPixelAlphaBlend(bgra, imgW, imgH, cx + px + ox, cy - py + oy, b, g, r, a);
                    SetPixelAlphaBlend(bgra, imgW, imgH, cx - px + ox, cy - py + oy, b, g, r, a);
                }
            }
        }

        long rx2 = (long)rx * rx;
        long ry2 = (long)ry * ry;

        // Region 1: slope magnitude < 1
        int x = 0, y = ry;
        long d1 = ry2 - rx2 * ry + rx2 / 4;
        PlotSymmetric(x, y);

        while (2L * ry2 * x < 2L * rx2 * y)
        {
            x++;
            if (d1 < 0)
            {
                d1 += 2L * ry2 * x + ry2;
            }
            else
            {
                y--;
                d1 += 2L * ry2 * x - 2L * rx2 * y + ry2;
            }
            PlotSymmetric(x, y);
        }

        // Region 2: slope magnitude >= 1
        long d2 = ry2 * (x * 2L + 1) * (x * 2L + 1) / 4 + rx2 * (y - 1L) * (y - 1L) - rx2 * ry2;
        while (y > 0)
        {
            y--;
            if (d2 > 0)
            {
                d2 += -2L * rx2 * y + rx2;
            }
            else
            {
                x++;
                d2 += 2L * ry2 * x - 2L * rx2 * y + rx2;
            }
            PlotSymmetric(x, y);
        }
    }

    /// <summary>
    /// Draws an arrow: a line segment + a triangular arrow head at the end.
    /// </summary>
    public static void DrawArrowOnBgra(
        byte[] bgra, int imgW, int imgH,
        int startX, int startY, int endX, int endY,
        int lineThickness,
        byte b, byte g, byte r, byte a)
    {
        // Draw the shaft.
        DrawLineOnBgra(bgra, imgW, imgH, startX, startY, endX, endY, lineThickness, b, g, r, a);

        // Compute arrow head using the geometry helper.
        var (tipX, tipY, b1x, b1y, b2x, b2y) = AnnotationShapeGeometry.ComputeArrowHead(
            startX, startY, endX, endY);

        // Draw three lines forming the arrow head.
        DrawLineOnBgra(bgra, imgW, imgH, (int)tipX, (int)tipY, (int)b1x, (int)b1y, lineThickness, b, g, r, a);
        DrawLineOnBgra(bgra, imgW, imgH, (int)tipX, (int)tipY, (int)b2x, (int)b2y, lineThickness, b, g, r, a);
        // Close the arrow head base.
        DrawLineOnBgra(bgra, imgW, imgH, (int)b1x, (int)b1y, (int)b2x, (int)b2y, lineThickness, b, g, r, a);
    }

    /// <summary>
    /// Draws a path (series of connected line segments) on a BGRA pixel buffer.
    /// </summary>
    public static void DrawPathOnBgra(
        byte[] bgra, int imgW, int imgH,
        IReadOnlyList<(double X, double Y)> points,
        int thickness,
        byte b, byte g, byte r, byte a)
    {
        if (points.Count < 2) return;

        for (int i = 0; i < points.Count - 1; i++)
        {
            DrawLineOnBgra(bgra, imgW, imgH,
                (int)points[i].X, (int)points[i].Y,
                (int)points[i + 1].X, (int)points[i + 1].Y,
                thickness, b, g, r, a);
        }
    }

    /// <summary>
    /// Sets a pixel with alpha blending (src over dst).
    /// </summary>
    private static void SetPixelAlphaBlend(
        byte[] bgra, int imgW, int imgH,
        int x, int y,
        byte srcB, byte srcG, byte srcR, byte srcA)
    {
        if (x < 0 || x >= imgW || y < 0 || y >= imgH) return;

        int off = (y * imgW + x) * 4;
        float sA = srcA / 255f;
        float dA = bgra[off + 3] / 255f;
        float outA = sA + dA * (1 - sA);
        if (outA > 0)
        {
            bgra[off] = (byte)((srcB * sA + bgra[off] * dA * (1 - sA)) / outA);
            bgra[off + 1] = (byte)((srcG * sA + bgra[off + 1] * dA * (1 - sA)) / outA);
            bgra[off + 2] = (byte)((srcR * sA + bgra[off + 2] * dA * (1 - sA)) / outA);
            bgra[off + 3] = (byte)(outA * 255);
        }
    }
}
