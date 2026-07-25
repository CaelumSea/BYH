using SelectionAssistant.Core.Launcher;
using Xunit;

namespace SelectionAssistant.Core.Tests.Launcher;

public sealed class LauncherEntrySetTests
{
    // ── FindByTarget (used by the installed-app scanner to dedupe) ──

    [Fact]
    public void FindByTarget_CaseInsensitive_ReturnsEntry()
    {
        var set = new LauncherEntrySet();
        set.Add(new LauncherEntry("launcher-a", "App", LauncherKind.LocalApp, @"C:\Program Files\App\app.exe"));

        // Case-insensitive match on the Target path.
        var found = set.FindByTarget(@"c:\program files\app\APP.EXE");
        Assert.NotNull(found);
        Assert.Equal("launcher-a", found!.Id);
    }

    [Fact]
    public void FindByTarget_TrimmedQuery_ReturnsEntry()
    {
        var set = new LauncherEntrySet();
        set.Add(new LauncherEntry("launcher-a", "App", LauncherKind.LocalApp, @"C:\app.exe"));

        var found = set.FindByTarget(@"  C:\app.exe  ");
        Assert.NotNull(found);
    }

    [Fact]
    public void FindByTarget_NoMatch_ReturnsNull()
    {
        var set = new LauncherEntrySet();
        set.Add(new LauncherEntry("launcher-a", "App", LauncherKind.LocalApp, @"C:\app.exe"));

        Assert.Null(set.FindByTarget(@"C:\other.exe"));
    }

    [Fact]
    public void FindByTarget_EmptyOrNull_ReturnsNull()
    {
        var set = new LauncherEntrySet();
        set.Add(new LauncherEntry("launcher-a", "App", LauncherKind.LocalApp, @"C:\app.exe"));

        Assert.Null(set.FindByTarget(""));
        Assert.Null(set.FindByTarget("   "));
        Assert.Null(set.FindByTarget(null!));
    }

    [Fact]
    public void FindByTarget_MultipleEntries_ReturnsFirstMatch()
    {
        var set = new LauncherEntrySet();
        set.Add(new LauncherEntry("launcher-a", "First", LauncherKind.LocalApp, @"C:\shared.exe"));
        set.Add(new LauncherEntry("launcher-b", "Second", LauncherKind.LocalApp, @"C:\shared.exe"));

        var found = set.FindByTarget(@"C:\shared.exe");
        Assert.NotNull(found);
        Assert.Equal("launcher-a", found!.Id); // First wins — duplicate target is the caller's problem to prevent.
    }
}
