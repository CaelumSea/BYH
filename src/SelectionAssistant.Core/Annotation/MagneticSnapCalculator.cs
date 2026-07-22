using System;
using System.Collections.Generic;

namespace SelectionAssistant.Core.Annotation;

/// <summary>
/// Physical-pixel rectangle (matches Window.Position semantics).
/// </summary>
public readonly record struct PhysicalRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Snap hint returned by <see cref="MagneticSnapCalculator.ComputeSnap"/> when a snap edge is hit.
/// </summary>
/// <param name="Offset">The delta (in physical pixels) that was applied to align the edge.</param>
/// <param name="Axis">Which axis the snap acts on.</param>
/// <param name="Target">Which edge of which target the moving window aligned to.</param>
public readonly record struct SnapHint(int Offset, SnapAxis Axis, SnapTarget Target);

/// <summary>Axis of a snap operation.</summary>
public enum SnapAxis { X, Y }

/// <summary>Target edge that was snapped to.</summary>
public enum SnapTarget
{
    ScreenLeft,
    ScreenRight,
    ScreenTop,
    ScreenBottom,
    WindowLeft,
    WindowRight,
    WindowTop,
    WindowBottom,
}

/// <summary>
/// Pure-function magnetic-snap calculator for pinned screenshot windows.
/// All geometry is in physical pixels. No side effects, no I/O, AOT-safe.
/// </summary>
public static class MagneticSnapCalculator
{
    /// <summary>Default snap threshold in physical pixels. 20px ≈ 11 DIP at
    /// 175% DPI — user-tuned middle ground.
    /// Tuning history: 8 → 24 → 48 → 32 → 20 (current).</summary>
    public const double SnapThreshold = 20.0;

    /// <summary>
    /// R55: insets a physical-pixel rect on all four sides by <paramref name="margin"/>.
    /// Used by the pinned-screenshot window to derive the IMAGE rect (what snap
    /// geometry should run against) from the WINDOW rect — the window is larger
    /// than the image by a transparent shadow margin on every side. Pure
    /// function; the window layer calls this so the inset math is unit-testable.
    /// <paramref name="margin"/> is truncated to int per edge.
    /// </summary>
    public static PhysicalRect InsetRect(PhysicalRect rect, double margin)
    {
        int m = (int)margin;
        return new PhysicalRect(rect.Left + m, rect.Top + m,
                                rect.Right - m, rect.Bottom - m);
    }

    /// <summary>
    /// Compute the snapped position for a moving window.
    /// </summary>
    /// <param name="moving">The moving window's current physical rect (target position before snap).</param>
    /// <param name="workAreas">List of screen work areas (physical pixels).</param>
    /// <param name="others">List of other pinned windows' physical rects (exclude the moving window itself).</param>
    /// <param name="shiftHeld">If true, snap is disabled — returns the original position.</param>
    /// <param name="threshold">Snap threshold in physical pixels (default 8).</param>
    /// <returns>
    /// <c>snappedTopLeft</c>: the (X, Y) position after snapping (or original if no snap);
    /// <c>hints</c>: list of snap hints for each axis that hit (empty if no snap).
    /// </returns>
    public static ((int X, int Y) snappedTopLeft, IReadOnlyList<SnapHint> hints) ComputeSnap(
        PhysicalRect moving,
        IReadOnlyList<PhysicalRect> workAreas,
        IReadOnlyList<PhysicalRect> others,
        bool shiftHeld = false,
        double threshold = SnapThreshold)
    {
        if (shiftHeld)
        {
            return ((moving.Left, moving.Top), Array.Empty<SnapHint>());
        }

        int bestDx = 0;
        int bestAbsDx = int.MaxValue;
        SnapHint? bestXHint = null;

        int bestDy = 0;
        int bestAbsDy = int.MaxValue;
        SnapHint? bestYHint = null;

        // --- Check against screen work area edges ---
        foreach (var wa in workAreas)
        {
            // X axis: screen left
            CheckX(moving.Left, wa.Left, threshold,
                   ref bestDx, ref bestAbsDx, ref bestXHint,
                   0, SnapAxis.X, SnapTarget.ScreenLeft);
            // X axis: screen right
            CheckX(moving.Right, wa.Right, threshold,
                   ref bestDx, ref bestAbsDx, ref bestXHint,
                   wa.Right - moving.Width, SnapAxis.X, SnapTarget.ScreenRight);

            // Y axis: screen top
            CheckY(moving.Top, wa.Top, threshold,
                   ref bestDy, ref bestAbsDy, ref bestYHint,
                   0, SnapAxis.Y, SnapTarget.ScreenTop);
            // Y axis: screen bottom
            CheckY(moving.Bottom, wa.Bottom, threshold,
                   ref bestDy, ref bestAbsDy, ref bestYHint,
                   wa.Bottom - moving.Height, SnapAxis.Y, SnapTarget.ScreenBottom);
        }

        // --- Check against other pinned window edges ---
        foreach (var other in others)
        {
            // X axis: other's left edge vs moving's right edge
            CheckX(moving.Right, other.Left, threshold,
                   ref bestDx, ref bestAbsDx, ref bestXHint,
                   other.Left - moving.Width, SnapAxis.X, SnapTarget.WindowLeft);
            // X axis: other's right edge vs moving's left edge
            CheckX(moving.Left, other.Right, threshold,
                   ref bestDx, ref bestAbsDx, ref bestXHint,
                   other.Right, SnapAxis.X, SnapTarget.WindowRight);

            // Y axis: other's top edge vs moving's bottom edge
            CheckY(moving.Bottom, other.Top, threshold,
                   ref bestDy, ref bestAbsDy, ref bestYHint,
                   other.Top - moving.Height, SnapAxis.Y, SnapTarget.WindowTop);
            // Y axis: other's bottom edge vs moving's top edge
            CheckY(moving.Top, other.Bottom, threshold,
                   ref bestDy, ref bestAbsDy, ref bestYHint,
                   other.Bottom, SnapAxis.Y, SnapTarget.WindowBottom);
        }

        int snappedX = moving.Left + bestDx;
        int snappedY = moving.Top + bestDy;

        var hints = new List<SnapHint>(2);
        if (bestXHint is not null) hints.Add(bestXHint.Value);
        if (bestYHint is not null) hints.Add(bestYHint.Value);

        return ((snappedX, snappedY), hints);
    }

    private static void CheckX(
        int movingEdge, int targetEdge, double threshold,
        ref int bestDx, ref int bestAbsDx, ref SnapHint? bestHint,
        int snappedLeft, SnapAxis axis, SnapTarget target)
    {
        int delta = targetEdge - movingEdge;
        int absDelta = Math.Abs(delta);
        if (absDelta <= threshold && absDelta < bestAbsDx)
        {
            bestDx = delta;
            bestAbsDx = absDelta;
            bestHint = new SnapHint(delta, axis, target);
        }
    }

    private static void CheckY(
        int movingEdge, int targetEdge, double threshold,
        ref int bestDy, ref int bestAbsDy, ref SnapHint? bestHint,
        int snappedTop, SnapAxis axis, SnapTarget target)
    {
        int delta = targetEdge - movingEdge;
        int absDelta = Math.Abs(delta);
        if (absDelta <= threshold && absDelta < bestAbsDy)
        {
            bestDy = delta;
            bestAbsDy = absDelta;
            bestHint = new SnapHint(delta, axis, target);
        }
    }
}
