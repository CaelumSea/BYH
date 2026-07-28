using SelectionAssistant.Core.Translation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Translation;

/// <summary>
/// Guards the built-in provider preset list — the source of truth for the
/// "＋ Add Provider" menu. Pins the count and the new R26 entries (MiniMax,
/// MiMo, OpenRouter, OpenCode Go) so a future edit can't silently drop a
/// vendor or break the (id, baseUrl, defaultModel, chatPath) invariants.
/// </summary>
public sealed class ProviderPresetsTests
{
    [Fact]
    public void BuiltIn_HasNineEntries_AfterR26Additions()
    {
        // 5 originals (DeepSeek/SiliconFlow/OpenAI/Zhipu/Moonshot)
        // + 4 R26 additions (MiniMax/MiMo/OpenRouter/OpenCode Go).
        Assert.Equal(9, ProviderPresets.BuiltIn.Count);
    }

    [Fact]
    public void BuiltIn_AllFieldsAreNonEmpty()
    {
        foreach (ProviderPreset p in ProviderPresets.BuiltIn)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Id), $"preset {p.Name} has empty Id");
            Assert.False(string.IsNullOrWhiteSpace(p.Name), $"preset {p.Id} has empty Name");
            Assert.False(string.IsNullOrWhiteSpace(p.BaseUrl), $"preset {p.Id} has empty BaseUrl");
            Assert.False(string.IsNullOrWhiteSpace(p.DefaultModel), $"preset {p.Id} has empty DefaultModel");
            Assert.False(string.IsNullOrWhiteSpace(p.ChatCompletionsPath), $"preset {p.Id} has empty ChatCompletionsPath");
        }
    }

    [Fact]
    public void BuiltIn_AllIdsUnique()
    {
        string[] ids = ProviderPresets.BuiltIn.Select(p => p.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void BuiltIn_AllBaseUrlsAreHttps()
    {
        // Security invariant: never ship an http:// preset (key would transit
        // in cleartext). The client itself doesn't enforce HTTPS, so the
        // preset list is the guardrail for the default flow.
        foreach (ProviderPreset p in ProviderPresets.BuiltIn)
        {
            Assert.StartsWith("https://", p.BaseUrl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuiltIn_AllChatPathsAreChatCompletions()
    {
        // All current presets use the OpenAI chat-completions path. If a future
        // preset needs a different path (e.g. a Responses-API vendor), this
        // test should be updated intentionally, not silently.
        foreach (ProviderPreset p in ProviderPresets.BuiltIn)
        {
            Assert.Equal("chat/completions", p.ChatCompletionsPath);
        }
    }

    [Theory]
    [InlineData("deepseek")]
    [InlineData("siliconflow")]
    [InlineData("openai")]
    [InlineData("zhipu")]
    [InlineData("moonshot")]
    [InlineData("minimax")]
    [InlineData("mimo")]
    [InlineData("openrouter")]
    [InlineData("opencode-go")]
    public void BuiltIn_ContainsExpectedId(string expectedId)
    {
        Assert.Contains(ProviderPresets.BuiltIn, p => p.Id == expectedId);
    }

    [Fact]
    public void BuildSecretReference_UsesProviderUriScheme()
    {
        Assert.Equal("secret://provider/deepseek", ProviderPresets.BuildSecretReference("deepseek"));
        Assert.Equal("secret://provider/openrouter", ProviderPresets.BuildSecretReference("openrouter"));
    }

    [Fact]
    public void CustomPresetId_IsStable()
    {
        // The custom-entry menu item uses this id; renaming it would break
        // existing users' "custom" provider entries.
        Assert.Equal("custom", ProviderPresets.CustomPresetId);
    }

    [Fact]
    public void R26_AddedPresets_HaveCorrectBaseUrls()
    {
        // Pin the endpoint URLs we researched (anysearch + WebFetch on
        // 2026-07-26) so a typo regression is caught here, not at runtime.
        var byId = ProviderPresets.BuiltIn.ToDictionary(p => p.Id, p => p);

        Assert.Equal("https://api.minimaxi.com/v1", byId["minimax"].BaseUrl);
        Assert.Equal("https://api.xiaomimimo.com/v1", byId["mimo"].BaseUrl);
        Assert.Equal("https://openrouter.ai/api/v1", byId["openrouter"].BaseUrl);
        Assert.Equal("https://opencode.ai/zen/go/v1", byId["opencode-go"].BaseUrl);
    }

    [Fact]
    public void R26_OpenRouterDefaultModel_UsesProviderModelDoubleSegment()
    {
        // OpenRouter model ids are "provider/model" (e.g. deepseek/deepseek-chat).
        // Pin this so a future edit doesn't accidentally ship a single-segment
        // id that OpenRouter would reject with 404.
        var openrouter = ProviderPresets.BuiltIn.First(p => p.Id == "openrouter");
        Assert.Contains("/", openrouter.DefaultModel);
    }
}
