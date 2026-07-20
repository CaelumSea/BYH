using SelectionAssistant.Core.Input;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class SpotlightTriggerStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-spotlight-{Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFile_ReturnsDefault()
    {
        SpotlightTriggerSettings settings = SpotlightTriggerStore.LoadIfExists(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.True(settings.KeyboardShortcutEnabled);
        Assert.Equal("Ctrl+Alt+Space", settings.ToDisplayText());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        string path = TempPath();
        try
        {
            var original = new SpotlightTriggerSettings
            {
                KeyboardShortcutEnabled = true,
                Modifiers = GlobalHotKeyModifiers.Control |
                    GlobalHotKeyModifiers.Shift |
                    GlobalHotKeyModifiers.Windows,
                Key = "f12",
            };

            SpotlightTriggerStore.Save(original, path);
            SpotlightTriggerSettings loaded = SpotlightTriggerStore.LoadIfExists(path);

            Assert.True(loaded.KeyboardShortcutEnabled);
            Assert.Equal(original.Modifiers, loaded.Modifiers);
            Assert.Equal("F12", loaded.Key);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenLoad_Disabled_RoundTrips()
    {
        string path = TempPath();
        try
        {
            var original = new SpotlightTriggerSettings
            {
                KeyboardShortcutEnabled = false,
                Modifiers = GlobalHotKeyModifiers.None,
                Key = "Space",
            };

            SpotlightTriggerStore.Save(original, path);
            SpotlightTriggerSettings loaded = SpotlightTriggerStore.LoadIfExists(path);

            Assert.False(loaded.KeyboardShortcutEnabled);
            Assert.Equal(GlobalHotKeyModifiers.None, loaded.Modifiers);
            Assert.Equal("Space", loaded.Key);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AtomicWrite_TempFileCleanedUpAfterMove()
    {
        string path = TempPath();
        try
        {
            SpotlightTriggerStore.Save(SpotlightTriggerSettings.Default, path);

            Assert.False(File.Exists(path + ".tmp"));
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void Load_InvalidSchemaVersion_Throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ \"schemaVersion\": 99 }");

            Assert.Throws<ProviderConfigurationException>(
                () => SpotlightTriggerStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_NotObject_Throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "[1, 2, 3]");

            Assert.Throws<ProviderConfigurationException>(
                () => SpotlightTriggerStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_CorruptJson_Throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not json }");

            Assert.Throws<ProviderConfigurationException>(
                () => SpotlightTriggerStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_InvalidSettings_Throws()
    {
        string path = TempPath();
        try
        {
            var invalid = new SpotlightTriggerSettings
            {
                KeyboardShortcutEnabled = true,
                Modifiers = GlobalHotKeyModifiers.None,
                Key = "Space",
            };

            Assert.Throws<ArgumentException>(
                () => SpotlightTriggerStore.Save(invalid, path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_PartialFile_UsesDefaultsForMissing()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ \"schemaVersion\": 1 }");

            SpotlightTriggerSettings loaded = SpotlightTriggerStore.LoadIfExists(path);

            Assert.Equal("Ctrl+Alt+Space", loaded.ToDisplayText());
            Assert.True(loaded.KeyboardShortcutEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_TooLargeFile_Throws()
    {
        string path = TempPath();
        try
        {
            string padded = "{ \"schemaVersion\": 1, \"padding\": \""
                + new string('x', 9000)
                + "\" }";
            File.WriteAllText(path, padded);

            Assert.Throws<ProviderConfigurationException>(
                () => SpotlightTriggerStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
