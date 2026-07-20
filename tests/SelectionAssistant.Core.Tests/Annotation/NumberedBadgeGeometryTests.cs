using SelectionAssistant.Core.Annotation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Annotation;

public sealed class NumberedBadgeGeometryTests
{
    [Fact]
    public void GetRadius_At100Percent_Returns14()
    {
        Assert.Equal(14, NumberedBadgeGeometry.GetRadius(1.0));
    }

    [Fact]
    public void GetDiameter_At100Percent_Returns28()
    {
        Assert.Equal(28, NumberedBadgeGeometry.GetDiameter(1.0));
    }

    [Fact]
    public void GetRadius_At175Percent_ScalesCorrectly()
    {
        Assert.Equal(24.5, NumberedBadgeGeometry.GetRadius(1.75));
    }

    [Fact]
    public void GetDiameter_At200Percent_Returns56()
    {
        Assert.Equal(56, NumberedBadgeGeometry.GetDiameter(2.0));
    }

    [Fact]
    public void GetFontSize_At100Percent_Returns14()
    {
        Assert.Equal(14, NumberedBadgeGeometry.GetFontSize(1.0));
    }

    [Fact]
    public void GetFontSize_At150Percent_ScalesCorrectly()
    {
        Assert.Equal(21, NumberedBadgeGeometry.GetFontSize(1.5));
    }

    [Fact]
    public void GetStrokeThickness_At100Percent_Returns1()
    {
        Assert.Equal(1, NumberedBadgeGeometry.GetStrokeThickness(1.0));
    }

    [Fact]
    public void GetStrokeThickness_At200Percent_Returns2()
    {
        Assert.Equal(2, NumberedBadgeGeometry.GetStrokeThickness(2.0));
    }

    [Fact]
    public void GetStrokeThickness_At50Percent_ClampsToMinimum1()
    {
        // At very low DPI, stroke should still be at least 1 physical pixel.
        Assert.Equal(1, NumberedBadgeGeometry.GetStrokeThickness(0.5));
    }

    [Fact]
    public void GetPhysicalCenter_At100Percent_ReturnsSameCoordinates()
    {
        var badge = new NumberedBadge(1, 100, 200);

        (double cx, double cy) = NumberedBadgeGeometry.GetPhysicalCenter(badge, 1.0);

        Assert.Equal(100, cx);
        Assert.Equal(200, cy);
    }

    [Fact]
    public void GetPhysicalCenter_At150Percent_ScalesCoordinates()
    {
        var badge = new NumberedBadge(1, 100, 200);

        (double cx, double cy) = NumberedBadgeGeometry.GetPhysicalCenter(badge, 1.5);

        Assert.Equal(150, cx);
        Assert.Equal(300, cy);
    }

    [Fact]
    public void GetPhysicalCenter_At200Percent_DoublesCoordinates()
    {
        var badge = new NumberedBadge(1, 50, 75);

        (double cx, double cy) = NumberedBadgeGeometry.GetPhysicalCenter(badge, 2.0);

        Assert.Equal(100, cx);
        Assert.Equal(150, cy);
    }

    [Fact]
    public void GetPhysicalBounds_At100Percent_CenteredOnBadge()
    {
        var badge = new NumberedBadge(1, 100, 200);

        (double left, double top, double w, double h) =
            NumberedBadgeGeometry.GetPhysicalBounds(badge, 1.0);

        Assert.Equal(86, left);   // 100 - 14
        Assert.Equal(186, top);   // 200 - 14
        Assert.Equal(28, w);      // 14 * 2
        Assert.Equal(28, h);      // 14 * 2
    }

    [Fact]
    public void GetPhysicalBounds_At200Percent_ScalesAllDimensions()
    {
        var badge = new NumberedBadge(1, 100, 200);

        (double left, double top, double w, double h) =
            NumberedBadgeGeometry.GetPhysicalBounds(badge, 2.0);

        Assert.Equal(172, left);  // 200 - 28
        Assert.Equal(372, top);   // 400 - 28
        Assert.Equal(56, w);      // 28 * 2
        Assert.Equal(56, h);      // 28 * 2
    }

    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal(28, NumberedBadgeGeometry.DiameterDip);
        Assert.Equal(14, NumberedBadgeGeometry.RadiusDip);
        Assert.Equal(1, NumberedBadgeGeometry.StrokeThicknessDip);
        Assert.Equal(14, NumberedBadgeGeometry.FontSizeDip);
    }

    [Fact]
    public void NumberedBadge_RecordEquality()
    {
        var a = new NumberedBadge(1, 10, 20);
        var b = new NumberedBadge(1, 10, 20);
        var c = new NumberedBadge(2, 10, 20);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
