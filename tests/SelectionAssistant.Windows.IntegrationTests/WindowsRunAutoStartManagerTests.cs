using Microsoft.Win32;
using SelectionAssistant.Platform.Windows.Startup;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests;

/// <summary>
/// Tests <see cref="WindowsRunAutoStartManager"/> against a throwaway subkey
/// under HKCU (NOT the real Run key) so the user's actual autostart is never
/// touched. The internal ctor lets us point the manager at any HKCU subpath.
/// </summary>
public sealed class WindowsRunAutoStartManagerTests
{
    /// <summary>Unique HKCU subkey per test run; cleaned up in Dispose.</summary>
    private readonly string _testKeyPath =
        $@"SOFTWARE\BYH-Test-{System.Guid.NewGuid():N}";

    [Fact]
    public void IsEnabled_FalseWhenKeyMissing()
    {
        // Pointing at a subkey that doesn't exist yet → no value → false.
        var manager = MakeManager(exePath: @"C:\fake\BYH.exe");
        Assert.False(manager.IsEnabled());
    }

    [Fact]
    public void TryEnable_ThenIsEnabled_True()
    {
        var manager = MakeManager(exePath: @"C:\fake\BYH.exe");
        try
        {
            Assert.True(manager.TryEnable());
            Assert.True(manager.IsEnabled());
        }
        finally
        {
            CleanupTestKey();
        }
    }

    [Fact]
    public void TryDisable_RemovesValue()
    {
        var manager = MakeManager(exePath: @"C:\fake\BYH.exe");
        try
        {
            Assert.True(manager.TryEnable());
            Assert.True(manager.IsEnabled());
            Assert.True(manager.TryDisable());
            Assert.False(manager.IsEnabled());
        }
        finally
        {
            CleanupTestKey();
        }
    }

    [Fact]
    public void TryDisable_Idempotent_WhenValueAbsent()
    {
        // Disabling when never enabled must be a no-op success (幂等).
        var manager = MakeManager(exePath: @"C:\fake\BYH.exe");
        try
        {
            Assert.True(manager.TryDisable());
            Assert.False(manager.IsEnabled());
        }
        finally
        {
            CleanupTestKey();
        }
    }

    [Fact]
    public void IsEnabled_FalseWhenExePathMismatch()
    {
        // Value exists but points at a different exe → treated as stale / disabled.
        // This is the "exe moved / renamed" guard.
        var manager = MakeManager(exePath: @"C:\fake\BYH.exe");
        try
        {
            Assert.True(manager.TryEnable());  // writes C:\fake\BYH.exe
            // Now ask as if the current exe is elsewhere.
            var mismatched = MakeManager(exePath: @"C:\other\BYH.exe");
            Assert.False(mismatched.IsEnabled());
        }
        finally
        {
            CleanupTestKey();
        }
    }

    [Fact]
    public void TryEnable_OverwritesStaleValue()
    {
        // Re-enabling with a new exe path rewrites the value (re-points autostart
        // at the current location after a move).
        var first = MakeManager(exePath: @"C:\old\BYH.exe");
        try
        {
            Assert.True(first.TryEnable());
            var moved = MakeManager(exePath: @"C:\new\BYH.exe");
            Assert.True(moved.TryEnable());
            Assert.True(moved.IsEnabled());
        }
        finally
        {
            CleanupTestKey();
        }
    }

    private WindowsRunAutoStartManager MakeManager(string exePath) =>
        new(_testKeyPath, exePath);

    private void CleanupTestKey()
    {
        // Best-effort: delete the whole throwaway subkey. Never throws into the
        // test (a leftover subkey under SOFTWARE\BYH-Test-* is harmless debris).
        try
        {
            using RegistryKey? software = Registry.CurrentUser.OpenSubKey("SOFTWARE", writable: true);
            // The test subkey sits directly under SOFTWARE (e.g. BYH-Test-<guid>).
            string leaf = _testKeyPath.Substring(_testKeyPath.LastIndexOf('\\') + 1);
            software?.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
        }
        catch
        {
            // Debris cleanup is best-effort.
        }
    }
}
