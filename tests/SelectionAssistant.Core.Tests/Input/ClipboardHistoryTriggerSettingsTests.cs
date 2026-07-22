using SelectionAssistant.Core.Input;
using Xunit;

namespace SelectionAssistant.Core.Tests.Input;

public sealed class ClipboardHistoryTriggerSettingsTests
{
    [Fact]
    public void Default_HasCtrlAltV()
    {
        ClipboardHistoryTriggerSettings settings = ClipboardHistoryTriggerSettings.Default;

        Assert.True(settings.KeyboardShortcutEnabled);
        Assert.Equal(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt,
            settings.Modifiers);
        Assert.Equal("V", settings.Key);
        settings.Validate();
    }

    [Fact]
    public void Normalize_LowercaseV_BecomesUppercase()
    {
        ClipboardHistoryTriggerSettings settings = ClipboardHistoryTriggerSettings.Default with { Key = "v" };
        Assert.Equal("V", settings.Normalize().Key);
    }

    [Fact]
    public void Validate_NoModifiers_WhenEnabled_Throws()
    {
        ClipboardHistoryTriggerSettings settings = ClipboardHistoryTriggerSettings.Default with
        {
            Modifiers = GlobalHotKeyModifiers.None,
        };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Validate_UnknownModifier_Throws()
    {
        ClipboardHistoryTriggerSettings settings = ClipboardHistoryTriggerSettings.Default with
        {
            Modifiers = (GlobalHotKeyModifiers)255,
        };

        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }

    [Fact]
    public void Validate_DisabledWithNoModifiers_Ok()
    {
        ClipboardHistoryTriggerSettings settings = ClipboardHistoryTriggerSettings.Default with
        {
            KeyboardShortcutEnabled = false,
            Modifiers = GlobalHotKeyModifiers.None,
        };

        settings.Validate();
    }

    [Fact]
    public void ToDisplayText_FormatsCorrectly()
    {
        ClipboardHistoryTriggerSettings settings = ClipboardHistoryTriggerSettings.Default;
        Assert.Equal("Ctrl+Alt+V", settings.ToDisplayText());
    }
}
