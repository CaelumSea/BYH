using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class ModelsCacheStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-models-cache-{Guid.NewGuid():N}.json");

    [Fact]
    public void LoadIfExists_MissingFile_ReturnsEmpty()
    {
        var cache = ModelsCacheStore.LoadIfExists(
            Path.Combine(Path.GetTempPath(), "definitely-does-not-exist.json"));

        Assert.Empty(cache.ByProvider);
        Assert.Null(cache.Find("any"));
    }

    [Fact]
    public void LoadIfExists_CorruptJson_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "this is not json {{{");
            var cache = ModelsCacheStore.LoadIfExists(path);
            Assert.Empty(cache.ByProvider);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadIfExists_WrongSchemaVersion_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, """{"schemaVersion":999,"providers":[]}""");
            var cache = ModelsCacheStore.LoadIfExists(path);
            Assert.Empty(cache.ByProvider);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        string path = TempPath();
        try
        {
            var fetchedAt = new DateTime(2026, 7, 26, 12, 30, 0, DateTimeKind.Utc);
            var entry1 = new ModelsCacheEntry("deepseek", fetchedAt, new[] { "deepseek-chat", "deepseek-v4-flash" });
            var entry2 = new ModelsCacheEntry("openrouter", fetchedAt, new[] { "openai/gpt-4o", "anthropic/claude-3.5" });
            var cache = ModelsCache.Empty.With(entry1).With(entry2);

            ModelsCacheStore.Save(cache, path);
            var loaded = ModelsCacheStore.LoadIfExists(path);

            Assert.Equal(2, loaded.ByProvider.Count);

            ModelsCacheEntry? loaded1 = loaded.Find("deepseek");
            Assert.NotNull(loaded1);
            Assert.Equal("deepseek", loaded1!.ProviderId);
            Assert.Equal(fetchedAt, loaded1.FetchedAtUtc);
            Assert.Equal(new[] { "deepseek-chat", "deepseek-v4-flash" }, loaded1.Models);

            ModelsCacheEntry? loaded2 = loaded.Find("openrouter");
            Assert.NotNull(loaded2);
            Assert.Equal(2, loaded2!.Models.Count);
            Assert.Contains("openai/gpt-4o", loaded2.Models);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_OverwritesExistingEntry_ForSameProviderId()
    {
        // The With() method must REPLACE, not append, when the same provider id
        // is written twice — otherwise the cache grows unbounded.
        var fetchedAt = DateTime.UtcNow;
        var cache = ModelsCache.Empty
            .With(new ModelsCacheEntry("p", fetchedAt, new[] { "old-model" }))
            .With(new ModelsCacheEntry("p", fetchedAt, new[] { "new-model" }));

        Assert.Single(cache.ByProvider);
        Assert.Equal(new[] { "new-model" }, cache.Find("p")!.Models);
    }

    [Fact]
    public void With_PreservesOtherProviders()
    {
        var fetchedAt = DateTime.UtcNow;
        var cache = ModelsCache.Empty
            .With(new ModelsCacheEntry("a", fetchedAt, new[] { "a1" }))
            .With(new ModelsCacheEntry("b", fetchedAt, new[] { "b1" }))
            .With(new ModelsCacheEntry("a", fetchedAt, new[] { "a2" }));

        Assert.Equal(2, cache.ByProvider.Count);
        Assert.Equal(new[] { "a2" }, cache.Find("a")!.Models);
        Assert.Equal(new[] { "b1" }, cache.Find("b")!.Models);
    }

    [Fact]
    public void LoadIfExists_FiltersBlankAndNonStringIds()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "providers": [
                    {
                      "providerId": "p",
                      "fetchedAtUtc": "2026-07-26T12:00:00Z",
                      "models": ["valid", "", "  ", 42, null, "also-valid"]
                    }
                  ]
                }
                """);
            var cache = ModelsCacheStore.LoadIfExists(path);

            ModelsCacheEntry? entry = cache.Find("p");
            Assert.NotNull(entry);
            // The number 42 and null are skipped; blanks dropped.
            Assert.Equal(new[] { "valid", "also-valid" }, entry!.Models);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_CreatesParentDirectory_IfMissing()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"byh-cache-dir-{Guid.NewGuid():N}", "nested");
        string path = Path.Combine(dir, "models-cache.json");
        try
        {
            Assert.False(Directory.Exists(dir));
            ModelsCacheStore.Save(ModelsCache.Empty, path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch { }
            }
        }
    }

    [Fact]
    public void Save_IsAtomic_NoTmpFileLeftAfterSuccess()
    {
        string path = TempPath();
        try
        {
            ModelsCacheStore.Save(ModelsCache.Empty.With(
                new ModelsCacheEntry("p", DateTime.UtcNow, new[] { "m" })), path);

            Assert.True(File.Exists(path));
            // The atomic-write pattern moves path.tmp → path, so no .tmp should remain.
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void LoadIfExists_OversizedFile_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            // Write a file larger than MaximumFileBytes (256 KB).
            File.WriteAllText(path, new string('x', ModelsCacheStore.MaximumFileBytes + 1));
            var cache = ModelsCacheStore.LoadIfExists(path);
            Assert.Empty(cache.ByProvider);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadIfExists_MissingFetchedAt_DefaultsToUtcNow()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "providers": [
                    { "providerId": "p", "models": ["m"] }
                  ]
                }
                """);
            var cache = ModelsCacheStore.LoadIfExists(path);

            ModelsCacheEntry? entry = cache.Find("p");
            Assert.NotNull(entry);
            // The loader substitutes UtcNow when fetchedAtUtc is missing/invalid,
            // so the "X minutes ago" status line doesn't show a nonsense value.
            Assert.True(entry!.FetchedAtUtc <= DateTime.UtcNow);
            Assert.True(entry.FetchedAtUtc > DateTime.UtcNow.AddMinutes(-1));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
