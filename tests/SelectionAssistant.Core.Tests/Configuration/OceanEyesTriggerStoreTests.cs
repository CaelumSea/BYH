using SelectionAssistant.Core.Input;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class OceanEyesTriggerStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-trigger-{Guid.NewGuid():N}.json");

    public OceanEyesTriggerStoreTests()
    {
        // The store reads a static legacy-migration path; clear it before each
        // test so they don't bleed into each other.
        OceanEyesTriggerStore.SetLegacyMigrationPath(null);
    }

    [Fact]
    public void MissingFile_ReturnsSafeDefaults()
    {
        OceanEyesTriggerSettings settings = OceanEyesTriggerStore.LoadIfExists(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.True(settings.KeyboardShortcutEnabled);
        Assert.Equal("Ctrl+Alt+Q", settings.ToDisplayText());
        Assert.False(settings.MouseChordEnabled);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        string path = TempPath();
        try
        {
            var original = new OceanEyesTriggerSettings
            {
                KeyboardShortcutEnabled = true,
                Modifiers = GlobalHotKeyModifiers.Control |
                    GlobalHotKeyModifiers.Shift |
                    GlobalHotKeyModifiers.Windows,
                Key = "f12",
                MouseChordEnabled = true,
            };

            OceanEyesTriggerStore.Save(original, path);
            OceanEyesTriggerSettings loaded = OceanEyesTriggerStore.LoadIfExists(path);

            Assert.True(loaded.KeyboardShortcutEnabled);
            Assert.Equal(original.Modifiers, loaded.Modifiers);
            Assert.Equal("F12", loaded.Key);
            Assert.True(loaded.MouseChordEnabled);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PartialFile_UsesPerFieldDefaults()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ \"schemaVersion\": 1, \"mouseChordEnabled\": true }");

            OceanEyesTriggerSettings loaded = OceanEyesTriggerStore.LoadIfExists(path);

            Assert.Equal("Ctrl+Alt+Q", loaded.ToDisplayText());
            Assert.True(loaded.MouseChordEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 99 }")]
    [InlineData("{ \"schemaVersion\": 1, \"modifiers\": [\"Hyper\"] }")]
    [InlineData("{ \"schemaVersion\": 1, \"key\": \"Escape\" }")]
    public void InvalidConfiguration_Throws(string json)
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, json);

            Assert.Throws<ProviderConfigurationException>(
                () => OceanEyesTriggerStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_ParentPathIsFile_ThrowsProviderConfigurationException()
    {
        string blockerFile = Path.Combine(Path.GetTempPath(), $"byh-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blockerFile, "blocker");
        try
        {
            string path = Path.Combine(blockerFile, "ocean-eyes.json");
            var settings = OceanEyesTriggerSettings.Default;
            Assert.Throws<ProviderConfigurationException>(
                () => OceanEyesTriggerStore.Save(settings, path));
        }
        finally
        {
            File.Delete(blockerFile);
        }
    }

    /// <summary>
    /// R40 migration: if ocean-eyes.json is absent but the legacy quick-tools.json
    /// is present, the store reads the legacy file so existing users keep their
    /// bindings. The legacy file is NOT deleted (downgrade recovery).
    /// </summary>
    [Fact]
    public void MissingNewFile_MigratesFromLegacyQuickToolsFile()
    {
        string legacyPath = Path.Combine(Path.GetTempPath(), $"legacy-qt-{Guid.NewGuid():N}.json");
        string newPath = Path.Combine(Path.GetTempPath(), $"oe-{Guid.NewGuid():N}.json");
        try
        {
            // Write a legacy file with non-default bindings.
            var legacy = new OceanEyesTriggerSettings
            {
                KeyboardShortcutEnabled = true,
                Modifiers = GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Shift,
                Key = "F11",
                MouseChordEnabled = true,
            };
            // Use the new store to write the legacy shape (same schema, different
            // file name) — emulates a pre-R40 install.
            OceanEyesTriggerStore.Save(legacy, legacyPath);
            Assert.False(File.Exists(newPath));

            OceanEyesTriggerStore.SetLegacyMigrationPath(legacyPath);
            OceanEyesTriggerSettings loaded = OceanEyesTriggerStore.LoadIfExists(newPath);

            Assert.Equal("Ctrl+Shift+F11", loaded.ToDisplayText());
            Assert.True(loaded.MouseChordEnabled);
            // Legacy file is preserved (no destructive cleanup on read).
            Assert.True(File.Exists(legacyPath));
            // New file is NOT created until an explicit Save.
            Assert.False(File.Exists(newPath));
        }
        finally
        {
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
            if (File.Exists(newPath)) File.Delete(newPath);
            OceanEyesTriggerStore.SetLegacyMigrationPath(null);
        }
    }
}
