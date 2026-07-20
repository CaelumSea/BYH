namespace SelectionAssistant.Core.Launcher;

/// <summary>
/// One user-defined quick-launch entry. <see cref="Id"/> is the stable key
/// persisted to <c>launcher-entries.json</c> (always <c>launcher-*</c>);
/// <see cref="Name"/> is the display label shown in QuickTools;
/// <see cref="Target"/> is either a local executable path (when
/// <see cref="Kind"/> == <see cref="LauncherKind.LocalApp"/>) or a web URL
/// (when <see cref="Kind"/> == <see cref="LauncherKind.WebUrl"/>).
/// <para>
/// <see cref="Arguments"/> may contain placeholders that are expanded at
/// launch time by <see cref="ParameterReplace"/>:
/// <list type="bullet">
///   <item><c>{clip}</c> — replaced with current clipboard text</item>
///   <item><c>{sel}</c> — replaced with current selected text</item>
///   <item><c>{prompt:提示语}</c> — runtime input dialog shown to the user</item>
/// </list>
/// </para>
/// <para>
/// Unlike <c>PromptTemplate</c>, there are no built-in launcher entries —
/// every entry is user-added and uses the <c>launcher-</c> id prefix.
/// </para>
/// </summary>
public sealed record LauncherEntry(
    string Id,
    string Name,
    LauncherKind Kind,
    string Target,
    string Arguments = "",
    string WorkingDirectory = "",
    string IconOverride = "")
{
    /// <summary>
    /// True when <see cref="Arguments"/> contains at least one
    /// <c>{prompt:...}</c> placeholder, meaning launch must prompt the user
    /// before running.
    /// </summary>
    public bool NeedsPromptParameter =>
        Arguments.Contains("{prompt:", StringComparison.Ordinal);
}

/// <summary>Whether a launcher entry starts a local app or opens a web URL.</summary>
public enum LauncherKind
{
    /// <summary>Launch a local executable via Process.Start.</summary>
    LocalApp = 0,

    /// <summary>Open a URL via the system default browser (UseShellExecute=true).</summary>
    WebUrl = 1,
}

/// <summary>
/// Stable id conventions for launcher entries. Every id uses
/// <see cref="CustomPrefix"/> — there are no built-in entries.
/// </summary>
public static class LauncherEntryIds
{
    /// <summary>Prefix for all launcher entry ids (e.g. "launcher-a1b2c3d4").</summary>
    public const string CustomPrefix = "launcher-";

    /// <summary>True if the id is a launcher entry (starts with the prefix).</summary>
    public static bool IsLauncher(string id) =>
        id.StartsWith(CustomPrefix, StringComparison.Ordinal);
}

/// <summary>
/// The full ordered set of user launcher entries. All entries are user-added
/// (no built-ins), in the order the user arranged them via the settings UI
/// (move up/down). Persisted as <c>launcher-entries.json</c>.
/// </summary>
public sealed class LauncherEntrySet
{
    private readonly List<LauncherEntry> _entries;

    public LauncherEntrySet()
    {
        _entries = new List<LauncherEntry>();
    }

    /// <summary>Used by the store to build a set from loaded entries.</summary>
    private LauncherEntrySet(List<LauncherEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>The ordered list of entries (display order).</summary>
    public IReadOnlyList<LauncherEntry> Entries => _entries;

    /// <summary>Finds the entry for an id, or null if not present.</summary>
    public LauncherEntry? Find(string id) =>
        _entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Adds a new entry. The id must use the <c>launcher-</c> prefix and not
    /// already exist. Returns false if the id is missing the prefix or is a
    /// duplicate.
    /// </summary>
    public bool Add(LauncherEntry entry)
    {
        if (!LauncherEntryIds.IsLauncher(entry.Id))
        {
            return false;
        }
        if (Find(entry.Id) is not null)
        {
            return false;
        }
        _entries.Add(entry);
        return true;
    }

    /// <summary>
    /// Replaces the entry with the same id. Returns false if the id is not
    /// present (use <see cref="Add"/> for new entries).
    /// </summary>
    public bool Update(LauncherEntry entry)
    {
        int index = _entries.FindIndex(e => e.Id == entry.Id);
        if (index < 0)
        {
            return false;
        }
        _entries[index] = entry;
        return true;
    }

    /// <summary>Removes the entry with the given id. Returns true if removed.</summary>
    public bool Remove(string id)
    {
        int index = _entries.FindIndex(e => e.Id == id);
        if (index < 0)
        {
            return false;
        }
        _entries.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Moves the entry with the given id by <paramref name="delta"/> positions
    /// (negative = up, positive = down). Clamped to list bounds. Returns false
    /// if the id was not found; returns true (no-op) if the move would be a
    /// no-op due to clamping.
    /// </summary>
    public bool Move(string id, int delta)
    {
        if (delta == 0)
        {
            return Find(id) is not null;
        }
        int index = _entries.FindIndex(e => e.Id == id);
        if (index < 0)
        {
            return false;
        }
        int newIndex = Math.Clamp(index + delta, 0, _entries.Count - 1);
        if (newIndex == index)
        {
            return true;
        }
        (_entries[index], _entries[newIndex]) = (_entries[newIndex], _entries[index]);
        return true;
    }

    /// <summary>Snapshot list, in display order.</summary>
    public IReadOnlyList<LauncherEntry> AsList() => _entries;

    /// <summary>
    /// Builds a new set from the given entries (used by the store when
    /// loading). The supplied list is copied.
    /// </summary>
    public static LauncherEntrySet FromList(IEnumerable<LauncherEntry> entries)
    {
        var result = new List<LauncherEntry>(entries.Where(e => LauncherEntryIds.IsLauncher(e.Id)));
        // Deduplicate by id, keeping the first occurrence (file order).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        result.RemoveAll(e => !seen.Add(e.Id));
        return new LauncherEntrySet(result);
    }
}

/// <summary>
/// Factory for the default (empty) launcher set. The store returns a fresh
/// copy when <c>launcher-entries.json</c> is missing or corrupt.
/// </summary>
public static class LauncherEntryDefaults
{
    public static LauncherEntrySet CreateDefault() => new();
}
