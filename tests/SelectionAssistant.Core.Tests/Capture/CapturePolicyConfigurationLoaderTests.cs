using SelectionAssistant.Core.Capture;
using SelectionAssistant.Infrastructure.Capture;
using Xunit;

namespace SelectionAssistant.Core.Tests.Capture;

public sealed class CapturePolicyConfigurationLoaderTests
{
    [Fact]
    public void LoadIfExists_ParsesVersionedUserRule()
    {
        string path = WriteTemporaryPolicy(
            """
            {
              "schemaVersion": 1,
              "rules": [
                {
                  "match": { "processName": "cmd.exe" },
                  "detectionEnabled": true,
                  "accessibilityCapture": false,
                  "simulatedCopyMode": "None",
                  "clipboardStabilizationMs": 220,
                  "preserveCapturedClipboard": true,
                  "manualFallback": true
                }
              ]
            }
            """);

        try
        {
            PolicyRule rule = Assert.Single(CapturePolicyConfigurationLoader.LoadIfExists(path));

            Assert.Equal(PolicyMatchKind.ProcessName, rule.MatchKind);
            Assert.Equal("cmd.exe", rule.MatchValue);
            Assert.False(rule.Policy.AccessibilityEnabled);
            Assert.Equal(SimulatedCopyMode.None, rule.Policy.CopyMode);
            Assert.Equal(220, rule.Policy.ClipboardStabilizationMs);
            Assert.True(rule.Policy.PreserveCapturedClipboard);
        }
        finally
        {
            DeleteTemporaryPolicy(path);
        }
    }

    [Fact]
    public void LoadIfExists_RejectsUnknownSchemaVersion()
    {
        string path = WriteTemporaryPolicy("""{"schemaVersion":2,"rules":[]}""");

        try
        {
            Assert.Throws<CapturePolicyConfigurationException>(
                () => CapturePolicyConfigurationLoader.LoadIfExists(path));
        }
        finally
        {
            DeleteTemporaryPolicy(path);
        }
    }

    [Fact]
    public void LoadIfExists_RejectsAmbiguousMatchObject()
    {
        string path = WriteTemporaryPolicy(
            """
            {
              "schemaVersion": 1,
              "rules": [
                { "match": { "processName": "app", "exactPath": "C:\\app.exe" } }
              ]
            }
            """);

        try
        {
            Assert.Throws<CapturePolicyConfigurationException>(
                () => CapturePolicyConfigurationLoader.LoadIfExists(path));
        }
        finally
        {
            DeleteTemporaryPolicy(path);
        }
    }

    [Fact]
    public void LoadIfExists_MissingFileReturnsEmptyRules()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

        Assert.Empty(CapturePolicyConfigurationLoader.LoadIfExists(path));
    }

    private static string WriteTemporaryPolicy(string json)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BYH-policy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "capture-policies.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static void DeleteTemporaryPolicy(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory);
        }
    }
}
