using System.Text.Json;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// One provider's fetched model list + the UTC timestamp of the fetch.
/// </summary>
public sealed record ModelsCacheEntry(
    string ProviderId,
    DateTime FetchedAtUtc,
    IReadOnlyList<string> Models);

/// <summary>
/// R26: caches the model ids fetched from each provider's
/// <c>GET {BaseUrl}/models</c> endpoint, keyed by provider id, so the Settings
/// UI model dropdown can populate instantly on reopen even when offline, and a
/// failed refresh doesn't lose the last-known list. Mirrors the
/// <see cref="VisionCaptureStore"/> pattern: missing/corrupt file = empty
/// cache (no crash), writes are atomic (temp + Move), all serialization is
/// hand-written via <see cref="Utf8JsonWriter"/> (NativeAOT-safe, no reflection).
/// </summary>
public static class ModelsCacheStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 256 * 1024;
    public const int MaximumProviders = 64;
    public const int MaximumModelsPerProvider = 4096;

    /// <summary>
    /// Loads the cache. Returns an empty cache (never throws) if the file is
    /// missing, corrupt, oversized, or has an unknown schemaVersion.
    /// </summary>
    public static ModelsCache LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return ModelsCache.Empty;
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes)
        {
            return ModelsCache.Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                return ModelsCache.Empty;
            }

            if (!root.TryGetProperty("providers", out JsonElement providersEl) ||
                providersEl.ValueKind != JsonValueKind.Array)
            {
                return ModelsCache.Empty;
            }

            var dict = new Dictionary<string, ModelsCacheEntry>(StringComparer.Ordinal);
            int count = 0;
            foreach (JsonElement entryEl in providersEl.EnumerateArray())
            {
                if (++count > MaximumProviders) { break; }
                if (entryEl.ValueKind != JsonValueKind.Object) { continue; }

                string? providerId = ReadString(entryEl, "providerId", null);
                if (string.IsNullOrWhiteSpace(providerId)) { continue; }

                DateTime fetchedAtUtc = ReadDateTime(entryEl, "fetchedAtUtc", DateTime.MinValue);
                if (fetchedAtUtc == DateTime.MinValue)
                {
                    fetchedAtUtc = DateTime.UtcNow;
                }

                var models = new List<string>();
                if (entryEl.TryGetProperty("models", out JsonElement modelsEl) &&
                    modelsEl.ValueKind == JsonValueKind.Array)
                {
                    int modelCount = 0;
                    foreach (JsonElement m in modelsEl.EnumerateArray())
                    {
                        if (++modelCount > MaximumModelsPerProvider) { break; }
                        if (m.ValueKind != JsonValueKind.String) { continue; }
                        string? id = m.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            models.Add(id);
                        }
                    }
                }

                dict[providerId] = new ModelsCacheEntry(providerId, fetchedAtUtc, models);
            }

            return new ModelsCache(dict);
        }
        catch (JsonException)
        {
            return ModelsCache.Empty;
        }
        catch (IOException)
        {
            return ModelsCache.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return ModelsCache.Empty;
        }
    }

    /// <summary>Atomically writes the cache to disk.</summary>
    public static void Save(ModelsCache cache, string path)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", CurrentSchemaVersion);

                writer.WriteStartArray("providers");
                foreach (KeyValuePair<string, ModelsCacheEntry> kv in cache.ByProvider)
                {
                    ModelsCacheEntry entry = kv.Value;
                    writer.WriteStartObject();
                    writer.WriteString("providerId", entry.ProviderId);
                    writer.WriteString("fetchedAtUtc", entry.FetchedAtUtc);
                    writer.WriteStartArray("models");
                    foreach (string model in entry.Models)
                    {
                        writer.WriteStringValue(model);
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
                writer.Flush();
            }

            // Atomic replace (single API on Windows). Mirrors the other stores.
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入模型缓存文件。", exception);
        }
    }

    private static string? ReadString(JsonElement root, string name, string? defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return defaultValue;
        }

        return value.GetString();
    }

    private static DateTime ReadDateTime(JsonElement root, string name, DateTime defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return defaultValue;
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) { return defaultValue; }

        return DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime result)
            ? result
            : defaultValue;
    }
}

/// <summary>
/// Immutable snapshot of the model-id cache. Empty by default; mutated only by
/// constructing a new instance (the loader/writer don't expose mutators).
/// </summary>
public sealed record ModelsCache(IReadOnlyDictionary<string, ModelsCacheEntry> ByProvider)
{
    public static ModelsCache Empty { get; } =
        new(new Dictionary<string, ModelsCacheEntry>(0, StringComparer.Ordinal));

    /// <summary>Returns the cached entry for a provider, or null if never fetched.</summary>
    public ModelsCacheEntry? Find(string providerId) =>
        ByProvider.TryGetValue(providerId, out ModelsCacheEntry? entry) ? entry : null;

    /// <summary>
    /// Returns a new cache with <paramref name="entry"/> replacing any prior
    /// entry for the same provider id. Used by the refresh handler to merge a
    /// fresh fetch into the existing cache before persisting.
    /// </summary>
    public ModelsCache With(ModelsCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var dict = new Dictionary<string, ModelsCacheEntry>(ByProvider, StringComparer.Ordinal)
        {
            [entry.ProviderId] = entry,
        };
        return new ModelsCache(dict);
    }
}
