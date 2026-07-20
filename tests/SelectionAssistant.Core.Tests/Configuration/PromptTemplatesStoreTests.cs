using SelectionAssistant.Core.Translation;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class PromptTemplatesStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-prompt-templates-{Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFile_ReturnsBuiltInDefaults()
    {
        var set = PromptTemplatesStore.LoadIfExists(
            Path.Combine(Path.GetTempPath(), "definitely-does-not-exist.json"));

        Assert.Equal(PromptActionIds.Translate, set.Translate.Id);
        Assert.Equal(PromptActionIds.Summarize, set.Summarize.Id);
        Assert.Equal(PromptActionIds.Explain, set.Explain.Id);
        // All built-in prompts are non-empty (each action has its own editable default).
        Assert.False(string.IsNullOrWhiteSpace(set.Translate.Prompt));
        Assert.False(string.IsNullOrWhiteSpace(set.Summarize.Prompt));
        Assert.False(string.IsNullOrWhiteSpace(set.Explain.Prompt));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllActions()
    {
        string path = TempPath();
        try
        {
            var original = new PromptTemplateSet();
            original.TrySet(PromptActionIds.Summarize, "自定义总结指令");
            original.TrySet(PromptActionIds.Explain, "自定义解释指令");

            PromptTemplatesStore.Save(original, path);
            var loaded = PromptTemplatesStore.LoadIfExists(path);

            Assert.Equal("自定义总结指令", loaded.Summarize.Prompt);
            Assert.Equal("自定义解释指令", loaded.Explain.Prompt);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_WithDefaultSummarize_OmitsItFromFile()
    {
        // When a prompt equals the built-in default, it should be omitted from
        // the file (so future default improvements propagate). Verify by
        // checking the file content directly.
        string path = TempPath();
        try
        {
            var set = PromptTemplateDefaults.CreateDefault(); // all defaults
            PromptTemplatesStore.Save(set, path);

            string json = File.ReadAllText(path);
            Assert.Contains("\"translate\"", json);      // translate always written
            Assert.DoesNotContain("自定义", json);        // no custom overrides
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Find_UnknownActionId_ReturnsNull()
    {
        var set = new PromptTemplateSet();
        Assert.Null(set.Find("nonexistent"));
    }

    [Fact]
    public void TrySet_UnknownActionId_ReturnsFalse()
    {
        var set = new PromptTemplateSet();
        Assert.False(set.TrySet("nonexistent", "anything"));
    }

    [Fact]
    public void AtomicWrite_TempFileCleanedUpAfterMove()
    {
        string path = TempPath();
        try
        {
            var set = PromptTemplateDefaults.CreateDefault();
            PromptTemplatesStore.Save(set, path);

            Assert.False(File.Exists(path + ".tmp"), "temp file should be gone after atomic move");
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownActionId_IgnoredGracefully()
    {
        // Forward-compat: an unknown action id in the file should not crash,
        // and known actions in the same file should still load.
        string path = TempPath();
        try
        {
            string json = """
            {
              "schemaVersion": 1,
              "templates": [
                { "id": "future-action", "name": "Future", "prompt": "x" },
                { "id": "summarize", "name": "总结", "prompt": "我的总结指令" }
              ]
            }
            """;
            File.WriteAllText(path, json);

            var loaded = PromptTemplatesStore.LoadIfExists(path);
            Assert.Equal("我的总结指令", loaded.Summarize.Prompt);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsThinkingEnabled()
    {
        // Thinking is now per-action (lives on the prompt template, not the
        // provider). Saving a template with thinking on must round-trip.
        string path = TempPath();
        try
        {
            var original = new PromptTemplateSet();
            original.TrySet(PromptActionIds.Explain, "深度解释这段代码", thinkingEnabled: true);
            original.TrySet(PromptActionIds.Summarize, "快速总结", thinkingEnabled: false);

            PromptTemplatesStore.Save(original, path);
            var loaded = PromptTemplatesStore.LoadIfExists(path);

            Assert.True(loaded.Explain.ThinkingEnabled);
            Assert.False(loaded.Summarize.ThinkingEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyFileWithoutThinking_DefaultsFalse()
    {
        // A prompt-templates.json written before thinking existed (no
        // thinkingEnabled key on the entries) must load with thinking=false.
        string path = TempPath();
        try
        {
            string json = """
            {
              "schemaVersion": 1,
              "templates": [
                { "id": "explain", "name": "解释", "prompt": "解释一下" }
              ]
            }
            """;
            File.WriteAllText(path, json);

            var loaded = PromptTemplatesStore.LoadIfExists(path);
            Assert.False(loaded.Explain.ThinkingEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Add_CustomTemplate_RoundTrips()
    {
        string path = TempPath();
        try
        {
            var original = new PromptTemplateSet();
            Assert.True(original.Add(new PromptTemplate(
                "custom-polish", "润色", "润色以下文字，使其更流畅自然", true)));

            PromptTemplatesStore.Save(original, path);
            var loaded = PromptTemplatesStore.LoadIfExists(path);

            PromptTemplate? custom = loaded.Find("custom-polish");
            Assert.NotNull(custom);
            Assert.Equal("润色", custom!.Name);
            Assert.Equal("润色以下文字，使其更流畅自然", custom.Prompt);
            Assert.True(custom.ThinkingEnabled);
            // Built-ins are still present.
            Assert.Equal(4, loaded.AsList().Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Add_BuiltInId_Rejected()
    {
        var set = new PromptTemplateSet();
        // Cannot "add" a built-in id — it already exists.
        Assert.False(set.Add(new PromptTemplate(PromptActionIds.Translate, "x", "y")));
        Assert.False(set.Add(new PromptTemplate("translate", "dup", "z")));
    }

    [Fact]
    public void Add_DuplicateCustomId_Rejected()
    {
        var set = new PromptTemplateSet();
        Assert.True(set.Add(new PromptTemplate("custom-1", "A", "a")));
        Assert.False(set.Add(new PromptTemplate("custom-1", "B", "b")));
    }

    [Fact]
    public void Remove_OnlyCustomAllowed_BuiltInRejected()
    {
        var set = new PromptTemplateSet();
        set.Add(new PromptTemplate("custom-1", "A", "a"));

        // Built-ins cannot be removed.
        Assert.False(set.Remove(PromptActionIds.Translate));
        Assert.False(set.Remove(PromptActionIds.Summarize));
        Assert.False(set.Remove(PromptActionIds.Explain));

        // Custom can be removed.
        Assert.True(set.Remove("custom-1"));
        Assert.Null(set.Find("custom-1"));
    }

    [Fact]
    public void Load_FileWithCustomEntries_LoadsAll()
    {
        string path = TempPath();
        try
        {
            string json = """
            {
              "schemaVersion": 1,
              "templates": [
                { "id": "summarize", "name": "总结", "prompt": "改过的总结" },
                { "id": "custom-rewrite", "name": "改写", "prompt": "改写以下内容" },
                { "id": "custom-polish", "name": "润色", "prompt": "润色", "thinkingEnabled": true }
              ]
            }
            """;
            File.WriteAllText(path, json);

            var loaded = PromptTemplatesStore.LoadIfExists(path);
            // 3 built-in + 2 custom = 5 total.
            Assert.Equal(5, loaded.AsList().Count);
            Assert.Equal("改过的总结", loaded.Summarize.Prompt);
            Assert.Equal("改写", loaded.Find("custom-rewrite")!.Name);
            Assert.True(loaded.Find("custom-polish")!.ThinkingEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void IsBuiltIn_AndIsCustom_ClassifyCorrectly()
    {
        Assert.True(PromptActionIds.IsBuiltIn(PromptActionIds.Translate));
        Assert.True(PromptActionIds.IsBuiltIn(PromptActionIds.Summarize));
        Assert.True(PromptActionIds.IsBuiltIn(PromptActionIds.Explain));
        Assert.False(PromptActionIds.IsBuiltIn("custom-x"));

        Assert.True(PromptActionIds.IsCustom("custom-polish"));
        Assert.False(PromptActionIds.IsCustom(PromptActionIds.Translate));
        Assert.False(PromptActionIds.IsCustom("nonexistent"));
    }
}
