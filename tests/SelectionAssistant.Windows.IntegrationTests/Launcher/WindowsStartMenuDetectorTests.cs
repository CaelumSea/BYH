using System.Text;
using SelectionAssistant.Platform.Windows.Launcher;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Launcher;

public sealed class WindowsStartMenuDetectorTests
{
    private readonly WindowsStartMenuDetector _detector = new();

    // ── TryExtractTargetFromShortcutBytes (pure function) ──

    [Fact]
    public void ExtractTarget_FullPathAscii_ReturnsPath()
    {
        // Simulate a .lnk whose ASCII stream contains a full exe path.
        byte[] bytes = BuildFakeLnk("Garbage\x00header" + @"C:\Program Files\App\app.exe" + "\x00trailing");
        string? target = WindowsStartMenuDetector.TryExtractTargetFromShortcutBytes(bytes);
        Assert.Equal(@"C:\Program Files\App\app.exe", target);
    }

    [Fact]
    public void ExtractTarget_FullPathUtf16_ReturnsPath()
    {
        // Same idea but the path is stored as UTF-16LE (2 bytes per char),
        // which is what many installers actually write.
        string payload = "header\x00" + @"D:\Tools\Foo\foo.exe" + "\x00tail";
        byte[] bytes = BuildFakeLnk(Encoding.Unicode.GetBytes(payload));
        string? target = WindowsStartMenuDetector.TryExtractTargetFromShortcutBytes(bytes);
        Assert.Equal(@"D:\Tools\Foo\foo.exe", target);
    }

    [Fact]
    public void ExtractTarget_System32Relative_PrependsWindowsDir()
    {
        // System apps (mstsc, charmap, psr) store only the path after
        // \system32\ — the detector should reconstruct the full path.
        byte[] bytes = BuildFakeLnk("hdr\x00" + @"\system32\mstsc.exe" + "\x00tail");
        string? target = WindowsStartMenuDetector.TryExtractTargetFromShortcutBytes(bytes);
        Assert.EndsWith(@"\system32\mstsc.exe", target);
        Assert.Contains("system32", target);
    }

    [Fact]
    public void ExtractTarget_MscConsole_ReturnsPath()
    {
        // MMC consoles (.msc) should be detected just like .exe — they launch
        // via the shell handler.
        byte[] bytes = BuildFakeLnk("h" + @"C:\Windows\system32\compmgmt.msc" + "\x00");
        string? target = WindowsStartMenuDetector.TryExtractTargetFromShortcutBytes(bytes);
        Assert.EndsWith(@"compmgmt.msc", target);
    }

    [Fact]
    public void ExtractTarget_StripsIconResourceIndexSuffix()
    {
        // Some shortcuts append ",0" or ",-103" to point at an icon resource;
        // the target path itself must not include that suffix.
        byte[] bytes = BuildFakeLnk(@"C:\App\app.exe,0");
        string? target = WindowsStartMenuDetector.TryExtractTargetFromShortcutBytes(bytes);
        Assert.Equal(@"C:\App\app.exe", target);
    }

    [Fact]
    public void ExtractTarget_NoExecutable_ReturnsNull()
    {
        // Help/license .lnk files often reference a .txt/.pdf/.html, not an
        // exe — should return null so the caller filters them out.
        byte[] bytes = BuildFakeLnk(@"C:\App\readme.txt");
        string? target = WindowsStartMenuDetector.TryExtractTargetFromShortcutBytes(bytes);
        Assert.Null(target);
    }

    [Fact]
    public void ExtractTarget_TooShort_ReturnsNull()
    {
        // A file smaller than the 76-byte ShellLinkHeader is not a valid .lnk.
        byte[] bytes = new byte[10];
        string? target = WindowsStartMenuDetector.TryExtractTargetFromShortcutBytes(bytes);
        Assert.Null(target);
    }

    // ── IsNonLaunchableName (filter helper) ──

    [Theory]
    [InlineData("7-Zip Help")]        // contains "Help"
    [InlineData("Uninstall App")]     // contains "Uninstall"
    [InlineData("Pandoc User's Guide")] // contains "Guide"
    [InlineData("License")]           // exact match
    [InlineData("Readme")]            // contains "Readme"
    [InlineData("Tutorial")]          // contains "Tutorial"
    [InlineData("卸载")]               // Chinese uninstall (exact)
    [InlineData("卸载微信")]           // Chinese uninstall WeChat
    [InlineData("卸载火绒")]           // Chinese uninstall Huorong
    public void IsNonLaunchableName_FiltersDocsAndUninstallers(string name)
    {
        Assert.True(WindowsStartMenuDetector.IsNonLaunchableName(name));
    }

    [Theory]
    [InlineData("7-Zip File Manager")]
    [InlineData("Visual Studio Code")]
    [InlineData("Clash Verge")]
    [InlineData("Remote Desktop Connection")]
    [InlineData("Calculator")]
    public void IsNonLaunchableName_KeepsRealApps(string name)
    {
        Assert.False(WindowsStartMenuDetector.IsNonLaunchableName(name));
    }

    // ── DetectInstalledApps (smoke test against the real machine) ──

    [Fact]
    public void DetectInstalledApps_ReturnsNonEmptyAndDeduplicated()
    {
        // Runs against the real Start Menu on the test machine. We don't assert
        // any specific app (the test box may differ from a user's), only the
        // invariants: non-empty, no duplicate targets, no help/uninstall entries.
        IReadOnlyList<global::SelectionAssistant.Platform.Abstractions.DetectedApp> apps =
            _detector.DetectInstalledApps();

        Assert.NotEmpty(apps);

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            // No duplicate targets.
            Assert.True(targets.Add(app.ExecutablePath),
                $"Duplicate target: {app.ExecutablePath}");
            // Every target ends in .exe or .msc.
            Assert.True(
                app.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                app.ExecutablePath.EndsWith(".msc", StringComparison.OrdinalIgnoreCase),
                $"Unexpected extension: {app.ExecutablePath}");
            // No help/uninstall entries leaked through.
            Assert.False(WindowsStartMenuDetector.IsNonLaunchableName(
                System.IO.Path.GetFileNameWithoutExtension(app.ExecutablePath)),
                $"Non-launchable leaked: {app.Name}");
        }
    }

    /// <summary>
    /// Builds a byte array that mimics enough of a .lnk file to exercise the
    /// extractor: a 76-byte zeroed header (so the "smaller than header" check
    /// passes) followed by the given payload bytes. Real .lnk parsing is far
    /// more complex, but the extractor only string-scans the whole file, so
    /// this is sufficient.
    /// </summary>
    private static byte[] BuildFakeLnk(string payload)
    {
        byte[] header = new byte[0x4C]; // 76-byte ShellLinkHeader, all zeros
        byte[] payloadBytes = Encoding.Latin1.GetBytes(payload);
        byte[] result = new byte[header.Length + payloadBytes.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(payloadBytes, 0, result, header.Length, payloadBytes.Length);
        return result;
    }

    /// <summary>Overload that takes a pre-built UTF-16 payload.</summary>
    private static byte[] BuildFakeLnk(byte[] payload)
    {
        byte[] header = new byte[0x4C];
        byte[] result = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(payload, 0, result, header.Length, payload.Length);
        return result;
    }
}
