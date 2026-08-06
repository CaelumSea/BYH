using SelectionAssistant.Core.Input;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Input;

public sealed class ToolbarShortcutSettingsTests
{
    [Fact]
    public void Default_HasRcsBindings()
    {
        ToolbarShortcutSettings d = ToolbarShortcutSettings.Default;
        Assert.Equal("R", d.PromptKey);
        Assert.Equal("C", d.CopyKey);
        Assert.Equal("S", d.SpeakKey);
    }

    [Fact]
    public void Default_NormalizeAndValidate_Passes()
    {
        // The default must round-trip cleanly through Normalize + Validate.
        ToolbarShortcutSettings.Default.Normalize().Validate();
    }

    [Fact]
    public void Normalize_UpperCasesAndClearsBlank()
    {
        var raw = new ToolbarShortcutSettings
        {
            PromptKey = "r",
            CopyKey = "  ",
            SpeakKey = "s",
        };
        ToolbarShortcutSettings n = raw.Normalize();
        Assert.Equal("R", n.PromptKey);
        Assert.Null(n.CopyKey);        // blank → disabled
        Assert.Equal("S", n.SpeakKey);
    }

    [Fact]
    public void Validate_RejectsDuplicateBetweenSpeakAndCopy()
    {
        // The duplicate-detection must cover all three keys, not just the
        // original Prompt/Copy pair. Binding S to both Copy and Speak is
        // ambiguous and must be rejected.
        var settings = new ToolbarShortcutSettings
        {
            PromptKey = "R",
            CopyKey = "S",
            SpeakKey = "S",
        }.Normalize();
        Assert.Throws<ArgumentException>(() => settings.Validate());
    }

    [Fact]
    public void Validate_AcceptsAllThreeDistinct()
    {
        var settings = new ToolbarShortcutSettings
        {
            PromptKey = "R",
            CopyKey = "C",
            SpeakKey = "S",
        }.Normalize();
        settings.Validate(); // does not throw
    }

    [Fact]
    public void Validate_AcceptsDisabledSpeak()
    {
        // User can clear the Speak key to disable it.
        var settings = new ToolbarShortcutSettings
        {
            PromptKey = "R",
            CopyKey = "C",
            SpeakKey = null,
        }.Normalize();
        settings.Validate();
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-toolbar-shortcuts-{Guid.NewGuid():N}.json");

    [Fact]
    public void Store_SaveThenLoad_RoundTripsSpeakKey()
    {
        string path = TempPath();
        try
        {
            var original = new ToolbarShortcutSettings
            {
                PromptKey = "T",
                CopyKey = "Y",
                SpeakKey = "U",
            };
            ToolbarShortcutsStore.Save(original, path);
            ToolbarShortcutSettings loaded = ToolbarShortcutsStore.LoadIfExists(path);
            Assert.Equal("T", loaded.PromptKey);
            Assert.Equal("Y", loaded.CopyKey);
            Assert.Equal("U", loaded.SpeakKey);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Store_LegacyFileWithoutSpeakKey_DefaultsToS()
    {
        // Files written before Speak existed lack the speakKey field. They must
        // load with the default "S", not crash or null out.
        string path = TempPath();
        try
        {
            File.WriteAllText(path, """
                {"schemaVersion":1,"promptKey":"R","copyKey":"C"}
                """);
            ToolbarShortcutSettings loaded = ToolbarShortcutsStore.LoadIfExists(path);
            Assert.Equal("R", loaded.PromptKey);
            Assert.Equal("C", loaded.CopyKey);
            Assert.Equal("S", loaded.SpeakKey); // default, not null
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
