using System.Text.Json;

namespace SelectionAssistant.Infrastructure.Speech;

/// <summary>
/// Reads the mmx CLI's persisted config at <c>~/.mmx/config.json</c> so BYH can
/// reuse the MiniMax API key the user already configured via <c>mmx auth login</c>,
/// instead of forcing a second key entry. BYH only READS this file — it never
/// writes or refreshes tokens (that's mmx's job). On any failure (file missing,
/// unreadable, malformed JSON, all credentials empty) returns null; the caller
/// then surfaces a "no key configured" error.
/// </summary>
public static class MmxConfigReader
{
    /// <summary>
    /// Resolves MiniMax credentials from <c>~/.mmx/config.json</c>. Returns null
    /// when no usable credential is found.
    /// <para>
    /// Credential priority (matches mmx's own resolution): explicit
    /// <c>api_key</c> field first; otherwise <c>oauth.access_token</c>. The OAuth
    /// token may be expired (BYH does not refresh it) — the caller will get HTTP
    /// 401 from MiniMax and prompt the user to re-run <c>mmx auth login</c>.
    /// </para>
    /// </summary>
    /// <param name="configFilePath">
    /// Path to <c>~/.mmx/config.json</c>. When null, resolves to
    /// <c>%USERPROFILE%/.mmx/config.json</c>.
    /// </param>
    public static MmxCredential? Read(string? configFilePath = null)
    {
        string path = configFilePath ?? GetDefaultConfigPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;

            string? apiKey = ReadString(root, "api_key");
            string? oauthToken = ReadString(root, "oauth", "access_token");
            string token = !string.IsNullOrWhiteSpace(apiKey)
                ? apiKey!
                : !string.IsNullOrWhiteSpace(oauthToken)
                    ? oauthToken!
                    : string.Empty;
            if (token.Length == 0)
            {
                return null;
            }

            string region = ReadString(root, "region") ?? "global";
            return new MmxCredential(token, region);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>%USERPROFILE%/.mmx/config.json — the mmx CLI's default config location.</summary>
    public static string GetDefaultConfigPath()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".mmx", "config.json");
    }

    private static string? ReadString(JsonElement root, params string[] path)
    {
        JsonElement current = root;
        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}

/// <summary>A resolved MiniMax credential from mmx config: token + region.</summary>
public sealed record MmxCredential(string Token, string Region);
