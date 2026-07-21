using SelectionAssistant.Core.Capture;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class OceanEyesCaptureStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"oe-capture-{Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFile_ReturnsSafeDefaults()
    {
        OceanEyesCaptureSettings settings = OceanEyesCaptureStore.LoadIfExists(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.True(settings.AutoSaveEnabled);
        Assert.True(settings.CopyToClipboardEnabled);
        Assert.True(settings.UiaAssistEnabled);
        Assert.Contains("Ocean Eyes", settings.SavePath);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        string path = TempPath();
        try
        {
            var original = new OceanEyesCaptureSettings
            {
                SavePath = Path.Combine(Path.GetTempPath(), "oe-test"),
                AutoSaveEnabled = false,
                CopyToClipboardEnabled = true,
                UiaAssistEnabled = false,
            };

            OceanEyesCaptureStore.Save(original, path);
            OceanEyesCaptureSettings loaded = OceanEyesCaptureStore.LoadIfExists(path);

            Assert.Equal(original.SavePath, loaded.SavePath);
            Assert.False(loaded.AutoSaveEnabled);
            Assert.True(loaded.CopyToClipboardEnabled);
            Assert.False(loaded.UiaAssistEnabled);
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
            File.WriteAllText(path, "{ \"schemaVersion\": 1, \"autoSaveEnabled\": false }");

            OceanEyesCaptureSettings loaded = OceanEyesCaptureStore.LoadIfExists(path);

            Assert.False(loaded.AutoSaveEnabled);
            Assert.True(loaded.CopyToClipboardEnabled);
            Assert.True(loaded.UiaAssistEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 99 }")]
    [InlineData("{ \"schemaVersion\": 1, \"savePath\": 42 }")]
    public void InvalidConfiguration_Throws(string json)
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, json);

            Assert.Throws<ProviderConfigurationException>(
                () => OceanEyesCaptureStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Validate_RejectsPathWithControlCharacters()
    {
        // Path.GetInvalidPathChars is control chars (< 0x20); include one to
        // prove the validator catches them.
        var settings = new OceanEyesCaptureSettings
        {
            SavePath = "C:\\bad\u0003path",
        };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Normalize_ExpandsEnvironmentVariablesAndStripsTrailingSeparator()
    {
        string path = @"%TEMP%\oe-norm\";
        var settings = new OceanEyesCaptureSettings { SavePath = path };

        OceanEyesCaptureSettings normalized = settings.Normalize();

        Assert.False(normalized.SavePath.EndsWith('\\'));
        Assert.DoesNotContain('%', normalized.SavePath);
        Assert.True(Directory.Exists(Path.GetTempPath())); // %TEMP% resolved to a real dir
    }

    // ── R51 beautify fields ──────────────────────────────────────────

    [Fact]
    public void SaveThenLoad_RoundTripsBeautifyFields()
    {
        string path = TempPath();
        try
        {
            var original = new OceanEyesCaptureSettings
            {
                SavePath = Path.Combine(Path.GetTempPath(), "oe-beautify"),
                BeautifyPadding = 64,
                BeautifyCornerRadius = 12,
                BeautifyBackgroundHex = "#ABCDEF",
                BeautifyShadowOffsetX = 8,
                BeautifyShadowOffsetY = 8,
                BeautifyShadowBlurRadius = 24,
                BeautifyShadowOpacity = 0.75,
            };

            OceanEyesCaptureStore.Save(original, path);
            OceanEyesCaptureSettings loaded = OceanEyesCaptureStore.LoadIfExists(path);

            Assert.Equal(64, loaded.BeautifyPadding);
            Assert.Equal(12, loaded.BeautifyCornerRadius);
            Assert.Equal("#ABCDEF", loaded.BeautifyBackgroundHex);
            Assert.Equal(8, loaded.BeautifyShadowOffsetX);
            Assert.Equal(8, loaded.BeautifyShadowOffsetY);
            Assert.Equal(24, loaded.BeautifyShadowBlurRadius);
            Assert.Equal(0.75, loaded.BeautifyShadowOpacity);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OldV1File_MissingBeautifyFields_UsesDefaults()
    {
        // Simulates an ocean-eyes-capture.json written before R51 shipped:
        // only the original four fields. New beautify fields must default.
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                "{ \"schemaVersion\": 1, " +
                "\"savePath\": \"" + Path.Combine(Path.GetTempPath(), "oe-old").Replace("\\", "\\\\") + "\", " +
                "\"autoSaveEnabled\": true, \"copyToClipboardEnabled\": true, \"uiaAssistEnabled\": true }");

            OceanEyesCaptureSettings loaded = OceanEyesCaptureStore.LoadIfExists(path);

            Assert.Equal(32, loaded.BeautifyPadding);
            Assert.Equal(8, loaded.BeautifyCornerRadius);
            Assert.Equal("#FFFCF7EA", loaded.BeautifyBackgroundHex);
            Assert.Equal(4, loaded.BeautifyShadowOffsetX);
            Assert.Equal(4, loaded.BeautifyShadowOffsetY);
            Assert.Equal(16, loaded.BeautifyShadowBlurRadius);
            Assert.Equal(0.5, loaded.BeautifyShadowOpacity);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("beautifyPadding", 999)]
    [InlineData("beautifyCornerRadius", 999)]
    [InlineData("beautifyShadowOffsetX", 999)]
    [InlineData("beautifyShadowOffsetY", 999)]
    [InlineData("beautifyShadowBlurRadius", 999)]
    public void OutOfRangeBeautifyField_LoadThrows(string field, int value)
    {
        // Validate() runs inside LoadIfExists after the JSON is parsed.
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                "{ \"schemaVersion\": 1, " +
                "\"savePath\": \"" + Path.Combine(Path.GetTempPath(), "oe-bad").Replace("\\", "\\\\") + "\", " +
                "\"" + field + "\": " + value + " }");

            Assert.Throws<ProviderConfigurationException>(() =>
                OceanEyesCaptureStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ShadowOpacityOutOfRange_LoadThrows()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                "{ \"schemaVersion\": 1, " +
                "\"savePath\": \"" + Path.Combine(Path.GetTempPath(), "oe-bad2").Replace("\\", "\\\\") + "\", " +
                "\"beautifyShadowOpacity\": 2.0 }");

            Assert.Throws<ProviderConfigurationException>(() =>
                OceanEyesCaptureStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
