using System.Text;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Shared three-tier fuzzy-match helper for search-as-you-type panels.
/// Extracted from <see cref="SpotlightWindow"/> (R32) so <see cref="ClipboardHistoryWindow"/>
/// (R54) reuses the exact same substring + initials + pinyin matching.
/// </summary>
/// <remarks>
/// <b>Matching tiers</b> (a hit on any tier counts):
/// <list type="number">
///   <item><b>Substring</b> — case-insensitive <c>Contains</c>.</item>
///   <item><b>Initials scan</b> — each query char must match the start of a word
///   segment in the candidate. A segment starts at index 0, after a separator
///   (space/hyphen/dot/underscore), at a camelCase boundary (lower→upper), or
///   at a CJK/letter boundary.</item>
///   <item><b>Pinyin initials</b> — CJK characters are mapped to their lowercase
///   pinyin initial (e.g. 微→w, 信→x); the query is then matched as a substring
///   of that initial string. Non-CJK chars are skipped.</item>
/// </list>
/// </remarks>
public static class PinyinSearchHelper
{
    /// <summary>Returns true if <paramref name="candidate"/> matches
    /// <paramref name="query"/> on any of the three tiers.</summary>
    public static bool MatchesQuery(string candidate, string query)
    {
        // 1. Substring match.
        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Initials greedy scan.
        if (MatchInitials(candidate, query))
        {
            return true;
        }

        // 3. Pinyin initials match (Chinese characters only).
        string pinyin = ExtractPinyinInitials(candidate);
        if (pinyin.Length > 0 && pinyin.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Greedy scan: each character in <paramref name="query"/> must match the
    /// start of a word segment in <paramref name="candidate"/>. After a match,
    /// scanning continues from the next character (greedy).
    /// E.g. "bb" vs "Bilibili": B@0 matches 'b', skip to pos 1, word-end at 1
    /// (lowercase→lowercase boundary), b@2 matches 'b' → true.
    /// </summary>
    public static bool MatchInitials(string candidate, string query)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(query))
        {
            return false;
        }

        int qi = 0;
        bool prevIsLower = false;

        for (int ni = 0; ni < candidate.Length && qi < query.Length; ni++)
        {
            char c = candidate[ni];
            bool isSep = c is ' ' or '-' or '.' or '_';
            bool isCamel = prevIsLower && char.IsUpper(c);
            bool isCjkBoundary = prevIsLower && IsCjk(c);
            bool isLetterAfterCjk = ni > 0 && IsCjk(candidate[ni - 1]) && char.IsLetter(c) && !IsCjk(c);

            bool isSegmentStart = ni == 0 || isSep || isCamel || isCjkBoundary || isLetterAfterCjk;

            if (isSegmentStart && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[qi]))
            {
                qi++;
                if (qi >= query.Length)
                {
                    return true;
                }
            }

            if (!isSep)
            {
                prevIsLower = char.IsLower(c);
            }
            else
            {
                prevIsLower = false;
            }
        }
        return qi >= query.Length;
    }

    /// <summary>Returns true if <paramref name="c"/> is a CJK Unified Ideograph.</summary>
    public static bool IsCjk(char c) => c >= '\u4E00' && c <= '\u9FFF';

    /// <summary>
    /// Extracts pinyin initials from Chinese characters in <paramref name="text"/>.
    /// Non-CJK characters are skipped. Uses a built-in lookup table covering
    /// common characters (~600 entries). Returns lowercase initials.
    /// E.g. "微信" → "wx", "小旺AI截图" → "xwjt".
    /// </summary>
    public static string ExtractPinyinInitials(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (PinyinInitialMap.TryGetValue(c, out char initial))
            {
                sb.Append(initial);
            }
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
}
