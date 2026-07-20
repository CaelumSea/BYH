using SelectionAssistant.Core.Launcher;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Launcher;

public sealed class LauncherEntryStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-launcher-{Guid.NewGuid():N}.json");

    private static LauncherEntry MakeEntry(string suffix = "a", LauncherKind kind = LauncherKind.LocalApp) =>
        new LauncherEntry(
            Id: $"launcher-{suffix}",
            Name: $"App {suffix}",
            Kind: kind,
            Target: kind == LauncherKind.WebUrl ? $"https://example.com/{suffix}" : @$"C:\app\{suffix}.exe",
            Arguments: $"--{suffix}",
            WorkingDirectory: @$"C:\work\{suffix}",
            IconOverride: @$"C:\icons\{suffix}.ico");

    [Fact]
    public void MissingFile_ReturnsEmptySet()
    {
        var set = LauncherEntryStore.LoadIfExists(
            Path.Combine(Path.GetTempPath(), "definitely-does-not-exist.json"));

        Assert.Empty(set.Entries);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        string path = TempPath();
        try
        {
            var original = new LauncherEntrySet();
            original.Add(MakeEntry("full"));

            LauncherEntryStore.Save(original, path);
            var loaded = LauncherEntryStore.LoadIfExists(path);

            var e = Assert.Single(loaded.Entries);
            Assert.Equal("launcher-full", e.Id);
            Assert.Equal("App full", e.Name);
            Assert.Equal(LauncherKind.LocalApp, e.Kind);
            Assert.Equal(@"C:\app\full.exe", e.Target);
            Assert.Equal("--full", e.Arguments);
            Assert.Equal(@"C:\work\full", e.WorkingDirectory);
            Assert.Equal(@"C:\icons\full.ico", e.IconOverride);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenLoad_PreservesOrder()
    {
        string path = TempPath();
        try
        {
            var original = new LauncherEntrySet();
            original.Add(MakeEntry("first"));
            original.Add(MakeEntry("second"));
            original.Add(MakeEntry("third"));

            LauncherEntryStore.Save(original, path);
            var loaded = LauncherEntryStore.LoadIfExists(path);

            Assert.Equal(3, loaded.Entries.Count);
            Assert.Equal("launcher-first", loaded.Entries[0].Id);
            Assert.Equal("launcher-second", loaded.Entries[1].Id);
            Assert.Equal("launcher-third", loaded.Entries[2].Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsBothKinds()
    {
        string path = TempPath();
        try
        {
            var original = new LauncherEntrySet();
            original.Add(MakeEntry("local", LauncherKind.LocalApp));
            original.Add(MakeEntry("web", LauncherKind.WebUrl));

            LauncherEntryStore.Save(original, path);
            var loaded = LauncherEntryStore.LoadIfExists(path);

            Assert.Equal(2, loaded.Entries.Count);
            Assert.Equal(LauncherKind.LocalApp, loaded.Entries[0].Kind);
            Assert.Equal(LauncherKind.WebUrl, loaded.Entries[1].Kind);
            Assert.Equal("https://example.com/web", loaded.Entries[1].Target);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_WithEmptyOptionals_OmitsThem()
    {
        string path = TempPath();
        try
        {
            var set = new LauncherEntrySet();
            set.Add(new LauncherEntry(
                Id: "launcher-minimal",
                Name: "Min",
                Kind: LauncherKind.LocalApp,
                Target: @"C:\min.exe"));

            LauncherEntryStore.Save(set, path);
            string json = File.ReadAllText(path);

            Assert.DoesNotContain("arguments", json);
            Assert.DoesNotContain("workingDirectory", json);
            Assert.DoesNotContain("iconOverride", json);
            Assert.Contains("\"launcher-minimal\"", json);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AtomicWrite_TempFileCleanedUpAfterMove()
    {
        string path = TempPath();
        try
        {
            var set = new LauncherEntrySet();
            set.Add(MakeEntry("atom"));

            LauncherEntryStore.Save(set, path);

            Assert.False(File.Exists(path + ".tmp"), "temp file should be gone after atomic move");
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_FileWithEntries_LoadsAll()
    {
        string path = TempPath();
        try
        {
            string json = """
            {
              "schemaVersion": 1,
              "entries": [
                { "id": "launcher-a", "name": "A", "kind": "localApp", "target": "C:\\a.exe" },
                { "id": "launcher-b", "name": "B", "kind": "webUrl", "target": "https://b.com" },
                { "id": "launcher-c", "name": "C", "kind": "localApp", "target": "C:\\c.exe", "arguments": "--x" }
              ]
            }
            """;
            File.WriteAllText(path, json);

            var loaded = LauncherEntryStore.LoadIfExists(path);
            Assert.Equal(3, loaded.Entries.Count);
            Assert.Equal("A", loaded.Entries[0].Name);
            Assert.Equal("B", loaded.Entries[1].Name);
            Assert.Equal(LauncherKind.WebUrl, loaded.Entries[1].Kind);
            Assert.Equal("--x", loaded.Entries[2].Arguments);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownIdPrefix_IgnoredGracefully()
    {
        string path = TempPath();
        try
        {
            string json = """
            {
              "schemaVersion": 1,
              "entries": [
                { "id": "future-thing", "name": "Future", "kind": "localApp", "target": "C:\\f.exe" },
                { "id": "launcher-known", "name": "Known", "kind": "localApp", "target": "C:\\k.exe" }
              ]
            }
            """;
            File.WriteAllText(path, json);

            var loaded = LauncherEntryStore.LoadIfExists(path);
            var entry = Assert.Single(loaded.Entries);
            Assert.Equal("launcher-known", entry.Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_DuplicateId_DeduplicatesKeepingFirst()
    {
        string path = TempPath();
        try
        {
            string json = """
            {
              "schemaVersion": 1,
              "entries": [
                { "id": "launcher-dup", "name": "First", "kind": "localApp", "target": "C:\\first.exe" },
                { "id": "launcher-dup", "name": "Second", "kind": "localApp", "target": "C:\\second.exe" }
              ]
            }
            """;
            File.WriteAllText(path, json);

            var loaded = LauncherEntryStore.LoadIfExists(path);
            var entry = Assert.Single(loaded.Entries);
            Assert.Equal("First", entry.Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Add_DuplicateId_ReturnsFalse()
    {
        var set = new LauncherEntrySet();
        Assert.True(set.Add(MakeEntry("dup")));
        Assert.False(set.Add(MakeEntry("dup")));
    }

    [Fact]
    public void Add_NonLauncherId_ReturnsFalse()
    {
        var set = new LauncherEntrySet();
        var bad = new LauncherEntry(
            Id: "not-launcher-x",
            Name: "Bad",
            Kind: LauncherKind.LocalApp,
            Target: @"C:\bad.exe");
        Assert.False(set.Add(bad));
        Assert.Empty(set.Entries);
    }

    [Fact]
    public void Update_ExistingEntry_ReplacesIt()
    {
        var set = new LauncherEntrySet();
        set.Add(MakeEntry("upd"));

        var replacement = new LauncherEntry(
            Id: "launcher-upd",
            Name: "Updated",
            Kind: LauncherKind.WebUrl,
            Target: "https://updated.com",
            Arguments: "--new");

        Assert.True(set.Update(replacement));
        var found = set.Find("launcher-upd");
        Assert.NotNull(found);
        Assert.Equal("Updated", found!.Name);
        Assert.Equal(LauncherKind.WebUrl, found.Kind);
        Assert.Equal("--new", found.Arguments);
    }

    [Fact]
    public void Update_UnknownId_ReturnsFalse()
    {
        var set = new LauncherEntrySet();
        var unknown = MakeEntry("ghost");
        Assert.False(set.Update(unknown));
    }

    [Fact]
    public void Remove_ExistingId_RemovesIt()
    {
        var set = new LauncherEntrySet();
        set.Add(MakeEntry("rem"));
        Assert.True(set.Remove("launcher-rem"));
        Assert.Empty(set.AsList());
    }

    [Fact]
    public void Move_UpAtTop_NoOp()
    {
        var set = new LauncherEntrySet();
        set.Add(MakeEntry("top"));
        set.Add(MakeEntry("bot"));

        Assert.True(set.Move("launcher-top", -1));
        Assert.Equal("launcher-top", set.Entries[0].Id);
        Assert.Equal("launcher-bot", set.Entries[1].Id);
    }

    [Fact]
    public void Move_DownAtBottom_NoOp()
    {
        var set = new LauncherEntrySet();
        set.Add(MakeEntry("top"));
        set.Add(MakeEntry("bot"));

        Assert.True(set.Move("launcher-bot", 1));
        Assert.Equal("launcher-top", set.Entries[0].Id);
        Assert.Equal("launcher-bot", set.Entries[1].Id);
    }

    [Fact]
    public void Move_NegativeDelta_MovesUp()
    {
        var set = new LauncherEntrySet();
        set.Add(MakeEntry("a"));
        set.Add(MakeEntry("b"));
        set.Add(MakeEntry("c"));

        // Move "b" up by 1 (delta = -1).
        Assert.True(set.Move("launcher-b", -1));
        Assert.Equal("launcher-b", set.Entries[0].Id);
        Assert.Equal("launcher-a", set.Entries[1].Id);
        Assert.Equal("launcher-c", set.Entries[2].Id);
    }

    [Fact]
    public void IsLauncher_ClassifyCorrectly()
    {
        Assert.True(LauncherEntryIds.IsLauncher("launcher-abc"));
        Assert.True(LauncherEntryIds.IsLauncher("launcher-x"));
        Assert.False(LauncherEntryIds.IsLauncher("custom-abc"));
        Assert.False(LauncherEntryIds.IsLauncher("summarize"));
        Assert.False(LauncherEntryIds.IsLauncher(""));
    }
}
