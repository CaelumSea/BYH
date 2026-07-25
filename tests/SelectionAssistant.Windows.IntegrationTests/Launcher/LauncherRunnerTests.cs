using SelectionAssistant.Platform.Windows.Launcher;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Launcher;

/// <summary>
/// Tests the routing logic in <see cref="LauncherRunner"/> — specifically
/// <see cref="LauncherRunner.DecideFallback"/>, which decides whether a
/// failed primary launch should retry via <c>ShellExecuteEx</c> and with
/// which verb. The actual <c>Process.Start</c> / <c>ShellExecuteEx</c> calls
/// aren't unit-tested (they launch real apps and trigger UAC); the decision
/// function is pure and fully covered here.
/// </summary>
public sealed class LauncherRunnerTests
{
    // ── .lnk routing ──

    [Theory]
    [InlineData("C:\\Users\\test\\Desktop\\Codex.lnk")]
    [InlineData("D:\\shortcuts\\app.LNK")]             // case-insensitive
    [InlineData("C:\\ProgramData\\Start Menu\\Programs\\App\\app.lnk")]
    public void DecideFallback_LnkTarget_ReturnsOpenVerb(string target)
    {
        // A .lnk target always needs the shell to resolve, regardless of the
        // specific error from Process.Start (it's almost always
        // ERROR_FILE_NOT_FOUND but we don't gate on the code — the extension
        // alone tells us to retry through the shell).
        string? verb = LauncherRunner.DecideFallback(target, "[Win32 2] 启动失败：系统找不到指定的文件。");
        Assert.Equal("open", verb);
    }

    [Fact]
    public void DecideFallback_LnkTarget_WithOtherError_StillRetriesAsOpen()
    {
        // Even if the .lnk failed for a non-2 reason (e.g. the shell link
        // target itself is missing), we still route through ShellExecuteEx
        // because only the shell can resolve .lnk paths — Process.Start has
        // no path that works for shortcuts.
        string? verb = LauncherRunner.DecideFallback("C:\\app.lnk", "启动失败：some non-Win32 error");
        Assert.Equal("open", verb);
    }

    // ── Elevation routing (Win32 740) ──

    [Theory]
    [InlineData("C:\\Program Files\\App\\app.exe")]
    [InlineData("D:\\Tools\\DiskGenius\\DiskGenius.exe")]
    [InlineData("C:\\Windows\\System32\\regedit.exe")]
    public void DecideFallback_ElevationRequired_ReturnsRunasVerb(string target)
    {
        // 740 = ERROR_ELEVATION_REQUIRED: the target exe's manifest declares
        // requireAdministrator and our host process isn't elevated. The only
        // way to trigger UAC from .NET is ShellExecuteEx + verb="runas".
        string? verb = LauncherRunner.DecideFallback(target, "[Win32 740] 启动失败：请求的操作需要提升。");
        Assert.Equal("runas", verb);
    }

    // ── Non-fallthrough cases (return null = don't retry) ──

    [Theory]
    [InlineData("C:\\Program Files\\App\\app.exe")]           // genuine missing file
    [InlineData("D:\\deleted\\thing.exe")]                    // bad path
    public void DecideFallback_NonLnkExe_NonElevationError_ReturnsNull(string target)
    {
        // ERROR_FILE_NOT_FOUND on a real .exe means the path is wrong; the
        // shell can't do anything CreateProcess couldn't, so we don't retry.
        string? verb = LauncherRunner.DecideFallback(target, "[Win32 2] 启动失败：系统找不到指定的文件。");
        Assert.Null(verb);
    }

    [Fact]
    public void DecideFallback_AccessDenied_NonElevation_ReturnsNull()
    {
        // ERROR_ACCESS_DENIED (5) is not the elevation-required case (that's
        // 740). A plain access-denied means file ACLs block us; retrying via
        // the shell won't help.
        string? verb = LauncherRunner.DecideFallback("C:\\locked\\app.exe", "[Win32 5] 启动失败：拒绝访问。");
        Assert.Null(verb);
    }

    [Fact]
    public void DecideFallback_NonWin32Exception_ReturnsNull()
    {
        // Errors not prefixed with "[Win32 N]" (e.g. FileNotFoundException's
        // default message) have no code to gate on; never retry.
        string? verb = LauncherRunner.DecideFallback("C:\\app.exe", "启动失败：Could not find file.");
        Assert.Null(verb);
    }

    [Fact]
    public void DecideFallback_MscTarget_NonElevationError_ReturnsNull()
    {
        // .msc (MMC console) files aren't .lnk and aren't .exe manifests
        // requesting elevation; a missing-file error doesn't trigger a retry.
        string? verb = LauncherRunner.DecideFallback("C:\\Windows\\system32\\compmgmt.msc", "[Win32 2] 启动失败");
        Assert.Null(verb);
    }

    // ── Edge cases ──

    [Theory]
    [InlineData("app.lnk.txt")]         // looks like .lnk but isn't
    [InlineData("lnk")]                  // bare extension-less name
    [InlineData("C:\\folder\\lnk\\app.exe")]  // "lnk" appears as a folder
    public void DecideFallback_LnkAsSubstring_NotTreatedAsShortcut(string target)
    {
        // EndsWith(".lnk") must be a real extension match, not a substring
        // match. "app.lnk.txt", bare "lnk", and folder names containing "lnk"
        // should all fall through to the elevation check (which fails because
        // the error here is 2, not 740) → null.
        string? verb = LauncherRunner.DecideFallback(target, "[Win32 2] 启动失败");
        Assert.Null(verb);
    }

    [Fact]
    public void DecideFallback_EmptyError_ReturnsNull()
    {
        // Defensive: should never happen in practice (Start always returns
        // either null or a non-empty message) but the parser must not crash.
        string? verb = LauncherRunner.DecideFallback("C:\\app.exe", "");
        Assert.Null(verb);
    }

    [Fact]
    public void DecideFallback_MalformedPrefix_ReturnsNull()
    {
        // "[Win32 abc]" or "[Other 740]" should not parse as a code; the
        // method returns null rather than throwing.
        string? verb = LauncherRunner.DecideFallback("C:\\app.exe", "[Win32 not-a-number] something");
        Assert.Null(verb);
    }
}
