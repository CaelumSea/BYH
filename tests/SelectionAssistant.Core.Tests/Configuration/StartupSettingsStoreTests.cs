using System.IO;
using SelectionAssistant.Core.Startup;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class StartupSettingsStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"startup-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFile_ReturnsDefault()
    {
        // Default is LaunchAtStartup = false — BYH does not opt into autostart
        // without an explicit user toggle.
        string path = Path.Combine(Path.GetTempPath(), $"missing-{System.Guid.NewGuid():N}.json");
        StartupSettings loaded = StartupSettingsStore.LoadIfExists(path);
        Assert.False(loaded.LaunchAtStartup);
    }

    [Fact]
    public void SaveEnabled_RoundTrips()
    {
        string path = TempPath();
        try
        {
            StartupSettingsStore.Save(new StartupSettings { LaunchAtStartup = true }, path);
            StartupSettings loaded = StartupSettingsStore.LoadIfExists(path);
            Assert.True(loaded.LaunchAtStartup);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveDisabled_RoundTrips()
    {
        string path = TempPath();
        try
        {
            StartupSettingsStore.Save(new StartupSettings { LaunchAtStartup = false }, path);
            StartupSettings loaded = StartupSettingsStore.LoadIfExists(path);
            Assert.False(loaded.LaunchAtStartup);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MissingLaunchField_DefaultsFalse()
    {
        // A file with a valid schemaVersion but no launchAtStartup field
        // degrades to the default (false) rather than throwing.
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":1}");
            StartupSettings loaded = StartupSettingsStore.LoadIfExists(path);
            Assert.False(loaded.LaunchAtStartup);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NullLaunchField_DefaultsFalse()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":1,\"launchAtStartup\":null}");
            StartupSettings loaded = StartupSettingsStore.LoadIfExists(path);
            Assert.False(loaded.LaunchAtStartup);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WrongSchemaVersion_Throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":99,\"launchAtStartup\":true}");
            Assert.Throws<ProviderConfigurationException>(
                () => StartupSettingsStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "this is not json");
            Assert.Throws<ProviderConfigurationException>(
                () => StartupSettingsStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OversizedFile_Throws()
    {
        string path = TempPath();
        try
        {
            string padding = new string('x', 10_000);
            File.WriteAllText(path, $"{{\"schemaVersion\":1,\"launchAtStartup\":true,\"padding\":\"{padding}\"}}");
            Assert.Throws<ProviderConfigurationException>(
                () => StartupSettingsStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_IsAtomicOverwrite()
    {
        // A second Save cleanly replaces the first; no .tmp left behind.
        string path = TempPath();
        try
        {
            StartupSettingsStore.Save(new StartupSettings { LaunchAtStartup = true }, path);
            StartupSettingsStore.Save(new StartupSettings { LaunchAtStartup = false }, path);
            Assert.False(File.Exists(path + ".tmp"));
            StartupSettings loaded = StartupSettingsStore.LoadIfExists(path);
            Assert.False(loaded.LaunchAtStartup);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }
}
