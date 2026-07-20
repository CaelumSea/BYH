using SelectionAssistant.Platform.Windows.Capture;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Capture;

public sealed class WindowsUiAutomationBackendSmokeTests
{
    [Fact]
    public void NativeClient_CanBeCreatedWithoutManagedComWrappers()
    {
        using var backend = new WindowsUiAutomationBackend();
        Assert.True(backend.ProbeAvailability());
    }
}
