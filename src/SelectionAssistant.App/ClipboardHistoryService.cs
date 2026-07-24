using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Infrastructure.Configuration;
using SelectionAssistant.Infrastructure.Logging;
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
    private readonly RedactedLogger _logger;
    private readonly object _gate = new();
    private List<ClipboardEntry> _entries;
    private ClipboardHistorySettings _settings;
    private uint _lastSeenSequence;
    private bool _suppressNextChange; // set during PasteAsync to ignore our own write
    private int _disposed;

    public ClipboardHistoryService(
        Win32Clipboard clipboard,
        string historyPath,
        ClipboardHistorySettings settings,
        RedactedLogger logger)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentException.ThrowIfNullOrWhiteSpace(historyPath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _clipboard = clipboard;
        _historyPath = historyPath;
        _settings = settings.Normalize();
        _logger = logger;
        _entries = ClipboardHistoryStore.Load(historyPath);
        _lastSeenSequence = clipboard.GetSequenceNumber();

        clipboard.SubscribeChanges(OnClipboardChanged);
        _logger.Info("ClipboardHistory", $"Started with {_entries.Count} entries, max={_settings.MaxEntries}.");
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
    /// decreased). Persists nothing here — the caller (App) is responsible for
    /// persisting the settings file. Thread-safe.
    /// </summary>
    public void UpdateSettings(ClipboardHistorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();
        lock (_gate)
        {
            _settings = settings;
            _entries = ClipboardHistoryStore.EvictToMax(_entries, settings.MaxEntries);
        }
        _logger.Info("ClipboardHistory", $"Settings updated: enabled={settings.Enabled} max={settings.MaxEntries} autoPaste={settings.AutoPasteEnabled}.");
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
    /// existed. Persists immediately.</summary>
    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            int removed = _entries.RemoveAll(e => e.Id == id);
            if (removed > 0)
            {
                TryPersist();
            }
            return removed > 0;
        }
    }

    /// <summary>Clears all non-pinned entries. Pinned entries are kept. Persists.</summary>
    public void ClearNonPinned()
    {
        lock (_gate)
        {
            _entries = _entries.Where(e => e.IsPinned).ToList();
            TryPersist();
        }
        _logger.Info("ClipboardHistory", "Cleared all non-pinned entries.");
    }

    /// <summary>
    /// Pastes the entry's text back onto the clipboard (and, when
    /// <see cref="ClipboardHistorySettings.AutoPasteEnabled"/> is set, synthesizes
    /// a Ctrl+V via SendInput). Sets <see cref="_suppressNextChange"/> so our own
    /// write does not re-enter history. Returns false on a SetText failure.
    /// </summary>
    /// <remarks>
    /// <b>Auto-paste note:</b> the Ctrl+V synthesis is best-effort. Elevated/UWP
    /// targets may refuse the input; the text is already on the clipboard so the
    /// user can paste manually. Failure is logged, not thrown.
    /// </remarks>
    public bool PasteAsync(ClipboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        bool autoPaste;
        lock (_gate)
        {
            autoPaste = _settings.AutoPasteEnabled;
            _suppressNextChange = true;
        }

        bool placed = _clipboard.SetText(entry.Text);
        if (!placed)
        {
            _logger.Error("ClipboardHistory", "SetText failed during paste.");
            return false;
        }

        if (autoPaste)
        {
            try
            {
                // Best-effort Ctrl+V into the previously-focused window. Reuses
                // the same SendInput machinery as the toolbar "粘贴" button.
                // Failure (elevated/UWP refusal) is non-fatal — the text is
                // already on the clipboard for a manual paste.
                var injector = new SendInputHelper();
                injector.SendPasteChord();
            }
            catch (Exception)
            {
                _logger.Info("ClipboardHistory", "AutoPaste Ctrl+V synthesis failed; text is on clipboard for manual paste.");
            }
        }

        return true;
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

        // Dedup by sequence number — our own PasteAsync write bumps the
        // sequence; _suppressNextChange + the sequence check both guard it.
        if (_suppressNextChange)
        {
            _suppressNextChange = false;
            _lastSeenSequence = _clipboard.GetSequenceNumber();
            return;
        }

        uint sequence = _clipboard.GetSequenceNumber();
        if (sequence == _lastSeenSequence)
        {
            return;
        }
        _lastSeenSequence = sequence;

        string? text = _clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
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
            _entries = ClipboardHistoryStore.AddAndEvict(_entries, entry, _settings.MaxEntries);
            TryPersist();
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
            IsSensitive = group == ClipboardGroup.Sensitive,
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
            ClipboardHistoryStore.Save(_entries, _historyPath);
        }
        catch (Exception exception)
        {
            _logger.Error("ClipboardHistory", "Persist failed.", exception);
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
