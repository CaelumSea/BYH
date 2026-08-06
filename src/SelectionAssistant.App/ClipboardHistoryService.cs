using System.Security.Cryptography;
using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Infrastructure.Configuration;
using SelectionAssistant.Infrastructure.Logging;
using SelectionAssistant.Platform.Abstractions.Secrets;
using SelectionAssistant.Platform.Windows.Capture;
using SelectionAssistant.Platform.Windows.Clipboard;

namespace SelectionAssistant.App;

/// <summary>
/// R54 background clipboard-history controller. Owns a long-lived
/// <see cref="Win32Clipboard"/> for the app lifetime, subscribes to
/// <c>WM_CLIPBOARDUPDATE</c>, dedups by clipboard sequence number, applies the
/// exclude-apps privacy filter (foreground-window process name), classifies the
/// text via <see cref="ClipboardClassifier"/>, and persists each accepted entry
/// through <see cref="ClipboardHistoryStore"/> with LRU eviction.
/// </summary>
/// <remarks>
/// Lives in the composition root (<c>SelectionAssistant.App</c>) because it
/// spans three layers: <see cref="Win32Clipboard"/> (Platform.Windows), the
/// store + logger (Infrastructure), and the entry/classifier models (Core). Same
/// placement as <c>SelectionRuntime</c>.
/// <para>
/// <b>Threading:</b> the change callback fires on the Win32Clipboard message
/// thread (<c>BYH.ClipboardMessages</c>). All mutation of <see cref="_entries"/>
/// and all store I/O happen under <see cref="_gate"/>. UI consumers read
/// <see cref="Snapshot"/> (which takes the lock and returns a copy). The service
/// is created once (app lifetime) and disposed on shutdown.
/// </para>
/// <para>
/// <b>Excluded apps:</b> when the foreground process name at change-time
/// matches <see cref="ClipboardHistorySettings.ExcludeProcessNames"/>
/// (case-insensitive substring), the capture is silently dropped — this is the
/// first line of defense for password managers (1Password/KeePass/…).
/// </para>
/// <para>
/// <b>Dedup:</b> the Win32 clipboard sequence number is read at change-time and
/// compared to <see cref="_lastSeenSequence"/>; an unchanged sequence (our own
/// <see cref="PasteAsync"/> write, or a duplicate notification) is ignored.
/// </para>
/// </remarks>
public sealed class ClipboardHistoryService : IDisposable
{
    private readonly Win32Clipboard _clipboard;
    private readonly string _historyPath;
    private readonly string _tagsPath;
    private readonly string _iconLibraryPath;
    private readonly string _imagesDirectory;
    private readonly string _archiveDirectory;
    private readonly RedactedLogger _logger;
    private readonly IClipboardEntryCipher? _entryCipher;
    private readonly object _gate = new();
    private List<ClipboardEntry> _entries;
    private ClipboardTagData _tags;
    private UserIconLibrary _iconLibrary;
    private ClipboardHistorySettings _settings;
    private uint _lastSeenSequence;
    // 抑制配额：PasteAsync 自写剪贴板时置 1；取词管线注入复制阶段通常预留 2
    // （兼容少数分阶段发布内容的应用），RestoreOriginalClipboard 单独预留 1
    // （一次 EmptyClipboard + SetClipboardData 事务只产生一个系统更新通知）。
    // OnClipboardChanged 每次 Interlocked.Decrement 消耗 1。int 用 Interlocked 操作，
    // 无需 _gate。
    private int _suppressNextChanges;
    private int _disposed;
    // R103: lazily-loaded archive cache. Null + _archiveLoaded=false means "not
    // loaded yet"; once loaded, stays for the service lifetime (archive is
    // append-only from this service's perspective — eviction adds, nothing
    // removes). Refresh after user actions does NOT reload (archive is read-only
    // to the UI). InvalidateArchiveCache forces a reload on next access.
    private List<ClipboardEntry>? _archiveCache;
    private bool _archiveLoaded;

