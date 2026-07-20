namespace SelectionAssistant.Core.Translation;

/// <summary>
/// One named prompt template. <see cref="Id" /> is the stable key persisted to
/// <c>prompt-templates.json</c> (one of <see cref="PromptActionIds" /> for the
/// built-in actions, or a <c>custom-*</c> id for user-added actions);
/// <see cref="Name" /> is the display label; <see cref="Prompt" /> is the actual
/// system message sent to the model.
/// <para>
/// <see cref="ThinkingEnabled" /> is the single source of truth for whether the
/// model may reason before answering for this action. It lives here — not on
/// the provider — so the same provider behaves differently per action (e.g.
/// "explain" thinks, "translate" doesn't). Defaults to <c>false</c>.
/// </para>
/// <para>
/// 架构与数据流详见 <c>docs/architecture/04-prompt-templates.md</c>。
/// </para>
/// </summary>
public sealed record PromptTemplate(
    string Id,
    string Name,
    string Prompt,
    bool ThinkingEnabled = false,
    string? Shortcut = null);

/// <summary>
/// Stable action identifiers for the three built-in prompt templates. These
/// never change — they are the keys persisted to <c>prompt-templates.json</c>
/// and are always present in a <see cref="PromptTemplateSet" />. User-added
/// custom actions use ids with the <see cref="CustomPrefix" />.
/// </summary>
public static class PromptActionIds
{
    public const string Translate = "translate";
    public const string Summarize = "summarize";
    public const string Explain = "explain";

    /// <summary>Prefix for user-added custom action ids (e.g. "custom-a1b2c3d4").</summary>
    public const string CustomPrefix = "custom-";

    /// <summary>True if the id is one of the three built-in actions.</summary>
    public static bool IsBuiltIn(string actionId) =>
        actionId == Translate || actionId == Summarize || actionId == Explain;

    /// <summary>True if the id is a user-added custom action (starts with the prefix).</summary>
    public static bool IsCustom(string actionId) =>
        actionId.StartsWith(CustomPrefix, StringComparison.Ordinal);
}

/// <summary>
/// The full set of prompt templates — three built-in actions (translate /
/// summarize / explain) plus any number of user-added custom actions, all in a
/// single ordered list. All providers share this single global set; a template
/// change applies to every provider immediately. The built-in three always come
/// first in canonical order; custom actions follow in insertion order.
/// </summary>
public sealed class PromptTemplateSet
{
    private readonly List<PromptTemplate> _templates;

    public PromptTemplateSet()
    {
        // Default single-character toolbar shortcuts, picked from the pinyin
        // initials of each action so they are easy to remember: F = Fānyì
        // (翻译), J = Jiěshì (解释), Z = Zǒngjié (总结). They fire while the
        // selection toolbar is visible and are configurable per template.
        _templates =
        [
            new PromptTemplate(
                PromptActionIds.Translate,
                "翻译",
                "你是翻译器。把用户提供的文本翻译成简体中文。只输出译文，不要解释、不要添加说明。",
                Shortcut: "F"),
            new PromptTemplate(
                PromptActionIds.Summarize,
                "总结",
                "用一段话总结以下内容，只输出总结，不要额外说明。",
                Shortcut: "Z"),
            new PromptTemplate(
                PromptActionIds.Explain,
                "解释",
                "解释以下内容，用简洁易懂的语言，只输出解释。",
                Shortcut: "J"),
        ];
    }

    /// <summary>
    /// Used by the store to build a set from loaded entries. The supplied list
    /// is copied; the caller's list is not retained.
    /// </summary>
    private PromptTemplateSet(List<PromptTemplate> templates)
    {
        _templates = templates;
    }

    /// <summary>The ordered list of all templates (built-in first, then custom).</summary>
    public IReadOnlyList<PromptTemplate> Templates => _templates;

    // ── Convenience accessors for the three built-in actions ──
    // Kept for backward compatibility with existing callers / tests.
    public PromptTemplate Translate => Find(PromptActionIds.Translate)!;
    public PromptTemplate Summarize => Find(PromptActionIds.Summarize)!;
    public PromptTemplate Explain => Find(PromptActionIds.Explain)!;

    /// <summary>Returns the template for an action id, or null if not present.</summary>
    public PromptTemplate? Find(string actionId) =>
        _templates.FirstOrDefault(t => t.Id == actionId);

