using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Core.I18n;
using SelectionAssistant.Infrastructure.Configuration;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
// Disambiguate the Shapes.Path shape from System.IO.Path (brought in by
// ImplicitUsings). Lucide tag icons are rendered with Avalonia.Controls.Shapes.Path.
using Path = Avalonia.Controls.Shapes.Path;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// R54 v1.1 clipboard-history popup (Maccy-style + CopyQ-style vertical tab bar).
/// Summoned by its own global hotkey (default <c>Ctrl+Alt+V</c>).
/// </summary>
/// <remarks>
/// <b>v1.1 layout</b> — two panels:
/// <list type="bullet">
///   <item><b>Left nav</b> (148 px): vertical tab bar reusing the
///   <c>SettingsNav</c> button style. Built-in tabs (全部/链接/代码/JSON/命令/
///   数字/联系人/敏感/★置顶/❤收藏) are derived from entry group/pinned/favorite
///   at filter time; custom tags come from <see cref="SetTags"/>.
///   Right-click a custom-tag tab → rename/delete (App owns the prompt dialog).
///   <c>+ 新建</c> raises <see cref="CreateCustomTagRequested"/> (App prompts
///   for the name).</item>
///   <item><b>Right panel</b>: a search box filters entries via
///   <see cref="ClipboardSearchMatcher"/> (R101: multi-field text+tags+source,
///   space-separated multi-token AND; backed by <see cref="PinyinSearchHelper"/>
///   for substring + initials + pinyin, same engine as
///   <see cref="SpotlightWindow"/>); the list shows the entries of the active
///   tab + matching the query.</item>
/// </list>
/// <para>
/// <b>Click semantics (v1.1)</b>: single click = select only; double click =
/// paste + close. Mirrors the manual double-click detection in
/// <see cref="PinnedScreenshotWindow"/> (TickCount64 + threshold + 8 px movement).
/// Sensitive rows paste on double-click <i>directly</i> (no expand-first step);
/// the right-click <c>显示明文</c> menu item shows the plaintext in-place
/// <b>without</b> writing it to the clipboard.
/// </para>
/// <para>
/// <b>Per-row context menu</b>: built dynamically on right-press (so the
/// "移动到…" submenu always reflects the current custom tags) — 复制并粘贴 /
/// 复制（不关闭） / ★置顶 / ❤收藏 / 移动到…[custom tags] / 显示明文 (sensitive
/// only) / 删除.
/// </para>
/// <para>
/// The window holds no Win32/store references — it operates purely on the
/// <see cref="ClipboardEntry"/> list + tag data pushed by App and raises events
/// that App forwards to <c>ClipboardHistoryService</c>. This keeps the UI layer
/// free of the App layer.
/// </para>
/// </remarks>
public partial class ClipboardHistoryWindow : Window
{
    // Maximum interval between two clicks for the pair to count as a double-
    // click. 400 ms (slightly tighter than the Windows default ~500 ms) — keeps
    // the popup snappy while remaining forgiving of a normal double-click tempo.
    private const int DoubleClickMs = 400;
    // Maximum physical-pixel distance between two clicks for the pair to count
    // as a double-click (matches the Windows default SM_CXDOUBLECLK ~4 px, with
    // a slightly larger 8 px tolerance — same value as PinnedScreenshotWindow).
    private const double DoubleClickPx = 8.0;

    // Full set of rows (display order: pinned first, then newest). App pushes a
    // fresh snapshot whenever history changes; we rebuild _allRows from it.
    private readonly ObservableCollection<ClipboardHistoryEntryRow> _allRows = [];
    private readonly ObservableCollection<ClipboardHistoryEntryRow> _filteredRows = [];

    // Batch 123: incremental rendering. _filteredRows only holds the visible
    // slice (first InitialBatchSize rows, grown by LoadMoreBatchSize on scroll).
    // _filteredPool is the full matched set (cheap to compute, no controls);
    // _visibleCount is how many of it are currently materialized in _filteredRows.
    // Rationale: Avalonia 12 has no built-in virtualizing panel, and a plain
    // ItemsControl materializes every container. With MaxEntries=1000 + archive
    // rows, rebuilding _filteredRows fully on every tab switch/search/refresh
    // made the window visibly lag. The pool holds the full list so search/tab
    // semantics are unchanged; only the rendered slice is windowed.
    private readonly List<ClipboardHistoryEntryRow> _filteredPool = [];
    private int _visibleCount;
    // First batch — covers ~7-8 screenfuls at the default window height so the
    // initial open feels complete but renders in a few ms, not hundreds.
    private const int InitialBatchSize = 60;
    // Each scroll-to-bottom or arrow-key-past-edge appends this many rows.
    private const int LoadMoreBatchSize = 40;
    // Trigger LoadMore when the distance to the bottom is within this fraction
    // of one viewport (0.85 = top 85% of viewport scrolled → load next batch).
    private const double LoadMoreThresholdRatio = 0.85;

    // Built-in tab ids. 全部 is always present; the group/pin/favorite tabs are
    // rebuilt on each snapshot to only show tabs that have matching entries.
    private enum ClipboardTab
    {
        All,
        Link, Json, Code, Shell, Number, Sensitive,
        Image, // R54 v2: image entries (auto-classified, like the group tabs)
        Pinned, Favorite,
        // Sentinel: a custom-tag tab is active (see _activeCustomTagName).
        Custom,
    }

    private ClipboardTab _activeTab = ClipboardTab.All;
    private string? _activeCustomTagName; // set when _activeTab == Custom
    // R54 v2: ordered list of currently-visible nav tabs, mirroring the buttons
    // built by RebuildNav (All, then group tabs that have entries, then
    // Image/Pinned/Favorite if present, then custom tags in order). Drives
    // Ctrl+Tab / Ctrl+Shift+Tab cycling. Rebuilt on every RebuildNav call.
    private List<(ClipboardTab tab, string? customTagName)> _navOrder = [];
    private int _selectedIndex;
    private bool _allowClose;
    private bool _maskSensitive = true;

    // R54 v2: directory holding image-entry PNGs (App pushes via
    // SetImagesDirectory). Combined with ClipboardEntry.ImageFileName in ToRow
    // to build the full path for thumbnail decode + paste-back.
    private string _imagesDirectory = string.Empty;

    // Custom tag names in display order (App pushes via SetTags). Empty by
    // default; rebuilt whenever the user adds/renames/deletes/reorders a tag.
    private IReadOnlyList<string> _customTags = [];

    // R54 v1.2: custom tag name → emoji icon (App pushes via SetTags). Empty by
    // default; drives whether a custom-tag nav button shows [emoji] or # prefix.
    private IReadOnlyDictionary<string, string> _tagIcons =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // R54 v1.2 v6: user-imported icons (App pushes via SetUserIcons). Each entry
    // is (name, pathData); the stored tag-icon value is "user:<name>".
    private IReadOnlyList<UserIcon> _userIcons = [];

    // R54 v2: deduplicated set of all entry-tag strings seen across the current
    // snapshot. Rebuilt in SetEntries by scanning every entry's EntryTags. Used
    // to drive the "Add tag…" autocomplete suggestions so the user can reuse a
    // previously-typed label (e.g. type "A" → "AWS" appears) without retyping.
    // Not persisted — it's a pure projection of the entry data.
    private HashSet<string> _knownEntryTags = new(StringComparer.Ordinal);

    // Ids whose preview the user has revealed (via right-click 显示明文) despite
    // being sensitive. Reset whenever the snapshot is refreshed.
    private readonly HashSet<Guid> _revealed = [];

    // Last single-click position/tick — used by the manual double-click check.
    private long _lastClickTicks;
    private PixelPoint _lastClickScreen;

    // R54 v1.2 v3: drag-to-reorder state for custom-tag nav buttons.
    //
    // PREVIOUS DESIGN (failed twice on real hardware): per-Button PointerPressed
    // that called e.Pointer.Capture(button) + e.Handled = true. Avalonia's Button
    // (ClickableTemplatedControl) ALSO captures the pointer internally on press
    // and re-routes release through its own state machine; combined with the
    // ScrollViewer wrapping NavButtonsPanel, the captured button's Bounds never
    // updated and there was zero visual feedback — users reported the drag "did
    // nothing". See GitHub discussion #19554 (PointerMoved only fires once unless
    // the capture owner is right).
    //
    // CURRENT DESIGN: capture lives on NavButtonsPanel (the StackPanel that hosts
    // the buttons), not on any Button. We hit-test the pressed Button by reading
    // e.Source on the panel's PointerPressed. Panel capture gives us every move/
    // release without fighting Button, and because we never put an indicator
    // child into the StackPanel, button Bounds stay stable for hit-testing. The
    // gold insertion caret is a separate overlay Border parented on the nav
    // tower's top-level Grid, positioned by Canvas-ish Top via RenderTransform —
    // it floats above the buttons and never perturbs layout.
    private const double TagDragThreshold = 6.0; // larger = stabler vs. scroll jitter
    private Border? _dragTagRow;          // the row Border being dragged (null = idle)
    private string? _dragTagName;         // tag name of _dragTagRow
    private Point _dragStartPanel;        // press position in NavButtonsPanel coords
    private bool _dragCommitted;          // true once movement exceeded threshold
    private int _dragInsertIndex = -1;    // current drop target index (-1 = none)
    private Border? _dragCaret;           // floating gold insertion indicator

    public ClipboardHistoryWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _filteredRows;

        // Batch 123: incremental rendering — grow the visible slice when the
        // user scrolls near the bottom (mouse wheel, scrollbar drag, PageDown).
        ResultsScroll.ScrollChanged += OnResultsScrollChanged;

        // R54 v1.2 v4: drag-to-reorder is now handled per-row on a dedicated
        // drag-handle Border (see BuildCustomTagNavButton + OnDragHandle*). No
        // panel-level subscription is needed.

        Opened += (_, _) =>
        {
            SearchInput.Text = string.Empty;
            SearchInput.Focus();
            // Reset the click-pair anchor so a click just before the previous
            // Hide() isn't misread as the first half of a double-click on reopen.
            _lastClickTicks = 0;
            _revealed.Clear();
            // R100: 窗口打开动画已去掉(用户要求)。窗口直接静态出现。
        };

        // R100 动画1: 关闭直接 Hide(瞬切)。用户要求"一次性弹出来", 不要缩回动画。
        // 只有出现时 scale 弹入(0.85→1.0), 关闭直接消失。
        Deactivated += (_, _) => Hide();
        Closing += (sender, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
	    }

	    /// <summary>Resolves the current theme font-size token. Centralized so the
	    /// ~30 dynamically-built controls in this file pick up the language-aware
	    /// sizes (Chinese +1pt) without each repeating the resource lookup.</summary>
	    private static double GetFontSize(string tokenKey) =>
	        Application.Current?.Resources[tokenKey] is double d ? d : 11;