    public ClipboardHistoryService(
        Win32Clipboard clipboard,
        string historyPath,
        string tagsPath,
        string iconLibraryPath,
        string imagesDirectory,
        ClipboardHistorySettings settings,
        RedactedLogger logger,
        IClipboardEntryCipher? entryCipher = null,
        string? archiveDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentException.ThrowIfNullOrWhiteSpace(historyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconLibraryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagesDirectory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _clipboard = clipboard;
        _historyPath = historyPath;
        _tagsPath = tagsPath;
        _iconLibraryPath = iconLibraryPath;
        _imagesDirectory = imagesDirectory;
        // R102: null falls back to a sibling "clipboard-archive" dir next to the
        // history file, so existing callers that don't pass this still get
        // archiving. Keep the param optional to avoid breaking App.axaml.cs
        // construction order if it ever forgets to wire it.
        _archiveDirectory = string.IsNullOrEmpty(archiveDirectory)
            ? Path.Combine(Path.GetDirectoryName(historyPath) ?? string.Empty, "clipboard-archive")
            : archiveDirectory;
        _settings = settings.Normalize();
        _logger = logger;
        _entryCipher = entryCipher;
        _entries = ClipboardHistoryStore.Load(historyPath, entryCipher);
        _tags = ClipboardTagStore.Load(tagsPath);
        _iconLibrary = UserIconLibraryStore.Load(iconLibraryPath);
        Directory.CreateDirectory(_imagesDirectory);
        Directory.CreateDirectory(_archiveDirectory);
        _lastSeenSequence = clipboard.GetSequenceNumber();

        // R54 v2 Phase 2: one-time eager migration. If a cipher is wired in,
        // the file is at an older schema (v1/v2 plaintext), AND there is at
        // least one sensitive entry, persist once now so the secret text is
        // encrypted at rest on disk before the app goes idle. Subsequent starts
        // see schemaVersion == CurrentSchemaVersion and skip this entirely (no
        // extra writes, no backup-software churn). The cost is a single Save
        // (5-50ms for ~50 sensitive entries); if there are no sensitive entries
        // at all, we still re-save to bump the schema version so the check is a
        // cheap integer compare on every future launch.
        if (entryCipher is not null &&
            ClipboardHistoryStore.ReadSchemaVersion(historyPath) < ClipboardHistoryStore.CurrentSchemaVersion)
        {
            // SubscribeChanges hasn't happened yet (below), so no other thread
            // can touch _entries — but we still take the lock to honor the
            // "TryPersist must run under _gate" contract uniformly.
            lock (_gate)
            {
                TryPersist();
            }
        }

        clipboard.SubscribeChanges(OnClipboardChanged);
        _logger.Info("ClipboardHistory", $"Started with {_entries.Count} entries, max={_settings.MaxEntries}, maxImages={_settings.MaxImageEntries}, tags={_tags.CustomTags.Count}, userIcons={_iconLibrary.Icons.Count}, encrypted={entryCipher is not null}, archive={_archiveDirectory}.");
    }

    /// <summary>Current settings (normalized). Replacing via
    /// <see cref="UpdateSettings"/> re-applies eviction under the new cap.</summary>
    public ClipboardHistorySettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    /// <summary>A point-in-time copy of the entries in display order (pinned
    /// first, then newest). Safe to hand to the UI thread.</summary>
    public IReadOnlyList<ClipboardEntry> Snapshot
    {
        get { lock (_gate) return ClipboardHistoryStore.OrderForDisplay(_entries); }
    }

    /// <summary>R103: lazily loads the archive (all monthly shards under
    /// <c>_archiveDirectory</c>) and returns a cached snapshot. Safe to call
    /// from any thread. The first call triggers <see cref="ClipboardArchiveStore.LoadAll"/>;
    /// subsequent calls return the cached list without disk I/O. Archive entries
    /// are text-only (images are never archived — see batch 102), so this list
    /// contains no image entries.</summary>
    /// <remarks><b>Cache lifetime</b>: the cache lives for the service lifetime
    /// (not the window lifetime). Rationale: archive is append-only from the
    /// service's view, and reloading on every window open would re-parse ~10MB
    /// of JSON for a 10k-entry history. If memory pressure ever matters, call
    /// <see cref="InvalidateArchiveCache"/>.</remarks>
    public IReadOnlyList<ClipboardEntry> ArchiveSnapshot
    {
        get
        {
            lock (_gate)
            {
                if (!_archiveLoaded)
                {
                    try
                    {
                        _archiveCache = ClipboardArchiveStore.LoadAll(_archiveDirectory, _entryCipher);
                        _logger.Info("ClipboardHistory",
                            $"Archive loaded: {_archiveCache.Count} entries from {_archiveDirectory}.");
                    }
                    catch (Exception ex)
                    {
                        // Never let archive load failure break the main clipboard
                        // flow — the live snapshot still works, just no archive
                        // search until the next invalidation.
                        _logger.Error("ClipboardHistory",
                            $"ArchiveLoad failed ({ex.GetType().Name}): {ex.Message}");
                        _archiveCache = new List<ClipboardEntry>();
                    }
                    _archiveLoaded = true;
                }
                return _archiveCache!;
            }
        }
    }

    /// <summary>R103: finds an entry by id, first in the live snapshot then in
    /// the archive cache. Returns null if not found in either. Used by App's
    /// paste/copy handlers so archived entries can be pasted back without the
    /// caller knowing whether the id is live or archived. The live lookup is
    /// under <c>_gate</c>; the archive lookup uses <see cref="ArchiveSnapshot"/>
    /// which has its own locking.</summary>
    public ClipboardEntry? FindEntryById(Guid id)
    {
        lock (_gate)
        {
            foreach (ClipboardEntry e in _entries)
            {
                if (e.Id == id) return e;
            }
        }
        foreach (ClipboardEntry e in ArchiveSnapshot)
        {
            if (e.Id == id) return e;
        }
        return null;
    }

    /// <summary>R103: forces the archive cache to reload on the next
    /// <see cref="ArchiveSnapshot"/> access. Call after explicit archive
    /// mutation (none exist today — archive is append-only via eviction — but
    /// kept for future delete-from-archive), or to reclaim memory if a long-
    /// running session accumulated a large cache that's no longer needed.</summary>
    public void InvalidateArchiveCache()
    {
        lock (_gate)
        {
            _archiveLoaded = false;
            _archiveCache = null;
        }
    }

    /// <summary>A point-in-time copy of the tag data (custom tags + assignments).
    /// Safe to hand to the UI thread.</summary>
    public ClipboardTagData Tags
    {
        get { lock (_gate) return _tags; }
    }

    /// <summary>R54 v1.2 v6: the user-imported icon library (SVG path-data).
    /// Safe to hand to the UI thread.</summary>
    public UserIconLibrary IconLibrary
    {
        get { lock (_gate) return _iconLibrary; }
    }

    /// <summary>True when an entry with the given id exists (pinned or not).</summary>
    public bool Contains(Guid id)
    {
        lock (_gate)
        {
            return _entries.Any(e => e.Id == id);
        }
    }

    /// <summary>
    /// Replaces the settings and re-applies eviction (in case MaxEntries
    /// decreased). The caller (App) is responsible for persisting the settings
    /// FILE; this method persists the history file when eviction actually
    /// occurred (R102: so the archived entries don't get re-archived on the
    /// next launch — without this, the in-memory shrink would be lost on
    /// restart and the same entries would be evicted-and-archived again).
    /// Thread-safe.
    /// </summary>
    public void UpdateSettings(ClipboardHistorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();
        lock (_gate)
        {
            _settings = settings;
            // R102: lowering MaxEntries is treated like LRU eviction — the
            // dropped text entries are archived (the user didn't ask to delete
            // them, they just shrunk the live window). EvictImagesToMax below
            // still handles its own image-file deletion as before.
            _entries = ClipboardHistoryStore.EvictToMax(
                _entries, settings.MaxEntries, out IReadOnlyList<ClipboardEntry> evicted);
            ArchiveEvictedTextEntries(evicted);
            // R54 v2: a reduced MaxImageEntries may need to evict images too.
            // EvictImagesToMax deletes the dropped PNGs (best-effort) itself.
            EvictImagesToMax();
            TryPersist();
        }
        _logger.Info("ClipboardHistory", $"Settings updated: enabled={settings.Enabled} max={settings.MaxEntries} maxImages={settings.MaxImageEntries} captureImages={settings.CaptureImagesEnabled} autoPaste={settings.AutoPasteEnabled}.");
    }

    /// <summary>
    /// Toggles the pinned flag on the entry with the given id. Returns the new
    /// pinned state, or null when the id is unknown. Persists immediately.
    /// </summary>
    public bool? TogglePin(Guid id)
    {
        lock (_gate)
        {
            int index = _entries.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                return null;
            }

            ClipboardEntry current = _entries[index];
            _entries[index] = current with { IsPinned = !current.IsPinned };
            bool newPinned = _entries[index].IsPinned;
            TryPersist();
            return newPinned;
        }
    }

    /// <summary>Removes the entry with the given id. Returns true when it
    /// existed. For image entries the PNG file is also deleted. Persists
    /// immediately.</summary>
    public bool Delete(Guid id)
    {
        string? imageToDelete;
        lock (_gate)
        {
            ClipboardEntry? found = _entries.FirstOrDefault(e => e.Id == id);
            imageToDelete = found?.ImageFileName;
            int removed = _entries.RemoveAll(e => e.Id == id);
            if (removed > 0)
            {
                TryPersist();
            }
            else
            {
                return false;
            }
        }
        // Delete outside the lock (disk I/O).
        DeleteImageFile(imageToDelete);
        return true;
    }

