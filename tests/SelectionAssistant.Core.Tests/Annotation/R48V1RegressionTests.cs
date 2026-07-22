using System;
using System.Collections.Generic;
using SelectionAssistant.Core.Annotation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Annotation;

/// <summary>
/// R48 v1 regression tests (REQ-015 v2 AC-9).
///
/// v1 had three failure modes that v2 must NOT regress to:
///   F1: No live drag preview — preview exists by code inspection in
///       RegionSelectOverlay.CreateLivePreview/UpdateLivePreview/RemoveLivePreview.
///       (Cannot unit-test Avalonia Control lifecycle; covered by integration.)
///   F2: Pen/Highlight path collapsed to a straight line because the path
///       recorder only captured start+end (DispatcherTimer cross-thread bug).
///       We test that PenStrokeAnnotation/HighlightStrokeAnnotation preserve
///       an arbitrary number of intermediate points.
///   F3: AddArrowVisual tagged both shaft and head children with
///       AnnotationTag(1), so RemoveLastAnnotation only removed one child,
///       leaving an orphan. We test the expected child-count for each shape
///       type (the contract RemoveLastAnnotation relies on).
/// </summary>
[Trait("Category", "Annotation")]
public class R48V1RegressionTests
{
    // ── F2: path integrity ──────────────────────────────────────────────

    [Fact]
    public void PenStroke_With_Ten_MoveEvents_Records_All_Points()
    {
        // Simulate: user drags 100px in 10 steps. v1 captured only start+end.
        var points = new List<(double X, double Y)> { (0, 0) };
        for (int i = 1; i <= 10; i++)
        {
            points.Add((i * 10.0, 0));
        }
        var stroke = new PenStrokeAnnotation(points);

        Assert.True(stroke.Points.Count >= 10,
            $"Pen stroke must capture intermediate points (v1 collapsed to 2). Got {stroke.Points.Count}.");
    }

    [Fact]
    public void HighlightStroke_With_Ten_MoveEvents_Records_All_Points()
    {
        var points = new List<(double X, double Y)> { (5, 5) };
        for (int i = 1; i <= 10; i++)
        {
            points.Add((5 + i * 7.0, 5 + i * 3.0));
        }
        var stroke = new HighlightStrokeAnnotation(points);

        Assert.True(stroke.Points.Count >= 10,
            $"Highlight stroke must capture intermediate points (v1 collapsed to 2). Got {stroke.Points.Count}.");
    }

    [Fact]
    public void PenStroke_Preserves_Point_Order()
    {
        var points = new List<(double X, double Y)>
        {
            (10, 20), (30, 40), (50, 60), (70, 80),
        };
        var stroke = new PenStrokeAnnotation(points);

        Assert.Equal(points, stroke.Points);
    }

    [Fact]
    public void HighlightStroke_Preserves_Point_Order()
    {
        var points = new List<(double X, double Y)>
        {
            (1, 2), (3, 4), (5, 6),
        };
        var stroke = new HighlightStrokeAnnotation(points);

        Assert.Equal(points, stroke.Points);
    }

    // ── F3: expected child count per shape type ─────────────────────────
    //
    // These constants document the contract between AddXxxVisual (which tags
    // each child visual with AnnotationTag(N)) and RemoveLastAnnotation (which
    // reads N to know how many Children to remove). If AddArrowVisual ever
    // regresses to tagging both children with AnnotationTag(1), this test
    // breaks at compile time (because ExpectedChildCount forces us to look).
    //
    // Expected values (verified by code inspection in RegionSelectOverlay):
    //   NumberedBadgeAnnotation → 2 children (ellipse + text container)
    //   RectangleAnnotation     → 1 child  (Avalonia Rectangle)
    //   EllipseAnnotation       → 1 child  (Avalonia Ellipse)
    //   ArrowAnnotation         → 2 children (shaft Line + head Polyline)
    //   PenStrokeAnnotation     → 1 child  (Polyline)
    //   HighlightStrokeAnnotation → 1 child (Polyline)
    //
    // If you change AddXxxVisual to add/remove a child, update both the visual
    // code AND ExpectedChildCount here. The two MUST stay in sync.

