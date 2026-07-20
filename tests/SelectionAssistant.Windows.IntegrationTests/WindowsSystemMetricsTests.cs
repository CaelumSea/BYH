using SelectionAssistant.Platform.Windows;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests;

public sealed class WindowsSystemMetricsTests
{
    [Fact]
    public void GestureMetrics_ArePositive()
    {
        var metrics = new WindowsSystemMetrics();

        Assert.True(metrics.DragThresholdX > 0);
        Assert.True(metrics.DragThresholdY > 0);
        Assert.True(metrics.DoubleClickTimeMs > 0);
        Assert.True(metrics.DoubleClickWidth > 0);
        Assert.True(metrics.DoubleClickHeight > 0);
    }
}
