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
        // Path.GetInvalidPathChars() is platform-specific: Windows returns a
        // large set of control chars, macOS/Linux returns essentially only NUL.
        // Pick the first one the current OS actually forbids. NUL (\0) would
        // corrupt the string, so skip the test if only NUL is available.
        char[] invalid = Path.GetInvalidPathChars();
        char bad = invalid.FirstOrDefault(c => c != '\0');
        if (bad == '\0')
        {
            // macOS/Linux: no embeddable invalid char to test against.
            return;
        }

        var settings = new OceanEyesCaptureSettings
        {
            SavePath = "bad" + bad + "path",
        };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Normalize_ExpandsEnvironmentVariablesAndStripsTrailingSeparator()
    {
        // Use a variable the test sets itself — cross-platform. Relies on
        // Environment.ExpandEnvironmentVariables using %VAR% syntax on all OSes
        // (it does not expand $VAR on Unix).
        string varName = "BYH_TEST_NORM_VAR";
        string tempDir = Path.Combine(Path.GetTempPath(), "oe-norm");
        Environment.SetEnvironmentVariable(varName, tempDir);
        try
        {
            char sep = Path.DirectorySeparatorChar;
            string path = $"%{varName}%{sep}";
            var settings = new OceanEyesCaptureSettings { SavePath = path };

            OceanEyesCaptureSettings normalized = settings.Normalize();

            Assert.False(normalized.SavePath.EndsWith(sep));
            Assert.DoesNotContain('%', normalized.SavePath);
            Assert.Equal(tempDir, normalized.SavePath); // expanded to the value we set
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }
}