    /// <summary>Removes several live entries in one locked mutation and one
    /// history-file write. Unknown ids are ignored. Image files are deleted
    /// after releasing the lock, matching <see cref="Delete(Guid)"/>.</summary>
    public int DeleteMany(IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return 0;
        }

        var requested = new HashSet<Guid>(ids);
        var imagesToDelete = new List<string?>();
        int removed = 0;
        lock (_gate)
        {
            for (int index = _entries.Count - 1; index >= 0; index--)
            {
                ClipboardEntry entry = _entries[index];
                if (!requested.Contains(entry.Id))
                {
                    continue;
                }

                imagesToDelete.Add(entry.ImageFileName);
                _entries.RemoveAt(index);
                removed++;
            }

            if (removed > 0)
            {
                TryPersist();
            }
        }

        foreach (string? imageFileName in imagesToDelete)
        {
            DeleteImageFile(imageFileName);
        }
        return removed;
    }

    /// <summary>R54 v2: adds a free-form annotation tag (e.g. "AWS", "Stripe")
    /// to the entry with the given id. Independent of the custom-tag tab system
    /// — these tags are purely per-entry badges, never become nav tabs. Returns
    /// true when the tag was added (entry found, tag valid, not a duplicate, not
    /// exceeding the per-entry cap of <see cref="MaxEntryTagsPerEntry"/>).
    /// Returns false silently otherwise (no throw). Persists on success.</summary>
    /// <param name="tag">Raw tag text; trimmed inside. Whitespace-only or null
    /// is rejected.</param>
    public bool AddEntryTag(Guid id, string tag)
    {
        string trimmed = tag?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            // R99 Bug B 诊断: 空字符串被 UI 拦在前面(Commit 里 Length>0 检查),
            // 走到这里说明别的调用路径(测试/未来扩展)传了空.
            _logger.Info("ClipboardHistory", $"AddEntryTag: id={id} empty tag — ignored.");
            return false;
        }

        lock (_gate)
        {
            int index = _entries.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                // R99 Bug B 诊断: entry 不在内存列表(去重/清空已丢). 即便
                // Bug A 修好了 Enter 吞事件, 这里仍可能因 id 失效而加不上.
                _logger.Info("ClipboardHistory", $"AddEntryTag: id={id} not found — ignored.");
                return false;
            }

            ClipboardEntry current = _entries[index];
            // Already present (ordinal) = no-op, report false so the UI can skip.
            if (current.EntryTags.Any(t => string.Equals(t, trimmed, StringComparison.Ordinal)))
            {
                _logger.Info("ClipboardHistory", $"AddEntryTag: id={id} '{trimmed}' duplicate — skipped.");
                return false;
            }
            if (current.EntryTags.Count >= MaxEntryTagsPerEntry)
            {
                _logger.Info("ClipboardHistory", $"AddEntryTag: id={id} '{trimmed}' cap({MaxEntryTagsPerEntry}) reached — refused.");
                return false;
            }

            var next = new List<string>(current.EntryTags.Count + 1) { trimmed };
            next.AddRange(current.EntryTags); // newest first, matches badge order
            _entries[index] = current with { EntryTags = next };
            TryPersist();
            // R99 Bug B 诊断: 成功路径也记一条. 若用户反馈"加了但 UI 没显示",
            // 这条日志在但 badge 没出 → 问题在 RefreshClipboardHistoryWindow
            // 的 UI 刷新链路, 不在持久化.
            _logger.Info("ClipboardHistory", $"AddEntryTag: id={id} '{trimmed}' added (now {next.Count}).");
            return true;
        }
    }

    /// <summary>R54 v2: removes an annotation tag from the entry. Returns true
    /// when the tag was found and removed. Persists on success.</summary>
    public bool RemoveEntryTag(Guid id, string tag)
    {
        string trimmed = tag?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return false;

        lock (_gate)
        {
            int index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) return false;

            ClipboardEntry current = _entries[index];
            var next = current.EntryTags
                .Where(t => !string.Equals(t, trimmed, StringComparison.Ordinal))
                .ToList();
            if (next.Count == current.EntryTags.Count)
            {
                return false; // nothing removed
            }

            _entries[index] = current with { EntryTags = next };
            TryPersist();
            return true;
        }
    }

    /// <summary>R54 v2: cap on annotation tags per entry — guards against a
    /// runaway script or accidental bulk-paste bloating a single entry's badge
    /// row. 12 is comfortable headroom over typical 2-3 tag use.</summary>
    public const int MaxEntryTagsPerEntry = 12;

    /// <summary>R54 v2: sets the user's manual correction of the auto-classified
    /// group. Pass <paramref name="group"/> = null to revert to the automatic
    /// classification. <b>Sensitive is linked:</b> overriding <em>to</em>
    /// Sensitive sets <see cref="ClipboardEntry.IsSensitive"/> (so the entry is
    /// masked in the UI and DPAPI-encrypted at rest on the next persist);
    /// overriding <em>out of</em> Sensitive clears it. Images cannot be
    /// overridden (their group is structurally Text and they don't show in the
    /// text tabs) — a false result is returned in that case. Returns true when
    /// the entry was found and mutated, false when the id is unknown or the
    /// target is an image. Persists immediately.</summary>
    /// <param name="id">Entry id.</param>
    /// <param name="group">The new effective group, or null to revert to the
    /// automatic classification.</param>
    public bool SetGroupOverride(Guid id, ClipboardGroup? group)
    {
        lock (_gate)
        {
            int index = _entries.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                // R99 Bug B 诊断: 没找到 entry 说明 UI 传来的 id 已不在内存
                // 列表(可能被 AddAndEvict 去重丢弃, 或 ClearNonPinned 清掉).
                _logger.Info("ClipboardHistory", $"SetGroupOverride: id={id} not found — ignored.");
                return false;
            }
            ClipboardEntry current = _entries[index];
            // Images are structurally Text and never participate in text tabs —
            // refusing the override keeps the invariant that Kind==Image ⇒
            // GroupOverride==null (also enforced at WriteEntry write time).
            if (current.Kind == ClipboardEntryKind.Image)
            {
                _logger.Info("ClipboardHistory", $"SetGroupOverride: id={id} is image — override refused.");
                return false;
            }

            // Derive the new IsSensitive so the cipher/masking follow the user's
            // correction. The effective group is the override when set, else the
            // auto Group (matters for the "revert to auto" path: if the auto
            // classifier said Sensitive, the entry stays sensitive).
            ClipboardGroup effective = group ?? current.Group;
            bool newSensitive = effective == ClipboardGroup.Sensitive;
            _entries[index] = current with
            {
                GroupOverride = group,
                IsSensitive = newSensitive,
            };
            TryPersist();
            // R99 Bug B 诊断: 记录每次 override 的入参/旧值/新值. 若 TryPersist
            // 失败(TryPersist 内部 catch 只记日志不抛), 内存正确但磁盘 stale,
            // 重启后会恢复旧值 —— 这条日志配合下面 TryPersist 的 Error 日志可
            // 区分"操作没执行" vs "执行了但没落盘".
            _logger.Info("ClipboardHistory",
                $"SetGroupOverride: id={id} prevGroupOverride={current.GroupOverride} prevIsSensitive={current.IsSensitive} group={group} overrideSet={(group is null ? "cleared" : "set")} newSensitive={newSensitive}.");
            return true;
        }
    }

    /// <summary>Clears all non-pinned entries. Pinned entries are kept. PNG files
    /// of cleared image entries are deleted. Persists.</summary>
    public void ClearNonPinned()
    {
        List<string> imagesToDelete;
        lock (_gate)
        {
            imagesToDelete = _entries
                .Where(e => !e.IsPinned && !string.IsNullOrEmpty(e.ImageFileName))
                .Select(e => e.ImageFileName!)
                .ToList();
            _entries = _entries.Where(e => e.IsPinned).ToList();
            TryPersist();
        }
        foreach (string name in imagesToDelete)
        {
            DeleteImageFile(name);
        }
        _logger.Info("ClipboardHistory", "Cleared all non-pinned entries.");
    }

    /// <summary>R54 v2: clears every entry older than the one with the given id,
    /// except those the user has intentionally marked. Kept entries: pinned (★),
    /// favorited (❤), custom-tag-tab assignments, and per-entry annotation tags
    /// (EntryTags). The reference entry itself and anything newer survive. Uses
    /// strict <c>CapturedAt &lt;</c> so same-timestamp siblings of the reference
    /// are not swept. Returns the number of entries removed. Image PNGs of
    /// removed image entries are deleted off the lock. Persists.</summary>
    public int ClearOlderEntries(Guid referenceId)
    {
        List<string> imagesToDelete;
        int removed;
        lock (_gate)
        {
            ClipboardEntry? reference = _entries.FirstOrDefault(e => e.Id == referenceId);
            if (reference is null)
            {
                return 0;
            }
            DateTimeOffset threshold = reference.CapturedAt;

            // Partition: entries strictly older than the reference AND not
            // protected by any user marker are candidates for removal. Log each
            // candidate's protection verdict so a "it deleted too much" report
            // can be diagnosed from the log without guessing.
            var toDelete = new List<ClipboardEntry>();
            foreach (ClipboardEntry e in _entries)
            {
                if (e.CapturedAt >= threshold) continue;
                int assigned = _tags.Assignments.TryGetValue(e.Id, out IReadOnlySet<string>? set) ? set.Count : 0;
                bool prot = ClipboardHistoryStore.IsProtected(e, assigned);
                if (!prot)
                {
                    toDelete.Add(e);
                }
                else
                {
                    _logger.Info("ClipboardHistory",
                        $"ClearOlder: keeping protected entry {e.Id} (pinned={e.IsPinned}, entryTags={e.EntryTags.Count}, assignedTags={assigned}).");
                }
            }
            if (toDelete.Count == 0)
            {
                return 0;
            }

            imagesToDelete = toDelete
                .Where(e => !string.IsNullOrEmpty(e.ImageFileName))
                .Select(e => e.ImageFileName!)
                .ToList();

            var deleteIds = toDelete.Select(e => e.Id).ToHashSet();
            _entries = _entries.Where(e => !deleteIds.Contains(e.Id)).ToList();
            removed = toDelete.Count;
            TryPersist();
        }
        foreach (string name in imagesToDelete)
        {
            DeleteImageFile(name);
        }
        _logger.Info("ClipboardHistory", $"Cleared {removed} entries older than the selected one (protected entries kept).");
        return removed;
    }

    /// <summary>R54 v2: dry-run counterpart of <see cref="ClearOlderEntries"/>.
    /// Returns (wouldDelete, wouldKeep) counts WITHOUT modifying anything, so
    /// the UI can show a confirmation dialog ("delete N, keep M") before the
    /// destructive call. Same protection rule, same threshold semantics.</summary>
    public (int wouldDelete, int wouldKeep) PreviewClearOlder(Guid referenceId)
    {
        lock (_gate)
        {
            ClipboardEntry? reference = _entries.FirstOrDefault(e => e.Id == referenceId);
            if (reference is null)
            {
                // The UI's row id didn't match any live entry — usually means
                // the snapshot is stale (new capture replaced the list). Log it
                // so "no deletable entries" reports are diagnosable instead of
                // looking like a silent IsProtected failure.
                _logger.Info("ClipboardHistory",
                    $"PreviewClearOlder: reference id {referenceId} not found in {_entries.Count} entries — snapshot likely stale.");
                return (0, 0);
            }
            DateTimeOffset threshold = reference.CapturedAt;
            int del = 0, keep = 0;
            foreach (ClipboardEntry e in _entries)
            {
                if (e.CapturedAt >= threshold) continue; // newer-or-equal: untouched
                int assigned = _tags.Assignments.TryGetValue(e.Id, out IReadOnlySet<string>? set) ? set.Count : 0;
                if (ClipboardHistoryStore.IsProtected(e, assigned)) keep++;
                else del++;
            }
            return (del, keep);
        }
    }

    /// <summary>R54 v2: a "protected" entry is one the user has deliberately
    /// marked and must survive a ClearOlderEntries sweep. Delegates to the pure
    /// <see cref="ClipboardHistoryStore.IsProtected"/> so the rule is unit-
    /// tested at the store layer (the service has no test project). Kept as a
    /// thin instance wrapper that supplies the assignment count from
    /// <c>_tags</c>.</summary>
    private bool IsProtected(ClipboardEntry entry)
    {
        int assigned = _tags.Assignments.TryGetValue(entry.Id, out IReadOnlySet<string>? set) ? set.Count : 0;
        return ClipboardHistoryStore.IsProtected(entry, assigned);
    }

    /// <summary>
    /// 取词管线在发 <c>Ctrl+Insert</c>/<c>Ctrl+C</c> 注入复制前调用，让历史服务忽略
    /// 接下来 <paramref name="count"/> 次 <c>WM_CLIPBOARDUPDATE</c>。注入复制阶段
    /// 通常 count=2；还原阶段由 <c>Win32ClipboardCapture</c> 精确传入 count=1。
    /// 语义与 <see cref="PasteAsync"/> 的单次抑制一致，只是支持连续多次自写。线程安全：
    /// 在 <see cref="_gate"/> 内自增，<c>OnClipboardChanged</c> 用
    /// <see cref="Interlocked"/> 无锁递减。<paramref name="count"/>)&lt;=0 为无操作。
    /// </summary>
    public void SuppressNextChanges(int count)
    {
        // count>0：累加配额（取词注入通常 +2，restore +1）。count<0：回滚配额
        //（chord 没真发出或 restore 失败时撤销）。
        // count==0 无操作。钳制到 [0,8]：下界保证不残留负配额污染真实复制，上界防止
        // 异常路径无限累积误吞后续复制。
        if (count == 0)
        {
            return;
        }
        lock (_gate)
        {
            int next = _suppressNextChanges + count;
            _suppressNextChanges = next < 0 ? 0 : (next > 8 ? 8 : next);
        }
    }

    /// <summary>
    /// Pastes the entry back onto the clipboard (and, when
    /// <see cref="ClipboardHistorySettings.AutoPasteEnabled"/> is set, synthesizes
    /// a Ctrl+V via SendInput). Text entries use <c>SetText</c>; R54 v2 image
    /// entries read their PNG from disk and use <c>SetPng</c>. Sets
    /// <see cref="_suppressNextChanges"/> so our own write does not re-enter
    /// history. Returns false on a write failure.
    /// </summary>
    /// <remarks>
    /// <b>Auto-paste note:</b> the Ctrl+V synthesis is best-effort. Elevated/UWP
    /// targets may refuse the input; the content is already on the clipboard so
    /// the user can paste manually. Failure is logged, not thrown.
    /// </remarks>
    public bool PasteAsync(ClipboardEntry entry, bool autoPaste)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // autoPaste is passed by the caller (the window's PasteAndHide always
        // passes true — double-click means "paste into the target now", the
        // universal Win+V/Maccy/CopyQ contract). The AutoPasteEnabled setting is
        // consulted by callers that want the user's preference instead.
        lock (_gate)
        {
            // 自写剪贴板前累加 1 个配额（=1 次抑制）。用累加而非赋值，与
            // SuppressNextChanges 一致，避免覆盖未消耗的取词配额。
            _suppressNextChanges = Math.Min(_suppressNextChanges + 1, 8);
        }

        // R54 v2: image entries paste via CF_DIB (format 8, universally recognized
        // by Word/Paint/chat clients). PREFERENCE ORDER:
        //   1. The .dib file captured at copy time — it IS the exact CF_DIB payload
        //      the source app put on the clipboard, so it's guaranteed compatible.
        //   2. Fallback: rebuild a DIB from the stored .png via PngToDibConverter
        //      (for legacy entries captured before .dib storage shipped).
        // (The earlier attempt always rebuilt from PNG, but that converter failed
        //  at runtime on real images — see "PNG→DIB conversion failed" logs — so
        //  we now lead with the .dib file which is already known-good.)
        bool placed;
        if (entry.Kind == ClipboardEntryKind.Image && !string.IsNullOrEmpty(entry.ImageFileName))
        {
            try
            {
                string baseName = Path.GetFileNameWithoutExtension(entry.ImageFileName);
                string dibPath = Path.Combine(_imagesDirectory, baseName + ".dib");
                byte[]? dib = null;
                if (File.Exists(dibPath))
                {
                    // Preferred path: the .dib captured at copy time IS the
                    // source app's real CF_DIB — 100% compatible, no conversion.
                    dib = File.ReadAllBytes(dibPath);
                }
                else
                {
                    // Legacy entry (no .dib): rebuild a DIB from the PNG. This
                    // can fail under NativeAOT (Avalonia CopyPixels on some
                    // images); if it does, log the real exception and fall back
                    // to CF_PNG so modern apps (Win10 1809+) still get the image.
                    string pngPath = Path.Combine(_imagesDirectory, entry.ImageFileName);
                    byte[] png = File.ReadAllBytes(pngPath);
                    try
                    {
                        dib = PngToDibConverter.ConvertPngToDib(png);
                    }
                    catch (Exception convEx)
                    {
                        _logger.Error("ClipboardHistory", "PNG→DIB conversion threw (legacy entry); falling back to CF_PNG.", convEx);
                    }
                    if (dib is null)
                    {
                        // Last-resort: write CF_PNG. Fewer apps read it, but it's
                        // better than a silent failure (and the next copy of the
                        // same image will have a .dib and paste cleanly).
                        placed = _clipboard.SetPng(png);
                        if (!placed)
                        {
                            _logger.Error("ClipboardHistory", "SetPng fallback also failed during paste.");
                        }
                        return placed;
                    }
                }
                placed = _clipboard.SetImageDib(dib);
                if (!placed)
                {
                    _logger.Error("ClipboardHistory", "SetImageDib failed during paste.");
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.Error("ClipboardHistory", $"Image file missing for paste: {entry.ImageFileName}.", exception);
                return false;
            }
        }
        else
        {
            placed = _clipboard.SetText(entry.Text);
            if (!placed)
            {
                _logger.Error("ClipboardHistory", "SetText failed during paste.");
                return false;
            }
        }

        if (autoPaste)
        {
            try
            {
                // The history popup is Topmost + ShowActivated, so it stole focus
                // from the target input. Hide() (called by the window before
                // PasteRequested) returns focus asynchronously — give Windows a
                // beat to restore the foreground window before we synthesize
                // Ctrl+V, otherwise the keystroke lands on the wrong window (or
                // nowhere). 120ms is enough on real hardware without feeling laggy.
                Thread.Sleep(120);
                var injector = new SendInputHelper();
                injector.SendPasteChord();
            }
            catch (Exception)
            {
                _logger.Info("ClipboardHistory", "AutoPaste Ctrl+V synthesis failed; content is on clipboard for manual paste.");
            }
        }

        return true;
    }

    // ── R54 v1.1: tag management (custom tags + favorite) ──

    /// <summary>Adds a custom tag name. Persists immediately. Returns the
    /// updated tag data (or the unchanged data if the name was blank/dup).</summary>
    public ClipboardTagData AddCustomTag(string name)
    {
        lock (_gate)
        {
            _tags = ClipboardTagStore.AddCustomTag(_tags, name);
            TryPersistTags();
            return _tags;
        }
    }

    /// <summary>Renames a custom tag (updates all assignments). Persists.</summary>
    public ClipboardTagData RenameCustomTag(string oldName, string newName)
    {
        lock (_gate)
        {
            _tags = ClipboardTagStore.RenameCustomTag(_tags, oldName, newName);
            TryPersistTags();
            return _tags;
        }
    }

    /// <summary>Deletes a custom tag (removes from all assignments, keeps
    /// entries). Persists.</summary>
    public ClipboardTagData DeleteCustomTag(string name)
    {
        lock (_gate)
        {
            _tags = ClipboardTagStore.DeleteCustomTag(_tags, name);
            TryPersistTags();
            return _tags;
        }
    }

    /// <summary>Assigns <paramref name="tagName"/> to the entry. Idempotent.
    /// Used for both custom tags and <see cref="ClipboardTagData.FavoriteTagName"/>.
    /// Persists.</summary>
    public ClipboardTagData AssignToTag(Guid entryId, string tagName)
    {
        lock (_gate)
        {
            _tags = ClipboardTagStore.Assign(_tags, entryId, tagName);
            TryPersistTags();
            return _tags;
        }
    }

    /// <summary>Removes <paramref name="tagName"/> from the entry. Idempotent.
    /// Persists.</summary>
    public ClipboardTagData UnassignFromTag(Guid entryId, string tagName)
    {
        lock (_gate)
        {
            _tags = ClipboardTagStore.Unassign(_tags, entryId, tagName);
            TryPersistTags();
            return _tags;
        }
    }

    /// <summary>Toggles the favorite tag on the entry. Convenience over
    /// Assign/Unassign. Persists.</summary>
    public void ToggleFavorite(Guid entryId)
    {
        lock (_gate)
        {
            _tags = _tags.HasTag(entryId, ClipboardTagData.FavoriteTagName)
                ? ClipboardTagStore.Unassign(_tags, entryId, ClipboardTagData.FavoriteTagName)
                : ClipboardTagStore.Assign(_tags, entryId, ClipboardTagData.FavoriteTagName);
            TryPersistTags();
        }
    }

    // ── R54 v1.2: icon + reorder ──

    /// <summary>Sets (or clears, when <paramref name="emoji"/> is blank) the
    /// emoji icon for the custom tag. Persists.</summary>
    public ClipboardTagData SetTagIcon(string name, string emoji)
    {
        lock (_gate)
        {
            _tags = ClipboardTagStore.SetTagIcon(_tags, name, emoji);
            TryPersistTags();
            return _tags;
        }
    }

    /// <summary>Moves the custom tag to <paramref name="toIndex"/> in the
    /// display order (0-based among custom tags only). Used by drag-to-reorder.
    /// Persists.</summary>
    public ClipboardTagData MoveTag(string name, int toIndex)
    {
        lock (_gate)
        {
            _tags = ClipboardTagStore.ReorderTag(_tags, name, toIndex);
            TryPersistTags();
            return _tags;
        }
    }

    // ── R54 v1.2 v6: user-imported icon library ──

    /// <summary>Adds extracted icons to the user library and persists. Returns
    /// the new library snapshot. Dedupes by name (later wins).</summary>
    public UserIconLibrary AddUserIcons(IEnumerable<UserIcon> icons)
    {
        lock (_gate)
        {
            _iconLibrary = UserIconLibraryStore.AddIcons(_iconLibrary, icons);
            UserIconLibraryStore.Save(_iconLibraryPath, _iconLibrary);
            return _iconLibrary;
        }
    }

    /// <summary>Removes the named icon from the user library and persists.
    /// Returns the new library snapshot.</summary>
    public UserIconLibrary RemoveUserIcon(string name)
    {
        lock (_gate)
        {
            _iconLibrary = UserIconLibraryStore.RemoveIcon(_iconLibrary, name);
            UserIconLibraryStore.Save(_iconLibraryPath, _iconLibrary);
            return _iconLibrary;
        }
    }

    private void OnClipboardChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Honor the master enable toggle without unsubscribing (cheaper than a
        // subscribe/unsubscribe churn, and keeps the listener warm for a fast
        // re-enable).
        if (!_settings.Enabled)
        {
            return;
        }

        // 消费抑制配额（PasteAsync 自写 1 次，或取词管线经 SuppressNextChanges 预存
        // 2 次）。配额在 _gate 内自增；这里用 Interlocked 无锁递减，避免 OnClipboardChanged
        // （剪贴板消息线程，高频）与注入线程争 _gate。remaining>=0 表示有配额，跳过本次；
        // remaining<0 表示配额已被本次 Decrement 推到负数（即本来就没有配额），立即归零并
        // 继续走正常流程——保证一次意外的 Decrement 不会永久污染后续真实复制。
        int remaining = Interlocked.Decrement(ref _suppressNextChanges);
        if (remaining >= 0)
        {
            _lastSeenSequence = _clipboard.GetSequenceNumber();
            return;
        }
        // remaining<0：没有配额，Decrement 把它推到了 -1。修正回 0，继续正常处理。
        Interlocked.CompareExchange(ref _suppressNextChanges, 0, remaining);

        uint sequence = _clipboard.GetSequenceNumber();
        if (sequence == _lastSeenSequence)
        {
            return;
        }
        _lastSeenSequence = sequence;

        string? text = _clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            // R54 v2: no text on the clipboard — try an image capture instead.
            // The image path is gated behind CaptureImagesEnabled and the
            // exclude-apps filter (same privacy rule as text: never record from
            // a password manager, even if it happens to put an image up).
            if (!_settings.CaptureImagesEnabled)
            {
                return;
            }

            string? imgSource = null;
            try
            {
                imgSource = _clipboard.GetForegroundProcessName();
            }
            catch (Exception exception)
            {
                _logger.Error("ClipboardHistory", "GetForegroundProcessName failed (image path).", exception);
            }

            if (imgSource is not null && IsExcluded(imgSource, _settings.ExcludeProcessNames))
            {
                _logger.Info("ClipboardHistory", $"Image capture skipped: excluded source '{imgSource}'.");
                return;
            }

            ClipboardEntry? imageEntry = TryCaptureImage(imgSource);
            if (imageEntry is null)
            {
                return; // no image either, or unsupported DIB format
            }

            lock (_gate)
            {
                // R102: AddAndEvict with the out overload to capture evicted
                // entries. For the image path, the AddAndEvict itself doesn't
                // delete image files (it's a pure function), so any image entry
                // that fell off here must have its .png/.dib cleaned up. Text
                // entries that fell off are archived instead. EvictImagesToMax
                // below still handles its own image-file deletion as before.
                _entries = ClipboardHistoryStore.AddAndEvict(
                    _entries, imageEntry, _settings.MaxEntries, out IReadOnlyList<ClipboardEntry> evictedFromImageAdd);
                ArchiveEvictedTextEntries(evictedFromImageAdd);
                foreach (ClipboardEntry ev in evictedFromImageAdd)
                {
                    if (ev.Kind == ClipboardEntryKind.Image)
                    {
                        DeleteImageFile(ev.ImageFileName);
                    }
                }
                EvictImagesToMax();
                TryPersist();
            }
            return;
        }

        // Exclude-apps filter: drop captures originating from password managers.
        string? source = null;
        try
        {
            source = _clipboard.GetForegroundProcessName();
        }
        catch (Exception exception)
        {
            _logger.Error("ClipboardHistory", "GetForegroundProcessName failed.", exception);
        }

        if (source is not null && IsExcluded(source, _settings.ExcludeProcessNames))
        {
            _logger.Info("ClipboardHistory", $"Capture skipped: excluded source '{source}'.");
            return;
        }

        ClipboardEntry entry = BuildEntry(text, source);

        lock (_gate)
        {
            // R99 Bug B: AddAndEvict 去重时会迁移旧条目的用户标注到新头部.
            // 这里记一条确认日志(仅当新 entry 真的继承了标注时), 方便观察迁移
            // 是否生效. 诊断用, 日后可降级或删除.
            // R102: 同时用 out 重载捕获被 LRU 淘汰的文本条目, 搬到月度归档
            // 而不是丢弃(只归档系统自动淘汰; 用户主动 Delete/Clear 不走这里).
            _entries = ClipboardHistoryStore.AddAndEvict(
                _entries, entry, _settings.MaxEntries, out IReadOnlyList<ClipboardEntry> evicted);
            ArchiveEvictedTextEntries(evicted);
            ClipboardEntry? inserted = _entries.Count > 0 ? _entries[0] : null;
            if (inserted is not null && inserted.Id == entry.Id &&
                (inserted.IsSensitive || inserted.GroupOverride is not null || inserted.EntryTags.Count > 0))
            {
                _logger.Info("ClipboardHistory",
                    $"OnClipboardChanged(text): deduped a re-copy of text that had user marks — " +
                    $"migrated onto new entry {entry.Id} (sensitive={inserted.IsSensitive}, groupOverride={inserted.GroupOverride}, entryTags={inserted.EntryTags.Count}).");
            }

            TryPersist();
        }
    }

    /// <summary>
    /// R54 v2: reads the current clipboard image (CF_DIB), converts it to PNG,
    /// writes it to <c>_imagesDirectory</c>, and returns a new image entry.
    /// Returns null when there is no image, the DIB format is unsupported, or
    /// the PNG write fails. The file is named by a content-hash suffix so
    /// identical pixels dedup to the same <see cref="ClipboardEntry.ImageFileName"/>
    /// (the store's dedup then drops the duplicate). Must NOT be called under
    /// <c>_gate</c> (it does clipboard + disk I/O).
    /// </summary>
    private ClipboardEntry? TryCaptureImage(string? sourceProcessName)
    {
        // P2 memory: read the CF_DIB via the ArrayPool-backed path so the
        // up-to-32 MB buffer is rented (not `new byte[]`), then returned by the
        // `using` below once we've copied the bytes into the PNG + written the
        // .dib file. Without this every clipboard-image change allocated a
        // fresh LOH byte[] (NativeAOT LOH never compacts / returns to OS),
        // which was the second source of idle private-bytes growth after the
        // Backup() path. Payload.Length is the valid byte count; Buffer may be
        // an oversized pool rental, so all consumers use the length.
        using ImageDibPayload dib = _clipboard.GetImageDibPooled();
        if (dib.IsEmpty)
        {
            return null;
        }

        (byte[] png, int width, int height)? converted =
            DibToPngConverter.ConvertDibToPng(dib.Buffer, dib.Length);
        if (converted is null)
        {
            _logger.Info("ClipboardHistory", "Image capture skipped: unsupported DIB format.");
            return null;
        }

        byte[] pngBytes = converted.Value.png;

        // Name by content hash so identical images dedup. Hash suffix is the
        // dedup key (identical pixels → identical name → store drops the dup).
        string hash = Convert.ToHexString(SHA256.HashData(pngBytes), 0, 8).ToLowerInvariant();
        string baseName = $"clip-{hash}";
        string pngName = baseName + ".png";
        string dibName = baseName + ".dib";
        string pngPath = Path.Combine(_imagesDirectory, pngName);
        string dibPath = Path.Combine(_imagesDirectory, dibName);

        try
        {
            Directory.CreateDirectory(_imagesDirectory);
            if (!File.Exists(pngPath))
            {
                File.WriteAllBytes(pngPath, pngBytes);
            }
            // R54 v2: also persist the ORIGINAL CF_DIB bytes for paste-back.
            // Pasting via SetImageDib(CF_DIB) is universally recognized (Word,
            // Paint, chat clients), whereas SetPng (CF_PNG only) is ignored by
            // most apps — that was the "copy doesn't work" bug. Storing the raw
            // DIB avoids a lossy PNG→DIB re-encode and restores the exact bytes
            // the source app put up. We still keep the PNG too (used for the
            // thumbnail decode + any PNG-preferring consumer).
            // P2: write only dib.Length bytes (Buffer may be an oversized pool
            // rental; writing the whole array would persist garbage padding).
            if (!File.Exists(dibPath))
            {
                WriteAllBytes(dibPath, dib.Buffer, dib.Length);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Error("ClipboardHistory", $"Failed to write image files '{pngName}'/'{dibName}'.", exception);
            return null;
        }

        return new ClipboardEntry
        {
            Kind = ClipboardEntryKind.Image,
            Text = string.Empty,
            ImageFileName = pngName, // stores the .png name; paste-back derives .dib from the same base
            SourceProcessName = sourceProcessName,
            CapturedAt = DateTimeOffset.UtcNow,
            Group = ClipboardGroup.Text, // images are not Smart-auto-grouped
            IsSensitive = false,
        };
    }

    /// <summary>P2: writes the first <paramref name="length"/> bytes of
    /// <paramref name="buffer"/> to <paramref name="path"/>. Used by
    /// <see cref="TryCaptureImage"/> where <paramref name="buffer"/> may be an
    /// oversized ArrayPool rental (the valid payload is
    /// <paramref name="length"/>, not buffer.Length). Equivalent to
    /// <see cref="File.WriteAllBytes(string, byte[])"/> but length-bounded.</summary>
    private static void WriteAllBytes(string path, byte[] buffer, int length)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 81920, useAsync: false);
        stream.Write(buffer, 0, length);
    }

    /// <summary>
    /// R54 v2: trims non-pinned image entries to
    /// <see cref="ClipboardHistorySettings.MaxImageEntries"/> and deletes the PNG
    /// files of evicted entries (avoids Ditto-style disk bloat). Must be called
    /// under <c>_gate</c>. Image and text entries share the same
    /// <c>_entries</c> list; this pass only removes images, leaving the text cap
    /// (<see cref="ClipboardHistoryStore.AddAndEvict"/>) untouched.
    /// </summary>
    private void EvictImagesToMax()
    {
        int maxImages = _settings.MaxImageEntries;
        var images = _entries
            .Select((e, i) => (entry: e, index: i))
            .Where(t => t.entry.Kind == ClipboardEntryKind.Image && !t.entry.IsPinned)
            .OrderBy(t => t.entry.CapturedAt) // oldest first
            .ToList();

        int toDrop = Math.Max(0, images.Count - maxImages);
        if (toDrop == 0)
        {
            return;
        }

        var dropIds = images.Take(toDrop).Select(t => t.entry.Id).ToHashSet();
        var dropFileNames = _entries
            .Where(e => dropIds.Contains(e.Id) && !string.IsNullOrEmpty(e.ImageFileName))
            .Select(e => e.ImageFileName!)
            .ToList();

        _entries = _entries.Where(e => !dropIds.Contains(e.Id)).ToList();
        foreach (string name in dropFileNames)
        {
            DeleteImageFile(name);
        }
    }

    /// <summary>Best-effort deletion of an image PNG. Never throws — a missing
    /// or locked file is logged and ignored (the entry is already gone from the
    /// list, so a stray file is cosmetic, not a correctness issue).</summary>
    private void DeleteImageFile(string? imageFileName)
    {
        if (string.IsNullOrEmpty(imageFileName))
        {
            return;
        }

        // R54 v2: an image entry has BOTH a .png (thumbnail source) and a .dib
        // (paste-back payload), sharing the same base name. Delete both so
        // evicted/cleared/deleted entries don't leak either file.
        try
        {
            string baseName = Path.GetFileNameWithoutExtension(imageFileName);
            string pngPath = Path.Combine(_imagesDirectory, baseName + ".png");
            string dibPath = Path.Combine(_imagesDirectory, baseName + ".dib");
            if (File.Exists(pngPath))
            {
                File.Delete(pngPath);
            }
            if (File.Exists(dibPath))
            {
                File.Delete(dibPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Error("ClipboardHistory", $"Failed to delete image files '{imageFileName}'.", exception);
        }
    }

    /// <summary>R102: archives the text entries among <paramref name="evicted"/>
    /// to the monthly archive shards. Image entries are skipped (their files are
    /// deleted by the caller via <see cref="DeleteImageFile"/>; archiving only
    /// the metadata would leave dangling file references). Called under
    /// <c>_gate</c> by the LRU paths (text capture, image capture, settings
    /// shrink). The archive I/O is bounded (a month file is ≤ ~1MB) and
    /// clipboard changes are infrequent, so holding the lock during the
    /// read-modify-write is acceptable and keeps the eviction→archive step
    /// atomic (no race where another eviction lands between computing
    /// <paramref name="evicted"/> and writing it). Failures are logged and
    /// swallowed: the entry is already gone from <c>_entries</c>, so a missed
    /// archive just means it's lost (same as the pre-R102 behavior).</summary>
    private void ArchiveEvictedTextEntries(IReadOnlyList<ClipboardEntry> evicted)
    {
        if (evicted.Count == 0) return;
        // Filter to text-only defensively (image entries should never reach
        // here, but a future caller might pass mixed kinds).
        List<ClipboardEntry> textEntries = evicted
            .Where(e => e.Kind == ClipboardEntryKind.Text)
            .ToList();
        if (textEntries.Count == 0) return;

        try
        {
            int written = ClipboardArchiveStore.AppendToArchive(
                textEntries, _archiveDirectory, _entryCipher);
            if (written > 0)
            {
                _logger.Info("ClipboardHistory",
                    $"Archived {written} evicted text entr{(written == 1 ? "y" : "ies")} to {_archiveDirectory}.");
            }
        }
        catch (Exception ex)
        {
            // Never let archive failure break the main clipboard flow.
            _logger.Error("ClipboardHistory",
                $"ArchiveAppend failed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// Pure: builds a <see cref="ClipboardEntry"/> from raw text + source,
    /// running the classifier. Exposed (internal) so tests can verify entry
    /// construction without a live clipboard.
    /// </summary>
    internal static ClipboardEntry BuildEntry(string text, string? sourceProcessName)
    {
        ClipboardGroup group = ClipboardClassifier.Classify(text);
        return new ClipboardEntry
        {
            Text = text,
            SourceProcessName = sourceProcessName,
            CapturedAt = DateTimeOffset.UtcNow,
            Group = group,
            // Batch 124: Sensitive is manual-only. Classify never returns
            // Sensitive anymore, so IsSensitive is always false at capture
            // time; it can only be flipped on by the user via
            // SetGroupOverride ("Move to → 🔒 Sensitive").
            IsSensitive = false,
        };
    }

    /// <summary>
    /// Pure: true when <paramref name="processName"/> matches any of the
    /// exclude patterns (case-insensitive substring). Exposed for unit tests.
    /// </summary>
    internal static bool IsExcluded(string processName, IReadOnlyList<string> excludePatterns)
    {
        if (excludePatterns.Count == 0 || string.IsNullOrEmpty(processName))
        {
            return false;
        }

        foreach (string pattern in excludePatterns)
        {
            if (!string.IsNullOrEmpty(pattern) &&
                processName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void TryPersist()
    {
        // Must be called under _gate.
        try
        {
            ClipboardHistoryStore.Save(_entries, _historyPath, _entryCipher);
        }
        catch (Exception exception)
        {
            // R99 Bug B 候选根因 #2 诊断: 持久化失败时内存正确但磁盘 stale,
            // 重启后恢复旧值 → 用户感觉"操作偶尔失效". 补充路径 + 异常类型,
            // 区分这是磁盘满/权限/杀软锁文件 哪一种.
            _logger.Error("ClipboardHistory",
                $"Persist failed (path={_historyPath}): {exception.GetType().Name}. In-memory state is correct but disk is stale — user changes will be lost on restart.",
                exception);
        }
    }

    private void TryPersistTags()
    {
        // Must be called under _gate. R54 v1.1: tag/assignment persistence. Kept
        // separate from TryPersist so a tag edit never rewrites the (potentially
        // large) clipboard-history.json — only clipboard-history-tags.json.
        try
        {
            ClipboardTagStore.Save(_tags, _tagsPath);
        }
        catch (Exception exception)
        {
            _logger.Error("ClipboardHistory", "Tag persist failed.", exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _clipboard.UnsubscribeChanges();
        }
        catch (Exception exception)
        {
            _logger.Error("ClipboardHistory", "UnsubscribeChanges failed during dispose.", exception);
        }

        _clipboard.Dispose();
        _logger.Info("ClipboardHistory", "Stopped.");
    }
}
