using System.Text.Json;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// One provider entry parsed from <c>providers.json</c>. Pure data — no secrets.
/// The composition root maps this to a runtime provider options object.
/// </summary>
/// <remarks>
/// <see cref="SystemPrompt" /> is optional. When null/whitespace the built-in
/// translation template is used. (Thinking mode is no longer a per-provider
/// setting — it lives on the prompt template, which is the single source of
/// truth, so the same provider can think for one action and not another.)
/// </remarks>
public sealed record ProviderProfileEntry(
    string Id,
    string Name,
    string BaseUrl,
    string? ApiKeyReference,
    string DefaultModel,
    string ChatCompletionsPath,
    int TimeoutSeconds,
    int MaxSourceCharacters,
    string? SystemPrompt = null);

public sealed record ProviderConfiguration(
    string? DefaultProviderId,
    IReadOnlyList<ProviderProfileEntry> Providers);

/// <summary>
/// Mutable companion to <see cref="ProviderConfiguration" /> for CRUD
/// operations. The runtime holds this, mutates it, then persists via
/// <see cref="ProviderConfigurationLoader.Save" />.
/// </summary>
public sealed class MutableProviderConfiguration
{
    public string? DefaultProviderId { get; set; }

    public List<ProviderProfileEntry> Providers { get; } = [];

    public MutableProviderConfiguration() { }

    public MutableProviderConfiguration(string? defaultProviderId, IEnumerable<ProviderProfileEntry> providers)
    {
        DefaultProviderId = defaultProviderId;
        Providers.AddRange(providers);
    }

    /// <summary>Snapshots to an immutable ProviderConfiguration for the Save method.</summary>
    public ProviderConfiguration ToImmutable() => new(DefaultProviderId, Providers);

    public ProviderProfileEntry? FindById(string id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

public static class ProviderConfigurationLoader
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 64 * 1024;
    public const int MaximumProviders = 32;

    public static ProviderConfiguration LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return new ProviderConfiguration(null, []);
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes)
        {
            throw new ProviderConfigurationException("Provider 配置文件超过 64 KB 上限。");
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
                throw new ProviderConfigurationException("不支持的 provider schemaVersion。");
            }

            string? defaultId = root.TryGetProperty("defaultProviderId", out JsonElement idElement) &&
                idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;

            if (!root.TryGetProperty("providers", out JsonElement providersElement) ||
                providersElement.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderConfigurationException("Provider 配置文件缺少 providers 数组。");
            }

            if (providersElement.GetArrayLength() > MaximumProviders)
            {
                throw new ProviderConfigurationException($"Provider 条目不能超过 {MaximumProviders} 个。");
            }

            var providers = new List<ProviderProfileEntry>(providersElement.GetArrayLength());
            foreach (JsonElement entryElement in providersElement.EnumerateArray())
            {
                providers.Add(ParseEntry(entryElement));
            }

            return new ProviderConfiguration(defaultId, providers);
        }
        catch (ProviderConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProviderConfigurationException("Provider 配置文件不是有效 JSON。", exception);
        }
        catch (IOException exception)
        {
            throw new ProviderConfigurationException("无法读取 Provider 配置文件。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProviderConfigurationException("没有权限读取 Provider 配置文件。", exception);
        }
    }

    /// <summary>
    /// Atomically writes a provider configuration to disk. Writes to a temp
    /// file first, then moves it into place — so a crash mid-write never
    /// corrupts the existing config. Uses Utf8JsonWriter (AOT-safe, no
    /// reflection serialization). Never writes API keys (those live in the
    /// DPAPI secret store; only the secret:// reference is persisted here).
    /// </summary>
    public static void Save(ProviderConfiguration config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (config.Providers.Count > MaximumProviders)
        {
            throw new ProviderConfigurationException($"Provider 条目不能超过 {MaximumProviders} 个。");
        }

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

                if (!string.IsNullOrEmpty(config.DefaultProviderId))
                {
                    writer.WriteString("defaultProviderId", config.DefaultProviderId);
                }

                writer.WriteStartArray("providers");
                foreach (ProviderProfileEntry entry in config.Providers)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", entry.Id);
                    writer.WriteString("name", entry.Name);
                    writer.WriteString("baseUrl", entry.BaseUrl);
                    if (!string.IsNullOrEmpty(entry.ApiKeyReference))
                    {
                        writer.WriteString("apiKeyReference", entry.ApiKeyReference);
                    }
                    writer.WriteString("defaultModel", entry.DefaultModel);
                    writer.WriteString("chatCompletionsPath", entry.ChatCompletionsPath);
                    writer.WriteNumber("timeoutSeconds", entry.TimeoutSeconds);
                    writer.WriteNumber("maxSourceCharacters", entry.MaxSourceCharacters);
                    if (!string.IsNullOrWhiteSpace(entry.SystemPrompt))
                    {
                        writer.WriteString("systemPrompt", entry.SystemPrompt);
                    }
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
            }

            // Atomic move: temp → final. Overwrites existing file.
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tempPath, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Clean up the temp file if the move failed.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入 Provider 配置文件。", exception);
        }
    }

    /// <summary>Convenience: load, then save back (useful for normalizing/reformatting).</summary>
    public static void SaveOrCreate(ProviderConfiguration config, string path) => Save(config, path);

    private static ProviderProfileEntry ParseEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ProviderConfigurationException("每个 provider 条目必须是对象。");
        }

        string id = RequireString(element, "id");
        string name = RequireString(element, "name");
        string baseUrl = RequireString(element, "baseUrl");
        string defaultModel = RequireString(element, "defaultModel");

        string? apiKeyReference = element.TryGetProperty("apiKeyReference", out JsonElement keyRef) &&
            keyRef.ValueKind == JsonValueKind.String
                ? keyRef.GetString()
                : null;

        string chatCompletionsPath = OptionalString(element, "chatCompletionsPath", "chat/completions");
        int timeoutSeconds = ReadInteger(element, "timeoutSeconds", 60);
        int maxSourceChars = ReadInteger(element, "maxSourceCharacters", 8000);

        // Optional field (added 2026-07-17 for custom prompts). Defaults to
        // null ("use built-in translation template") when absent or invalid, so
        // existing providers.json files keep working.
        // NOTE: thinkingEnabled is intentionally NOT read here anymore — it
        // moved to the prompt template. Legacy files carrying the key are
        // ignored silently (forward-compatible).
        string? systemPrompt = element.TryGetProperty("systemPrompt", out JsonElement promptElement) &&
            promptElement.ValueKind == JsonValueKind.String
                ? promptElement.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            systemPrompt = null;
        }

        return new ProviderProfileEntry(
            id, name, baseUrl, apiKeyReference, defaultModel,
            chatCompletionsPath, timeoutSeconds, maxSourceChars,
            systemPrompt);
    }

    private static string RequireString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ProviderConfigurationException($"Provider 条目缺少有效的 {name}。");
        }

        return value.GetString()!.Trim();
    }

    private static string OptionalString(JsonElement element, string name, string defaultValue)
    {
        if (!element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            return defaultValue;
        }

        return value.GetString()!.Trim();
    }

    private static int ReadInteger(JsonElement element, string name, int defaultValue)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return defaultValue;
        }

        return value.TryGetInt32(out int result) && result > 0
            ? result
            : throw new ProviderConfigurationException($"{name} 必须是正整数。");
    }
}

public sealed class ProviderConfigurationException : Exception
{
    public ProviderConfigurationException(string message) : base(message) { }

    public ProviderConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }
}
