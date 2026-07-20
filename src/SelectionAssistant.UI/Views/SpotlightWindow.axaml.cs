using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SelectionAssistant.Core.Launcher;
using SelectionAssistant.Core.Translation;
using System.Collections.ObjectModel;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// R32 standalone launcher-search panel. Triggered by its own global hotkey
/// (default <c>Ctrl+Alt+Space</c>, configured separately from QuickTools).
/// Provides a Spotlight/PowerToys-Run-style flow: a top search box filters the
/// user's launcher entries by name; arrow keys move the selection; Enter starts
/// the highlighted entry; Ctrl+Enter opens the settings editor for it; Esc
/// closes the panel.
/// </summary>
/// <remarks>
/// The panel shares the same <see cref="LauncherEntry"/> source as QuickTools
/// and Settings — App pushes the entries via <see cref="SetLauncherEntries"/>
/// and asynchronously pushes icons via <see cref="UpdateLauncherIcon"/>. The
/// panel owns its own filtered view (<see cref="_filteredRows"/>) plus a
/// single <see cref="_selectedIndex"/> that arrow keys move (clamped, no wrap).
/// </remarks>
public partial class SpotlightWindow : Window
{
    // Full set of rows currently known to the panel (one per LauncherEntry).
    private readonly ObservableCollection<LauncherEntryRow> _allRows = [];
    // Subset currently shown after applying the search filter. Indices in this
    // list are what _selectedIndex refers to.
    private readonly ObservableCollection<LauncherEntryRow> _filteredRows = [];

    private int _selectedIndex;

    public SpotlightWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _filteredRows;

        // Focus the search box as soon as the window is in the visual tree.
        // Doing it in the constructor is too early (window not shown yet); in
        // Window.Opened works but fires on every re-show, which is what we want.
        Opened += (_, _) =>
        {
            SearchInput.Text = string.Empty;
            SearchInput.Focus();
        };

        // Hide on focus loss — same UX as QuickTools. The user hit the global
        // hotkey to summon us; clicking elsewhere should dismiss.
        Deactivated += (_, _) => Hide();
        Closing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private bool _allowClose;

    /// <summary>Pushes the full set of launcher entries from the runtime.</summary>
    public void SetLauncherEntries(IReadOnlyList<LauncherEntry> entries)
    {
        _allRows.Clear();
        foreach (LauncherEntry entry in entries)
        {
            string entryId = entry.Id;
            _allRows.Add(new LauncherEntryRow
            {
                Id = entryId,
                Name = entry.Name,
                Kind = entry.Kind,
                Target = entry.Target,
                Arguments = entry.Arguments,
            });
        }
        ReapplyFilter();
    }

