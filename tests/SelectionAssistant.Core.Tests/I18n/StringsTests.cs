using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SelectionAssistant.Core.I18n;
using Xunit;

namespace SelectionAssistant.Core.Tests.I18n;

public sealed class StringsTests
{
    /// <summary>
    /// Every public property on <see cref="Strings"/> (each one is a
    /// per-key accessor like <c>Toolbar_Translate</c>) must resolve to a
    /// real translated value in the CURRENT dictionary, never the missing-
    /// key fallback (which would return the property name verbatim).
    /// </summary>
    /// <remarks>
    /// This is the single most valuable i18n test: a typo in the property
    /// name, a forgotten entry, or a renamed key all surface here as a hard
    /// test failure before the build can ship. Without it, a missing string
    /// silently renders as the key text in production.
    /// </remarks>
    [Fact]
    public void EveryProperty_ResolvesToTranslatedValue()
    {
        PropertyInfo[] properties = typeof(Strings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.GetMethod is not null && p.GetMethod.IsPublic)
            .ToArray();

        Assert.NotEmpty(properties);

        string[] missing = properties
            .Select(p => (name: p.Name, value: (string)p.GetValue(null)!))
            .Where(kv => kv.value == kv.name)  // Get() returns the key on miss
            .Select(kv => kv.name)
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The English and Chinese dictionaries must contain exactly the same
    /// key set. A key present in one but missing in the other is a bug —
    /// whichever language is active renders it as the key text, and the
    /// other language never sees the translation.
    /// </summary>
    [Fact]
    public void EnglishAndChineseDictionaries_HaveSameKeys()
    {
        HashSet<string> en = Strings.GetEnglishKeys().ToHashSet();
        HashSet<string> zh = Strings.GetChineseKeys().ToHashSet();

        Assert.NotEmpty(en);
        Assert.NotEmpty(zh);
        Assert.Equal(en.Count, zh.Count);
        Assert.Superset(en, zh);
        Assert.Superset(zh, en);
    }

    /// <summary>
    /// Every public property name on <see cref="Strings"/> must be a real
    /// key in BOTH dictionaries. Catches a property added without a
    /// corresponding dictionary entry (which would otherwise render as the
    /// property name in one of the languages).
    /// </summary>
    [Fact]
    public void EveryProperty_HasEntryInBothDictionaries()
    {
        HashSet<string> propertyNames = typeof(Strings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.GetMethod is not null && p.GetMethod.IsPublic)
            .Select(p => p.Name)
            .ToHashSet();

        HashSet<string> en = Strings.GetEnglishKeys().ToHashSet();
        HashSet<string> zh = Strings.GetChineseKeys().ToHashSet();

        string[] missingInEn = propertyNames.Except(en).ToArray();
        string[] missingInZh = propertyNames.Except(zh).ToArray();
        Assert.Empty(missingInEn);
        Assert.Empty(missingInZh);
    }

    [Fact]
    public void Get_MissingKey_ReturnsKeyItself()
    {
        // Documented contract — surfaces a forgotten key as its own name
        // rather than a blank. Random GUID guarantees a miss.
        string key = $"__Missing_{System.Guid.NewGuid():N}";
        Assert.Equal(key, Strings.Get(key));
    }

    [Fact]
    public void Get_KnownKey_ReturnsNonKeyValue()
    {
        // Sanity: a known key must NOT return the key itself. Pick a key
        // guaranteed to exist by EveryProperty_HasEntryInBothDictionaries.
        string value = Strings.Common_Cancel;
        Assert.NotEqual(nameof(Strings.Common_Cancel), value);
        Assert.False(string.IsNullOrEmpty(value));
    }
}