	    /// <summary>R100 动画3: 给 popup 的 card Border 加弹动入场(参考贴图 R56 的
    /// scale pop)。scale 从 0.85 弹到 1.0(BackEaseOut 带轻微过冲), 配 Opacity
    /// 0→1 较快淡入让 scale 弹动主体不被透明度拖累。在 popup.Open() 之前调用。
    /// 参数对齐 PinnedScreenshotWindow: PopStartScale=0.85 / PopDuration=350ms /
    /// BackEaseOut(贴图同款"macOS dock bounce"质感)。
    /// NativeAOT-safe: ScaleTransform + DoubleTransition 是 PinnedScreenshotWindow
    /// wheel-zoom 验证过的路径(不用 MatrixTransform, 那个在 AOT 下静默失败)。
    /// 不用于 ShowImagePopup —— 它有自己的 zoom ScaleTransform, 叠加会冲突。</summary>
    private static void AttachPopupEntrance(Popup popup, Border card, int durationMs = 180)
    {
        const double popStartScale = 0.85; // 15% grow
        var popEasing = new CubicEaseOut(); // 单调减速, 无过冲(不要"冲过再缩回")
        var scale = new ScaleTransform(popStartScale, popStartScale);
        card.RenderTransform = scale;
        card.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        // ⚠️ scale 的 transition 必须挂在 ScaleTransform 本身(不是 card)——
        // transition 监听"拥有该属性的对象"。ScaleX/ScaleY 属于 ScaleTransform。
        // 贴图 wheel-zoom (cs:277) 同款: _scaleTransform.Transitions。
        // 不用 Opacity —— 避免 popup 背景渐显。
        // durationMs 默认 200(所有 popup 统一); 个别需调时传参覆盖。
        scale.Transitions = new Transitions
        {
            new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(durationMs), Easing = popEasing },
            new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(durationMs), Easing = popEasing },
        };
        popup.Opened += (_, _) =>
        {
            // 下一帧过渡到终值, 让初始 0.85 先在首帧生效再开始插值。
            Dispatcher.UIThread.Post(() =>
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            });
        };
    }

    // ── Push state from App ──

    /// <summary>Replaces the displayed entries from a service snapshot. Call on
    /// the UI thread. <paramref name="maskSensitive"/> controls whether
    /// sensitive rows show ●●●● until revealed via the context menu.</summary>
    public void SetEntries(
        IReadOnlyList<ClipboardEntry> entries,
        IReadOnlyList<ClipboardEntry> archive,
        bool maskSensitive)
    {
        _maskSensitive = maskSensitive;
        _allRows.Clear();
        // R54 v2: rebuild the entry-tag autocomplete set from the incoming
        // snapshot (union of every entry's tags). Pure projection — the store
        // is the source of truth, this just speeds up the "Add tag…" popup.
        var known = new HashSet<string>(StringComparer.Ordinal);
        // Live entries first (most relevant, newest). R103: isArchived=false.
        foreach (ClipboardEntry entry in entries)
        {
            _allRows.Add(ToRow(entry, isArchived: false));
            foreach (string t in entry.EntryTags)
            {
                if (!string.IsNullOrWhiteSpace(t)) known.Add(t);
            }
        }
        // R103: archived entries appended after live. They live in _allRows so
        // the search matcher can surface them when the query matches, but they
        // are EXCLUDED from the empty-query view (ReapplyFilter filters on
        // !IsArchived when query is empty) so the default list stays uncluttered.
        // Images are never archived (batch 102), so these are all text rows.
        foreach (ClipboardEntry entry in archive)
        {
            _allRows.Add(ToRow(entry, isArchived: true));
            foreach (string t in entry.EntryTags)
            {
                if (!string.IsNullOrWhiteSpace(t)) known.Add(t);
            }
        }
        _knownEntryTags = known;
        RebuildNav();
        ReapplyFilter();
        LoadThumbnails();
    }

    /// <summary>R54 v2: sets the directory holding image-entry PNGs. Must be
    /// called before <see cref="SetEntries"/> so <see cref="ToRow"/> can build
    /// full image paths. Called once from App at window first-show.</summary>
    public void SetImagesDirectory(string imagesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagesDirectory);
        _imagesDirectory = imagesDirectory;
    }

    /// <summary>R54 v2: decodes thumbnails for image rows off-thread and posts
    /// each finished bitmap back to the UI thread (same pattern as
    /// GalleryWindow.LoadThumbnailsAsync). Reuses Bitmap.DecodeToWidth so only
    /// the needed resolution is decoded. Failures are logged + skipped.</summary>
    private void LoadThumbnails()
    {
        var imageRows = _allRows.Where(r => r.IsImage && r.Thumbnail is null && !string.IsNullOrEmpty(r.ImagePath)).ToList();
        if (imageRows.Count == 0)
        {
            return;
        }

        const int thumbSize = 64;
        _ = Task.Run(() =>
        {
            foreach (ClipboardHistoryEntryRow row in imageRows)
            {
                Bitmap? thumb = null;
                try
                {
                    using var stream = File.OpenRead(row.ImagePath!);
                    thumb = Bitmap.DecodeToWidth(stream, thumbSize, BitmapInterpolationMode.HighQuality);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Missing/locked file — leave the placeholder; not fatal.
                }

                if (thumb is null)
                {
                    continue;
                }

                var captured = thumb;
                Dispatcher.UIThread.Post(() =>
                {
                    // Match by id; the row may have been evicted before decode
                    // finished (snapshot refresh). Dispose to avoid a leak then.
                    var target = _allRows.FirstOrDefault(r => r.Id == row.Id);
                    if (target is null || target.Thumbnail is not null)
                    {
                        captured.Dispose();
                        return;
                    }
                    target.Thumbnail = captured;
                });
            }
        });
    }

    /// <summary>R54 v2: lazily decodes the FULL-resolution bitmap for an image
    /// row and posts it to <see cref="ClipboardHistoryEntryRow.FullBitmap"/> on
    /// the UI thread. Used when the row is expanded or the "View image…" popup
    /// opens — the collapsed thumbnail is a 64px decode (cheap) and must NOT be
    /// upscaled for the large view (that was the "why is it so blurry" bug).
    /// No-op if already loaded. Decode is off-thread; failures are ignored.</summary>
    private void EnsureFullBitmap(ClipboardHistoryEntryRow row)
    {
        if (row.FullBitmap is not null || string.IsNullOrEmpty(row.ImagePath))
        {
            return;
        }

        string path = row.ImagePath;
        Guid id = row.Id;
        _ = Task.Run(() =>
        {
            Bitmap? full = null;
            try
            {
                using var stream = File.OpenRead(path);
                full = new Bitmap(stream); // full-resolution decode
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Missing/locked file — leave FullBitmap null (expanded view stays
                // blank; not fatal).
            }

            if (full is null)
            {
                return;
            }

            var captured = full;
            Dispatcher.UIThread.Post(() =>
            {
                var target = _allRows.FirstOrDefault(r => r.Id == id);
                if (target is null)
                {
                    captured.Dispose(); // row evicted during decode
                    return;
                }
                if (target.FullBitmap is null)
                {
                    target.FullBitmap = captured;
                }
                else
                {
                    captured.Dispose(); // already loaded (race)
                }
            });
        });
    }

    /// <summary>R54 v1.1/v1.2: pushes the current custom-tag list + per-entry tag
    /// assignments + per-tag icons (from <c>ClipboardHistoryService.Tags</c>).
    /// Rebuilds the custom-tag nav buttons (with icons) and updates the ❤
    /// markers + custom-tag membership on rows. Call on the UI thread.</summary>
    /// <param name="customTags">User-created tag names in display order.</param>
    /// <param name="favoriteById">Entry id → true when favorited.</param>
    /// <param name="customTagsById">Entry id → list of custom tag names assigned
    /// to that entry (empty/missing = no custom tags).</param>
    /// <param name="tagIcons">Custom tag name → emoji icon (v1.2). Missing/empty
    /// = no icon (shows the <c>#</c> prefix).</param>
    public void SetTags(
        IReadOnlyList<string> customTags,
        IReadOnlyDictionary<Guid, bool> favoriteById,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> customTagsById,
        IReadOnlyDictionary<string, string> tagIcons)
    {
        _customTags = customTags;
        _tagIcons = tagIcons;
        foreach (ClipboardHistoryEntryRow row in _allRows)
        {
            row.IsFavorite = favoriteById.TryGetValue(row.Id, out bool fav) && fav;
            row.CustomTags = customTagsById.TryGetValue(row.Id, out IReadOnlyList<string>? tags)
                ? tags
                : [];
        }
        RebuildNav();
        ReapplyFilter();
    }

    /// <summary>R54 v1.2 v6: pushes the user-imported icon library. Stored for
    /// the icon picker's "我的图标" group + tag-icon rendering (user:&lt;name&gt;).
    /// Call on the UI thread.</summary>
    public void SetUserIcons(IReadOnlyList<UserIcon> icons)
    {
        _userIcons = icons;
        RebuildNav();
    }

    /// <summary>Paste the given entry (App delegates to the service). Arg = id.
    /// The window hides itself before raising this for the "复制并粘贴" / double-
    /// click path; "复制（不关闭）" leaves the window open.</summary>
    public event Action<Guid>? PasteRequested;

    /// <summary>Paste the given entry but keep the window open. Arg = id.</summary>
    public event Action<Guid>? CopyRequested;

    /// <summary>Toggle the pinned flag. Arg = id. App re-pushes the snapshot.</summary>
    public event Action<Guid>? PinToggled;

    /// <summary>Delete a single entry. Arg = id. App re-pushes the snapshot.</summary>
    public event Action<Guid>? DeleteRequested;

    /// <summary>R54 v2: add a free-form annotation tag to an entry. Args =
    /// (entry id, tag text). App calls the service then re-pushes the snapshot
    /// so the badge appears. Independent of the custom-tag tab system.</summary>
    public event Action<Guid, string>? EntryTagAdded;

    /// <summary>R54 v2: remove an annotation tag from an entry. Args =
    /// (entry id, tag text). App calls the service then re-pushes the snapshot.</summary>
    public event Action<Guid, string>? EntryTagRemoved;

    /// <summary>R54 v2: clear every entry older than the given one, keeping
    /// pinned/favorited/tagged entries. Arg = the reference entry id. App calls
    /// <c>ClearOlderEntries</c> then re-pushes the snapshot.</summary>
    public event Action<Guid>? ClearOlderRequested;

    /// <summary>R54 v2: dry-run query for the confirmation dialog. Returns
    /// (wouldDelete, wouldKeep) for the entries older than the given one, using
    /// the same protection rule as <see cref="ClearOlderRequested"/>. The
    /// window calls this BEFORE showing the confirm popup so the user sees the
    /// exact toll ("delete N, keep M") and never accidentally nukes everything.
    /// App wires this to <c>PreviewClearOlder</c> on the service.</summary>
    public event Func<Guid, (int wouldDelete, int wouldKeep)>? PreviewClearOlderRequested;

    /// <summary>Footer "设置" clicked — open the clipboard-history settings.</summary>
    public event Action? SettingsRequested;

    // ── R54 v1.1 tag-management events ──
    //
    // The window owns the inline tag-name input panel (a small TextBox in the
    // nav tower), so these events carry the already-resolved name. App just
    // forwards them to ClipboardHistoryService + refreshes the snapshot.

    /// <summary>Create a custom tag with the given (trimmed, non-blank) name.</summary>
    public event Action<string>? CreateCustomTagRequested;

    /// <summary>Rename a custom tag. Args = (oldName, newName).</summary>
    public event Action<string, string>? RenameCustomTagRequested;

    /// <summary>Delete a custom tag. Arg = tag name.</summary>
    public event Action<string>? DeleteCustomTagRequested;

    /// <summary>Assign <paramref name="tagName"/> to the entry. Arg = (entryId, tagName).</summary>
    public event Action<Guid, string>? AssignTagRequested;

    /// <summary>Remove <paramref name="tagName"/> from the entry. Arg = (entryId, tagName).</summary>
    public event Action<Guid, string>? UnassignTagRequested;

    /// <summary>Toggle the favorite flag on an entry. Arg = entryId.</summary>
    public event Action<Guid>? FavoriteToggled;

    /// <summary>R54 v2: set the user's manual group correction on an entry.
    /// Args = (entryId, group). Pass null to revert to the automatic
    /// classification. App calls <c>SetGroupOverride</c> on the service (which
    /// also flips IsSensitive when the target/clear is Sensitive) then
    /// re-pushes the snapshot so the badge + tab membership update. Images are
    /// rejected upstream (the menu item doesn't show for image rows).</summary>
    public event Action<Guid, ClipboardGroup?>? GroupOverrideRequested;

    // ── R54 v1.2: icon + reorder events ──

    /// <summary>Set (or clear, when emoji is blank) the icon for a custom tag.
    /// Args = (tagName, emoji).</summary>
    public event Action<string, string>? SetTagIconRequested;

    /// <summary>Move a custom tag to a new position in the display order.
    /// Args = (tagName, toIndex) — toIndex is 0-based among custom tags only.</summary>
    public event Action<string, int>? ReorderTagRequested;

    // ── R54 v1.2 v6: user-imported icon library events ──

    /// <summary>Import SVG files into the user icon library. Each item is
    /// (fileName, svgContent); App extracts path-data, dedupes, persists.</summary>
    public event Action<IReadOnlyList<(string FileName, string SvgContent)>>? ImportIconsRequested;

    /// <summary>Remove a user-imported icon by name. Arg = icon name (the part
    /// after the <c>user:</c> prefix).</summary>
    public event Action<string>? RemoveUserIconRequested;

    /// <summary>Pin an image entry as an always-on-top floating sticker.
    /// Arg = the image's PNG file path on disk. The App forwards this to the
    /// runtime, which reads the bytes and creates a PinnedScreenshotWindow —
    /// the UI layer stays free of Platform.Windows (same pattern as Gallery's
    /// RequestPin).</summary>
    public event Action<string>? PinOnTopRequested;

    public void PrepareForShutdown() => _allowClose = true;

    // ── Row construction ──

    private ClipboardHistoryEntryRow ToRow(ClipboardEntry entry, bool isArchived = false)
    {
        bool reveal = _revealed.Contains(entry.Id);
        string preview = reveal
            ? ClipboardHistoryStore.BuildPreview(entry.Text, isSensitive: false, maskSensitive: false)
            : ClipboardHistoryStore.BuildPreview(entry.Text, entry.IsSensitive, _maskSensitive);
        string expanded = reveal
            ? ClipboardHistoryStore.BuildExpanded(entry.Text, isSensitive: false, maskSensitive: false)
            : ClipboardHistoryStore.BuildExpanded(entry.Text, entry.IsSensitive, _maskSensitive);

        // R54 v2: image entries get an "Image" badge + a thumbnail path; the
        // text preview is left empty (the thumbnail shows instead). Thumbnails
        // are decoded off-thread after the row is realized (LoadThumbnails).
        string? imagePath = entry.Kind == ClipboardEntryKind.Image && !string.IsNullOrEmpty(entry.ImageFileName)
            ? System.IO.Path.Combine(_imagesDirectory, entry.ImageFileName)
            : null;

        // R54 v2: the effective group drives both the badge and the tab filter.
        // Images are structurally Text (never overridden) and always render an
        // "Image" badge; for text rows, the user's GroupOverride wins over the
        // auto Group when present. GroupLabel carries the *effective* label so
        // the meta-line badge reflects the user's correction; AutoGroup +
        // GroupOverride are passed separately so the "Move to…" submenu can show
        // the Auto target and the current check state.
        ClipboardGroup effective = entry.GroupOverride ?? entry.Group;
        string groupLabel = entry.Kind == ClipboardEntryKind.Image
            ? "Image"
            : GroupToLabel(effective);

        return new ClipboardHistoryEntryRow
        {
            Id = entry.Id,
            Kind = entry.Kind,
            ImagePath = imagePath,
            Text = entry.Text,
            Preview = entry.Kind == ClipboardEntryKind.Image ? string.Empty : preview,
            ExpandedText = entry.Kind == ClipboardEntryKind.Image ? string.Empty : expanded,
            GroupLabel = groupLabel,
            AutoGroup = entry.Group,
            GroupOverride = entry.Kind == ClipboardEntryKind.Image ? null : entry.GroupOverride,
            SourceLabel = entry.SourceProcessName ?? string.Empty,
            TimeLabel = FormatTime(entry.CapturedAt),
            IsSensitive = entry.IsSensitive,
            IsPinned = entry.IsPinned,
            EntryTags = entry.EntryTags,
            // R103: archive flag drives the "Archived" badge + restricted menu.
            // Archived entries are never pinned (pin prevents eviction), so the
            // live IsPinned above is always false for them — but we don't force
            // it here; the source value is authoritative.
            IsArchived = isArchived,
        };
    }

    private static string GroupToLabel(ClipboardGroup group) => group switch
    {
        ClipboardGroup.Sensitive => Strings.Clip_Group_Sensitive,
        ClipboardGroup.Link => Strings.Clip_Group_Link,
        ClipboardGroup.Json => Strings.Clip_Group_Json,
        ClipboardGroup.Code => Strings.Clip_Group_Code,
        ClipboardGroup.Shell => Strings.Clip_Group_Command,
        ClipboardGroup.Number => Strings.Clip_Group_Number,
        _ => string.Empty, // Text → no badge
    };

    private static string FormatTime(DateTimeOffset capturedAt)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan age = now - capturedAt.ToLocalTime();
        if (age.TotalMinutes < 1) return Strings.Common_TimeJustNow;
        if (age.TotalHours < 1) return string.Format(Strings.Common_TimeMinutesAgo, (int)age.TotalMinutes);
        if (age.TotalDays < 1) return capturedAt.ToLocalTime().ToString("HH:mm");
        if (age.TotalDays < 7) return string.Format(Strings.Common_TimeDaysAgo, (int)age.TotalDays);
        return capturedAt.ToLocalTime().ToString("MM-dd");
    }

    // ── Nav bar construction ──

    private void RebuildNav()
    {
        NavButtonsPanel.Children.Clear();
        // R54 v2: mirror the visible-tab order for Ctrl+Tab cycling. Rebuilt in
        // lockstep with the buttons below — each AddToNav(...) append keeps the
        // two lists parallel so the keyboard handler can index by position.
        _navOrder = [(ClipboardTab.All, null)];
        NavButtonsPanel.Children.Add(BuildNavButton(Strings.Clip_Tab_All, ClipboardTab.All, isActive: _activeTab == ClipboardTab.All));

        // R54 v2: built-in tabs are always shown (even when no entry matches the
        // group) so the nav structure is stable. Trimmed to the categories the
        // user actually wants: JSON folded into Code, Contacts/Numbers dropped
        // (too niche), Pinned dropped (it's a sort effect, not a category —
        // pinned entries already float to the top of every list).
        AddBuiltIn(Strings.Clip_Tab_Links, ClipboardTab.Link);
        AddBuiltIn(Strings.Clip_Tab_Code, ClipboardTab.Code);
        AddBuiltIn(Strings.Clip_Tab_Commands, ClipboardTab.Shell);
        AddBuiltIn(Strings.Clip_Tab_Sensitive, ClipboardTab.Sensitive);
        AddBuiltIn(Strings.Clip_Tab_Images, ClipboardTab.Image);
        AddBuiltIn(Strings.Clip_Tab_Favorites, ClipboardTab.Favorite);

        // Custom tabs — always shown so the user can assign to them even when
        // no entry is tagged yet. Right-click → rename/delete/set-icon (App
        // prompts). These are the only user-managed tabs; built-ins are fixed.
        foreach (string tagName in _customTags)
        {
            bool isActive = _activeTab == ClipboardTab.Custom && _activeCustomTagName == tagName;
            _navOrder.Add((ClipboardTab.Custom, tagName));
            NavButtonsPanel.Children.Add(BuildCustomTagNavButton(tagName, isActive));
        }

        // + 新建 tab button (always present, distinct accent-colored label).
        NavButtonsPanel.Children.Add(BuildCreateTagNavButton());
    }

    /// <summary>R54 v2: appends a built-in tab unconditionally (always shown,
    /// even when empty) and keeps <c>_navOrder</c> in sync for Ctrl+Tab cycling.
    /// Replaces the old <c>AppendBuiltInIfAny</c> which hid tabs with no matching
    /// entry — the user preferred a stable, fixed nav.</summary>
    private void AddBuiltIn(string label, ClipboardTab tab)
    {
        _navOrder.Add((tab, null));
        NavButtonsPanel.Children.Add(BuildNavButton(label, tab, isActive: _activeTab == tab));
    }

    private Button BuildNavButton(string label, ClipboardTab tab, bool isActive)
    {
        // R54 v2: split the "🔗 Links" label into an emoji glyph + the text part
        // so the layout matches the custom-tab rows (StackPanel of [icon | name])
        // exactly — same column rhythm, same TextTrimming on the name, and a
        // ToolTip so a trimmed label ("Sensit…") is still discoverable.
        string emoji = string.Empty;
        string name = label;
        int space = label.IndexOf(' ');
        if (space > 0)
        {
            emoji = label[..space];
            name = label[(space + 1)..];
        }

        var iconBlock = new TextBlock
        {
            Text = emoji,
            FontSize = GetFontSize("ByhFontSizeBodyLarge"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var nameBlock = new TextBlock
        {
            Text = name,
            FontSize = GetFontSize("ByhFontSizeBodySmall"),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var button = new Button
        {
            Classes = { "SettingsNav" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 5),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { iconBlock, nameBlock },
            },
        };
        if (isActive)
        {
            button.Classes.Add("Active");
        }
        // ToolTip shows the full name if the label is trimmed (e.g. "Sensit…").
        ToolTip.SetTip(button, name);
        button.Click += (_, _) => SelectBuiltInTab(tab);
        return button;
    }

    /// <summary>Lookup for the Lucide icon matching a stored icon value. The
    /// value is a Lucide slug prefixed with <c>lucide:</c> (e.g.
    /// <c>lucide:tag</c>); a bare emoji char or unknown value returns null
    /// (caller renders it as text or a "#" placeholder).</summary>
    private static LucideIcons.Icon? TryGetLucideIcon(string? iconValue)
    {
        if (string.IsNullOrEmpty(iconValue) || !iconValue.StartsWith("lucide:", StringComparison.Ordinal))
        {
            return null;
        }
        string slug = iconValue["lucide:".Length..];
        foreach (LucideIcons.Icon icon in LucideIcons.Catalog)
        {
            if (string.Equals(icon.Name, slug, StringComparison.Ordinal))
            {
                return icon;
            }
        }
        return null;
    }

    /// <summary>Lookup for the user-imported icon matching a stored icon value.
    /// The value is a name prefixed with <c>user:</c> (e.g. <c>user:my-tag</c>);
    /// returns null otherwise or when not found.</summary>
    private UserIcon? TryGetUserIcon(string? iconValue)
    {
        if (string.IsNullOrEmpty(iconValue) ||
            !iconValue.StartsWith(UserIconLibrary.StoragePrefix, StringComparison.Ordinal))
        {
            return null;
        }
        string name = iconValue[UserIconLibrary.StoragePrefix.Length..];
        foreach (UserIcon ic in _userIcons)
        {
            if (string.Equals(ic.Name, name, StringComparison.Ordinal))
            {
                return ic;
            }
        }
        return null;
    }

    /// <summary>Builds the visual for a tag icon value. Returns:
    /// <list type="bullet">
    ///   <item>A <see cref="Path"/> (13×13, stroke = themed) when the value is a
    ///   Lucide slug (<c>lucide:tag</c>) or a user icon (<c>user:my-tag</c>).</item>
    ///   <item>A <see cref="TextBlock"/> with the emoji char when the value is a
    ///   legacy emoji (backward compat with v1.2-v4 tags).</item>
    ///   <item><c>null</c> when the value is empty (caller shows "#" or
    ///   nothing).</item>
    /// </list></summary>
    private Control? BuildTagIconVisual(string? iconValue)
    {
        if (string.IsNullOrEmpty(iconValue))
        {
            return null;
        }
        string? pathData = null;
        if (TryGetLucideIcon(iconValue) is { } lucide)
        {
            pathData = lucide.PathData;
        }
        else if (TryGetUserIcon(iconValue) is { } user)
        {
            pathData = user.PathData;
        }
        if (pathData is not null)
        {
            return new Path
            {
                Classes = { "TagIcon" },
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Data = StreamGeometry.Parse(pathData),
            };
        }
        // Legacy emoji: render as text.
        return new TextBlock
        {
            Text = iconValue,
            FontSize = GetFontSize("ByhFontSizeBodyLarge"),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private Border BuildCustomTagNavButton(string tagName, bool isActive)
    {
        // v1.2 v6 (7th attempt): the WHOLE row is a single Border (NOT a Button
        // and NOT a [handle | button] split). This removes the ugly ⠿ drag handle
        // the user rejected, AND fixes the drag for good: because the row is not
        // a Button, there is no ClickableTemplatedControl internal-capture state
        // machine to fight, and press-then-capture on the row itself is clean.
        // The row owns both interactions:
        //   • press + move > threshold  → drag-reorder (capture the row)
        //   • press + release < threshold → click → select the tab
        //   • right-press → ContextMenu (Avalonia auto-opens on the Border)
        // Visual parity with Button.SettingsNav is via the Border.TagNavRow style
        // (same padding/radius/hover/active fills), so custom tabs look identical
        // to the built-in tabs above them.
        string? icon = _tagIcons.TryGetValue(tagName, out string? e) && !string.IsNullOrEmpty(e) ? e : null;
        Control? iconVisual = BuildTagIconVisual(icon);

        var nameLabel = new TextBlock
        {
            Classes = { "TagLabel" },
            Text = tagName,
            FontSize = GetFontSize("ByhFontSizeBodySmall"),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Control iconOrHash = iconVisual ?? new TextBlock
        {
            Classes = { "TagLabel" },
            Text = "#",
            FontSize = GetFontSize("ByhFontSizeBody"),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (IBrush?)Application.Current?.FindResource("ByhTextSecondaryBrush"),
        };

        var row = new Border
        {
            Classes = { "TagNavRow" },
            Tag = tagName, // identifies the tag for drag + ContextMenu handlers
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                // Tight spacing so the icon sits just left of the name, matching
                // how the built-in tabs read "📋 全部" (emoji + space + label).
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { iconOrHash, nameLabel },
            },
        };
        if (isActive)
        {
            row.Classes.Add("Active");
        }
        ToolTip.SetTip(row, string.Format(Strings.Clip_TagNavTooltip, tagName));

        // Right-click → manage custom tag (rename / set icon / delete).
        var menu = new ContextMenu();
        var renameItem = new MenuItem { Header = Strings.Clip_Tag_Rename, Tag = tagName };
        renameItem.Click += (_, _) => ShowTagInputPanel(TagInputMode.Rename, currentName: tagName);
        var iconItem = new MenuItem { Header = Strings.Clip_Tag_SetIcon, Tag = tagName };
        iconItem.Click += (_, _) => ShowIconPickerPanel(tagName);
        var clearIconItem = new MenuItem
        {
            Header = Strings.Clip_Tag_ClearIcon,
            Tag = tagName,
            IsEnabled = icon is not null,
        };
        clearIconItem.Click += (_, _) => SetTagIconRequested?.Invoke(tagName, string.Empty);
        var deleteItem = new MenuItem { Header = Strings.Clip_Tag_Delete, Tag = tagName };
        deleteItem.Click += (_, _) => DeleteCustomTagRequested?.Invoke(tagName);
        menu.Items.Add(renameItem);
        menu.Items.Add(iconItem);
        menu.Items.Add(clearIconItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);
        row.ContextMenu = menu;

        // The row owns its pointer routing. See OnTagRowPointer* for the
        // press→arm→(move>threshold→drag | release<threshold→click) logic.
        row.PointerPressed += OnTagRowPointerPressed;
        row.PointerMoved += OnTagRowPointerMoved;
        row.PointerReleased += OnTagRowPointerReleased;

        return row;
    }

    // ── R54 v1.2: drag-to-reorder custom tags ──
    //
    // A click on a custom-tag button selects that tab (SelectCustomTagTab). A
    // drag (movement > TagDragThreshold) reorders it instead — we track an
    // insert index via the pointer's Y position over NavButtonsPanel and draw a
    // 2 px gold line at the drop target. Release finalizes via
    // ReorderTagRequested. We attach PointerPressed/Moved/Released on each
    // custom-tag button; the handlers read _dragTagName to know which tag moves.

    // ── R54 v1.2 v6: whole-row drag + click ──
    //
    // The custom-tag row is a plain Border (NOT a Button). It owns both the
    // click (select tab) and the drag (reorder) via three pointer handlers:
    //   press   → arm + capture the row Border (clean: no Button state machine)
    //   moved   → if past threshold, commit a drag (track insert index + dim)
    //   release → if dragged, finalize reorder; if not, treat as a click
    // Because we capture on press and mark the press handled, the ScrollViewer
    // never starts a pan and never steals the move stream — the exact failure
    // mode that broke attempts 1-6.

    private void OnTagRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border row || row.Tag is not string name)
        {
            return;
        }
        if (e.GetCurrentPoint(row).Properties.PointerUpdateKind
            != PointerUpdateKind.LeftButtonPressed)
        {
            return; // right-press: leave to Avalonia to open the ContextMenu
        }
        if (!_customTags.Contains(name, StringComparer.Ordinal))
        {
            return;
        }

        _dragTagName = name;
        _dragTagRow = row;
        _dragStartPanel = e.GetPosition(NavButtonsPanel);
        _dragCommitted = false;
        _dragInsertIndex = -1;
        // Capture on the row immediately. The row is a plain Border, so there is
        // no Button-internal capture to fight, and capture-on-press also prevents
        // the ScrollViewer from starting a pan (which was stealing moves).
        e.Pointer.Capture(row);
        e.Handled = true;
    }

    private void OnTagRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTagRow is null || _dragTagName is null)
        {
            return;
        }
        Point now = e.GetPosition(NavButtonsPanel);
        if (!_dragCommitted)
        {
            double dy = now.Y - _dragStartPanel.Y;
            double dx = now.X - _dragStartPanel.X;
            if (Math.Abs(dx) < TagDragThreshold && Math.Abs(dy) < TagDragThreshold)
            {
                return;
            }
            _dragCommitted = true;
            _dragTagRow.Classes.Add("Dragging"); // visual: row is "in flight"
        }
        UpdateDragInsertIndex(now);
    }

    private void OnTagRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragTagName is null || _dragTagRow is null)
        {
            return;
        }
        try { e.Pointer.Capture(null); } catch { /* best-effort */ }

        Border row = _dragTagRow;
        if (_dragCommitted)
        {
            int toIndex = _dragInsertIndex;
            string name = _dragTagName;
            row.Classes.Remove("Dragging");
            ResetDragState();
            if (toIndex >= 0)
            {
                ReorderTagRequested?.Invoke(name, toIndex);
            }
            return;
        }
        // Under-threshold release = a click. The row is not a Button, so no Click
        // routed; select the tab ourselves.
        string clickedName = _dragTagName;
        ResetDragState();
        SelectCustomTagTab(clickedName);
    }

    /// <summary>Computes the custom-tag insertion index at the pointer Y over
    /// the nav panel and positions the floating gold caret. <paramref name=
    /// "panelPos"/> is in NavButtonsPanel coordinates. Stores the result in
    /// <see cref="_dragInsertIndex"/>.</summary>
    private void UpdateDragInsertIndex(Point panelPos)
    {
        // Custom-tag rows are Border-wrapped Grids in NavButtonsPanel. Identify
        // them by their inner Grid's string Tag matching _customTags. We never
        // insert an indicator child into the StackPanel (that would shift the
        // rows below and corrupt the Bounds we read here); instead a separate
        // overlay Border is positioned over the tower.
        // Custom-tag rows are Borders carrying Tag = tagName. Identify them by
        // that string Tag matching _customTags. We never insert an indicator
        // child into the StackPanel (that would shift the rows below and corrupt
        // the Bounds we read here); instead a separate overlay Border is
        // positioned over the tower.
        var customRows = NavButtonsPanel.Children
            .OfType<Border>()
            .Where(b => b.Tag is string name && _customTags.Contains(name, StringComparer.Ordinal))
            .ToList();

        if (customRows.Count == 0)
        {
            _dragInsertIndex = -1;
            HideDragCaret();
            return;
        }

        // Find insertion slot: the index of the first custom row whose vertical
        // center is below the pointer.
        int slot = customRows.Count;
        double caretY = customRows[^1].Bounds.Bottom; // default: after last
        for (int i = 0; i < customRows.Count; i++)
        {
            double top = customRows[i].Bounds.Top;
            double bottom = customRows[i].Bounds.Bottom;
            double center = (top + bottom) / 2.0;
            if (panelPos.Y < center)
            {
                slot = i;
                caretY = top; // caret sits at the top edge of this row
                break;
            }
        }
        _dragInsertIndex = slot;
        ShowDragCaret(caretY);
    }

    /// <summary>Shows (or repositions) the floating gold insertion caret at the
    /// given Y within NavButtonsPanel's coordinate space. The caret is a thin
    /// overlay Border parented on NavTowerOverlay (a Canvas in the AXAML) so it
    /// never disturbs the StackPanel layout. Created lazily on first use.</summary>
    private void ShowDragCaret(double yInPanel)
    {
        if (_dragCaret is null)
        {
            var gold = (IBrush?)Application.Current?.FindResource("ByhGoldBrush");
            _dragCaret = new Border
            {
                Background = gold ?? Brushes.Gold,
                CornerRadius = new CornerRadius(1),
                Height = 2,
                Width = 80,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsVisible = false,
            };
            if (NavTowerOverlay is not null)
            {
                NavTowerOverlay.Children.Add(_dragCaret);
            }
        }

        if (NavTowerOverlay is Canvas canvas && _dragCaret is { } caret)
        {
            Point originInCanvas = NavButtonsPanel.TranslatePoint(new Point(0, yInPanel), canvas)
                ?? new Point(0, yInPanel);
            Canvas.SetTop(caret, originInCanvas.Y - 1);
            Canvas.SetLeft(caret, 6);
            caret.IsVisible = true;
        }
    }

    private void HideDragCaret()
    {
        if (_dragCaret is not null)
        {
            _dragCaret.IsVisible = false;
        }
    }

    private void ResetDragState()
    {
        if (_dragTagRow is not null)
        {
            _dragTagRow.Classes.Remove("Dragging");
        }
        _dragTagRow = null;
        _dragTagName = null;
        _dragCommitted = false;
        _dragInsertIndex = -1;
        HideDragCaret();
    }

    // ── R54 v1.2: emoji icon picker ──

    // A curated catalog of emoji that read well as small tag icons at 11pt.
    // ~96 entries, grouped loosely by theme so the picker scrolls but stays
    // browsable. The WrapPanel lays them out 8-wide.
    private static readonly string[] EmojiCatalog =
    [
        // work / objects
        "💼","📁","📂","🗂️","📎","📌","📍","📋",
        "📝","✏️","🖊️","🖌️","📐","📏","🔑","🔒",
        // tech / code
        "⚙️","🔧","🧪","🧠","💻","🖥️","⌨️","🖥️",
        "🔍","💡","🔋","🧩","⚙️","🛠️","📡","💽",
        // communication
        "💬","📧","📨","📩","📮","📣","📢","📞",
        "🔔","✅","❌","⚠️","❓","❗","💭","🗨️",
        // time / planning
        "⏰","⏳","📅","📆","🗓️","⌚","⏱️","🕘",
        "🚀","🎯","🏁","📊","📈","📉","📇","🗳️",
        // money / shopping
        "💰","💳","💸","🛒","🏷️","🎁","💎","💰",
        // life / leisure
        "❤️","⭐","🌟","✨","🔥","🌈","☕","🍵",
        "🎵","🎶","🎨","🎬","🎮","📚","📖","🌍",
        "🏠","🚗","✈️","🏃","💪","😊","🍀","🌹",
    ];

    // R54 v1.2: the active emoji-picker popup (null when closed). Tracked so we
    // can close it before opening another / on tag refresh.
    private Popup? _iconPicker;

    /// <summary>Shows the icon picker as a scrollable Popup for the given tag.
    /// v1.2 v5: now offers Lucide vector icons (grouped, ~114) plus a legacy
    /// emoji row. Selecting an icon (or "无图标") raises SetTagIconRequested with
    /// <c>lucide:&lt;name&gt;</c>, a bare emoji char, or empty. Uses a plain
    /// Popup + Buttons (NOT a ContextMenu) because Avalonia's menu routing
    /// swallows clicks on Buttons nested inside MenuItems.</summary>
    private void ShowIconPickerPanel(string tagName)
    {
        _iconPicker?.Close();

        var accent = (IBrush?)Application.Current?.FindResource("ByhAccentBrush");
        var secondary = (IBrush?)Application.Current?.FindResource("ByhTextSecondaryBrush");

        // Builds a single icon cell: a Button whose content is a Path (Lucide or
        // user-imported) or a TextBlock (emoji). Captures the value to store on
        // click. For user icons, an optional removeMenuItem attaches a right-click
        // "删除此图标" so users can prune their library from the picker.
        Button MakeCell(string display, string storeValue, string? pathData)
        {
            Control content;
            if (!string.IsNullOrEmpty(pathData))
            {
                content = new Path
                {
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.Uniform,
                    StrokeThickness = 1.7,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Stroke = accent ?? Brushes.Goldenrod,
                    Data = StreamGeometry.Parse(pathData),
                };
            }
            else
            {
                content = new TextBlock { Text = display, FontSize = GetFontSize("ByhFontSizeSubheading") };
            }
            var cell = new Button
            {
                Content = content,
                Width = 34,
                Height = 34,
                Padding = new Thickness(2),
                Margin = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(cell, display);
            string captured = storeValue;
            cell.Click += (_, _) =>
            {
                SetTagIconRequested?.Invoke(tagName, captured);
                _iconPicker?.Close();
            };
            return cell;
        }

        WrapPanel MakeWrap() => new()
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 34,
            ItemHeight = 34,
        };

        // ── User-imported icons ("我的图标") + import button ──
        // Shows first so the user's own library is immediately reachable, and
        // includes a "+ 导入图标包…" button (opens an SVG file picker) and a
        // per-icon right-click "删除" so the library is fully manageable here.
        var userPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
        var userHeaderRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2, 0, 2),
        };
        userHeaderRow.Children.Add(new TextBlock
        {
            Text = Strings.Clip_IconPicker_MyIcons,
            FontSize = GetFontSize("ByhFontSizeCaption"),
            FontWeight = FontWeight.SemiBold,
            Foreground = secondary,
        });
        var importButton = new Button
        {
            Content = Strings.Clip_IconPicker_Import,
            FontSize = GetFontSize("ByhFontSizeCaption"),
            Padding = new Thickness(6, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(importButton, 1);
        userHeaderRow.Children.Add(importButton);
        userPanel.Children.Add(userHeaderRow);

        WrapPanel userWrap = MakeWrap();
        if (_userIcons.Count == 0)
        {
            userPanel.Children.Add(new TextBlock
            {
                Text = Strings.Clip_IconPicker_ImportHint,
                FontSize = GetFontSize("ByhFontSizeCaption"),
                Foreground = secondary,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (UserIcon ic in _userIcons)
            {
                Button cell = MakeCell(ic.Name, UserIconLibrary.StoragePrefix + ic.Name, ic.PathData);
                // Right-click → delete this user icon from the library.
                var cellMenu = new ContextMenu();
                var del = new MenuItem { Header = Strings.Clip_IconPicker_Delete, Tag = ic.Name };
                string capturedName = ic.Name;
                del.Click += (_, _) =>
                {
                    RemoveUserIconRequested?.Invoke(capturedName);
                    _iconPicker?.Close();
                };
                cellMenu.Items.Add(del);
                cell.ContextMenu = cellMenu;
                userWrap.Children.Add(cell);
            }
            userPanel.Children.Add(userWrap);
        }
        importButton.Click += async (_, _) =>
        {
            await ImportIconFilesAsync();
            _iconPicker?.Close(); // reopen fresh after the library updates
        };

        // ── Lucide groups ──
        var lucidePanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
        string? lastGroup = null;
        WrapPanel? currentWrap = null;
        foreach (LucideIcons.Icon icon in LucideIcons.Catalog)
        {
            if (icon.Group != lastGroup)
            {
                lastGroup = icon.Group;
                var groupHeader = new TextBlock
                {
                    Text = icon.Group,
                    FontSize = GetFontSize("ByhFontSizeCaption"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 4, 0, 2),
                    Foreground = secondary,
                };
                currentWrap = MakeWrap();
                lucidePanel.Children.Add(groupHeader);
                lucidePanel.Children.Add(currentWrap);
            }
            currentWrap!.Children.Add(MakeCell(icon.Label, "lucide:" + icon.Name, icon.PathData));
        }

        // ── Legacy emoji row ──
        var emojiHeader = new TextBlock
        {
            Text = Strings.Clip_IconPicker_Emoji,
            FontSize = GetFontSize("ByhFontSizeCaption"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 2),
            Foreground = secondary,
        };
        var emojiWrap = MakeWrap();
        foreach (string emoji in EmojiCatalog)
        {
            emojiWrap.Children.Add(MakeCell(emoji, emoji, null));
        }

        var noneButton = new Button
        {
            Content = Strings.Clip_IconPicker_None,
            FontSize = GetFontSize("ByhFontSizeBodyLarge"),
            Padding = new Thickness(8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
        };
        noneButton.Click += (_, _) =>
        {
            SetTagIconRequested?.Invoke(tagName, string.Empty);
            _iconPicker?.Close();
        };

        // The whole catalog scrolls; the title + 无图标 stay pinned on top.
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 360,
            MaxWidth = 320,
            Content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children = { userPanel, lucidePanel, emojiHeader, emojiWrap },
            },
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children =
            {
                new TextBlock
                {
                    Text = string.Format(Strings.Clip_IconPicker_ChooseFor, tagName),
                    FontSize = GetFontSize("ByhFontSizeBody"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 0, 0, 6),
                    Classes = { "Muted" },
                },
                noneButton,
                scroll,
            },
        };

        var card = new Border
        {
            Child = panel,
            Background = (IBrush?)Application.Current?.FindResource("ByhSurfaceBrush"),
            BorderBrush = (IBrush?)Application.Current?.FindResource("ByhGoldBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
        };
        card.SetValue(Border.BoxShadowProperty, Application.Current?.FindResource("ByhShadowMedium"));

        var popup = new Popup
        {
            Child = card,
            Placement = PlacementMode.Right,
            PlacementTarget = this,
            HorizontalOffset = 4,
            VerticalOffset = 0,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = false,
        };
        _iconPicker = popup;
        ((ISetLogicalParent)popup).SetParent(this);
        AttachPopupEntrance(popup, card); // R100 动画3
        popup.Open();
    }

    /// <summary>Opens a multi-select SVG file picker, reads each file, and raises
    /// <see cref="ImportIconsRequested"/> with (fileName, svgContent) pairs. App
    /// extracts path-data + persists. Supports both single icons and whole icon
    /// packs (e.g. a Lucide/Tabler folder export).</summary>
    private async Task ImportIconFilesAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Clip_IconPicker_ImportTitle,
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("SVG icons") { Patterns = ["*.svg"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] },
            ],
        });
        if (files.Count == 0) return;

        var pairs = new List<(string FileName, string SvgContent)>();
        foreach (var file in files)
        {
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                string svg = System.IO.File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(svg))
                {
                    pairs.Add((System.IO.Path.GetFileName(path), svg));
                }
            }
            catch
            {
                // skip unreadable files
            }
        }
        if (pairs.Count > 0)
        {
            ImportIconsRequested?.Invoke(pairs);
        }
    }

    private Button BuildCreateTagNavButton()
    {
        var accent = Application.Current?.FindResource("ByhAccentBrush") as IBrush;
        var button = new Button
        {
            Classes = { "SettingsNav" },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 5),
            Content = new TextBlock
            {
                Text = Strings.Clip_NewTab,
                FontSize = GetFontSize("ByhFontSizeBodySmall"),
                FontWeight = FontWeight.SemiBold,
                Foreground = accent,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        button.Click += (_, _) => ShowTagInputPanel(TagInputMode.Create);
        return button;
    }

    // ── R54 v1.2: tag-name input via a Popup (not a separate Window — a
    //    transparent frameless dialog Window shown over another AcrylicBlur
    //    window crashes the Win32 compositor, so we use an in-window Popup that
    //    hosts a small TextBox + confirm/cancel card).

    private enum TagInputMode { Create, Rename }

    // The active tag-name input popup (null when closed). Tracked so we close it
    // before opening another / on refresh.
    private Popup? _tagNamePopup;
    private TagInputMode _tagNameMode;
    private string? _tagNameRenameOld;
    private Guid? _tagNameAssignOnCreate;

    private void ShowTagInputPanel(TagInputMode mode, string? currentName = null, Guid? assignOnCreateEntryId = null)
    {
        _tagNamePopup?.Close();
        _tagNameMode = mode;
        _tagNameRenameOld = currentName;
        _tagNameAssignOnCreate = mode == TagInputMode.Create ? assignOnCreateEntryId : null;

        var title = new TextBlock
        {
            Text = mode == TagInputMode.Rename ? string.Format(Strings.Clip_RenameTo, currentName) : Strings.Clip_NewTag,
            FontSize = GetFontSize("ByhFontSizeBody"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
            Classes = { "Kicker" },
        };

        var input = new TextBox
        {
            Text = mode == TagInputMode.Rename ? currentName : string.Empty,
            FontSize = GetFontSize("ByhFontSizeSubheading"),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(6, 4),
            PlaceholderText = Strings.Clip_TagNamePlaceholder,
        };

        var error = new TextBlock
        {
            FontSize = GetFontSize("ByhFontSizeCaption"),
            Foreground = Brushes.IndianRed,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var confirmBtn = new Button
        {
            Content = Strings.Common_Confirm,
            Classes = { "Primary" },
            FontSize = GetFontSize("ByhFontSizeBody"),
            Padding = new Thickness(14, 5),
            IsEnabled = !string.IsNullOrWhiteSpace(input.Text),
        };
        var cancelBtn = new Button
        {
            Content = Strings.Common_Cancel,
            FontSize = GetFontSize("ByhFontSizeBody"),
            Padding = new Thickness(14, 5),
        };

        // v1.2 v3: pendingIcon holds the emoji chosen from the inline picker row
        // (null = no icon). Declared before Confirm() because Confirm captures it
        // — C# requires a captured local to be declared before the local function
        // that uses it.
        string? pendingIcon = mode == TagInputMode.Rename && currentName is not null
            ? (_tagIcons.TryGetValue(currentName, out string? existing) ? existing : null)
            : null;

        // Confirm logic: validate + raise the event + close.
        // v1.2 v3: in Create mode, if the user picked an emoji from the inline
        // picker row, we set it right after creating the tag (SetTagIconRequested
        // is a no-op if the name doesn't exist yet, so create-then-icon).
        void Confirm()
        {
            string? raw = input.Text?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                error.Text = Strings.Clip_Error_NameEmpty;
                return;
            }
            bool exists = _customTags.Contains(raw, StringComparer.Ordinal);
            if (mode == TagInputMode.Create && exists)
            {
                error.Text = Strings.Clip_Error_NameExists;
                return;
            }
            if (mode == TagInputMode.Rename && currentName is not null && raw != currentName && exists)
            {
                error.Text = Strings.Clip_Error_NameExists;
                return;
            }

            if (mode == TagInputMode.Rename && currentName is not null)
            {
                RenameCustomTagRequested?.Invoke(currentName, raw);
                // Apply a freshly picked icon to the renamed tag too.
                if (!string.IsNullOrEmpty(pendingIcon))
                {
                    SetTagIconRequested?.Invoke(raw, pendingIcon);
                }
            }
            else
            {
                CreateCustomTagRequested?.Invoke(raw);
                if (!string.IsNullOrEmpty(pendingIcon))
                {
                    SetTagIconRequested?.Invoke(raw, pendingIcon);
                }
                if (_tagNameAssignOnCreate is Guid entryId)
                {
                    AssignTagRequested?.Invoke(entryId, raw);
                }
            }
            _tagNamePopup?.Close();
        }

        // v1.2 v5: inline icon picker row. A short curated subset of Lucide
        // icons (rendered as vector Paths) + a "更多…" cell that opens the full
        // picker + a "无" cell that clears the selection. Selecting stores
        // "lucide:<slug>" in pendingIcon (declared above) and highlights the
        // chosen cell with a gold border.
        var iconRowLabel = new TextBlock
        {
            Text = Strings.Clip_IconOptional,
            FontSize = GetFontSize("ByhFontSizeCaption"),
            Margin = new Thickness(0, 2, 0, 0),
            Classes = { "Muted" },
        };
        var iconRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 26,
            ItemHeight = 26,
            MaxWidth = 300,
        };
        // Curated shortlist of the most "tag-like" Lucide slugs.
        string[] shortLucide =
        [
            "tag","star","heart","bookmark","folder","link","code",
            "terminal","lightbulb","target","rocket","flag","pin",
            "key","settings","book","mail","user","palette","music",
        ];
        var accent = (IBrush?)Application.Current?.FindResource("ByhAccentBrush");
        Button? selectedIconBtn = null;
        void MarkSelected(Button btn, string storeValue)
        {
            if (selectedIconBtn is { } prev)
            {
                prev.BorderBrush = Brushes.Transparent;
                prev.BorderThickness = new Thickness(0);
            }
            selectedIconBtn = btn;
            btn.BorderBrush = (IBrush?)Application.Current?.FindResource("ByhGoldBrush");
            btn.BorderThickness = new Thickness(1.5);
            pendingIcon = storeValue;
        }
        // Build a Lucide icon cell.
        Button MakeInlineCell(string slug)
        {
            string storeValue = "lucide:" + slug;
            LucideIcons.Icon? li = TryGetLucideIcon(storeValue);
            Control content = li is not null
                ? new Path
                {
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform,
                    StrokeThickness = 1.7,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Stroke = accent ?? Brushes.Goldenrod,
                    Data = StreamGeometry.Parse(li.PathData),
                }
                : new TextBlock { Text = "?", FontSize = GetFontSize("ByhFontSizeBodyLarge") };
            var cell = new Button
            {
                Content = content,
                Padding = new Thickness(1),
                Margin = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(cell, li?.Label ?? slug);
            if (pendingIcon == storeValue)
            {
                MarkSelected(cell, storeValue);
            }
            cell.Click += (_, _) => MarkSelected(cell, storeValue);
            return cell;
        }
        foreach (string slug in shortLucide)
        {
            iconRow.Children.Add(MakeInlineCell(slug));
        }
        // Hint: the full icon catalog (114 Lucide + emoji) is available after
        // creation via the tag's right-click "设置图标…" menu. The inline row
        // keeps to a compact shortlist so the create dialog stays small.
        // "无" clears the selection.
        var noneCell = new Button
        {
            Content = "✕",
            FontSize = GetFontSize("ByhFontSizeBody"),
            Padding = new Thickness(1),
            Margin = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(noneCell, "No icon");
        noneCell.Click += (_, _) =>
        {
            if (selectedIconBtn is { } prev)
            {
                prev.BorderBrush = Brushes.Transparent;
                prev.BorderThickness = new Thickness(0);
            }
            selectedIconBtn = null;
            pendingIcon = null;
        };
        iconRow.Children.Add(noneCell);

        confirmBtn.Click += (_, _) => Confirm();
        cancelBtn.Click += (_, _) => _tagNamePopup?.Close();
        input.TextChanged += (_, _) =>
        {
            confirmBtn.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            error.Text = string.Empty;
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Confirm(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _tagNamePopup?.Close(); }
        };

        var card = new Border
        {
            Background = (IBrush?)Application.Current?.FindResource("ByhSurfaceBrush"),
            BorderBrush = (IBrush?)Application.Current?.FindResource("ByhGoldBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 6,
                Children =
                {
                    title,
                    input,
                    iconRowLabel,
                    iconRow,
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                        ColumnSpacing = 8,
                        Children =
                        {
                            error,
                            cancelBtn,
                            confirmBtn,
                        },
                    },
                },
            },
        };
        // Place error/cancel/confirm in the grid columns.
        Grid.SetColumn(error, 0);
        Grid.SetColumn(cancelBtn, 1);
        Grid.SetColumn(confirmBtn, 2);
        card.SetValue(Border.BoxShadowProperty, Application.Current?.FindResource("ByhShadowMedium"));

        var popup = new Popup
        {
            Child = card,
            Placement = PlacementMode.Center,
            PlacementTarget = this,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = false,
        };
        _tagNamePopup = popup;
        ((ISetLogicalParent)popup).SetParent(this);
        popup.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        AttachPopupEntrance(popup, card); // R100 动画3
        popup.Open();
    }

    private void SelectBuiltInTab(ClipboardTab tab)
    {
        _activeTab = tab;
        _activeCustomTagName = null;
        RebuildNav();
        ReapplyFilter();
    }

    private void SelectCustomTagTab(string tagName)
    {
        _activeTab = ClipboardTab.Custom;
        _activeCustomTagName = tagName;
        RebuildNav();
        ReapplyFilter();
    }

    /// <summary>R54 v2: cycles the active nav tab forward (+1, Ctrl+Tab) or
    /// backward (-1, Ctrl+Shift+Tab). Wraps around. No-op when fewer than two
    /// tabs are visible. Finds the current tab in <c>_navOrder</c> by matching
    /// both <c>_activeTab</c> and <c>_activeCustomTagName</c>, then activates
    /// the neighbour. Delegates to <see cref="SelectBuiltInTab"/> /
    /// <see cref="SelectCustomTagTab"/> so the button visuals + filter refresh.</summary>
    private void SelectNavTab(int delta)
    {
        if (_navOrder.Count < 2) return;

        int currentIndex = _navOrder.FindIndex(t =>
            t.tab == _activeTab &&
            (t.tab != ClipboardTab.Custom || t.customTagName == _activeCustomTagName));
        if (currentIndex < 0) currentIndex = 0;

        // Cyclic: +1 wraps to 0, -1 wraps to last.
        int next = (currentIndex + delta + _navOrder.Count) % _navOrder.Count;
        (ClipboardTab tab, string? customTagName) = _navOrder[next];
        if (tab == ClipboardTab.Custom && customTagName is not null)
        {
            SelectCustomTagTab(customTagName);
        }
        else
        {
            SelectBuiltInTab(tab);
        }
    }

    // ── Search filter ──

    private void OnSearchInputTextChanged(object? sender, TextChangedEventArgs e) => ReapplyFilter();

    private void ReapplyFilter()
    {
        IEnumerable<ClipboardHistoryEntryRow> pool = _activeTab switch
        {
            ClipboardTab.Link => _allRows.Where(r => r.GroupLabel == GroupToLabel(ClipboardGroup.Link)),
            // R54 v2: Code tab also absorbs JSON entries (JSON folded into Code
            // per user request — they're both structured/code content). The
            // classifier still tags them as ClipboardGroup.Json, but the UI no
            // longer has a separate JSON tab.
            ClipboardTab.Code => _allRows.Where(r =>
                r.GroupLabel == GroupToLabel(ClipboardGroup.Code) ||
                r.GroupLabel == GroupToLabel(ClipboardGroup.Json)),
            ClipboardTab.Shell => _allRows.Where(r => r.GroupLabel == GroupToLabel(ClipboardGroup.Shell)),
            ClipboardTab.Sensitive => _allRows.Where(r => r.IsSensitive),
            ClipboardTab.Image => _allRows.Where(r => r.IsImage),
            ClipboardTab.Favorite => _allRows.Where(r => r.IsFavorite),
            ClipboardTab.Custom when _activeCustomTagName is not null =>
                _allRows.Where(r => r.HasCustomTag(_activeCustomTagName)),
            _ => _allRows,
        };

        string query = (SearchInput.Text ?? string.Empty).Trim();

        List<ClipboardHistoryEntryRow> matches;
        if (string.IsNullOrEmpty(query))
        {
            // R103: empty query — show live entries only. Archived entries are
            // historical and would clutter the default view (the live list is
            // already capped at MaxEntries); they surface only when the user
            // actively searches for something. The tab filter still applies on
            // top, so e.g. switching to "Links" still shows only live Link rows.
            matches = pool.Where(r => !r.IsArchived).ToList();
        }
        else
        {
            // R103: with a query — search across BOTH live and archived entries
            // in the active tab's pool. Archived matches appear AFTER live
            // matches (stable OrderBy on IsArchived: false=0 sorts before
            // true=1), so the most-recently-relevant results stay on top and
            // archived hits read as "also found in history".
            matches = pool
                .Where(r => ClipboardSearchMatcher.IsMatch(
                    r.Text, r.EntryTags, r.CustomTags, r.SourceLabel, query))
                .OrderBy(r => r.IsArchived ? 1 : 0)
                .ToList();
        }

        // Batch 123: keep the full matched set in _filteredPool (cheap, no
        // controls) and only render the first slice in _filteredRows. The
        // remainder loads on scroll-to-bottom or arrow-past-edge (LoadMore).
        _filteredPool.Clear();
        _filteredPool.AddRange(matches);

        _visibleCount = Math.Min(InitialBatchSize, _filteredPool.Count);

        _filteredRows.Clear();
        for (int i = 0; i < _visibleCount; i++)
        {
            _filteredRows.Add(_filteredPool[i]);
        }
        _selectedIndex = _filteredRows.Count > 0 ? 0 : -1;
        SyncRowSelection();
        UpdateCategoryHeader();
        UpdateLoadMoreFooter();
    }

    /// <summary>Batch 123: appends the next <see cref="LoadMoreBatchSize"/> rows
    /// from <see cref="_filteredPool"/> into <see cref="_filteredRows"/>. Called
    /// by the scroll-to-bottom handler and by <see cref="MoveSelection"/> when
    /// the selection would otherwise walk off the rendered edge. No-op when
    /// everything is already visible.</summary>
    private void LoadMore()
    {
        if (_visibleCount >= _filteredPool.Count)
        {
            return;
        }

        int newCount = Math.Min(_visibleCount + LoadMoreBatchSize, _filteredPool.Count);
        for (int i = _visibleCount; i < newCount; i++)
        {
            _filteredRows.Add(_filteredPool[i]);
        }
        _visibleCount = newCount;

        UpdateLoadMoreFooter();
    }

    /// <summary>Batch 123: triggers incremental loading when the user scrolls
    /// near the bottom of the result list. Covers mouse wheel, scrollbar drag,
    /// and keyboard PageDown/End — all reach this single handler via the
    /// ScrollViewer's offset change. Distance-to-bottom is measured in pixels;
    /// LoadMore fires once the remaining scroll fits within (1 − threshold) of
    /// one viewport, which gives a comfortable lead so new rows appear before
    /// the user actually hits the end.</summary>
    private void OnResultsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        ScrollViewer sv = ResultsScroll;
        double extent = sv.Extent.Height;
        double viewport = sv.Viewport.Height;
        if (extent <= 0 || viewport <= 0)
        {
            return;
        }
        double distanceToBottom = extent - sv.Offset.Y - viewport;
        if (distanceToBottom <= viewport * (1.0 - LoadMoreThresholdRatio))
        {
            LoadMore();
        }
    }

    /// <summary>Batch 123: shows/hides the "N more entries" hint below the list.
    /// Hidden when everything is rendered; visible (counting the un-rendered
    /// remainder) otherwise, so the user knows there is more to scroll to.</summary>
    private void UpdateLoadMoreFooter()
    {
        if (LoadMoreFooter is null)
        {
            return;
        }
        int remaining = _filteredPool.Count - _visibleCount;
        if (remaining > 0)
        {
            LoadMoreFooter.Text = string.Format(Strings.Clip_LoadMore_Remaining, remaining);
            LoadMoreFooter.IsVisible = true;
        }
        else
        {
            LoadMoreFooter.IsVisible = false;
        }
    }

    private void UpdateCategoryHeader()
    {
        string tabName = _activeTab switch
        {
            ClipboardTab.Link => Strings.Clip_CategoryHeader_Links,
            // Code tab now covers JSON too (folded together).
            ClipboardTab.Code => Strings.Clip_CategoryHeader_Code,
            ClipboardTab.Shell => Strings.Clip_CategoryHeader_Commands,
            ClipboardTab.Sensitive => Strings.Clip_CategoryHeader_Sensitive,
            ClipboardTab.Image => Strings.Clip_CategoryHeader_Images,
            ClipboardTab.Favorite => Strings.Clip_CategoryHeader_Favorites,
            ClipboardTab.Custom when _activeCustomTagName is not null => "# " + _activeCustomTagName,
            _ => Strings.Clip_CategoryDefault,
        };
        CategoryHeader.Text = string.Format(Strings.Clip_CategoryCount, tabName, _filteredRows.Count);
    }

    // ── Keyboard navigation ──

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                // Three-tier Esc: close popups first, then collapse any expanded
                // rows, and only when nothing else is open/expanded does Esc hide
                // the window. Without this, a single Esc while a full-text popup
                // or an expanded row is showing would close the whole window.
                if (_fullTextPopup?.IsOpen == true) { _fullTextPopup.Close(); return; }
                if (_iconPicker?.IsOpen == true) { _iconPicker.Close(); return; }
                if (_tagNamePopup?.IsOpen == true) { _tagNamePopup.Close(); return; }
                if (_entryTagPopup?.IsOpen == true) { _entryTagPopup.Close(); return; }
                if (_confirmPopup?.IsOpen == true) { _confirmPopup.Close(); return; }
                bool anyExpanded = false;
                foreach (ClipboardHistoryEntryRow r in _filteredRows)
                {
                    if (r.IsExpanded) { r.IsExpanded = false; anyExpanded = true; }
                }
                if (!anyExpanded)
                {
                    // R100 动画1: 关闭瞬切(用户要求"一次性弹出来", 不缩回)。
                    Hide();
                }
                return;

            case Key.Down:
                e.Handled = MoveSelection(+1);
                return;

            case Key.Up:
                e.Handled = MoveSelection(-1);
                return;

            case Key.Enter:
                e.Handled = true;
                if (CurrentSelectedRow is { } row)
                {
                    PasteAndHide(row);
                }
                return;

            case Key.P when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                if (CurrentSelectedRow is { } pinRow)
                {
                    PinToggled?.Invoke(pinRow.Id);
                }
                return;

            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                // R54 v1.1: Ctrl+F toggles favorite on the selected row.
                e.Handled = true;
                if (CurrentSelectedRow is { } favRow)
                {
                    FavoriteToggled?.Invoke(favRow.Id);
                }
                return;

            // R54 v2: Ctrl+Tab / Ctrl+Shift+Tab cycle the nav tabs (browser-
            // style). Forward wraps All→…→last→All; Shift reverses. Only fires
            // when Ctrl is held so a plain Tab (future focus traversal) is left
            // untouched.
            case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                SelectNavTab(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1);
                return;

            case Key.Delete:
                e.Handled = true;
                if (CurrentSelectedRow is { } delRow)
                {
                    DeleteRequested?.Invoke(delRow.Id);
                }
                return;
        }
    }

    private bool MoveSelection(int delta)
    {
        if (_filteredRows.Count == 0)
        {
            return false;
        }
        int target = _selectedIndex + delta;
        // Batch 123: if the target row would land past the rendered edge,
        // grow the visible slice in LoadMoreBatchSize chunks until either
        // the target is materialized or the pool is exhausted. This keeps
        // arrow-key navigation seamless when the pool is larger than the
        // visible slice (only the first InitialBatchSize are rendered).
        while (target >= _visibleCount && _visibleCount < _filteredPool.Count)
        {
            LoadMore();
        }
        int newIndex = Math.Clamp(target, 0, _filteredRows.Count - 1);
        if (newIndex == _selectedIndex)
        {
            return false;
        }
        _selectedIndex = newIndex;
        SyncRowSelection();
        ScrollSelectedIntoView();
        return true;
    }

    private ClipboardHistoryEntryRow? CurrentSelectedRow =>
        _selectedIndex >= 0 && _selectedIndex < _filteredRows.Count
            ? _filteredRows[_selectedIndex]
            : null;

    private void PasteAndHide(ClipboardHistoryEntryRow row)
    {
        Hide();
        PasteRequested?.Invoke(row.Id);
    }

    private void SyncRowSelection()
    {
        for (int i = 0; i < _filteredRows.Count; i++)
        {
            _filteredRows[i].IsSelected = i == _selectedIndex;
        }
    }

    private void ScrollSelectedIntoView()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _filteredRows.Count)
        {
            ResultsList.ScrollIntoView(_filteredRows[_selectedIndex]);
        }
    }

    // ── Mouse interactions ──

    // v1.1: single-click = select; double-click = paste+close. We detect the
    // double manually on PointerReleased (TickCount64 + 8 px movement) rather
    // than using Avalonia's Tapped event, so the first click still selects and
    // only a true second click pastes. Sensitive rows paste directly on double-
    // click — no expand-first step.
    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ClipboardHistoryEntryRow row)
        {
            return;
        }

        PointerPointProperties props = e.GetCurrentPoint(border).Properties;
        bool isRight = props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed;

        // Select the row on any button press so the menu/keyboard acts on the
        // visible selection too.
        int index = IndexOfRow(row);
        if (index >= 0)
        {
            _selectedIndex = index;
            SyncRowSelection();
        }

        // Right button → build + open the dynamic context menu (depends on row
        // flags + current custom tags). Built fresh each press and opened
        // explicitly via menu.Open(border) — the robust pattern used by
        // SettingsWindow.OnAddProviderClick, which doesn't depend on Avalonia
        // noticing a freshly-attached Border.ContextMenu.
        if (isRight)
        {
            e.Handled = true;
            BuildRowContextMenu(row).Open(border);
            return;
        }

        // Left button: selection already happened above. The double-click check
        // happens in OnRowPointerReleased.
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ClipboardHistoryEntryRow row)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind
            != PointerUpdateKind.LeftButtonReleased)
        {
            return;
        }

        long now = Environment.TickCount64;
        PixelPoint screenPos = this.PointToScreen(e.GetPosition(this));

        bool isDouble = _lastClickTicks != 0 &&
            (now - _lastClickTicks) <= DoubleClickMs &&
            Math.Abs(screenPos.X - _lastClickScreen.X) <= DoubleClickPx &&
            Math.Abs(screenPos.Y - _lastClickScreen.Y) <= DoubleClickPx;

        if (isDouble)
        {
            // v1.1: sensitive rows paste directly on double-click — no expand
            // step. (Show plaintext only via the right-click menu, which never
            // writes the clipboard.)
            _lastClickTicks = 0; // consume the pair; next click is a fresh start
            PasteAndHide(row);
            return;
        }

        // R54 v2: image rows — single click toggles a larger inline preview
        // (collapse the 64px thumbnail, expand a row-width image), double-click
        // still pastes. Mirrors text's single-click=expand / double-click=paste.
        // The expanded view binds to FullBitmap (full-resolution decode) — load
        // it lazily on expand so the cheap 64px Thumbnail stays the collapsed
        // default (the expanded view used to bind Thumbnail → 64px upscaled to
        // 280px = blurry).
        if (row.IsImage)
        {
            row.IsExpanded = !row.IsExpanded;
            if (row.IsExpanded)
            {
                EnsureFullBitmap(row);
            }
            _lastClickTicks = now;
            _lastClickScreen = screenPos;
            return;
        }

        // Single click — toggle inline expand/collapse. R54 v1.2 v3 originally
        // routed long text (>300 chars) to a separate full-text Popup, but that
        // Popup's IsLightDismiss captured the second click of a double-click, so
        // users could never paste long entries (the dismiss ate click #2 before
        // it reached the row). Now ALL text expands inline (the ExpandedText
        // TextBlock has MaxLines=16 + Wrap, which is enough to read the content;
        // the full-text Popup is kept only as an explicit right-click "View full"
        // action, never auto-opened on single click). This keeps single-click =
        // expand, double-click = paste working uniformly.
        row.IsExpanded = !row.IsExpanded;
        _lastClickTicks = now;
        _lastClickScreen = screenPos;
    }

    // R54 v1.2: the active full-text popup (null when closed). One at a time.
    private Popup? _fullTextPopup;

    // R54 v2: the active entry-tag input popup (null when closed). Separate
    // from _fullTextPopup so the Esc handler can close either. Only one of
    // them is open at a time in practice (opening one closes the other).
    private Popup? _entryTagPopup;

    // R54 v2: the active ClearOlder confirmation popup (null when closed). Same
    // one-at-a-time discipline; closed by Esc, Cancel, Confirm, or light-dismiss.
    private Popup? _confirmPopup;

    /// <summary>R54 v2: shows a confirmation popup before the destructive
    /// ClearOlderEntries. Queries the dry-run counts via
    /// <see cref="PreviewClearOlderRequested"/> and presents "Delete N entries?
    /// (M tagged/pinned entries will be kept)" with Confirm/Cancel. Only on
    /// Confirm does it raise <see cref="ClearOlderRequested"/>. Guard against
    /// the footgun where right-clicking the newest entry means "older" = nearly
    /// everything.</summary>
    private void ConfirmClearOlder(ClipboardHistoryEntryRow row)
    {
        _confirmPopup?.Close();

        int wouldDelete = 0, wouldKeep = 0;
        if (PreviewClearOlderRequested is { } preview)
        {
            (wouldDelete, wouldKeep) = preview(row.Id);
        }

        // Nothing older to clear — tell the user and bail without a confirm.
        if (wouldDelete == 0)
        {
            var noneCard = new Border
            {
                Background = (IBrush?)Application.Current?.FindResource("ByhSurfaceBrush"),
                BorderBrush = (IBrush?)Application.Current?.FindResource("ByhGoldBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Child = new TextBlock
                {
                    Text = wouldKeep > 0
                        ? string.Format(Strings.Clip_ClearNone_Kept, wouldKeep)
                        : Strings.Clip_ClearNone_Empty,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = GetFontSize("ByhFontSizeBodyLarge"),
                },
            };
            var nonePopup = new Popup
            {
                Child = noneCard,
                Placement = PlacementMode.Center,
                PlacementTarget = this,
                IsLightDismissEnabled = true,
                WindowManagerAddShadowHint = false,
            };
            _confirmPopup = nonePopup;
            ((ISetLogicalParent)nonePopup).SetParent(this);
            nonePopup.Open();
            return;
        }

        var msg = new TextBlock
        {
            Text = string.Format(Strings.Clip_ClearConfirm, wouldDelete, wouldKeep),
            TextWrapping = TextWrapping.Wrap,
            FontSize = GetFontSize("ByhFontSizeBodyLarge"),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var confirmBtn = new Button
        {
            Content = string.Format(Strings.Clip_ClearConfirmButton, wouldDelete),
            FontSize = GetFontSize("ByhFontSizeBodyLarge"),
            Padding = new Thickness(16, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        confirmBtn.Click += (_, _) =>
        {
            _confirmPopup?.Close();
            ClearOlderRequested?.Invoke(row.Id);
        };

        var cancelBtn = new Button
        {
            Content = Strings.Common_Cancel,
            FontSize = GetFontSize("ByhFontSizeBodyLarge"),
            Padding = new Thickness(16, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        cancelBtn.Click += (_, _) => _confirmPopup?.Close();

        var card = new Border
        {
            Background = (IBrush?)Application.Current?.FindResource("ByhSurfaceBrush"),
            BorderBrush = (IBrush?)Application.Current?.FindResource("ByhGoldBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 8,
                Children =
                {
                    msg,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelBtn, confirmBtn },
                    },
                },
            },
        };
        card.SetValue(Border.BoxShadowProperty, Application.Current?.FindResource("ByhShadowMedium"));

        var popup = new Popup
        {
            Child = card,
            Placement = PlacementMode.Center,
            PlacementTarget = this,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = false,
        };
        _confirmPopup = popup;
        ((ISetLogicalParent)popup).SetParent(this);
        AttachPopupEntrance(popup, card); // R100 动画3
        popup.Open();
    }

    /// <summary>R54 v2: opens a lightweight popup anchored to <paramref name="row"/>
    /// with a TextBox for typing a new annotation tag. Autocompletes from tags
    /// already used elsewhere (<c>_knownEntryTags</c>). Enter commits (trim +
    /// non-empty + dedup), Escape / light-dismiss cancels. On commit, raises
    /// <see cref="EntryTagAdded"/>; App then re-pushes the snapshot so the badge
    /// appears. Independent of the custom-tag tab system's input panel.</summary>
    private void ShowEntryTagInputPopup(ClipboardHistoryEntryRow row)
    {
        _entryTagPopup?.Close();

        var label = new TextBlock
        {
            Text = Strings.Clip_AddTag,
            FontSize = GetFontSize("ByhFontSizeBody"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
            Classes = { "Muted" },
        };

        // AutoCompleteBox gives free autocomplete over previously-used tags.
        // FilterMode=StartsWith + MinimumPrefixLength=0 means the dropdown also
        // opens on focus, showing all known tags when the box is empty.
        var input = new AutoCompleteBox
        {
            FontSize = GetFontSize("ByhFontSizeSubheading"),
            MinWidth = 220,
            PlaceholderText = Strings.Clip_TagNamePlaceholder,
            FilterMode = AutoCompleteFilterMode.StartsWith,
            MinimumPrefixLength = 0,
            ItemsSource = _knownEntryTags.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
        };

        void Commit()
        {
            string trimmed = (input.Text ?? string.Empty).Trim();
            if (trimmed.Length > 0)
            {
                EntryTagAdded?.Invoke(row.Id, trimmed);
            }
            _entryTagPopup?.Close();
        }

        // R99 Bug A: AutoCompleteBox (MinimumPrefixLength=0 → 聚焦即弹下拉)
        // 用户用键盘在下拉里选中一项按 Enter 时, ACB 内部 OnKeyDown 会处理该
        // Enter(选中项 + 写 Text + 关下拉 + 标记 e.Handled=true), 导致原本
        // `input.KeyDown += ...` 的 lambda 收不到 Enter → Commit() 不调用 →
        // tag 没加. 这就是"加 tag 偶尔失效"的根因 —— 只有走键盘选下拉项才
        // 失效, 纯手打或鼠标点都正常.
        // 修法: 改用 AddHandler 以 Tunnel(隧道)方式订阅 KeyDownEvent, 在 ACB
        // 的冒泡处理之前先拿到 Enter; 且 handledEventsToo=true 保证即便 ACB
        // 已标记 Handled 也照常触发. Esc 一并放进同一个 handler 保持原行为.
        input.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Commit();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    _entryTagPopup?.Close();
                }
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        var card = new Border
        {
            Background = (IBrush?)Application.Current?.FindResource("ByhSurfaceBrush"),
            BorderBrush = (IBrush?)Application.Current?.FindResource("ByhGoldBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children = { label, input },
            },
        };
        card.SetValue(Border.BoxShadowProperty, Application.Current?.FindResource("ByhShadowMedium"));

        var popup = new Popup
        {
            Child = card,
            Placement = PlacementMode.Center,
            PlacementTarget = this,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = false,
        };
        _entryTagPopup = popup;
        ((ISetLogicalParent)popup).SetParent(this);
        AttachPopupEntrance(popup, card); // R100 动画3
        popup.Open();
        input.Focus(); // immediate typing, no extra click
    }

    /// <summary>Opens a scrollable popup showing the entry's full text. Used for
    /// long entries that don't fit the inline expand (MaxLines can't show them
    /// all). Read-only; copying is via the row's right-click menu / double-click
    /// paste as usual.</summary>
    private void ShowFullTextPopup(ClipboardHistoryEntryRow row, string text)
    {
        _fullTextPopup?.Close();

        var header = new TextBlock
        {
            Text = row.GroupLabel.Length > 0 ? $"{row.GroupLabel} · {row.TimeLabel}" : row.TimeLabel,
            FontSize = GetFontSize("ByhFontSizeBody"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
            Classes = { "Muted" },
        };

        var body = new TextBox
        {
            Text = text,
            FontSize = GetFontSize("ByhFontSizeSubheading"),
            LineHeight = 24,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            MaxHeight = 520,
        };

        var closeBtn = new Button
        {
            Content = Strings.Common_Close,
            FontSize = GetFontSize("ByhFontSizeBodyLarge"),
            Padding = new Thickness(18, 7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        closeBtn.Click += (_, _) => _fullTextPopup?.Close();

        var card = new Border
        {
            Background = (IBrush?)Application.Current?.FindResource("ByhSurfaceBrush"),
            BorderBrush = (IBrush?)Application.Current?.FindResource("ByhGoldBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Width = 760,
            MaxWidth = 760,
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 10,
                Children = { header, body, closeBtn },
            },
        };
        card.SetValue(Border.BoxShadowProperty, Application.Current?.FindResource("ByhShadowMedium"));

        var popup = new Popup
        {
            Child = card,
            Placement = PlacementMode.Center,
            PlacementTarget = this,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = false,
        };
        _fullTextPopup = popup;
        ((ISetLogicalParent)popup).SetParent(this);
        AttachPopupEntrance(popup, card); // R100 动画3
        popup.Open();
    }

    /// <summary>R54 v2: opens a centered large-image preview popup for an image
    /// entry. Reuses the already-decoded <see cref="ClipboardHistoryEntryRow.Thumbnail"/>
    /// bitmap (no second disk read). Capped to 70% of the screen so huge
    /// screenshots stay navigable. Closed via the Close button, light-dismiss,
    /// or the three-tier Esc handler (which checks _fullTextPopup).</summary>
    private void ShowImagePopup(ClipboardHistoryEntryRow row)
    {
        if (row.Thumbnail is null)
        {
            return;
        }

        // Ensure the full-resolution bitmap is loaded (the popup binds to it so
        // it's crisp; the 64px Thumbnail would be blurry at popup size).
        EnsureFullBitmap(row);

        _fullTextPopup?.Close();

        // The PREVIEW has two distinct sizes:
        //   - DISPLAY size (imgMaxW/imgMaxH, 50% screen) — the image's default
        //     1× footprint on open. User feedback settled on 50% as "just right".
        //   - CLIP size (clipW/clipH, 85% screen) — the hard boundary beyond
        //     which the zoomed/panned image is clipped. Making the clip region
        //     LARGER than the display region means the image can grow to
        //     ~1.7× (85/50) before any part is cut — so zooming in to inspect
        //     detail doesn't immediately hit an "invisible frame" at the 1× edge.
        //     The clip is invisible (transparent, no chrome); only its bounds matter.
        var primary = Screens.Primary;
        double imgMaxW = primary is not null
            ? primary.Bounds.Width * 0.5
            : 800;
        double imgMaxH = primary is not null
            ? primary.Bounds.Height * 0.5
            : 600;
        double clipW = primary is not null
            ? primary.Bounds.Width * 0.85
            : 1300;
        double clipH = primary is not null
            ? primary.Bounds.Height * 0.85
            : 900;

        var header = new TextBlock
        {
            Text = string.Format(Strings.Clip_ImagePopupHeader, row.TimeLabel),
            FontSize = GetFontSize("ByhFontSizeBodySmall"),
            Classes = { "Muted" },
            Margin = new Thickness(2, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        // Declare the card FIRST (empty) so the image's drag handler can use it
        // as a fixed coordinate reference + the clip boundary. FIXED Width/Height
        // (not Max) at the CLIP size (85% screen) — larger than the image's 50%
        // display size so the image has room to zoom/pan before being clipped.
        // No CornerRadius/Padding/BoxShadow: pure invisible clip + hit-test
        // boundary (the transparent fill is only so Border hit-tests for drag).
        var card = new Border
        {
            Width = clipW,
            Height = clipH,
            ClipToBounds = true,
            Background = Brushes.Transparent,
        };

        var image = new Image
        {
            // Bind to FullBitmap (full-res); if it hasn't decoded yet the Image
            // is briefly blank then fills in when the async decode posts back.
            // Fallback to Thumbnail so there's always something visible.
            Source = row.FullBitmap ?? row.Thumbnail,
            // MaxWidth/MaxHeight at the DISPLAY size (50% screen) + Center alignment
            // → the image's 1× footprint is 50% screen, centered in the 85%-screen
            // clip card. Stretch.Uniform keeps aspect ratio. RenderTransform zoom
            // grows it past the 50% display size into the surrounding 85% clip
            // space before being cut — no "invisible frame" at the 1× edge.
            Stretch = Stretch.Uniform,
            MaxWidth = imgMaxW,
            MaxHeight = imgMaxH,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // R54 v2: wheel-zoom + drag-pan via RenderTransform (Scale + Translate).
        // No ScrollViewer (it owns PointerWheelChanged → both zoom+scroll, the
        // gallery bug). The card is the clip boundary; the image transforms
        // freely inside it.
        var scaleTransform = new ScaleTransform(1.0, 1.0);
        var translateTransform = new TranslateTransform(0, 0);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(scaleTransform);
        transformGroup.Children.Add(translateTransform);
        image.RenderTransform = transformGroup;
        image.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        // Wheel zoom around the image center. NOTE: do NOT reset translate when
        // scale returns to 1× — that reset was the "拖拽以后再动滚轮会回到最中央"
        // bug (pan offset cleared on every zoom-out). The pan offset is now
        // sticky; the user's drag position survives any zoom level.
        void ApplyZoom(double factor)
        {
            double next = Math.Clamp(scaleTransform.ScaleX * factor, 0.25, 8.0);
            scaleTransform.ScaleX = next;
            scaleTransform.ScaleY = next;
        }

        // Wheel zoom (plain wheel). Attached to the image — e.Handled stops
        // bubbling so nothing scrolls.
        image.PointerWheelChanged += (s, e) =>
        {
            ApplyZoom(e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15);
            e.Handled = true;
        };

        // Left-drag pans the zoomed image. Delta is computed against the CARD
        // (the popup = the visible window, no RenderTransform) so it's true
        // screen pixels → 1:1 pan at any zoom (GetPosition(image) would return
        // transformed coords → 2× drift at scale 2). Capture is DEFERRED until
        // the pointer actually moves past a small threshold — pressing without
        // moving is a click (so double-click + click-outside-to-close still
        // work); capturing on press would steal the second tap of a double-click
        // and route it to the card instead of the image.
        Point? dragLast = null;
        bool didDrag = false;
        double dragThreshold = 3.0;
        card.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(card).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
            {
                dragLast = e.GetPosition(card);
                didDrag = false;
                e.Handled = true;
            }
        };
        card.PointerMoved += (s, e) =>
        {
            if (dragLast is Point last)
            {
                Point now = e.GetPosition(card);
                // Start capturing only once the user has dragged past the
                // threshold — a true pan, not a click.
                if (!didDrag)
                {
                    double dx = now.X - last.X;
                    double dy = now.Y - last.Y;
                    if (dx * dx + dy * dy < dragThreshold * dragThreshold)
                    {
                        return;
                    }
                    didDrag = true;
                    e.Pointer.Capture(card);
                }
                translateTransform.X += now.X - last.X;
                translateTransform.Y += now.Y - last.Y;
                dragLast = now;
            }
        };
        card.PointerReleased += (s, e) =>
        {
            bool wasDragging = didDrag;
            dragLast = null;
            didDrag = false;
            if (e.Pointer.Captured == card)
            {
                e.Pointer.Capture(null);
            }

            // A click (press→release without dragging) on the card OUTSIDE the
            // image closes the popup — this is the transparent ring around the
            // image (card is 85% screen, image is 50% centered). Without this,
            // clicking that empty area does nothing because light-dismiss only
            // fires for clicks OUTSIDE the popup entirely.
            if (!wasDragging)
            {
                Point pt = e.GetPosition(card);
                Rect imgBounds = image.Bounds;
                if (!imgBounds.Contains(pt))
                {
                    _fullTextPopup?.Close();
                }
            }
        };
        card.PointerCaptureLost += (s, e) =>
        {
            dragLast = null;
            didDrag = false;
        };

        // Double-click anywhere on the card closes the popup. Attached to the
        // CARD (not the image) so it fires even on the transparent ring around
        // the image, and so deferred pointer capture doesn't steal the second
        // tap (the image-only DoubleTapped was unreliable once capture routing
        // changed). Double-clicking the image to zoom is not a feature here,
        // so closing is the right double-click behavior everywhere.
        card.DoubleTapped += (s, e) => _fullTextPopup?.Close();

        card.Child = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto, *"),
            Children =
            {
                header,
                image,
            },
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(image, 1);

        var popup = new Popup
        {
            Child = card,
            Placement = PlacementMode.Center,
            PlacementTarget = this,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = false,
        };
        _fullTextPopup = popup;
        ((ISetLogicalParent)popup).SetParent(this);
        AttachPopupEntrance(popup, card); // R100 动画3 (图片弹窗也加入场, card scale 与 image zoom 互不干扰)
        popup.Open();
    }

    private void Reveal(ClipboardHistoryEntryRow row)
    {
        _revealed.Add(row.Id);
        row.Preview = ClipboardHistoryStore.BuildPreview(row.Text, isSensitive: false, maskSensitive: false);
        // Refresh the expanded view too so a reveal while expanded shows plaintext.
        row.ExpandedText = ClipboardHistoryStore.BuildExpanded(row.Text, isSensitive: false, maskSensitive: false);
    }

    /// <summary>R54 v2: the inverse of <see cref="Reveal"/> — re-masks a
    /// previously revealed sensitive row back to ●●●●. Drops the id from
    /// <c>_revealed</c> and rebuilds the preview/expanded text with masking
    /// re-enabled. Lets the user toggle between plaintext and masked without
    /// re-opening the window.</summary>
    private void Remask(ClipboardHistoryEntryRow row)
    {
        _revealed.Remove(row.Id);
        row.Preview = ClipboardHistoryStore.BuildPreview(row.Text, isSensitive: true, maskSensitive: true);
        row.ExpandedText = ClipboardHistoryStore.BuildExpanded(row.Text, isSensitive: true, maskSensitive: true);
    }

    private int IndexOfRow(ClipboardHistoryEntryRow row)
    {
        for (int i = 0; i < _filteredRows.Count; i++)
        {
            if (ReferenceEquals(_filteredRows[i], row))
            {
                return i;
            }
        }
        return -1;
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke();

    /// <summary>R54 v1.2: drag the frameless window by pressing its chrome
    /// (the 12 px RootBorder padding around the two panels). Restricted to the
    /// border itself via e.Source so it does NOT hijack clicks on interactive
    /// children (rows, nav buttons, search box) — those bubble up here too, but
    /// their source is the child control, not RootBorder, so we skip them and
    /// let the child's own handler run (single-click expand, double-click paste,
    /// tag drag-reorder, etc.).</summary>
    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only left-drag moves the window; right-click lets any context menu
        // (e.g. a tag button's) open normally.
        if (e.GetCurrentPoint(RootBorder).Properties.PointerUpdateKind
            != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }
        // CRITICAL: only treat it as a window-drag when the press landed on the
        // border's own chrome (the padding ring), not on a child. Without this
        // guard, BeginMoveDrag would hijack the pointer for EVERY press inside
        // the window (rows, search box, nav buttons) and break single-click /
        // double-click / tag-drag entirely.
        if (!ReferenceEquals(e.Source, RootBorder))
        {
            return;
        }
        try { BeginMoveDrag(e); } catch { /* ignore: e.g. already moved */ }
    }

    /// <summary>R54 v2: resize the frameless window by dragging one of the four
    /// transparent corner handles (ResizeCornerNW/NE/SW/SE in the AXAML). Each
    /// corner maps to a <see cref="WindowEdge"/>; BeginResizeDrag hands the
    /// gesture to the OS just like BeginMoveDrag does for the chrome. Only
    /// left-button initiates a resize — right-click falls through so any future
    /// context menu on the corner still opens. This handler is reached BEFORE
    /// OnRootPointerPressed's BeginMoveDrag because the corner Border is the
    /// event source (not RootBorder), so the move-drag guard skips it; there is
    /// no conflict between move and resize.</summary>
    private void OnResizeCornerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind
            != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }
        if (sender is not Control corner)
        {
            return;
        }
        WindowEdge edge = corner.Name switch
        {
            "ResizeCornerNW" => WindowEdge.NorthWest,
            "ResizeCornerNE" => WindowEdge.NorthEast,
            "ResizeCornerSW" => WindowEdge.SouthWest,
            _ => WindowEdge.SouthEast,
        };
        try { BeginResizeDrag(edge, e); } catch { /* ignore: already resizing */ }
    }

    // ── Per-row ContextMenu (built dynamically — depends on row flags + the
    //    current custom-tag list, both of which change at runtime) ──

    /// <summary>Builds the right-click menu for the given row. Built fresh on
    /// each right-press (in <see cref="OnRowPointerPressed"/>) so the "移动到…"
    /// submenu always reflects the current custom tags, then opened via
    /// <c>menu.Open(border)</c>.</summary>
    private ContextMenu BuildRowContextMenu(ClipboardHistoryEntryRow row)
    {
        var menu = new ContextMenu();

        var copyPaste = new MenuItem { Header = Strings.Clip_Row_CopyPaste, Tag = row };
        copyPaste.Click += (_, _) => PasteAndHide(row);

        var copyNoClose = new MenuItem { Header = Strings.Clip_Row_CopyKeepOpen, Tag = row };
        copyNoClose.Click += (_, _) => CopyRequested?.Invoke(row.Id);

        var pinItem = new MenuItem { Header = row.IsPinned ? Strings.Clip_Row_Unpin : Strings.Clip_Row_Pin, Tag = row };
        pinItem.Click += (_, _) => PinToggled?.Invoke(row.Id);

        var favItem = new MenuItem { Header = row.IsFavorite ? Strings.Clip_Row_Unfavorite : Strings.Clip_Row_Favorite, Tag = row };
        favItem.Click += (_, _) => FavoriteToggled?.Invoke(row.Id);

        var deleteItem = new MenuItem { Header = Strings.Common_Delete, Tag = row };
        deleteItem.Click += (_, _) => DeleteRequested?.Invoke(row.Id);

        menu.Items.Add(copyPaste);
        menu.Items.Add(copyNoClose);

        // R103: archived rows are historical — they're no longer in the live
        // window, so pin/favorite/tag/move/delete/reveal don't apply. Only copy
        // operations (already added above) make sense for an archived entry.
        // "View full…" is still offered for long text so the user can read the
        // whole thing before deciding to copy it. This early return skips all
        // the live-only menu construction below.
        if (row.IsArchived)
        {
            if (!row.IsImage && row.Text.Length > 300)
            {
                menu.Items.Add(new Separator());
                var viewFull = new MenuItem { Header = Strings.Clip_Row_ViewFull, Tag = row };
                viewFull.Click += (_, _) =>
                {
                    // Archived rows are never in _revealed (that set is only
                    // populated for live rows via the Reveal menu). Sensitive
                    // archived rows therefore always mask here — consistent with
                    // how they appear in the list.
                    string fullText = row.IsSensitive
                        ? new string('●', Math.Min(row.Text.Length, 32))
                        : row.Text;
                    ShowFullTextPopup(row, fullText);
                };
                menu.Items.Add(viewFull);
            }
            return menu;
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(pinItem);
        menu.Items.Add(favItem);

        // R54 v2: per-entry annotation tags (independent of the custom-tag tab
        // system). "Add tag…" opens a lightweight inline input popup anchored to
        // the row; the popup autocompletes from tags already used elsewhere.
        // When the row already has tags, a "Remove tag ›" submenu lists them so
        // the user can drop one without opening the input. Text AND image rows
        // can carry entry tags (unlike "Move to…" which is text-only).
        var addTagItem = new MenuItem { Header = Strings.Clip_Row_AddTag, Tag = row };
        addTagItem.Click += (_, _) => ShowEntryTagInputPopup(row);
        menu.Items.Add(addTagItem);

        if (row.EntryTags.Count > 0)
        {
            var removeTagMenu = new MenuItem { Header = Strings.Clip_Row_RemoveTag };
            foreach (string tag in row.EntryTags)
            {
                string captured = tag; // capture for the lambda
                var removeItem = new MenuItem { Header = tag, Tag = row };
                removeItem.Click += (_, _) => EntryTagRemoved?.Invoke(row.Id, captured);
                removeTagMenu.Items.Add(removeItem);
            }
            menu.Items.Add(removeTagMenu);
        }

        // R54 v2: "View full" — explicit way to open the scrollable full-text
        // popup for long entries. This used to auto-open on single click, but
        // that Popup's light-dismiss ate the second click of a double-click and
        // broke pasting long text. Now single-click expands inline (MaxLines=16)
        // and the popup is opt-in via this menu item only.
        if (!row.IsImage && !string.IsNullOrEmpty(row.Text) && row.Text.Length > 300)
        {
            var viewFull = new MenuItem { Header = Strings.Clip_Row_ViewFull, Tag = row };
            viewFull.Click += (_, _) =>
            {
                bool reveal = _revealed.Contains(row.Id);
                string fullText = reveal
                    ? row.Text
                    : (row.IsSensitive ? new string('●', Math.Min(row.Text.Length, 32)) : row.Text);
                ShowFullTextPopup(row, fullText);
            };
            menu.Items.Add(viewFull);
        }

        // R54 v2: "View image" — opens a large preview popup for image entries.
        // (Single-click toggles an inline ~280px preview; this is the full-size
        // pop-out for inspecting detail. Reuses the Thumbnail bitmap already
        // decoded — no second disk read.)
        if (row.IsImage && row.Thumbnail is not null)
        {
            var viewImage = new MenuItem { Header = Strings.Clip_Row_ViewImage, Tag = row };
            viewImage.Click += (_, _) => ShowImagePopup(row);
            menu.Items.Add(viewImage);

            // Pin the image as an always-on-top floating sticker. Handed to the
            // App/runtime via PinOnTopRequested (the runtime reads the PNG bytes
            // and creates a PinnedScreenshotWindow). Requires a real on-disk path.
            if (!string.IsNullOrEmpty(row.ImagePath))
            {
                var pinOnTop = new MenuItem { Header = Strings.Clip_Row_PinOnTop, Tag = row };
                pinOnTop.Click += (_, _) => PinOnTopRequested?.Invoke(row.ImagePath!);
                menu.Items.Add(pinOnTop);
            }
        }

        // "Move to…" submenu: only for text rows (R54 v2 — images can't be
        // tagged). Two independent sections:
        //
        //  1. "Categorize as" — override the auto-classified built-in group.
        //     Lets the user fix a wrong classification (move a missed secret
        //     into Sensitive, or pull a false-positive out). The active target
        //     is checkmarked; "Auto" reverts to the classifier result by
        //     clearing GroupOverride. Selecting the current effective target is
        //     a no-op (still fires the event, which is idempotent).
        //
        //  2. Custom tabs (#tag) — assign/unassign the entry to user-created
        //     tabs. When none exist yet, the only entry is "New tag…" — clicking
        //     it opens the create panel AND remembers this row so the new tag is
        //     auto-assigned on confirm (no second right-click needed). A trailing
        //     "New tag…" is always offered so the user can create + assign in
        //     one flow even when tabs already exist.
        if (!row.IsImage)
        {
            var moveTo = new MenuItem { Header = Strings.Clip_Row_MoveTo };

            // ── Section 1: categorize as (built-in group override) ──
            void AddGroupTarget(string label, ClipboardGroup? target)
            {
                // Check the target that the user would switch TO: the effective
                // group when target is a real group, or AutoGroup when target
                // is null (revert). Selecting the already-active target is a
                // harmless no-op but still listed so the check is informative.
                ClipboardGroup activeForCheck = target ?? row.AutoGroup;
                bool isCurrent = row.EffectiveGroup == activeForCheck &&
                                 (target is not null ? row.GroupOverride == target
                                                     : row.GroupOverride is null);
                var item = new MenuItem
                {
                    Header = (isCurrent ? "✓ " : "  ") + label,
                    Tag = row,
                };
                item.Click += (_, _) => GroupOverrideRequested?.Invoke(row.Id, target);
                moveTo.Items.Add(item);
            }
            AddGroupTarget(Strings.Clip_Group_Auto, null);
            AddGroupTarget(Strings.Clip_Group_LinkMenu, ClipboardGroup.Link);
            AddGroupTarget(Strings.Clip_Group_CodeMenu, ClipboardGroup.Code);
            AddGroupTarget(Strings.Clip_Group_CommandMenu, ClipboardGroup.Shell);
            AddGroupTarget(Strings.Clip_Group_SensitiveMenu, ClipboardGroup.Sensitive);
            // Note: JSON is folded into the Code tab at the filter layer, but as
            // an override target it's redundant (Code and JSON render the same
            // tab) — so we expose only Code here. Number has no tab and is
            // intentionally not exposed as an override target.

            moveTo.Items.Add(new Separator());

            // ── Section 2: custom tabs (#tag assignments) ──
            if (_customTags.Count == 0)
            {
                var createHint = new MenuItem
                {
                    Header = Strings.Clip_Row_NoTabsHint,
                    Tag = row,
                    FontStyle = FontStyle.Italic,
                };
                createHint.Click += (_, _) => ShowTagInputPanel(TagInputMode.Create, assignOnCreateEntryId: row.Id);
                moveTo.Items.Add(createHint);
            }
            else
            {
                foreach (string tagName in _customTags)
                {
                    string tag = tagName; // capture for the lambda
                    bool assigned = row.HasCustomTag(tag);
                    var item = new MenuItem
                    {
                        Header = (assigned ? "✓ " : "  ") + "# " + tag,
                        Tag = row,
                    };
                    item.Click += (_, _) =>
                    {
                        if (row.HasCustomTag(tag))
                        {
                            UnassignTagRequested?.Invoke(row.Id, tag);
                        }
                        else
                        {
                            AssignTagRequested?.Invoke(row.Id, tag);
                        }
                    };
                    moveTo.Items.Add(item);
                }
                // Always offer "New tag…" at the bottom of the list too — creates
                // the tab AND assigns it to this entry in one step.
                moveTo.Items.Add(new Separator());
                var createFromRow = new MenuItem { Header = Strings.Clip_Row_NewTabForEntry, Tag = row };
                createFromRow.Click += (_, _) => ShowTagInputPanel(TagInputMode.Create, assignOnCreateEntryId: row.Id);
                moveTo.Items.Add(createFromRow);
            }
            menu.Items.Add(moveTo);
        } // end if (!row.IsImage)

        // Sensitive-only: toggle plaintext reveal in place without writing the
        // clipboard. After reveal, the same slot becomes "Hide plaintext" so the
        // user can re-mask (●●●●) without reopening the window.
        if (row.IsSensitive)
        {
            bool isRevealed = _revealed.Contains(row.Id);
            var revealItem = new MenuItem
            {
                Header = isRevealed ? Strings.Clip_Row_HidePlain : Strings.Clip_Row_RevealPlain,
                Tag = row,
            };
            revealItem.Click += (_, _) =>
            {
                if (isRevealed) Remask(row); else Reveal(row);
            };
            menu.Items.Add(revealItem);
        }

        // R54 v2: bulk-clear everything older than this entry (below it in the
        // list), keeping pinned/favorited/tagged entries. A confirmation popup
        // shows the exact delete/keep counts first — protects against nuking
        // everything when the reference is the newest entry (where "older" =
        // almost the whole list).
        var clearOlder = new MenuItem { Header = Strings.Clip_Row_ClearOlder, Tag = row };
        clearOlder.Click += (_, _) => ConfirmClearOlder(row);
        menu.Items.Add(clearOlder);

        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);

        return menu;
    }
}
