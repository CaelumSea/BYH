using SelectionAssistant.Core.I18n;

namespace SelectionAssistant.Core.Translation;

/// <summary>
/// A user-visible name that can vary by UI language. Used by user-created
/// custom prompt actions (e.g. "润色" / "Polish") so the action name follows
/// the active UI language instead of being locked to whatever the user typed
/// when they created it.
/// </summary>
/// <remarks>
/// <b>Why a dedicated type?</b> Before this existed, <see cref="PromptTemplate.Name" />
/// was a plain <c>string</c> persisted verbatim to <c>prompt-templates.json</c>.
/// A user who created a custom action under the Chinese UI got a Chinese-only
/// name that leaked into the English UI (and vice versa). Carrying both
/// language variants on the record lets the consumer ask for the right one
/// via <see cref="Current" />.
/// <para>
/// <b>Fallback policy.</b> If the active language's field is empty, the other
/// language's field is returned; if both are empty, <paramref name="fallback" />
/// (typically the action id) is returned. This keeps a half-filled name
/// visible instead of blank.
/// </para>
/// <para>
/// <b>AOT / trim safety.</b> <see cref="Current" /> reads only the static
/// <see cref="AppLanguage.Current" /> field — no reflection, no dispatch.
/// </para>
/// <para>
/// <b>Implicit conversion from string.</b> Lets existing call sites
/// (<c>new PromptTemplate(id, "润色", prompt)</c>) keep compiling when the
/// record's <c>Name</c> type changes from <c>string</c> to
/// <see cref="LocalizedName" />; the string becomes a name with both
/// <see cref="Zh" /> and <see cref="En" /> set to the same value (the legacy
/// single-string behavior, which is exactly how pre-v2 JSON files load).
/// </para>
/// </remarks>
public sealed record LocalizedName
{
    /// <summary>The Chinese (zh-CN / zh-Hans / …) variant. May be empty.</summary>
    public string Zh { get; init; }

    /// <summary>The English (en) variant. May be empty.</summary>
    public string En { get; init; }

    /// <summary>Creates a name with both variants set to the same string.</summary>
    public LocalizedName(string both) : this(both, both) { }

    /// <summary>Creates a name with the given Chinese and English variants.</summary>
    public LocalizedName(string zh, string en)
    {
        Zh = zh ?? string.Empty;
        En = en ?? string.Empty;
    }

    /// <summary>Parameterless ctor for record <c>with</c> expressions / serializers.</summary>
    public LocalizedName() : this(string.Empty, string.Empty) { }

    /// <summary>
    /// The variant for the currently-active UI language, with fallback:
    /// active-language field → other-language field → <paramref name="fallback" />
    /// (defaults to empty string). Read by display sites (result window,
    /// settings list) that want a single localized string.
    /// </summary>
    public string Current(string fallback = "") =>
        AppLanguage.Current.IsChinese
            ? (string.IsNullOrEmpty(Zh) ? (string.IsNullOrEmpty(En) ? fallback : En) : Zh)
            : (string.IsNullOrEmpty(En) ? (string.IsNullOrEmpty(Zh) ? fallback : Zh) : En);

    /// <summary>
    /// Builds a <see cref="LocalizedName" /> from a single string, setting
    /// both variants to the same value. Used by the implicit converter and
    /// by the JSON loader for legacy single-<c>name</c> entries.
    /// </summary>
    public static LocalizedName FromString(string value) => new(value, value);

    /// <summary>
    /// Implicit conversion so callers can pass a plain string where a
    /// <see cref="LocalizedName" /> is expected; both variants get the same
    /// value (legacy single-string behavior). See class remarks.
    /// </summary>
    public static implicit operator LocalizedName(string value) => FromString(value);
}
