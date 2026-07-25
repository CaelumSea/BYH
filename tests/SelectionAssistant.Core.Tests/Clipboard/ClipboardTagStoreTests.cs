using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Clipboard;

public sealed class ClipboardTagStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-clip-tags-{Guid.NewGuid():N}.json");

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        ClipboardTagData data = ClipboardTagStore.Load(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));
        Assert.Empty(data.CustomTags);
        Assert.Empty(data.Assignments);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        string path = TempPath();
        try
        {
            var original = new ClipboardTagData
            {
                CustomTags = ["工作", "私聊"],
                Assignments = new Dictionary<Guid, IReadOnlySet<string>>
                {
                    [Id1] = new HashSet<string> { "工作", ClipboardTagData.FavoriteTagName },
                    [Id2] = new HashSet<string> { "私聊" },
                },
            };

            ClipboardTagStore.Save(original, path);
            ClipboardTagData loaded = ClipboardTagStore.Load(path);

            Assert.Equal(new[] { "工作", "私聊" }, loaded.CustomTags);
            Assert.True(loaded.HasTag(Id1, "工作"));
            Assert.True(loaded.HasTag(Id1, ClipboardTagData.FavoriteTagName));
            Assert.True(loaded.HasTag(Id2, "私聊"));
            Assert.False(loaded.HasTag(Id2, "工作"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AddCustomTag_NewName_Appends()
    {
        ClipboardTagData data = ClipboardTagData.Empty;
        ClipboardTagData result = ClipboardTagStore.AddCustomTag(data, "工作");
        Assert.Equal(new[] { "工作" }, result.CustomTags);
    }

    [Fact]
    public void AddCustomTag_Duplicate_NoOp()
    {
        ClipboardTagData data = ClipboardTagData.Empty with { CustomTags = ["工作"] };
        ClipboardTagData result = ClipboardTagStore.AddCustomTag(data, "工作");
        Assert.Same(data, result); // unchanged reference
    }

    [Fact]
    public void AddCustomTag_Blank_NoOp()
    {
        ClipboardTagData data = ClipboardTagData.Empty;
        Assert.Same(data, ClipboardTagStore.AddCustomTag(data, "   "));
    }

    [Fact]
    public void RenameCustomTag_UpdatesAssignments()
    {
        var data = new ClipboardTagData
        {
            CustomTags = ["工作"],
            Assignments = new Dictionary<Guid, IReadOnlySet<string>>
            {
                [Id1] = new HashSet<string> { "工作" },
            },
        };

        ClipboardTagData result = ClipboardTagStore.RenameCustomTag(data, "工作", "办公");

        Assert.Equal(new[] { "办公" }, result.CustomTags);
        Assert.True(result.HasTag(Id1, "办公"));
        Assert.False(result.HasTag(Id1, "工作"));
    }

    [Fact]
    public void DeleteCustomTag_RemovesFromAssignments_KeepsEntries()
    {
        var data = new ClipboardTagData
        {
            CustomTags = ["工作", "私聊"],
            Assignments = new Dictionary<Guid, IReadOnlySet<string>>
            {
                [Id1] = new HashSet<string> { "工作", ClipboardTagData.FavoriteTagName },
                [Id2] = new HashSet<string> { "私聊" },
            },
        };

        ClipboardTagData result = ClipboardTagStore.DeleteCustomTag(data, "工作");

        Assert.Equal(new[] { "私聊" }, result.CustomTags);
        // Id1 still has the favorite tag (not removed), just lost "工作".
        Assert.True(result.HasTag(Id1, ClipboardTagData.FavoriteTagName));
        Assert.False(result.HasTag(Id1, "工作"));
        // Id2 untouched.
        Assert.True(result.HasTag(Id2, "私聊"));
    }

    [Fact]
    public void Assign_ThenUnassign_RoundTrip()
    {
        ClipboardTagData data = ClipboardTagStore.Assign(ClipboardTagData.Empty, Id1, "工作");
        Assert.True(data.HasTag(Id1, "工作"));

        // Assign again is idempotent.
        ClipboardTagData again = ClipboardTagStore.Assign(data, Id1, "工作");
        Assert.Single(again.Assignments[Id1]);

        ClipboardTagData removed = ClipboardTagStore.Unassign(data, Id1, "工作");
        Assert.False(removed.HasTag(Id1, "工作"));
        // Key dropped when entry has no tags left.
        Assert.False(removed.Assignments.ContainsKey(Id1));
    }

    [Fact]
    public void Assign_FavoriteName_WorksLikeAnyTag()
    {
        ClipboardTagData data = ClipboardTagStore.Assign(ClipboardTagData.Empty, Id1, ClipboardTagData.FavoriteTagName);
        Assert.True(data.HasTag(Id1, ClipboardTagData.FavoriteTagName));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json }}}");
            ClipboardTagData data = ClipboardTagStore.Load(path);
            Assert.Empty(data.CustomTags);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── R54 v1.2: icon + reorder + schema migration tests ──

    [Fact]
    public void SetTagIcon_ThenRoundTrip_PersistsIcon()
    {
        var data = new ClipboardTagData { CustomTags = ["工作", "私聊"] };
        data = ClipboardTagStore.SetTagIcon(data, "工作", "💼");

        Assert.Equal("💼", data.IconFor("工作"));
        Assert.Null(data.IconFor("私聊"));

        // Round-trip through disk keeps the icon.
        string path = TempPath();
        try
        {
            ClipboardTagStore.Save(data, path);
            ClipboardTagData loaded = ClipboardTagStore.Load(path);
            Assert.Equal("💼", loaded.IconFor("工作"));
            Assert.Null(loaded.IconFor("私聊"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SetTagIcon_BlankEmoji_ClearsIcon()
    {
        var data = new ClipboardTagData
        {
            CustomTags = ["工作"],
            TagIcons = new Dictionary<string, string> { ["工作"] = "💼" },
        };
        Assert.Equal("💼", data.IconFor("工作"));

        ClipboardTagData cleared = ClipboardTagStore.SetTagIcon(data, "工作", "  ");
        Assert.Null(cleared.IconFor("工作"));
        Assert.Empty(cleared.TagIcons);
    }

    [Fact]
    public void SetTagIcon_UnknownTag_NoOp()
    {
        var data = new ClipboardTagData { CustomTags = ["工作"] };
        ClipboardTagData result = ClipboardTagStore.SetTagIcon(data, "不存在", "💼");
        Assert.Same(data, result); // unchanged — name isn't a custom tag
    }

    [Fact]
    public void RenameCustomTag_CarriesIconAlong()
    {
        var data = new ClipboardTagData
        {
            CustomTags = ["工作"],
            TagIcons = new Dictionary<string, string> { ["工作"] = "💼" },
        };
        ClipboardTagData result = ClipboardTagStore.RenameCustomTag(data, "工作", "办公");
        Assert.Equal(new[] { "办公" }, result.CustomTags);
        Assert.Equal("💼", result.IconFor("办公"));
        Assert.Null(result.IconFor("工作")); // old key gone
    }

    [Fact]
    public void DeleteCustomTag_RemovesItsIcon()
    {
        var data = new ClipboardTagData
        {
            CustomTags = ["工作", "私聊"],
            TagIcons = new Dictionary<string, string> { ["工作"] = "💼", ["私聊"] = "💬" },
        };
        ClipboardTagData result = ClipboardTagStore.DeleteCustomTag(data, "工作");
        Assert.Equal(new[] { "私聊" }, result.CustomTags);
        Assert.Null(result.IconFor("工作"));
        Assert.Equal("💬", result.IconFor("私聊")); // other icon kept
    }

    [Fact]
    public void ReorderTag_MovesWithinList()
    {
        var data = new ClipboardTagData { CustomTags = ["a", "b", "c"] };

        // Move "c" (index 2) to index 0.
        ClipboardTagData moved = ClipboardTagStore.ReorderTag(data, "c", 0);
        Assert.Equal(new[] { "c", "a", "b" }, moved.CustomTags);

        // Move "a" (now index 1) to the end (index 2).
        ClipboardTagData movedAgain = ClipboardTagStore.ReorderTag(moved, "a", 2);
        Assert.Equal(new[] { "c", "b", "a" }, movedAgain.CustomTags);
    }

    [Fact]
    public void ReorderTag_SameIndex_NoOp()
    {
        var data = new ClipboardTagData { CustomTags = ["a", "b"] };
        ClipboardTagData result = ClipboardTagStore.ReorderTag(data, "a", 0);
        Assert.Same(data, result); // no change → same reference
    }

    [Fact]
    public void ReorderTag_UnknownTag_NoOp()
    {
        var data = new ClipboardTagData { CustomTags = ["a"] };
        ClipboardTagData result = ClipboardTagStore.ReorderTag(data, "z", 0);
        Assert.Same(data, result);
    }

    [Fact]
    public void Load_V1SchemaFile_UpgradesToV2WithEmptyIcons()
    {
        // A schema-v1 file (customTags as a string array, no tagIcons) must load
        // seamlessly — icons come back empty, everything else intact. This is the
        // backward-compat path for users upgrading from v1.1.
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                {
                  "schemaVersion": 1,
                  "customTags": ["工作", "私聊"],
                  "assignments": {}
                }
                """);
            ClipboardTagData loaded = ClipboardTagStore.Load(path);
            Assert.Equal(new[] { "工作", "私聊" }, loaded.CustomTags);
            Assert.Empty(loaded.TagIcons);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownSchemaVersion_ReturnsEmpty()
    {
        // A future schema (e.g. v3) we don't understand must not partially load —
        // return Empty rather than risk misinterpretation.
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                {
                  "schemaVersion": 99,
                  "customTags": ["工作"]
                }
                """);
            ClipboardTagData loaded = ClipboardTagStore.Load(path);
            Assert.Empty(loaded.CustomTags);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
