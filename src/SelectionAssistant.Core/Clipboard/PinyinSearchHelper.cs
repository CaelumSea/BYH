using System.Text;

namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// Three-tier fuzzy-match helper for search-as-you-type panels.
/// Originally extracted from <c>SpotlightWindow</c> (R32), shared with
/// <c>ClipboardHistoryWindow</c> (R54) and <c>ClipboardSearchMatcher</c> (R101).
/// Lives in Core so search semantics are unit-testable without a UI reference.
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
    /// Non-CJK characters are skipped. Uses a built-in lookup table covering 7762
    /// common BMP CJK characters (通用规范汉字表 primary pinyin reading). Returns
    /// lowercase initials. E.g. "微信" → "wx", "纪录片" → "jlp", "小旺AI截图" → "xwjt".
    /// </summary>
    public static string ExtractPinyinInitials(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Most clipboard entries are predominantly ASCII. Reserving the full
        // candidate length used to allocate a very large, mostly-unused buffer
        // (for example a 279k-character terminal transcript) even when it had
        // few or no CJK characters. Grow on demand instead.
        var sb = new StringBuilder(Math.Min(text.Length, 256));
        foreach (char c in text)
        {
            if (PinyinInitialMap.TryGetValue(c, out char initial))
            {
                sb.Append(initial);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extracts the exact sequence inspected by <see cref="MatchInitials"/>.
    /// Matching a query as a subsequence of the returned string is equivalent
    /// to running the legacy greedy scan, but the relatively expensive segment
    /// discovery only needs to happen once when a search index is built.
    /// </summary>
    public static string ExtractSegmentInitials(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(text.Length, 256));
        bool prevIsLower = false;

        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            bool isSep = c is ' ' or '-' or '.' or '_';
            bool isCamel = prevIsLower && char.IsUpper(c);
            bool isCjkBoundary = prevIsLower && IsCjk(c);
            bool isLetterAfterCjk = index > 0 && IsCjk(text[index - 1]) &&
                                    char.IsLetter(c) && !IsCjk(c);

            if (index == 0 || isSep || isCamel || isCjkBoundary || isLetterAfterCjk)
            {
                sb.Append(c);
            }

            prevIsLower = isSep ? false : char.IsLower(c);
        }

        return sb.ToString();
    }

    /// <summary>Lookup table: CJK character → lowercase pinyin initial.</summary>
    private static readonly Dictionary<char, char> PinyinInitialMap = BuildPinyinInitialMap();

    private static Dictionary<char, char> BuildPinyinInitialMap()
    {
        var map = new Dictionary<char, char>(7762);

        void Add(char initial, string chars)
        {
            foreach (char c in chars) map[c] = initial;
        }

        // Generated from the Unihan kMandarin dataset (通用规范汉字表, 8105 chars).
        // Each CJK char maps to the first letter of its primary pinyin reading.
        // CJK Extension B+ chars (U+20000+) are excluded — they don't fit in a single
        // 'char' key and are extremely rare in clipboard/search text. BMP coverage: 7762 chars.
        Add('a', "啊");
        Add('b', "不丙亳伯伴佖佰便保俵俾倍倴傍傧僰儦八兵冰别剥办勃包匕北匾半卑博卜卞卟变叭吡吧呗咇哔哱哺啵嘣坂坋坌坒坝埗埠堡塝壁备奔妣妭婊婢嬖孛孢宝宾岜崩嶓巴币布帛帮幖并庇庳弁弊弼彪彬彼必忭怖悖悲惫愎憋扁扒扮扳抃把报抱拌拔拜拨捌捕捭掰搏搒搬摆摈摒摽播擘攽敝斌斑昄昪昺晡暴本朳杓杯板枹柄柈柏标栟梆棒棓椑榜槟檗欂步殡比毕毖毙汴沘波泵浜浡渤湴滗滨濒濞瀌灞炳焙煲煸熛爆爸版犇狈狴玢玤玻珌班琫琲璧瓣瓿甏甭畀畚疤病痹瘢瘪瘭癍白百皕砭砵碑碚碥碧磅礴祊禀秉秕稗笆笔笨笾筚箅箔篦簸簿粑糒绊绑绷编缤罢羓耙胈背胞脖脿膀膊膑膘臂般舭舶芭苄苞苯苾荜荸菝菠萆萹葆蒡蓓蓖蔀蔈蔽薄薜藨蚆蚌蛃蝙补表被袯裨裱褊褒褓褙襞觱诐谤豳豹贝败贬贲赑趵跋跛跸踣蹦蹩辈辨辩辫边迸逋逼遍避邠邦邲邴邶部鄙醭鐾钚钡钣钯钵钹铂铋锛镈镑镔镖镚镳闭阪陂陛雹霸靶靽鞁鞭鞴颁飑飙饱饼饽馝馞驳骉骠髀髌鬓魃鲃鲅鲌鲍鲾鳊鳔鳖鸨鹁鹎鼻龅");
        Add('c', "㬚㳘䅟䝙䢺䲠丑丛丞串乘亍产仇从仓传伥伧伺佽侈侘侧侪侴促俦俶倕倡偁偲偿储催傺儳充冁册冲凑出刍创初刬刺匆厂厕厝厨参叉叱吃吵吹呈呲哧唇唱啐啜啴啻喘嗔嗤嘈嘬嘲噇噌嚓囱圌场坻坼垂垞城埕埫堲堾塍墀处姹娼婤婵婼媸嫦存孱宠宬宸察寸尘尝尺层岑岔崇崔嵖嵯嶒巉川巢差帱常幢床庱廛弛弨彩彳彻徂徜忏忖忡忱怅怆怊怵恻悰悴惆惙惝惨惩惭愁慈憕憧憷成戳才扯承抄抻抽拆持挫捶掣措掺插揣搀搋搐搓搽摏摛摧摴撑撤撮撺操擦敕敞斥斶旵昌春昶晁晟晨曹曾朝杈材村杵杻枞枨查柴柷柽梣梴棤棰椆椎椽椿楚楮榇榱槌槎槽樗橙橱檫次欻此殂残毳氅氚汆汊池沉沧泚测浐涔淙淬淳滁漕漦漼潮潺澂澄澈澶濋瀍灿炊炒炽焯煁熜爨牚犨猖猜猝猹玚玼珫珵珹琛琡琤琮瑃瑳璀璁璨瓷瓻畅畜畴疢疮疵痓痤痴瘁瘛瘥瘳皴眵睬瞅瞋瞠矗矬砗础碜碴磁磋祠禅秤称程稠穿窗窜笞策筹篡篪簇粗粲粹糍糙纯绌绰绸缞缠羼翀翅翠耖耻聪肠胣脆脞腠臣臭舂舛舱船艚艟苁苌苍茈茌茝茨茬茶茺草莝莼莿菖菜萃葱蒇蔟蔡藏虫虿蚕蚩蛏蜍蝉蝽螬螭蟾蠢衩衬裁裎褚褫襜觇触词诚诧谄谌谗谶豺财赐赤赪趁超跐踌踔踟踩踹蹅蹉蹙蹭蹰蹴蹿躇躔车辍辏辞辰辿迟逞逴遄邨郴酂酢酬酲醇醋采钏钗钞铖铲铳锄锉错锤锸镡镩镲镵闯阊阐陈除陲雌雏雠韂颤餐饬馇馋驰骋骖骢骣鬯魑鲳鲿鸧鸱鹑鹚鹾黜黪齿龀龊");
        Add('d', "㙍䃅䏲䗖丁东丢丹亶亸仃代佃但低侗倒傣儋兑党兜典冬冻凋凳凼刀刁到剁剟动单厾叇叠叨叮叼吊吨呆呔咄咚哆哒哚啖啶喋嗒嗲嘀嘚嘟噔囤地坫垈垌垛垤垫垯垱埭埵堆堕堞堤堵塅墩多大夺奠妒妲娣媂嫡宕定对导岛岱岽峒嶝巅帝带底店度弟弹当待得德忉怛怠怼恫悼惇惦惮惰憺懂戥戴扂打扽抖抵担挡捣捯掂掇掉掸搭敌敦斗断旦昳朵杕杜柁柢栋档棣椟椴楯歹殆殚段殿毒氐氘氡氮汈沌沓洞涤淀淡渎渡滇滴澹灯炖炟点焘煅爹牍牒犊狄独玎玓玳玷珰琔瑖璒瓞电甸疍疔疸痘瘅瘩癜癫登的盗盯盹盾眈睇督睹瞪短砀砘砥硐碇碉碓碘碟碡碲磴礅祋祷禘稻窎窦端笃笛笪第等答筜箪篼簖簟籴纛绐绖缎缔羝翟耋耑耵耷耽聃肚胆胨胴腚舠舵芏荙荡荻菂菪萏萣董蒂蔸蚪蛋蝶蠹袋裆裰褡觌订诋诞读调谍谛谠豆貂贷赌赕趸跌跶跺踮踱踶蹀蹈蹢蹬蹲蹾躲轪达迨迪迭递逗逮遁道邓邸郸都酊钉钓钝钿铎铛铞铤铥铫锝锭锻镀镝镦镫阇阘队阽陡雕靛靼鞑鞮顶顿颠饳骀骶髑髢鱽鲷鲽鸫黛黩鼎");
        Add('f', "㕮㳇丰乏付份仿伏伐佛俘俯俸偾傅冯凡凤凫分剕副匐匪反发吠否吩呋咐唪啡坊坟垡垺墦复夫奉奋妃妇妨孚孵富封峰崶帆幅幞幡府废弗忿怫悱愤房扉扶抚拂拊放敷斐斧方昉服朏枋枫桴梵棐棻棼榑榧樊氛氟氾汾沣沨沸法泛洑浮浲涪淝滏瀵烦烽焚燔父牥犯狒玞珐琈璠甫畈番疯痱矾砆砜砝祓福稃符筏篚簠粉粪繁纷纺绂绋绯缚缝缶罘罚翂翡翻肤肥肪肺腐腑腓腹舫艴芙芣芬芳芾苻茀范茯莩菔菲葑蕃藩蘩蚄蚨蜂蜉蜚蝠蝮袱覆讣讽访诽豮负贩费赋赗赙赴趺跗蹯辅辐返逢邡郛鄜酆酚釜钒钫锋镄阀阜防附霏非韨风飞饭馥驸鲂鲋鲱鲼麸黻黼鼢");
        Add('g', "㭎㽏䢼丐个乖亘仡估伽佝供倌傀光公共关冈冠冮刚刮刽刿剐割功勾匦卦古各告呙呱呷咕咣咯哏哥哽哿唝嗝嘎嘏噶固国圪圭坩坬垓垙垢埂埚堌堽塥够夬妫姑姤姽媾孤宄官宫寡尕尜尬尴岗岣崞崮工巩帼干广庋庚廆弓彀归怪恭惯感戆戈戤扞拐拱挂掴掼搁搞擀改攻故敢旮旰晐晷暅更杆杠杲构果枸柑柜栝根格桂桄桧梏梗棍棺椁概榖槁槔橄歌毂毌氿汞汩沟沽泔洸浭涫淦港溉滆滚澉瀔灌炔爟牯牿犷狗玕珖珙珪琯瑰瓘瓜甘疙疳痼癸皈皋盖盥睾瞽矸矼硅硌磙祼秆稿竿笱筀筦筶筻箍管篙篝簋粿糕纥纲绀给绠绲缑缟缸罐罟罡羔羖羹耇耕耿聒肛肝股肱胍胱胳腘膈膏臌舸艮苟苷茛荄莞菇菰葛蒄蓇藁虢虼蚣蛄蛊蜾蝈衮袼裹褂观规觏觚觥诂诖诟诡该诰谷贡购贯贵赅赓赣赶跟跪躬轨轱辊辜过逛遘邽郜郭酐酤钆钙钢钩钴铬锅锆锢镉镐闺阁陔隔雇雊革鞲顾馃馆馉馘骨骼高鬲鬶鬼鲑鲠鲧鲴鳏鳜鳡鳤鸪鸹鸽鹄鹒鹘鹳鼓龚龟鿍");
        Add('h', "㘎㧑㬊㸌㿠乎互亥亨伙会何佸侯候冱凰函划劐劾化卉华厚号合后含吼吽呵呼和咍咴哄哈哕哗哼唤唬唿喉喊喙喝喤嗐嗥嗨嗬嘿嚄嚆嚎嚯回囫圜坏垎垕垾堠堼壑壕壶夥夯奂好姮婚婳媓嫭嬛孩宏宦害寒寰岵峘崡嵅幌幻弘弧彗很徊徨徽忽怀怙恍恒恚恢恨悍悔患惑惚惛惠惶慌慧憨憾或户戽扈护挥捍换撖撼擐攉斛旱昈昊昏昒晃晖晗晦暵曷杭核桁桓桦槐槲横橞欢毁毫氦汇汉汗沆沪河泓洄洪洹活浍浑浒浛浣浩海涣涵涸淏淮淴混湖湟溷滉滑滹漶漷潢澴濠濩瀚灏火灰烀烘烠烩焊焓焕煌煳熇狐狠猢猴猾獾玒环珩珲琀琥瑚瑝璜瓠画痕痪瘊癀皇皓皞盉盍盒砉硔祜祸禾秽竑笏篁篌簧糇糊縠红纮绗绘缓缳罕翃翙翚翮翯翰耗耠肓胡胲航艎花茴荁荒荟荤荭荷获菏菡萑葫蒿蔊蕙蕻薅薨藿蘅虎虷虹虺蚝蚶蛔蛤蝗蝴蟥蟪蠖衡袆褐觟觳訇讧讳诃诙话诨诲谎谼豁豢豪貆貉货贺贿赫踝轰轷辉还逅逭遑邗邯郃郇郈郝鄗鄠酣醐醢钬铧铪锪锽锾镬镮闳阂阍阖阚隍隳隺霍鞨韩顸颃颌颔颢饸馄骅骇骸骺鬟魂鲎鲘鲩鳇鳠鸻鸿鹕鹖鹤鹮鹱麾黄黉黑鼾齁龁龢");
        Add('j', "㛃㠇㵐䌹䐃䴔䴖䴗举久九乩井亟交京仅今介件价伋伎佳佶佼侥俊俭俱倔倞借倦倨假偈健傕僦僬僭僵儆兢具兼冀冏军决净减几击刭剂剑剞剧剪剿劂加劫劲劼匠即卷卺厥厩及叚句叫叽吉君咀咎唧啾喈嗟嘉噍噘噤噱嚼圾均坚坰垍基堇境墐墼夹奖奸妓妗姐姜姞姣姬娇娟娵婕婧嫁嫉孑孓季家寂寄将尖就尽局居届屐屦岊岌岠岬峤峧峻崌崛嵇嵴巨己巾廑建弆弶径徛徼忌急恔恝悈悸惊惎惧憬戋戒戛戟戢截戬扃技抉拒拘拣拮挤捃捐捡据捷掎掘接掬揪揭搅搛撅攫救教敫敬斝斠斤旌既旧晋晙景晶暕暨机杰极枅枧架枷柩柬桀桊桔桕桨桷检棘椐椒楗楫榉槚槿橘橛檞歼殛殣毽江汫汲沮泂泃泇泾洁洎洚津浃浆浇济浕浚浸涓涧渐湔湝湫溅溍滘漈漖澽激瀱灸炅炬炯烬焆焌焗焦煎燋爝爵牮犄犋犍犟狙狡狷猄獍獗玃玑玖玠玦珈珏珒珺琎琚瑨瑾璟璥璬甲界畯畸畿疆疖疚疥疽疾痂痉瘕瘠皆皎皦皭皲监眷睑睛睫矍矜矩矫矶砄砠碣碱礁礓祭祲禁秬积秸稷稼稽穄究窖窘窭竞竟竣竫竭笄笈笕笳笺筋筥简箕箭篯籍粳精糨紧絜纠级纪经结绛绝绞绢继绩缄缉缙缣缰缴罽羁羯翦耤耩聚肌肩肼胛胫胶脊脚腈腒腱膙臼舅舰艰艽节芥芨芰苣苴茄茎茧茭茳荆荐荚荠荩莒莙菁菅菊菌菹葭蒋蒟蒹蒺蓟蕉蕨蕺藉藠虮蚧蛟蛱蜐蠲街衿袈袷裥裾褯襟见觉觊觐角觖解觭謇警计讥讦记讲讵诀诘诫谏谨谫谲豇贱贾赆赍赳趄趼跏距跤践跻跽踞踺踽蹇蹐蹶轿较辑近进迥迦迳迹遽郊郏郡鄄酒酱酵醮醵金鉴钜钧钾铗铰锏锔锦锩键锯镌镓镜镢间阄阱阶际降隽集雎霁靓靖静靳鞠鞫鞬鞯韭颈颉颊颎飓饥饯饺馑驹驾骄骏骥骱髻鬏鱾鲒鲚鲛鲣鲪鲫鲸鳉鳒鸠鸡鹃鹡鹣鹪鹫麂麇麖鼱齑龃");
        Add('k', "㧟㸆亏亢伉侃侉侩倥克况凯刊刳刻剀剋勘匡匮匼卡口叩可吭咔咖咳哐哙哭啃喀喟喹喾嗑困圐圹坎坑块坤坷垦垮垲埪堃堪墈壳壸夔夸夼奎姱婫孔客宽寇尻岢岿崁崆嵁库康廓开快忾恐恪恳恺悃悝愦愧慨慷戡戣扛扣扩抗抠括拷挎捆控揆揩旷昆暌枯柯栲框棵楷槛櫆款氪洘洭渴溃溘炌炕炣烤焜煃牁犒狂狯珂琨疴盔看眍眶睽瞌瞰矻矿砍硁硿磕磡科稞空窟窠窥筐筘筷箜篑糠纩绔缂考聩肯胩胯脍芤苛苦莰葵蒈蒉蒯蔻蛞蝌蝰衎裈裉裤诓诳课贶跨跬轲逵邝郐酷醌钪铐铠铿锎锞锟锴闶闿阃阔隗靠颏颗馈馗骒骙骷髁髋髡魁鲙鲲鹍龈龛");
        Add('l', "㥄㫰㮾㰀䁖䂮䴕两临丽乐乱了亮仂仑令伦伶佬例侣俍俐俚俩俪俫倮偻傈僇僚儡六兰冷冽凉凌凓凛列刘利剅剌力劣励劳勒勠卢卤卵历厉厘另叻吏吕吝呖呤咙咧哢哩唠唳啉啦啰啷喇喱喽嘞嘹噜囵囹圙坜坽垃垄垆垏垒埌埒堎塄塱墚奁姈姥娄娈娌婪嫘嫠嫪嫽孪寥寮尥屡履岚岭峛峦崀崂崃崚嵝嶙帘庐廉廊廖廪录律徕怜恋悢愣憭懒懔戮戾抡拉拎拢拦挛捋捞捩掠掳揽搂摞撂撩撸擂敛斓料旅旒旯昤昽晾朗朸李来林枥柃柳栊栌栎栏栗栳栾桹梁梠梨梾梿棂棱椋椤楝楞楼榄榈榔榴橑橹檑檩殓氇氯沥沦泐泠泪泷泸泺洌洛流浏浪浬浰涝涞涟淋渌溇溜溧滤滥滦漉漋漏漓漤漯漻潋潞潦潾澛澜澧澪濂濑灵炉炼烂烈烙烺熘燎牢犁狸狼猁猎猡獠率玲珋珑珕珞琅理琉琏琭琳瑓瑬璃璐璘瓴甪留略疁疗疠疬痢痨瘌瘘瘤瘰癃癗癞眬睐瞭瞵砬砺砻砾硫碌磊磏磷礌礼祾禄离稂稆稑窿立笠笼筤箓箖箩篓篥篮篱簏簕簝籁类粒粝粮粱粼累纶练络绫绺绿缆缕缡缧缭罍罗罱罶罹羚羸翎翷老耒耢耧聆聊聋联肋胧胪脔脟脶脸腊膂膦舲舻良芦苈苓茏荔荖荦莅莉莨莱莲菉菱萝落葎蒌蒗蓏蓝蓠蓢蓼蔹蔺蕗蕾藜藟虏虑蛉蛎蜊蜡蝲蝼螂螺蠃蠊蠡裂裢裣裸褛褴襕览詈论诔谅谰赁赂赉赖趔跞路踉蹽躏躐轮轳轹辂辆辌辘辚辣辽连逦逯逻遛遴邋邻郎郦酃酪酹醨醪醴里量釐銮鎏钌铃铑铝铹铼链锂锊锍锒锣镂镏镠镣镥镧镭镰镴闾阆阑陆陇陋陵隆隶雒雳零雷霖露鞡颅领飗饹馏驴骆骊骝骡髅髎鬣魉鲁鲈鲡鲢鲤鲮鳓鳞鳢鸬鸰鸾鹂鹠鹨鹩鹭鹿麓麟黎黧龄龙");
        Add('m', "㠓丏么乜买亩仫们侔偭免冒冕冥劢勉勐勔募卖卯名吗命咩咪哞唛喵嘛嘧埋墁墓墨妈妙妹姆娩媄媒媚嫚嫫嬷孖孟宓密寐寞尨岷峁嵋帽幂幔幕幪庙弥弭忙忞悯愍愐慕慢懋懑懵扪抹抿拇描摩摸摹敉敏旄旻明昧昴暝暮曼朦木末杧杩杪枚某梅梦棉楙楣模檬殁母每毛毪民氓汨沐沔没沫泌泖泯洣洺浼淼渑渺渼湄湎湣溟满漠漫漭灭焖煤牟牡牤牦牧牻犸猕猛猫獴玛玫珉瑁瑂甍瘼皿盟目盲眄眇眉眊眠眯眸睦瞀瞄瞑瞒瞢矛码硭碈磨礞祃祕祢秒秘秣穆篾米糜縻绵缅缈缗缦缪美耄耱脉脒腼膜艋艨芈芒芼苗苜苠茂茅茆茉茗茫荬莓莫莽萌蒙蓂蓦蔑蔓藐藦蘑蘼虻蚂蛑蛮蜜蜢蝥螟螨蟆蟊蟒蠓袂袤觅谋谜谟谧谩谬貊貌貘贸迈迷邈邙郿鄚酩酶醚醾鍪钔钼铆铭锚锰镁镅镆镘门闵闷闽陌霉霾靡面靺鞔颟馍馒马骂髦鬘魅魔鳗鳘鸣鹋鹛鹲麋麦麻麽默黾鿏");
        Add('n', "乃乸伲佞你侬倪傉傩内农凝努匿南呐呢呶咛哝哪啮喃喏嗫囊囔囡坭垴埝奈女奴奶妞妮娘娜婻嫩孥孬孽宁尼尿峱嵲年廿弄弩念忸怒怩恁恧恼您懦扭拈拟拧拿挠挪捏捺捻搦撵攮旎昵暖曩柠柰楠氖泞泥浓涅淖溺牛狃狞猊猱瑙甯男疟睨砮硇碾秾笯糯糵纳纽耏耐耨聂聍肭胬能脑脓脲腩腻臑臬艿苧茑菍萘萳蔫薿蘖虐蛲蝻衄衲袅讷诺赧蹑辇辗迺逆那酿钕钠钮铌铙锘镊镍镎闹陧难霓颞馁馕驽鲇鲵鸟麑黏鼐齉");
        Add('p', "㛹䥽䴙丕乒乓仆仳伾佩俜俳偏僻凭判刨剖剽劈匍匏匹厖叛叵呸咆品哌啤啪喷嘌嘭噗噼嚭圃圮坡坪坯埔埤培堋墣姘娉婆媲嫔嫖屁屏帔帕帡平庖庞弸彭彷徘怕怦扑批抔抛抨披拍拼捧掊排撇攀旁旆普曝朋朴杷枇枰桲棚椪楩槃殍毗氆氕沛泊泙泡泮泼洴派浦涄淜淠湃湓溥滂漂潖潘潽澎澼濮瀑炮烹爬爿片牌牝犏狉狍玭玶珀琵琶璞瓢瓶甓畔疱疲痞癖皤皮盆盘盼睥瞟瞥砒砰破硼碰磐磻票穙笸筢篇篷簰粕纰缥罴翩耪聘胖胚胼脬脯脾膨舥芃芘苉苤苹荓莆菩萍葡葩蒎蒱蒲蓬薸蚍蚲蜱螃螵蟛蟠衃袍袢裒裴襻譬评谝谱貔贫赔趴跑蹁蹒蹼辔辟迫逄邳郫鄱配酦酺醅钋钷铍铺锫镤镨陪陴雱霈霹颇频颦飘骈骗魄鲆鲏鳑鹏鼙");
        Add('q', "㭕䓖䓛䓫七且丘乔乞乾亓亲仟企伣佥佺侨侵俅俏俟倩倾全其凄切券前劁劝劬勍勤区千却卿去取吣启呇呛嗪嘁噙器囚囷圈圊圲圻坥埆埼堑墘墙奇契妻妾娶婍婘嫱孅寝屈屺岂岍岐岖岨峭崎嵌嵚巧巯庆庼弃强怯恰悄悛悫悭情惬愀愆愭慊慬憔憩戕戗戚扦抢拤拳挈掐掮揿搴撬擎擒敲旗晴曲朐期权杄杞枪柒栖桤桥棋棨棬椠楸榷槭樯樵橇檎檠欠欺歉歧氍气氢氰求汔汧汽沁沏泅泉泣洽浅淇清渠溱漆潜灈炝牵犬犰玘玱球琦琪琴琼瑔璆璩畎畦痊瘸癯癿瞧瞿砌硗硚确碃碏碛碶磜磬磲祁祇祈祛祺禽秋秦穷穹窃窍竘筇筌签箐箧糗綦綮绮绻缱缲缺罄羌羟群翘耆肷胠脐腔芊芎芑芡芩芪芹苘茕茜荃荞萁萋萩葜葺蒨蔃蔷蕖蕲蘧虔虬蚯蛆蛐蛩蛴蜞蜣蜷蜻蝤蠼衢衾袪裘裙褰襁觑訄讫诎诠诮请谦谯谴赇起趋趣跂跄跷蹊躯轻辁迁迄逑逡遒遣邛邱郄郪酋醛銎钎钤钦钱钳铅铨锓锖锜锲锵锹镪阒阕阙阡雀青靬鞒鞘鞧顷颀颧驱骎骐骑骞髂鬈鲭鲯鳅鳈鳍鸲鹊鹐鹙麒麹黔黢黥鼩鼽齐龋");
        Add('r', "䎃乳人仁仍仞任偌儒儴入冉冗刃嚅嚷堧壤壬如妊娆媆嬬孺容嵘弱忍惹戎扔扰揉攘日枘染柔桡榕汝汭洳润溶溽濡瀼热然熔燃爇狨瑞瑢瓀瓤睿禳稔穰箬糅纫纴绒绕缛肉芮苒若茸茹荏荛荣葚蒻蓉蓐蕊蕤薷蘘蚋蚺蝾融蠕衽褥襦认让讱蹂轫软辱鄀铷锐镕闰阮鞣韧颥饪饶驲髯");
        Add('s', "㟃㧐䏡䴓三上世丝丧书事什仕仨伞伤伸似佘使侁侍俗倏傃傻僧僳兕兽凇凘删刷刹剡剩劭势勺匙十卅升厍厦厮叁双叔受叟史司吮呻咝哂哨唆唢售唰唼商啥啬善嗉嗍嗓嗖嗜嗣嗦嗽嗾嘶噬噻四圣垧埏埘埽塑塞塾墅墒墡士声夙失奢奭妁始姒姗姝娀娑娠婌婶媞嫂嬗孀孙孰守宋实审室宿寺寿射少尚尸屎属山屾岁崧嵊嵩巳市帅师帨庶廋式弑忪怂思恃恕悚愫慎慑戍所扇手扫抒拭拴拾挲捎损授掞搔搜搠搡摄摅摔撒撕擅擞收散数斯施旞时昇是晌晒晱暑曙朔术杀杉束松枢柖柿树栓栻桑桫梢梭梳森棽椹楒榫槊歃歙死殇殊殳毵毹氏水汕汜沈沙沭泗洒洓浉涉涑涘涩涮淑淞深渗湜湿溞溯溲溹滠漱潲潵潸澌澍濉炻烁烧煞煽熟熵燊燧爽牲狩狮狲狻猞玿珅珊琐瑟璱璲甚生甡甥甦甩申畬畲疏疝痧瘆瘙瘦盛省眚眭睃睄睡睢瞍瞫瞬矢矧石砂砷硕碎磉礵示社祀祏神祟私秫稍税稣穑穗穟窣竖竦笋笙笥筛筮筲算簌粟糁素索纱纾绅绍绥绱绳绶缌缩缫缮署羧耍耜耸肃肆肾胂胜脎腧腨腮膳膻臊舌舍舐舒舜舢艄艏艘色芍芟苏苕苫荪荽莎莘莳菘菽萨葰蒐蒜蒴蓍蓑蔌蔬薮薯虒虱虽蚀蛇蛳蛸蜀蜃螋螫蟀蟮衫衰裟裳襚襫视觞觫誓讪讼设识诉试诗诜说诵谁谂谇谡谥豉豕贳赊赎赏赛赡赦跚蹜身轼输述送适逝速遂邃邵邿鄃鄯酥酸酾释钐铄铈铩铯锁锶锼闩闪陎陕陞隃隋随隧隼霎霜靸韶顺颂颡飒飔飕食飧饰饲馊馓首驶驷骕骚骟骦髓鲥鲨鲹鲺鳃鳝鸤鸶鹔鹴麝黍鼠鼫");
        Add('t', "㛚㻬䗴䣘䲢䴘亭他仝体佗佟佻侂侹倓倘倜停偷傥僮兔凸剃剔厅台叹同吐吞听哃唐唾啕啼嗵嘡嚏团图土圢坉坍坛坦坨堂堍塌塔塘填天太头套她妥婷嬥它屉屠屯峂帑帖庭庹廷弢彖彤徒忐忑忒忝忳态恬恸悌惔惕慆慝托投抟抬拓拖挑挞挺捅掏探推掭提搪摊擿昙晪暾曈替朓条柝桃桐桯桶梃梌梯梼棠椭榃榻樘橐橦檀殄毯汀汤汰沱沺泰洮涂涕涛淌淘淟添渟湉湍溏溚溻滔滕滩潭潼炭炱烃烔烫烶焞煓煺熥特猯獭珽瑅瑭璮甜田町畋疃疼痛痰瘫盷眺瞳砣砼碳磹祧秃稌突窕童笤筒箨粜糖縢统绦绨绹缇羰耥肽胎脱腆腯腾腿膛臀舔艇苔茼荑荼莛菟菼萄萚萜葖葶薹藤蜓蜕蜩螗螣螳袒裼褟褪覃誊讨谈谭豚贪贴趟趯趿跆跎跳踏踢蹄蹋蹚躺迢退逃透途逖通遆遢邰郯鄌酞酡酮酴醍钍钛钭钽铁铊铜铴铽锑锬镋镗闼阗陀陶霆鞳韬颋颓题餮饕饧饨驮驼骰髫魋鲀鲐鲖鲦鳀鳎鸵鹈黇鼍鼗龆鿎");
        Add('w', "万丸为乌五亡亹仵伍伟伪位佤侮倭偎偓兀刎剜务勿午卧卫危吴吻吾呒呜味哇唔唯喂嗡围圩圬坞塆外妄妧妩委威娃娓娲婉婠婺完宛寤尉尪尾屋屼峗崴嵬巍巫帏帷幄庑弯往微忘忤怃悟惋惘惟慰戊我挖挝挽捂握文斡无旺旿晚晤望未杌枉桅梧椀榅武歪毋污汪汶沃沩洈洧洼洿浯涠涡涴渥温渭湾溦滃潍炆炜烷焐煟煨物牾猥猬王玟玩玮珷珸琟琬璺瓦瓮畏畖畹痦痿瘟皖硊硙硪碗碨稳窊窝紊纨纬纹维绾网罔翁肟胃脘腕腽舞艉芄芜芠芴苇莴菀萎葳蓊蔚蕰蕹薇薳蚊蛙蜈蜗蜿螱袜诬误诿谓豌踒辋辒迕违逶邬郚鋈钨铻问闱闻阌隈雯雾霨靰韦韪顽骛魍魏鲔鳁鳂鳚鹀鹉鹜鹟鼯龌");
        Add('x', "㙦㬎㳚㴔䗛䜣下习乡些享亵仙伈休伭侠俙信修偕偰傒像僖儇兄先兮兴冔写冼凶刑削勋勖勰匈匣协卸厢县叙吁向吓吸咥咸咺咻响哓哮唏啸喜喧嗅嘘嘻噀嚣囟型垿埙墟夏夐夕奚姓娴婞婿媭媳嫌嬉孝学宣宪宵寻小屃屑屣岘岫峃峋峡崄崤嶍嶲巇巡巷巽希席幸序庠庥廨弦形徇徐徙循心忺忻性恂恓恤息悉悬悻惜想惺愃憙懈戌戏挟挦掀揳携撷擤效敩斜新旋旬旭旴昔昕星昡昫显晅晓晞晰暄暇暶暹暿曛曦朽杏析枭枲枵柙栒校栩械楔楦榍榭樨橡檄欣歆歇殉氙汐汛汹泄泫泻洗洨洫洵浔浠消涍涎淅淆渫渲湑湘溆溪溴滫漩潇潟澥瀣炘炫烜烯煊煋煦熄熊熏熙熹熻燮燹爔牺犀狎狝狭猃猇猩献獬獯玄现玹玺珛珝珣珦琄琇瑄瑆瑕璇瓖痃痫癣皙皛盱相眩睎瞎硍硎硒硖硝碹祆祥禊禒禤禧秀稀穴穸窨窸笑筅筱箫箱籼粞糈系絮纤线绁细绚绡绣绤绪续缃缐缬罅羞羡羲翈翔翕翛翾肖肸胁胥胸脩腥腺膝舄舷舾芗芯苋茓荀荇荥荨莶菥萧萱葙葸蓄蓰蓿蕈薛薢薤薪薰藓虓虚虾蚬蜥蝎螅蟋蟏蟹血衅行衒衔袖袭襄西觋觿训讯许讻诇询详诩谐谑谖谞谢谿象豨貅贤跣跹踅躞轩辖辛迅选逊逍遐邂邢邪郗郤酅酗酰醑醒醯醺鑫钘铉铏铣销锈锌锡锨镶闲阋陉限险陷隙隰雄雪需霄霞霰靴鞋项须顼飨饩饷饻馅馐香馨驯骁骍骧髹魆魈鲜鲞鲟鳕鳛鸮鸺鹇黠鼷");
        Add('y', "㑊㙘㶲㺄䓨䲟䶮一与业严丫乂义乙也予于云亚亦亿以仪仰伊优伛伢佁佑余佚佣佯佾侑依俑俞俣俨倚倻偃允元兖养冤冶刈刖劓勇勚匀匜医卣印压厌原厣又友右叶吆吖吟吲呀呓员呦咉咏咦咬咽咿哑哟唁唷喁喑喻嘤噎噫嚚因园囿圄圆圉圫圯垚垟垠垣垭垸埇域埸堉堐堙堨堰塬墉墕壅壹夜夤夭央夷奄奕妍妖妘妤妪姚姨姻娅娱婴媖媛媱媵嫄嫕嫣嬴嬿孕宇宜宥宧宴寅寓尢尤尧尹屹屿岈岩岳峄峣峪峿崖崟崦崾嵎嵛嶷已幺幼幽应庸庾廙延异弇弈弋引彝彟彦彧影役徉御徭忆忧怏怡怨怿恙恹恽恿悆悒悠悦愈愉意愔愚愠愿慭慵懿戭扅扆扊扬抑押拥挹掖掩掾揄揖揠援揶摇撄攸敔於旖旸昀易映昱晏晔晕曜曰曳月有杙杨杳枍柚栐样桠棪棫椅椰椸楪楹榆槱樱樾橼檐欤欲欹歅殃殒殪殷毅毓氤氧氩氲永沂沄沅沇油沿泱泳洇洋洢浟浥浴涌涢涯液淤淫淯淹渊渔渝渰游湮湲溁源溢溵滟滢滧滪演漪漹漾潆潏潩澭瀛瀹炀炎烊烟烨烻焉焰焱煜煴熠熨燏燕燚燠爚爰爷爻牖牙犹狁狱狳狺猗猰猷猺猿玉玙玡玥珢珧琊琰瑀瑗瑛瑜瑶璎甗用甬由疑疡疣疫痈痍痒瘀瘐瘗瘾瘿癔盂盈益盐眙眢眼睚矞矣砑研砚硬祎祐禋禹禺秧移窅窈窑窬窳竽筠筵筼箢簃籥粤繄繇纡约纭绎缊缘缢缨罂罨羊羑羕羱羽羿翊翌翳翼耀耘耰耶聿肄育肴胤胭胰腋腌腰腴膺臃臆臾舀舁舆舣艅艳艺芋芫芸芽苑苡英茔茚茵荧荫药莜莠莸莹莺萤营萦萸蓣蓥蕴薁薏虞虤蚁蚓蚜蚰蚴蛘蛹蜎蜒蜮蜴蝇蝓蝘蝣螈螠蟫衍衙衣袁裔裕裛褕要觃觎言訚誉议讶译诒诣语诱谀谊谒谕谚谣谳豫贻赝赟赢越跃踊踦轧轶轺辕迂迎运迓远迤逸逾遇遗遥遹邀邑邕邘邮邺郁郓郢郧郾鄅鄘鄞鄢酉酏酝酽釉野鋆钇钖钥钰钺铀铕铘铟铱银锳镒镛镱闫阅阈阉阎阳阴院陨隅隐雁雅雍雨雩霪靥靿鞅韫音韵页预颍颐颖颙颜飏餍饔饫饮饴馌馧驭驿骃验髃鬻魇鱼鱿鲉鲬鳐鳙鸢鸦鸭鸯鸳鹆鹝鹞鹢鹦鹬鹰麀黝黟黡鼋鼬鼹龂龉龠");
        Add('z', "㑇㤘䃎䎖䏝䓬䗪䦃丈专中主之乍争仄仉仔仗仲众伫住佐作侄侏侦俎倧倬债值做偡偬僎僔兆兹再冢准凿则制劄助匝卒卓占卮叕只召吒吱周咂咋咒咤咨咫咱哉哲哳唑唣啁啄啧啫啭喆喳嗞嘱嘴噂噪在圳址坐坠埴增壮奏奓奘妆妯姊姿嫜子字孜孳宅宗宙宰寁寨尊展岞峙峥崒崭崽嵫嶂嶟嶦州左帐帙帚帜帧帻幛庄庤座张彘彰征徵志忠忮怍怎怔总恣惴慥憎战扎执扺找抓折拃拄拙招择拯拶拽指挓挚挣振捉捽掌掷揍揕揸搌摘摭撙撞撰擢攒攥支政整斋斟斩斫旃族旐旨早昃昝昣昨昭昼晊晢晫智暂暲曌最朕札朱杂杖杼枕枝枣枳柊柘柞柱栀栅栈栉株栴栽桌桎桢桩梓梽棁棕棹植椓楂榛榨榰槜槠樟樽橥止正殖毡汁汋沚治沼沾泜注泽洙洲浈浊浙浞涨涿淄渍渚渣湛溠滋滍滓滞漳漴潴澡濯灶灼灾炙炷炸烛烝照煮燥爪牂状狰猪獐珇珍珠琢瑑瑧瑱璋璪瓒甄甑甾畛畤疐疭疰疹痄症痔痣瘃瘴瘵皂皱盅盏直真眦眨着睁瞩瞻知矰砖砟砧砫砸磔祉祖祗祚祝祯禔禚禛种租秩秭稙稚稹穜窀窄窒站章竹竺笊笫笮筑筝箦箴箸篆簉簪籀籽粘粢粥粽糌糟紫絷纂纣纵纸纻纼组织终绉综绽缀缁缒缜缯缵罩罪置罾翥者耔职肇肘肢肫肿胀胄胗胙胝脂脏腙臜臧自至致臻舟舯舳舴芝芷苎茁茋茱茽荮著葬葴蒸蓁蔗蕞藻蘸虸蚤蚱蛀蛛蛭蛰蜇蜘螽蟑蠋衠衷袗装褶觜觯訾詟詹证诅诈诊诌诏诛诤诸诹诼谆谪谮谵豸贞责账质贮贼贽赀赃资赈赒赘赚赜赞赠赭走赵趑趱足趾跖跱踪踬踯踵躁躅躜转轴轵轸载轾辀辄辎辙这迮追逐造遭遮遵邹邾郅郑鄑鄣鄫鄹酌酎酯醉重錾针钊钟钲钻铚铡铢铮铸锃锗锥锧锱锺镃镇镞镯长闸阵阻阼陟陬障隹雉霅震颛飐馔驵驺驻骓骘骤髭髽鬃鬒鬷鲊鲗鲝鲰鲻鳟鳣鸩鸷鸼鹧鹯麈黹鼒齇龇");
        // 
        return map;
    }
}
