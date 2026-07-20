using SelectionAssistant.Core.Input;
using SelectionAssistant.Platform.Windows.Input;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Input;

public sealed class WindowsGlobalHotKeyTests
{
    [Theory]
    [InlineData("Q", 0x51u)]
    [InlineData("5", 0x35u)]
    [InlineData("F1", 0x70u)]
    [InlineData("F12", 0x7Bu)]
    [InlineData("Space", 0x20u)]
    public void ToVirtualKey_MapsSupportedKeys(string key, uint expected) =>
        Assert.Equal(expected, WindowsGlobalHotKey.ToVirtualKey(key));

    [Fact]
    public void RegisteringSameShortcutTwice_ReportsConflict_AndReleaseAllowsReuse()
    {
        var settings = new OceanEyesTriggerSettings
        {
            Modifiers = GlobalHotKeyModifiers.Control |
                GlobalHotKeyModifiers.Alt |
                GlobalHotKeyModifiers.Shift |
                GlobalHotKeyModifiers.Windows,
            Key = "F12",
        };

        using (var first = new WindowsGlobalHotKey(settings))
        {
            first.Start();
            using var second = new WindowsGlobalHotKey(settings);
            GlobalHotKeyRegistrationException exception = Assert.Throws<GlobalHotKeyRegistrationException>(
                second.Start);
            Assert.Equal(settings.ToDisplayText(), exception.Shortcut);
        }

        using var reused = new WindowsGlobalHotKey(settings);
        reused.Start();
    }
}
