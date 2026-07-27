using System.Text.Json;
using SelectionAssistant.Core.Appearance;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// AOT-safe, schema-versioned persistence for the small user-facing profile.
/// </summary>
public static class UserProfileStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 4 * 1024;

    public static UserProfileSettings LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return UserProfileSettings.Default;
        }

        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                throw new ProviderConfigurationException("Profile configuration exceeds the 4 KB limit.");
            }

            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                throw new ProviderConfigurationException("Unsupported profile schemaVersion.");
            }

            string displayName = root.TryGetProperty("displayName", out JsonElement value)
                ? value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : throw new ProviderConfigurationException("displayName must be a string.")
                : string.Empty;

            UserProfileSettings settings = new UserProfileSettings
            {
                DisplayName = displayName,
            }.Normalize();
            settings.Validate();
            return settings;
        }
        catch (ProviderConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ProviderConfigurationException("Unable to read profile configuration.", exception);
        }
    }

    public static void Save(UserProfileSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        settings = settings.Normalize();
        settings.Validate();

        string tempPath = path + ".tmp";
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
                writer.WriteString("displayName", settings.DisplayName);
                writer.WriteEndObject();
                writer.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("Unable to write profile configuration.", exception);
        }
    }
}
