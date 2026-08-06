using SelectionAssistant.Infrastructure.Speech;
using Xunit;

namespace SelectionAssistant.Core.Tests.Speech;

public sealed class MmxConfigReaderTests
{
    private static string TempConfig(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mmx-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Read_ApiKeyField_ReturnsTokenAndRegion()
    {
        string path = TempConfig("""
            {"api_key":"sk-test123","region":"cn"}
            """);
        try
        {
            MmxCredential? cred = MmxConfigReader.Read(path);
            Assert.NotNull(cred);
            Assert.Equal("sk-test123", cred!.Token);
            Assert.Equal("cn", cred.Region);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Read_OAuthFallback_UsesAccessToken()
    {
        // api_key absent → falls back to oauth.access_token.
        string path = TempConfig("""
            {"region":"global","oauth":{"access_token":"oauth-tok-456","refresh_token":"r"}}
            """);
        try
        {
            MmxCredential? cred = MmxConfigReader.Read(path);
            Assert.NotNull(cred);
            Assert.Equal("oauth-tok-456", cred!.Token);
            Assert.Equal("global", cred.Region);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Read_ApiKeyTakesPrecedenceOverOAuth()
    {
        // Both present → api_key wins (matches mmx's own resolution order).
        string path = TempConfig("""
            {"api_key":"sk-preferred","oauth":{"access_token":"oauth-fallback"}}
            """);
        try
        {
            MmxCredential? cred = MmxConfigReader.Read(path);
            Assert.NotNull(cred);
            Assert.Equal("sk-preferred", cred!.Token);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Read_NoCredentials_ReturnsNull()
    {
        string path = TempConfig("""
            {"region":"global"}
            """);
        try
        {
            Assert.Null(MmxConfigReader.Read(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Read_MissingRegion_DefaultsToGlobal()
    {
        string path = TempConfig("""
            {"api_key":"sk-x"}
            """);
        try
        {
            MmxCredential? cred = MmxConfigReader.Read(path);
            Assert.NotNull(cred);
            Assert.Equal("global", cred!.Region);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Read_FileMissing_ReturnsNull()
    {
        string path = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json");
        Assert.Null(MmxConfigReader.Read(path));
    }

    [Fact]
    public void Read_MalformedJson_ReturnsNull()
    {
        // Not valid JSON — reader swallows JsonException and returns null.
        string path = TempConfig("{ this is not json");
        try
        {
            Assert.Null(MmxConfigReader.Read(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Read_EmptyCredentialStrings_ReturnsNull()
    {
        // Whitespace-only api_key + oauth token should be treated as "no key".
        string path = TempConfig("""
            {"api_key":"   ","oauth":{"access_token":""}}
            """);
        try
        {
            Assert.Null(MmxConfigReader.Read(path));
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
