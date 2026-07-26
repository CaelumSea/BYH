using SelectionAssistant.Core.Input;
using Xunit;

namespace SelectionAssistant.Core.Tests.Input;

public sealed class SpotlightTriggerSettingsTests
{
    [Fact]
    public void Default_HasCtrlAltSpace()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default;

        Assert.True(settings.KeyboardShortcutEnabled);
        Assert.Equal(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt,
            settings.Modifiers);
        Assert.Equal("Space", settings.Key);
        settings.Validate();
    }

    [Fact]
    public void Normalize_LowercaseSpace_BecomesSpace()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default with { Key = "space" };

        Assert.Equal("Space", settings.Normalize().Key);
    }

    [Fact]
    public void Normalize_UppercaseLetter_StaysUppercase()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default with { Key = "q" };

        Assert.Equal("Q", settings.Normalize().Key);
    }

    [Fact]
    public void Validate_NoModifiers_WhenEnabled_Throws()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default with
        {
            Modifiers = GlobalHotKeyModifiers.None,
        };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Validate_UnknownModifier_Throws()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default with
        {
            Modifiers = (GlobalHotKeyModifiers)255,
        };

        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }

    [Fact]
    public void Validate_InvalidKey_Throws()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default with { Key = "XYZ" };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Validate_DisabledWithNoModifiers_Ok()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default with
        {
            KeyboardShortcutEnabled = false,
            Modifiers = GlobalHotKeyModifiers.None,
        };

        settings.Validate();
    }

    [Fact]
    public void ToDisplayText_FormatsCorrectly()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default;

        Assert.Equal("Ctrl+Alt+Space", settings.ToDisplayText());
    }

    [Fact]
    public void ToDisplayText_IncludesShiftAndWin()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default with
        {
            Modifiers = GlobalHotKeyModifiers.Control |
                GlobalHotKeyModifiers.Alt |
                GlobalHotKeyModifiers.Shift |
                GlobalHotKeyModifiers.Windows,
            Key = "A",
        };

        Assert.Equal("Ctrl+Alt+Shift+Win+A", settings.ToDisplayText());
    }

    // ── R54 window size ──

    [Fact]
    public void Default_HasLegacyXamlWindowSize()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default;

        Assert.Equal(560, settings.WindowWidth);
        Assert.Equal(480, settings.WindowHeight);
        settings.Validate();
    }

    [Fact]
    public void Normalize_ClampsWindowWidthToFloor()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default with
        {
            WindowWidth = SpotlightTriggerSettings.MinWindowWidth - 50,
        };

        Assert.Equal(SpotlightTriggerSettings.MinWindowWidth, settings.Normalize().WindowWidth);
    }

    [Fact]
    public void Normalize_ClampsWindowHeightToCeiling()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default with
        {
            WindowHeight = SpotlightTriggerSettings.MaxWindowHeight + 500,
        };

        Assert.Equal(SpotlightTriggerSettings.MaxWindowHeight, settings.Normalize().WindowHeight);
    }

    [Fact]
    public void Validate_WidthBelowRange_Throws()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerSettings.Default with
        {
            WindowWidth = 10,
        };

        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }
}