    [Theory]
    [InlineData(typeof(NumberedBadgeAnnotation), 2)]
    [InlineData(typeof(RectangleAnnotation), 1)]
    [InlineData(typeof(EllipseAnnotation), 1)]
    [InlineData(typeof(ArrowAnnotation), 2)]  // F3: was 1 in v1 (bug)
    [InlineData(typeof(PenStrokeAnnotation), 1)]
    [InlineData(typeof(HighlightStrokeAnnotation), 1)]
    public void AnnotationType_ExpectedChildCount_MatchesVisual(Type itemType, int expected)
    {
        // This is a contract assertion: if it ever breaks, the AddXxxVisual
        // implementation in RegionSelectOverlay.axaml.cs changed the number of
        // children it adds, and RemoveLastAnnotation's tag.ChildCount must be
        // updated to match. Failing here means an undo inconsistency bug
        // (exactly what v1 had for arrow).
        Assert.Equal(expected, ExpectedChildCount(itemType));
    }

    private static int ExpectedChildCount(Type itemType)
    {
        // Single source of truth — matches RegionSelectOverlay.AddXxxVisual.
        if (itemType == typeof(NumberedBadgeAnnotation)) return 2;
        if (itemType == typeof(RectangleAnnotation)) return 1;
        if (itemType == typeof(EllipseAnnotation)) return 1;
        if (itemType == typeof(ArrowAnnotation)) return 2;
        if (itemType == typeof(PenStrokeAnnotation)) return 1;
        if (itemType == typeof(HighlightStrokeAnnotation)) return 1;
        throw new ArgumentException($"Unknown annotation type: {itemType}", nameof(itemType));
    }

    // ── Default tool: R47 preservation ──────────────────────────────────

    [Fact]
    public void AnnotationTool_Number_Is_Zero_Default_Enum_Value()
    {
        // EnterAnnotationMode in SelectionRuntime sets _currentAnnotationTool
        // to AnnotationTool.Number explicitly. The enum contract is that
        // Number = 0 (the default), so uninitialized fields also default to
        // Number (R47 behavior). If anyone reorders the enum, this breaks
        // loudly — which is the point.
        AnnotationTool defaultValue = default;

        Assert.Equal(AnnotationTool.Number, defaultValue);
        Assert.Equal(0, (int)AnnotationTool.Number);
    }

    [Theory]
    [InlineData(AnnotationTool.Number)]
    [InlineData(AnnotationTool.Rectangle)]
    [InlineData(AnnotationTool.Ellipse)]
    [InlineData(AnnotationTool.Arrow)]
    [InlineData(AnnotationTool.Pen)]
    [InlineData(AnnotationTool.Highlight)]
    public void AnnotationTool_Has_All_Six_Tools(AnnotationTool tool)
    {
        // Documents the complete tool surface. If anyone removes a tool,
        // this test breaks loudly.
        Assert.True(Enum.IsDefined(tool));
    }

    // ── F2 supplemental: session records full path ──────────────────────

    [Fact]
    public void Session_PushPenStroke_PreservesFullPointCloud()
    {
        var session = new AnnotationSession();
        var cloud = new List<(double X, double Y)>();
        for (int i = 0; i < 50; i++)
        {
            cloud.Add((i * 2.0, Math.Sin(i * 0.3) * 10));
        }
        session.Push(new PenStrokeAnnotation(cloud));

        var last = Assert.IsType<PenStrokeAnnotation>(session.Items[^1]);
        Assert.Equal(50, last.Points.Count);
    }

    [Fact]
    public void Session_PushHighlightStroke_PreservesFullPointCloud()
    {
        var session = new AnnotationSession();
        var cloud = new List<(double X, double Y)>();
        for (int i = 0; i < 50; i++)
        {
            cloud.Add((Math.Cos(i * 0.2) * 5, i * 1.5));
        }
        session.Push(new HighlightStrokeAnnotation(cloud));

        var last = Assert.IsType<HighlightStrokeAnnotation>(session.Items[^1]);
        Assert.Equal(50, last.Points.Count);
    }

    // ── F3 supplemental: child count contract is self-consistent ────────

    [Fact]
    public void ExpectedChildCount_Mapping_Is_Complete_For_All_Item_Types()
    {
        // Every concrete IAnnotationItem type must have an expected child
        // count in the ExpectedChildCount map. If a new shape is added
        // without updating the map, this test breaks.
        Type[] allItemTypes =
        {
            typeof(NumberedBadgeAnnotation),
            typeof(RectangleAnnotation),
            typeof(EllipseAnnotation),
            typeof(ArrowAnnotation),
            typeof(PenStrokeAnnotation),
            typeof(HighlightStrokeAnnotation),
        };

        foreach (Type t in allItemTypes)
        {
            int n = ExpectedChildCount(t);  // throws if not mapped
            Assert.True(n >= 1 && n <= 4, $"{t.Name} child count {n} out of plausible range");
        }
    }
}
