using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class ClipboardHistorySettingsStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"clip-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void SaveThenLoad_RoundTripsImageSettings()
    {
        // Regression: captureImagesEnabled / maxImageEntries used to be absent
        // from Load/Save, so the Settings page's image cap + toggle reverted to
        // defaults on every restart (and a lowered cap silently deleted images
        // with no persisted evidence of the value that caused it).
        string path = TempPath();
        try
        {
            var original = ClipboardHistorySettings.Default with
            {
                CaptureImagesEnabled = false,
                MaxImageEntries = 5,
            };

            ClipboardHistorySettingsStore.Save(original, path);
            ClipboardHistorySettings loaded = ClipboardHistorySettingsStore.LoadIfExists(path);

            Assert.False(loaded.CaptureImagesEnabled);
            Assert.Equal(5, loaded.MaxImageEntries);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsUnlimitedImageCap()
    {
        // The user's "0 = unlimited" choice must survive a restart, otherwise
        // the cap silently comes back on and starts deleting images again.
        string path = TempPath();
        try
        {
            ClipboardHistorySettingsStore.Save(
                ClipboardHistorySettings.Default with { MaxImageEntries = 0 }, path);

            ClipboardHistorySettings loaded = ClipboardHistorySettingsStore.LoadIfExists(path);

            Assert.Equal(0, loaded.MaxImageEntries);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LegacyFileWithoutImageKeys_FallsBackToDefaults()
    {
        // Settings files written before the fix have neither key; they must keep
        // loading (image capture on, cap 50) instead of failing.
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                "{ \"schemaVersion\": 1, \"enabled\": true, \"maxEntries\": 1000 }");

            ClipboardHistorySettings loaded = ClipboardHistorySettingsStore.LoadIfExists(path);

            Assert.True(loaded.CaptureImagesEnabled);
            Assert.Equal(50, loaded.MaxImageEntries);
            Assert.Equal(1000, loaded.MaxEntries);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
