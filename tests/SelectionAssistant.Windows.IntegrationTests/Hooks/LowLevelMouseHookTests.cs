using SelectionAssistant.Platform.Windows.Hooks;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Hooks;

public sealed class LowLevelMouseHookTests
{
    [Fact]
    public void StartAndDispose_InstallsAndStopsDedicatedHookThread()
    {
        var messages = new List<string>();

        using (var hook = new LowLevelMouseHook(messages.Add))
        {
            hook.Start();
        }

        Assert.Contains(messages, message => message.StartsWith("Mouse hook installed", StringComparison.Ordinal));
        Assert.Contains("Mouse hook stopped.", messages);
    }
}