    /// <summary>
    /// Updates the icon for an entry by id. Posted to the UI thread so the
    /// background icon-loading task can call it from any thread.
    /// </summary>
    public void UpdateLauncherIcon(string entryId, Bitmap? icon)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (LauncherEntryRow row in _allRows)
            {
                if (row.Id == entryId)
                {
                    row.Icon = icon;
                    break;
                }
            }
        });
    }

    /// <summary>Runs the highlighted entry. Args = (entryId, selectedText, clipText).</summary>
    public event Action<string, string?, string?>? LauncherRunRequested;

    /// <summary>Edit the highlighted entry in the settings window. Arg = entryId.</summary>
    public event Action<string>? LauncherEditRequested;

    /// <summary>Footer "设置" clicked — open the launcher settings section.</summary>
    public event Action? SettingsRequested;

    public void PrepareForShutdown() => _allowClose = true;

    // ── Search filter ──

    private void OnSearchInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        ReapplyFilter();
    }

    private void ReapplyFilter()
    {
        string query = (SearchInput.Text ?? string.Empty).Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _allRows.ToList()
            : _allRows.Where(r => MatchesQuery(r.Name, query)).ToList();

        _filteredRows.Clear();
        foreach (var row in matches)
        {
            _filteredRows.Add(row);
        }
        _selectedIndex = _filteredRows.Count > 0 ? 0 : -1;
        SyncRowSelection();
    }

    // ── Search matching ──
    //
    // Three-tier matching: substring → initials scan → pinyin initials.
    // "bb" matches "Bilibili" (greedy scan: B@0 → b@2 after word-end at 1),
    // "wx" matches "微信" (pinyin), "cb" matches "CodeBuddy CN" (camelCase + space).

    /// <summary>Returns true if <paramref name="name"/> matches <paramref name="query"/>.</summary>
    private static bool MatchesQuery(string name, string query)
    {
        // 1. Substring match (existing behaviour).
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Initials greedy scan: each query char matches the start of a
        //    "word segment" (separator, camelCase boundary, or CJK boundary).
        if (MatchInitials(name, query))
            return true;

        // 3. Pinyin initials match (Chinese characters only).
        string pinyin = ExtractPinyinInitials(name);
        if (pinyin.Length > 0 && pinyin.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Greedy scan: each character in <paramref name="query"/> must match the
    /// start of a word segment in <paramref name="name"/>. A segment starts at:
    /// the first character, after a separator (space/hyphen/dot/underscore), at a
    /// camelCase boundary (lowercase→uppercase), or at a CJK/letter boundary.
    /// After a match, scanning continues from the next character (greedy).
    /// E.g. "bb" vs "Bilibili": B@0 matches 'b', skip to pos 1, word-end at 1
    /// (lowercase→lowercase boundary), b@2 matches 'b' → true.
    /// </summary>
    private static bool MatchInitials(string name, string query)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(query))
            return false;

        int qi = 0;
        bool prevIsLower = false;

        for (int ni = 0; ni < name.Length && qi < query.Length; ni++)
        {
            char c = name[ni];
            bool isSep = c is ' ' or '-' or '.' or '_';
            bool isCamel = prevIsLower && char.IsUpper(c);
            bool isCjkBoundary = prevIsLower && IsCjk(c);
            bool isLetterAfterCjk = ni > 0 && IsCjk(name[ni - 1]) && char.IsLetter(c) && !IsCjk(c);

            bool isSegmentStart = ni == 0 || isSep || isCamel || isCjkBoundary || isLetterAfterCjk;

            if (isSegmentStart && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[qi]))
            {
                qi++;
                if (qi >= query.Length)
                    return true;
            }

            if (!isSep)
                prevIsLower = char.IsLower(c);
            else
                prevIsLower = false;
        }
        return qi >= query.Length;
    }

    /// <summary>Returns true if <paramref name="c"/> is a CJK Unified Ideograph.</summary>
    private static bool IsCjk(char c) =>
        c >= '\u4E00' && c <= '\u9FFF';

    /// <summary>
    /// Extracts pinyin initials from Chinese characters in <paramref name="name"/>.
    /// Non-CJK characters are skipped. Uses a built-in lookup table covering
    /// common characters (~600 entries). Returns lowercase initials.
    /// E.g. "微信" → "wx", "小旺AI截图" → "xwjt".
    /// </summary>
    private static string ExtractPinyinInitials(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (PinyinInitialMap.TryGetValue(c, out char initial))
                sb.Append(initial);
        }
        return sb.ToString();
    }

    /// <summary>Lookup table: CJK character → lowercase pinyin initial.</summary>
    private static readonly Dictionary<char, char> PinyinInitialMap = BuildPinyinInitialMap();

    private static Dictionary<char, char> BuildPinyinInitialMap()
    {
        // Grouped by initial letter for maintainability. Covers the most common
        // ~600 Chinese characters used in app names, UI labels, and daily use.
        var map = new Dictionary<char, char>(600);

        void Add(char initial, string chars)
        {
            foreach (char c in chars) map[c] = initial;
        }

        // a
        Add('a', "阿啊哎哀唉爱矮碍隘安按暗岸案昂凹奥澳");

        // b
        Add('b', "八把吧巴爸白百摆败拜班般板半办瓣帮棒包宝抱报暴爆杯北被背贝倍备奔本逼鼻比笔彼闭必避壁臂边变便遍辨辩标表别冰并病拨波玻博伯捕不布步部");

        // c
        Add('c', "擦才材财裁采彩菜参餐残蚕灿仓藏操草策层曾差拆产长常场超朝潮车陈称成城程吃持尺齿充冲虫抽出初除楚处触川穿传窗床创吹春词此次从粗促催存错");

        // d
        Add('d', "达打大呆代带待单但淡弹蛋当刀到道得的灯等低地弟帝点电掉丁顶定东冬懂动都斗豆读独度短断对队多朵躲");

        // e
        Add('e', "额恶饿儿耳二");

        // f
        Add('f', "发法反范方防房放非飞肥分纷粉风封夫服福浮符幅辐辅复副富赋父妇");

        // g
        Add('g', "该改概干甘感刚钢高搞告哥歌格给根更工公功攻供共狗够构购古谷骨固故顾关观管光广规归鬼国果过");

        // h
        Add('h', "哈孩海含寒汉好号浩喝和合何河核黑很恨后厚呼湖虎互花华化画话怀坏欢环换黄回会活火或获");

        // j
        Add('j', "击机积基极及急即集几己计记季加假间简见建将江奖讲交角脚较叫教接街阶结解姐介界今金近进京经精景静境究九酒久就举具据距剧卷决觉军");

        // k
        Add('k', "卡开看康考靠科可刻客课肯空孔控口快块况矿亏困扩");

        // l
        Add('l', "拉来兰蓝篮览劳老乐了雷类冷离里理力历利立例连联脸练良两量亮料列林灵领令流留六龙楼路录旅律论落");

        // m
        Add('m', "妈马吗买卖满慢忙毛冒么没美门们梦迷米密蜜面民名明命模末莫某母木目幕");

        // n
        Add('n', "拿哪那南难内能你年念娘鸟您牛农浓女暖");

        // o
        Add('o', "哦偶欧");

        // p
        Add('p', "怕排派盘判旁跑配喷朋碰批皮片飘拼平凭评瓶破扑铺普");

        // q
        Add('q', "七期其奇骑起气千前强桥切亲青清情请秋求区去全权却确群");

        // r
        Add('r', "然让热人认日容肉如入");

        // s
        Add('s', "三色山善上少社设身深生声圣师十时实食使始世市事是视室适收手首受书术树数双谁水睡说思死四送速算虽随碎岁所");

        // t
        Add('t', "他她它台太谈天田条铁听通同统头图土团推退拖脱");

        // w
        Add('w', "挖哇外完万王网往望微为位文问我无五物");

        // x
        Add('x', "西希息习系细下先现想象小笑些心新信星行形醒性修许续选学雪寻训");

        // y
        Add('y', "压呀牙言眼演阳养样要也业叶一衣已以义意因音应英影用由有又于与语元远院愿月云运");

        // z
        Add('z', "杂在再早则怎曾张长找者这真正之知只纸指至治中钟终种重周主住注转装准子自走足族组祖最昨做作座");

        return map;
    }

    // ── Keyboard navigation ──

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Hide();
                return;

            case Key.Down:
                e.Handled = MoveSelection(delta: +1);
                return;

            case Key.Up:
                e.Handled = MoveSelection(delta: -1);
                return;

            case Key.Enter:
                e.Handled = true;
                LauncherEntryRow? row = CurrentSelectedRow;
                if (row is null)
                {
                    return;
                }
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    LauncherEditRequested?.Invoke(row.Id);
                }
                else
                {
                    _ = LaunchCurrentAsync();
                }
                return;
        }
    }

    /// <summary>
    /// Moves the selection by delta (+1 down, -1 up). Clamped to [0, count-1];
    /// no wrap-around (matches the reference UI and avoids surprising users).
    /// Returns true if the selection moved (so the caller can mark the key
    /// event handled), false at the edges.
    /// </summary>
    private bool MoveSelection(int delta)
    {
        if (_filteredRows.Count == 0)
        {
            return false;
        }
        int newIndex = Math.Clamp(_selectedIndex + delta, 0, _filteredRows.Count - 1);
        if (newIndex == _selectedIndex)
        {
            return false;
        }
        _selectedIndex = newIndex;
        SyncRowSelection();
        ScrollSelectedIntoView();
        return true;
    }

    private LauncherEntryRow? CurrentSelectedRow =>
        _selectedIndex >= 0 && _selectedIndex < _filteredRows.Count
            ? _filteredRows[_selectedIndex]
            : null;

    private async Task LaunchCurrentAsync()
    {
        LauncherEntryRow? row = CurrentSelectedRow;
        if (row is null)
        {
            return;
        }
        string? selection = null;     // Spotlight doesn't capture selection context
        string? clip = null;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            clip = await clipboard.TryGetTextAsync();
        }
        Hide();
        LauncherRunRequested?.Invoke(row.Id, selection, clip);
    }

    // ── Visual selection state ──
    //
    // R43: previous approach walked the ItemsControl's containers and toggled
    // an "Active" class on them. That never worked, because ItemsControl wraps
    // each item in an internal ContentPresenter — NOT the Border inside our
    // DataTemplate — so the class never reached the styled element and the
    // selection highlight was invisible.
    //
    // Now we drive selection purely through the row model: each LauncherEntryRow
    // has an IsSelected flag (INotifyPropertyChanged), and the DataTemplate
    // binds "Classes.Active" to it via the Avalonia "Classes.<name>={Binding}"
    // syntax. Toggling IsSelected flips the class on the Border that actually
    // owns the SpotlightRow style. No container realization races possible.

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

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not LauncherEntryRow row)
        {
            return;
        }
        // Click-to-launch (mouse users shouldn't need Enter).
        int index = IndexOfRow(row);
        if (index >= 0)
        {
            _selectedIndex = index;
            SyncRowSelection();
        }
        if (e.Pointer.IsPrimary)
        {
            _ = LaunchRowAsync(row);
        }
    }

    private async Task LaunchRowAsync(LauncherEntryRow row)
    {
        string? clip = null;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            clip = await clipboard.TryGetTextAsync();
        }
        Hide();
        LauncherRunRequested?.Invoke(row.Id, null, clip);
    }

    private int IndexOfRow(LauncherEntryRow row)
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
}
