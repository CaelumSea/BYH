using SelectionAssistant.Core.Capture;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class VisionCaptureStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-vision-{Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var settings = VisionCaptureStore.LoadIfExists(
            Path.Combine(Path.GetTempPath(), "definitely-does-not-exist.json"));

        // R24 final design: Qwen3.5-4B with thinking disabled was confirmed on
        // the user's machine on 2026-07-18.
        Assert.True(settings.Enabled);
        Assert.Equal("Qwen/Qwen3.5-4B", settings.Model);
        Assert.Equal("Free OCR.", settings.OcrPrompt);
        Assert.True(settings.DisableThinking);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        string path = TempPath();
        try
        {
            var original = new VisionCaptureSettings
            {
                Enabled = false,
                ProviderId = "siliconflow",
                Model = "PaddlePaddle/PaddleOCR-VL-1.5",
                OcrPrompt = "document parsing.",
                UiaPrefillEnabled = true,
                DisableThinking = false,
            };

            VisionCaptureStore.Save(original, path);
            var loaded = VisionCaptureStore.LoadIfExists(path);

            Assert.False(loaded.Enabled);
            Assert.Equal("siliconflow", loaded.ProviderId);
            Assert.Equal("PaddlePaddle/PaddleOCR-VL-1.5", loaded.Model);
            Assert.Equal("document parsing.", loaded.OcrPrompt);
            Assert.True(loaded.UiaPrefillEnabled);
            Assert.False(loaded.DisableThinking);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_WithPartialFile_FallsBackPerField()
    {
        // A file that omits fields should fall back to per-field defaults
        // (forward-compatible: future fields added to the schema are safe).
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                "{ \"schemaVersion\": 1, \"enabled\": false }");

            var loaded = VisionCaptureStore.LoadIfExists(path);

            Assert.False(loaded.Enabled);
            Assert.Equal("siliconflow", loaded.ProviderId);
            Assert.Equal("Qwen/Qwen3.5-4B", loaded.Model);
            Assert.Equal("Free OCR.", loaded.OcrPrompt);
            Assert.False(loaded.UiaPrefillEnabled);
            Assert.True(loaded.DisableThinking);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_WithWrongSchemaVersion_Throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ \"schemaVersion\": 99 }");

            Assert.Throws<ProviderConfigurationException>(
                () => VisionCaptureStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_IsAtomic_TempFileCleanedUp()
    {
        string path = TempPath();
        try
        {
            VisionCaptureStore.Save(VisionCaptureSettings.Default, path);

            Assert.True(File.Exists(path));
            // The temp file must not linger after a successful save.
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
