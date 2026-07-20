using SelectionAssistant.Core.Input;
using Xunit;

namespace SelectionAssistant.Core.Tests.Input;

public sealed class OceanEyesTriggerSettingsTests
{
    [Fact]
    public void Defaults_UseKeyboardShortcutAndDisableMouseChord()
    {
        OceanEyesTriggerSettings settings = OceanEyesTriggerSettings.Default;

        Assert.True(settings.KeyboardShortcutEnabled);
        Assert.Equal(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt,
            settings.Modifiers);
        Assert.Equal("Q", settings.Key);
        Assert.Equal("Ctrl+Alt+Q", settings.ToDisplayText());
        Assert.False(settings.MouseChordEnabled);
        settings.Validate();
    }

    [Theory]
    [InlineData(" q ", "Q")]
    [InlineData("space", "Space")]
    [InlineData("f12", "F12")]
    public void Normalize_CanonicalizesSupportedKeys(string input, string expected)
    {
        OceanEyesTriggerSettings settings = OceanEyesTriggerSettings.Default with { Key = input };

        Assert.Equal(expected, settings.Normalize().Key);
    }

    [Fact]
    public void Validate_RejectsBareGlobalKey()
    {
        OceanEyesTriggerSettings settings = OceanEyesTriggerSettings.Default with
        {
            Modifiers = GlobalHotKeyModifiers.None,
        };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Validate_RejectsUnsupportedKey()
    {
        OceanEyesTriggerSettings settings = OceanEyesTriggerSettings.Default with { Key = "Escape" };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Validate_AllowsDisabledKeyboardShortcut()
    {
        OceanEyesTriggerSettings settings = OceanEyesTriggerSettings.Default with
        {
            KeyboardShortcutEnabled = false,
            Modifiers = GlobalHotKeyModifiers.None,
            MouseChordEnabled = true,
        };

        settings.Validate();
    }
}
