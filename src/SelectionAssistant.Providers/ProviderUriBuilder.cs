using System;

namespace SelectionAssistant.Providers;

/// <summary>
/// URI-aware join of a base URL and a relative path (§9.3). Uses Uri
/// composition so a trailing path segment like "/v1" is never accidentally
/// discarded by naive string concatenation:
/// <code>
/// baseUrl = https://gateway.example/company/openai/v1
/// path    = chat/completions
/// → https://gateway.example/company/openai/v1/chat/completions   (correct)
/// NOT:    https://gateway.example/company/openaichat/completions  (concat bug)
/// </code>
/// </summary>
internal static class ProviderUriBuilder
{
    public static string Build(string baseUrl, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        // Ensure the base ends with "/" so Uri relative composition treats the
        // last segment as a directory, not a file (Uri(".../v1","x") → ".../x").
        string normalizedBase = baseUrl.EndsWith('/')
            ? baseUrl
            : baseUrl + "/";

        var baseUri = new Uri(normalizedBase, UriKind.Absolute);
        var fullUri = new Uri(baseUri, relativePath);
        return fullUri.AbsoluteUri;
    }
}