    /// <summary>
    /// Returns the template bound to a single-character toolbar shortcut (e.g.
    /// <c>"F"</c>), or null if no template uses that key. Comparison is
    /// ordinal-ignore-case so 'f' and 'F' both match. Used by the low-level
    /// keyboard hook to translate a pressed key into an action id.
    /// </summary>
    public PromptTemplate? FindByShortcut(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        return _templates.FirstOrDefault(t =>
            !string.IsNullOrEmpty(t.Shortcut) &&
            string.Equals(t.Shortcut, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Replaces the prompt for an existing action id, preserving its current
    /// <see cref="PromptTemplate.ThinkingEnabled" />. Returns false if the action
    /// id is not present.
    /// </summary>
    public bool TrySet(string actionId, string prompt)
    {
        PromptTemplate? existing = Find(actionId);
        if (existing is null)
        {
            return false;
        }
        int index = _templates.IndexOf(existing);
        _templates[index] = existing with { Prompt = prompt };
        return true;
    }

    /// <summary>
    /// Replaces both the prompt and the thinking-mode flag for an existing
    /// action id in one write. Returns false if the action id is not present.
    /// Used by the edit window, which saves both fields together.
    /// </summary>
    public bool TrySet(string actionId, string prompt, bool thinkingEnabled)
    {
        PromptTemplate? existing = Find(actionId);
        if (existing is null)
        {
            return false;
        }
        int index = _templates.IndexOf(existing);
        _templates[index] = existing with { Prompt = prompt, ThinkingEnabled = thinkingEnabled };
        return true;
    }

    /// <summary>
    /// Replaces the prompt, thinking-mode flag, and single-character toolbar
    /// shortcut for an existing action id in one write. Pass <c>null</c> or
    /// empty for <paramref name="shortcut" /> to clear the binding. Returns
    /// false if the action id is not present. Used by the edit window's save
    /// flow (R34), which commits all three fields together.
    /// </summary>
    public bool TrySet(string actionId, string prompt, bool thinkingEnabled, string? shortcut)
    {
        PromptTemplate? existing = Find(actionId);
        if (existing is null)
        {
            return false;
        }
        string? normalized = string.IsNullOrWhiteSpace(shortcut) ? null : shortcut.Trim().ToUpperInvariant();
        int index = _templates.IndexOf(existing);
        _templates[index] = existing with
        {
            Prompt = prompt,
            ThinkingEnabled = thinkingEnabled,
            Shortcut = normalized,
        };
        return true;
    }

    /// <summary>
    /// Adds a user custom action. The id must use the <c>custom-</c> prefix and
    /// not already exist. Returns false if the id is missing the prefix or is a
    /// duplicate (built-in actions cannot be added).
    /// </summary>
    public bool Add(PromptTemplate template)
    {
        if (!PromptActionIds.IsCustom(template.Id))
        {
            return false;
        }
        if (Find(template.Id) is not null)
        {
            return false;
        }
        _templates.Add(template);
        return true;
    }

    /// <summary>
    /// Removes a user custom action. Built-in actions (translate/summarize/
    /// explain) cannot be removed — returns false. Returns true if a custom
    /// action was removed.
    /// </summary>
    public bool Remove(string actionId)
    {
        if (PromptActionIds.IsBuiltIn(actionId))
        {
            return false;
        }
        PromptTemplate? existing = Find(actionId);
        if (existing is null)
        {
            return false;
        }
        return _templates.Remove(existing);
    }

    /// <summary>Snapshot list, in display order (built-in first, then custom).</summary>
    public IReadOnlyList<PromptTemplate> AsList() => _templates;

    /// <summary>
    /// Creates a deep copy — a new set with the same templates in the same order.
    /// Used by the store when building a set from parsed entries.
    /// </summary>
    public static PromptTemplateSet FromList(IEnumerable<PromptTemplate> templates)
    {
        // Ensure the three built-ins are always present; merge any custom ones after.
        var builtIns = new PromptTemplateSet();
        var result = new List<PromptTemplate>(builtIns._templates);
        foreach (PromptTemplate t in templates)
        {
            int existing = result.FindIndex(x => x.Id == t.Id);
            if (existing >= 0)
            {
                // Loaded entry overrides the built-in defaults. But preserve
                // the built-in shortcut when the loaded entry has none — a
                // legacy prompt-templates.json written before shortcuts existed
                // has no `shortcut` field and would otherwise lose F/J/Z.
                PromptTemplate merged = string.IsNullOrEmpty(t.Shortcut) && !string.IsNullOrEmpty(result[existing].Shortcut)
                    ? t with { Shortcut = result[existing].Shortcut }
                    : t;
                result[existing] = merged;
            }
            else
            {
                result.Add(t);          // custom action
            }
        }
        return new PromptTemplateSet(result);
    }
}

/// <summary>
/// Factory for the built-in default template set. The store returns a fresh
/// copy of this when <c>prompt-templates.json</c> is missing or corrupt.
/// </summary>
public static class PromptTemplateDefaults
{
    public static PromptTemplateSet CreateDefault() => new();
}
