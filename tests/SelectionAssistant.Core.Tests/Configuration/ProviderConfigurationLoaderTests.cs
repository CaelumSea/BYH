using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class ProviderConfigurationLoaderTests
{
    private static string WriteTemp(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"byh-providers-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void MissingFile_ReturnsEmptyConfiguration()
    {
        var config = ProviderConfigurationLoader.LoadIfExists(
            Path.Combine(Path.GetTempPath(), "definitely-does-not-exist.json"));

        Assert.Null(config.DefaultProviderId);
        Assert.Empty(config.Providers);
    }

    [Fact]
    public void ValidFile_ParsesAllFields()
    {
        string json = """
        {
          "schemaVersion": 1,
          "defaultProviderId": "deepseek",
          "providers": [{
            "id": "deepseek",
            "name": "DeepSeek",
            "baseUrl": "https://api.deepseek.com/v1",
            "apiKeyReference": "secret://provider/deepseek",
            "defaultModel": "deepseek-chat",
            "chatCompletionsPath": "chat/completions",
            "timeoutSeconds": 90,
            "maxSourceCharacters": 4000
          }]
        }
        """;
        string path = WriteTemp(json);
        try
        {
            var config = ProviderConfigurationLoader.LoadIfExists(path);

            Assert.Equal("deepseek", config.DefaultProviderId);
            Assert.Single(config.Providers);
            ProviderProfileEntry p = config.Providers[0];
            Assert.Equal("deepseek", p.Id);
            Assert.Equal("DeepSeek", p.Name);
            Assert.Equal("https://api.deepseek.com/v1", p.BaseUrl);
            Assert.Equal("secret://provider/deepseek", p.ApiKeyReference);
            Assert.Equal("deepseek-chat", p.DefaultModel);
            Assert.Equal(90, p.TimeoutSeconds);
            Assert.Equal(4000, p.MaxSourceCharacters);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DefaultsApplied_WhenOptionalFieldsAbsent()
    {
        string json = """
        {
          "schemaVersion": 1,
          "providers": [{
            "id": "ollama",
            "name": "Local Ollama",
            "baseUrl": "http://127.0.0.1:11434/v1",
            "defaultModel": "qwen3:8b"
          }]
        }
        """;
        string path = WriteTemp(json);
        try
        {
            var config = ProviderConfigurationLoader.LoadIfExists(path);

            ProviderProfileEntry p = config.Providers[0];
            Assert.Null(p.ApiKeyReference); // local, no auth
            Assert.Equal("chat/completions", p.ChatCompletionsPath); // default
            Assert.Equal(60, p.TimeoutSeconds); // default
            Assert.Equal(8000, p.MaxSourceCharacters); // default
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WrongSchemaVersion_Throws()
    {
        string json = """{"schemaVersion": 99, "providers": []}""";
        string path = WriteTemp(json);
        try
        {
            Assert.Throws<ProviderConfigurationException>(() =>
                ProviderConfigurationLoader.LoadIfExists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InvalidJson_Throws()
    {
        string path = WriteTemp("{ not valid json");
        try
        {
            Assert.Throws<ProviderConfigurationException>(() =>
                ProviderConfigurationLoader.LoadIfExists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingProvidersArray_Throws()
    {
        string json = """{"schemaVersion": 1}""";
        string path = WriteTemp(json);
        try
        {
            Assert.Throws<ProviderConfigurationException>(() =>
                ProviderConfigurationLoader.LoadIfExists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SystemPrompt_Parsed_LegacyThinkingKeyIgnored()
    {
        // A legacy providers.json that still carries thinkingEnabled (the key
        // moved to prompt templates) must load without error and simply ignore
        // the now-obsolete key — the provider entry has no ThinkingEnabled field.
        string json = """
        {
          "schemaVersion": 1,
          "providers": [{
            "id": "custom",
            "name": "Custom",
            "baseUrl": "https://example.com/v1",
            "defaultModel": "m",
            "systemPrompt": "Explain this code.",
            "thinkingEnabled": true
          }]
        }
        """;
        string path = WriteTemp(json);
        try
        {
            var config = ProviderConfigurationLoader.LoadIfExists(path);
            ProviderProfileEntry p = config.Providers[0];
            Assert.Equal("Explain this code.", p.SystemPrompt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OmittedPromptFields_DefaultToNull()
    {
        string json = """
        {
          "schemaVersion": 1,
          "providers": [{
            "id": "deepseek",
            "name": "DeepSeek",
            "baseUrl": "https://api.deepseek.com",
            "defaultModel": "deepseek-v4-flash"
          }]
        }
        """;
        string path = WriteTemp(json);
        try
        {
            var config = ProviderConfigurationLoader.LoadIfExists(path);
            ProviderProfileEntry p = config.Providers[0];
            Assert.Null(p.SystemPrompt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_RoundTripsSystemPrompt()
    {
        var original = new ProviderConfiguration(null, new[]
        {
            new ProviderProfileEntry(
                "custom", "Custom", "https://example.com/v1", null, "m",
                "chat/completions", 60, 8000,
                SystemPrompt: "Summarize this."),
        });

        string path = Path.Combine(Path.GetTempPath(), $"byh-rt-{Guid.NewGuid():N}.json");
        try
        {
            ProviderConfigurationLoader.Save(original, path);
            var loaded = ProviderConfigurationLoader.LoadIfExists(path);

            ProviderProfileEntry p = loaded.Providers[0];
            Assert.Equal("Summarize this.", p.SystemPrompt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_OmitsPromptFieldsWhenDefault()
    {
        // A provider with no custom prompt/thinking should not write those keys,
        // so the file stays backward-compatible / minimal.
        var original = new ProviderConfiguration(null, new[]
        {
            new ProviderProfileEntry(
                "deepseek", "DeepSeek", "https://api.deepseek.com", null,
                "deepseek-v4-flash", "chat/completions", 60, 8000),
        });

        string path = Path.Combine(Path.GetTempPath(), $"byh-min-{Guid.NewGuid():N}.json");
        try
        {
            ProviderConfigurationLoader.Save(original, path);
            string contents = File.ReadAllText(path);

            Assert.DoesNotContain("systemPrompt", contents);
            Assert.DoesNotContain("thinkingEnabled", contents);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("https://api.example.com/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://localhost:8080/v1")]
    public void Validate_AcceptsAbsoluteHttpBaseUrls(string baseUrl)
    {
        var entry = new ProviderProfileEntry(
            "custom-test", "Custom", baseUrl, "secret://provider/custom-test",
            "test-model", "chat/completions", 60, 8000);

        entry.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://")]
    [InlineData("api.example.com/v1")]
    [InlineData("file:///C:/provider")]
    public void Validate_RejectsIncompleteOrNonHttpBaseUrls(string baseUrl)
    {
        var entry = new ProviderProfileEntry(
            "custom-test", "Custom", baseUrl, "secret://provider/custom-test",
            "test-model", "chat/completions", 60, 8000);

        Assert.Throws<ArgumentException>(() => entry.Validate());
    }
}
