using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

/// <summary>视频卡片信息(跨页面通用)</summary>
public class VideoItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Author { get; set; } = "";

    /// <summary>
    /// UP 主头像地址。**只有动态流需要**(动态卡片顶部要摆头像)——
    /// 首页/搜索那些列表接口不带头像, 那里保持空串即可, 界面按"没有就不显示"处理。
    /// </summary>
    public string AuthorFace { get; set; } = "";

    public string Duration { get; set; } = "";
    public long ViewCount { get; set; }
    public long DanmakuCount { get; set; }
    public long Pubdate { get; set; }

    /// <summary>
    /// UP 主 mid。点卡片下方的 UP 主名要跳他的主页, 所以列表解析时就顺手带上
    /// (热门/排行/搜索/推荐这些接口的 `owner.mid` 都有)。
    /// 0 = 该来源没给(少数字段残缺的接口), 点的时候给个明确提示而不是毫无反应。
    /// </summary>
    public long OwnerMid { get; set; }

    /// <summary>
    /// 点赞数。**当前卡片上不显示**(用户要求把"N点赞"从卡片撤掉), 但解析照旧保留 ——
    /// 将来要恢复显示或做按赞排序时, 不必再回头改一遍所有列表解析。
    /// </summary>
    public long LikeCount { get; set; }

    /// <summary>
    /// 稿件 avid。
    /// 收藏相关接口(/x/v3/fav/resource/deal)只认 avid, 不认 bvid, 所以列表解析时
    /// 顺手把它带上; 为 0 表示这条来源没有提供(例如搜索/推荐接口), 用到时再按 bvid 补查。
    /// </summary>
    public long Aid { get; set; }

    /// <summary>
    /// 直播间号。&gt;0 表示这条是**直播间**而不是视频稿件:
    /// 播放走直播流地址、没有弹幕/进度上报/三连, 卡片上的"N 播放"也要换成"N 人气"。
    /// 复用 VideoItem 而不是另起一个模型, 是为了直接拿到现成的卡片、网格与全局屏蔽。
    /// </summary>
    public long RoomId { get; set; }

    public bool IsLive => RoomId > 0;

    private bool _isSelected;

    /// <summary>
    /// 列表编辑模式下的选中标记(仅 UI 用, 不参与业务)。
    ///
    /// 这里必须带变更通知: "全选 / 取消全选"是**从代码侧**改这个值, 没有 INPC 的话
    /// 界面上已经绑好的复选框不会跟着变 —— 勾选框会保持原样, 表现就是"点了全选没反应"。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>
    /// 播放数展示串。0 时返回空串 —— 动态时间线等来源拿不到播放数,
    /// 显示"▶ 0"是错误信息(截图实测), 拿不到就整个胶囊隐藏(与弹幕数同款约定)。
    /// </summary>
    public string ViewCountText => ViewCount > 0 ? FormatCount(ViewCount) : "";
    /// <summary>弹幕数的展示串(封面左下角胶囊)。0 或未知时为空, 由界面隐藏。</summary>
    public string DanmakuCountText => DanmakuCount > 0 ? FormatCount(DanmakuCount) : "";

    /// <summary>
    /// 发布日期的短格式(卡片文字区右侧): 同年显示 "9-1", 跨年带年份。
    /// Pubdate 没解析到(部分接口)时为空, 由界面隐藏。
    /// </summary>
    public string PubDateShort => FormatPubDateShort(Pubdate);

    /// <summary>
    /// 接口**直接给的**发布时间展示串(动态接口的 `pub_time` 就是这种已经排好版的文本,
    /// 形如 "2小时前" / "09-25")。非空时优先用它 —— 服务端算好的相对时间比本地推算准。
    /// </summary>
    public string PubTimeLabel { get; set; } = "";

    /// <summary>
    /// <see cref="Pubdate"/> 是不是**从展示文本反推**出来的近似值(而不是接口给的真实时间戳)。
    ///
    /// ★ 只有动态接口的扁平路径会置 true: 它只给 "2小时前" / "昨天 18:45", 不给时间戳,
    ///   于是拿本机时钟反推 —— 误差 = 服务端渲染那一刻到现在的间隔(通常几分钟到几小时)。
    ///   modules 路径有真实 pub_ts, 空间投稿有真实 created, 都保持 false。
    /// ★ 用途只有一个: 悬停提示加"约"字(见 <see cref="PubFullText"/>)。
    ///   **不要**拿它去做排序/去重之类的判断 —— 近似值量级够用, 精确比较不够。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PubDateApprox { get; set; }

    /// <summary>
    /// 动态卡片头部**直接显示**的发布时间。
    ///
    /// ★ 定稿(用户 2026-10-15): 直接显示说"人话", 精确时间放悬停(<see cref="PubFullText"/>)。
    ///   · 动态流: 用**接口原文**("昨天 18:45" / "2小时前") —— 服务端已排好版, 比本地推算准;
    ///   · 单 UP 的空间投稿: 接口只给时间戳、没有原文, 就按本机时钟算相对时间
    ///     (见 <see cref="FormatPubRelative"/>)—— 两处观感必须一致, 否则同一页里
    ///     一个"3小时前"、一个"10-14 18:45", 是两种语言。
    /// ★ 原文是服务端渲染那一刻算的, 页面开着不动它就一直是"2小时前";
    ///   悬停看到的精确时间由解析出的 Pubdate 算, 不受这个影响。
    /// </summary>
    public string PubText
    {
        get
        {
            if (!string.IsNullOrEmpty(PubTimeLabel)) return PubTimeLabel;
            if (Pubdate > 0) return FormatPubRelative(Pubdate);
            return "";
        }
    }

    /// <summary>
    /// 本地算的相对时间(接口没给展示文本时用): "刚刚" / "27分钟前" / "2小时前" / "昨天 18:45",
    /// 更早的退化成日期("09-20", 跨年带年份)。
    /// 口径刻意与 B 站动态接口的原文措辞对齐(见 ApiClient.ParseDynamicTime)—— 两处显示同一套语言。
    /// </summary>
    public static string FormatPubRelative(long pubdate)
    {
        if (pubdate <= 0) return "";
        try
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(pubdate).LocalDateTime;
            var span = DateTime.Now - t;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}分钟前";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}小时前";
            if (span.TotalDays < 2) return $"昨天 {t:HH:mm}";
            return t.Year == DateTime.Now.Year ? t.ToString("MM-dd") : t.ToString("yyyy-MM-dd");
        }
        catch { return ""; }
    }

    /// <summary>
    /// 悬停提示用的完整时间("2026-09-20 14:30")。拿不到时回落到 PubText。
    ///
    /// ★ 近似值会带上"约": 动态接口只给 "2小时前" 这种原文, Pubdate 是拿本机时钟**反推**的
    ///   (误差 = 服务端渲染到现在的间隔), 详细见 <see cref="PubDateApprox"/>。
    ///   直接显示到分钟会让用户以为那是服务端的精确发布时间 —— 加一个字就不用为此解释。
    /// </summary>
    public string PubFullText
        => Pubdate > 0 ? (PubDateApprox ? "约 " : "") + FormatPubDateTimeFull(Pubdate)
         : !string.IsNullOrEmpty(PubTimeLabel) ? PubTimeLabel : "";

    /// <summary>完整时间(悬停提示用), 一律带年份</summary>
    public static string FormatPubDateTimeFull(long pubdate)
    {
        if (pubdate <= 0) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(pubdate).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }
        catch { return ""; }
    }

    /// <summary>
    /// 动态卡片右侧那行数据("1.2万 播放 · 345 弹幕")。两项都拿不到时为空串, 由界面隐藏整行。
    /// ★ 与封面上那两枚独立角标分开: 动态卡的封面只留时长, 播放/弹幕挪到标题下面,
    ///   所以要拼成一句话而不是两个独立字符串(见 DynamicCard)。
    ///
    /// ★★ 实测(2026-10-17, 6 页 104 条真实动态): 动态接口的**扁平条目不给任何计数** ——
    ///   字段只有 id_str/author/pub_time/type/rid/title/cover/jump_url/visible/fold,
    ///   没有 stat, 也没有平铺的 play / video_review。而扁平是主路径 ⇒ **这行在动态页
    ///   实际上永远是空的**(下面 modules 兼容分支给的 stat 才有值, 那条路实测不走到)。
    ///   保留它是因为 modules 形态确实带 play/danmaku(且注释里写着服务端对某些客户端
    ///   形态会返回 modules); 但别指望在动态页看到它 —— 想让它亮起来得换接口或额外请求。
    /// </summary>
    public string DynamicStatText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>(2);
            if (ViewCount > 0) parts.Add(FormatCount(ViewCount) + " 播放");
            if (DanmakuCount > 0) parts.Add(FormatCount(DanmakuCount) + " 弹幕");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 发布日期的短格式。**抽成静态的给别处复用** —— 合集列表也要显示同一个格式,
    /// 各写一份必然漂移(改了一处另一处忘)。见 SeasonEpisode.PubDateShort。
    /// </summary>
    public static string FormatPubDateShort(long pubdate)
    {
        if (pubdate <= 0) return "";
        try
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(pubdate).LocalDateTime;
            var now = DateTime.Now;
            return t.Year == now.Year ? $"{t.Month}-{t.Day}" : $"{t.Year}-{t.Month}-{t.Day}";
        }
        catch { return ""; }
    }

    public static string FormatCount(long n)
    {
        if (n >= 100_000_000) return (n / 100_000_000.0).ToString("0.#") + "亿";
        if (n >= 10_000) return (n / 10_000.0).ToString("0.#") + "万";
        return n.ToString();
    }

    /// <summary>秒数 => "12:34" / "1:02:03"</summary>
    public static string FormatSeconds(int seconds)
    {
        if (seconds <= 0) return "--:--";
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }

    /// <summary>
    /// `Pubdate` 的本地时间文本(MM-dd HH:mm)。
    /// 各接口给这个字段的语义不一样 —— 历史里是"观看时间"(view_at), 推荐/搜索里是"发布时间" ——
    /// 所以名字保持中性, 由页面自己决定前缀文案(历史页用的是"观看于 {0}")。
    /// </summary>
    public string PubdateText
    {
        get
        {
            if (Pubdate <= 0) return "";
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(Pubdate).LocalDateTime.ToString("MM-dd HH:mm");
            }
            catch
            {
                return ""; // 越界时间戳(接口偶尔会给 0 或脏数据)不让它抛
            }
        }
    }
}

/// <summary>视频详情</summary>
public class VideoDetail
{
    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Pic { get; set; } = "";
    public string Owner { get; set; } = "";
    public long OwnerMid { get; set; }
    public string OwnerFace { get; set; } = "";
    public string Desc { get; set; } = "";
    public long ViewCount { get; set; }
    public long LikeCount { get; set; }
    public long DanmakuCount { get; set; }
    /// <summary>投币数 / 收藏数 / 分享数(操作栏"图标 + 数量"那套用; view 接口的 stat 里拿)</summary>
    public long CoinCount { get; set; }
    public long FavoriteCount { get; set; }
    public long ShareCount { get; set; }
    public long Pubdate { get; set; }
    public long Aid { get; set; }
    public long Cid { get; set; }
    public int DurationSec { get; set; }
    public string Duration => VideoItem.FormatSeconds(DurationSec);

    /// <summary>
    /// 该视频所属的合集(ugc_season); 不属于任何合集时为 null。
    /// ★ 跟着 view 接口一起解析, 而不是让播放器再单独请求一次 —— 见 ApiClient.GetVideoAsync。
    /// </summary>
    public SeasonInfo? Season { get; set; }

    /// <summary>
    /// 该视频的全部分P。**只有一个 P 时也会有一项**(即 pages[0]), 由界面自己判断要不要露入口。
    ///
    /// ★ 分P 与合集是两回事, 不能互相替代:
    ///   合集(Season) = 多个**独立稿件**, 每集有自己的 bvid/封面/播放量;
    ///   分P(Pages)   = **同一个稿件**被切成几段, bvid 完全一样, 换段只换 cid。
    ///   「克拉克森的农场 1-5季」(BV1JMbw62E72) 是 40 个 P 的**单个稿件**, 没有 ugc_season ——
    ///   这正是原先"合集"入口不出现、用户没法选集的原因(2026-10-11)。
    /// </summary>
    public List<VideoPage> Pages { get; set; } = new();
}

/// <summary>
/// 一个分P(view 接口的 pages[] 之一)。
///
/// ★ duration 的单位是**秒**(2026-10-11 实测 BV1JMbw62E72: P1 = 5395, 对应 90 分钟正片;
///   若按毫秒读就是 5.4 秒)。这与番剧分集(duration 毫秒)正好相反, 别串。
/// </summary>
public sealed class VideoPage
{
    /// <summary>P 序号(1 起, 接口原样给的)。</summary>
    public int Page { get; set; }

    /// <summary>这一段的 cid —— 换 P 就是换它, bvid 不用变。</summary>
    public long Cid { get; set; }

    /// <summary>分P 标题(如 "S01E01 拖拉机")。可能为空, 空时界面退回显示"P1"。</summary>
    public string Part { get; set; } = "";

    public int DurationSec { get; set; }
    public string Duration => VideoItem.FormatSeconds(DurationSec);

    /// <summary>
    /// 界面上这一行显示的标题: 有 part 用 part, 没有就退回 "P{序号}" ——
    /// 直接显示空的 part 会得到一条只有时长的空白行, 看着像加载坏了。
    /// </summary>
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Part) ? $"P{Page}" : Part;
}

/// <summary>
/// 合集(ugc_season)里的一集。
/// 字段挑的是"列表 UI 要用的那几个": 封面/标题/时长/播放量/发布日 + 换片必需的三件套。
/// </summary>
public sealed class SeasonEpisode
{
    /// <summary>稿件 avid。**必须 long** —— 新稿件 aid 已超过 int 上限(实测 117367564666453)</summary>
    public long Aid { get; set; }
    public string Bvid { get; set; } = "";
    /// <summary>该集的默认分 P cid(episodes[].cid, 与 page.cid 一致)</summary>
    public long Cid { get; set; }
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public int DurationSec { get; set; }
    public long ViewCount { get; set; }
    public long Pubdate { get; set; }

    /// <summary>所属分组(SeasonSection.Id); 0 = 没分组信息</summary>
    public long SectionId { get; set; }

    /// <summary>
    /// 该集在**整个合集**里的序号(1 起)。
    /// 跨分组连续编号, 因为界面上"第 N 集"是用户对合集的整体认知 ——
    /// 分组内重新从 1 数会让"第 3 集"在 10 个分组里各有一条, 定位不了。
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// 这一集是不是**它所在分组的第一个**。用来决定要不要在它上面画分组标题 ——
    /// WPF 的 ListBox 用展平数据源 + 这个标记, 比为分组再套一层 ItemsControl 简单得多。
    /// </summary>
    public bool IsSectionStart { get; set; }

    /// <summary>分组标题(仅 IsSectionStart 时会被界面用到)</summary>
    public string SectionTitle { get; set; } = "";

    /// <summary>
    /// 界面上要不要在这条**上面**画分组标题。= IsSectionStart 且整个合集确实有多个分组。
    ///
    /// 为什么不只判 IsSectionStart: 单分组的合集(如"正片"那一组有 148 集)官方也不显示分组名,
    /// 多一行没信息量的标题反而让人以为还有别的分组。这个判断需要"整个合集的分组数",
    /// 单条数据自己算不出来, 所以由灌数据的一方在绑定前统一写好(条目是普通 POCO, 没有变更通知,
    /// 必须在赋给 ItemsSource **之前**定下来)。
    /// </summary>
    public bool ShowSectionHeader { get; set; }

    public string DurationText => VideoItem.FormatSeconds(DurationSec);
    public string ViewCountText => ViewCount > 0 ? VideoItem.FormatCount(ViewCount) : "";

    /// <summary>
    /// 发布日期的短格式(合集条目第二行右侧)。
    /// ★ 复用 <see cref="VideoItem.FormatPubDateShort"/> —— 这条以前**漏了**:
    ///   XAML 里绑着 `{Binding PubDateShort}` 而模型没有这个属性, WPF 对绑定到不存在的属性
    ///   是**静默空白、编译零警告**(项目笔记里记过这个坑), 表现就是每一条的日期都是空的。
    /// </summary>
    public string PubDateShort => VideoItem.FormatPubDateShort(Pubdate);
    public string IndexText => "第 " + Index + " 集";
}

/// <summary>合集里的一个分组(sections[])。官方单分组时该分组就是"正片"</summary>
public sealed class SeasonSection
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public List<SeasonEpisode> Episodes { get; set; } = new();
}

/// <summary>视频合集(ugc_season)</summary>
public sealed class SeasonInfo
{
    /// <summary>合集 id。同时是 seasons_archives_list 的 season_id 参数</summary>
    public long SeasonId { get; set; }
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    /// <summary>UP 主 mid(合集归属人)</summary>
    public long Mid { get; set; }
    public string Intro { get; set; } = "";

    /// <summary>
    /// 服务端自报的总集数。**只用来做"是不是被截断了"的交叉校验** ——
    /// 界面上一律以实际拿到的 Episodes.Count 为准(自报数偶尔与实际不符)。
    /// </summary>
    public int EpCount { get; set; }

    /// <summary>分组。官方网页把它当"选集"的页签(如「马刀西游」/「全新故事小剧场」)</summary>
    public List<SeasonSection> Sections { get; set; } = new();

    /// <summary>
    /// 全部集的**展平**视图, 顺序 = sections 依次拼接 = 官方 sort_reverse=false 的顺序
    /// (实测 118 集合集逐 aid 完全一致, 见 ApiClient.GetSeasonAsync 的说明)。
    /// 单位置取"当前播的是第几集"、也直接当列表数据源。
    /// </summary>
    public List<SeasonEpisode> Episodes { get; set; } = new();
}

/// <summary>
/// 一路 DASH 音视频流(短视频"本地合流"用)。
///
/// 为什么需要它: 短视频想同时拿到"1080P + 拖进度条不卡 + 结尾不丢", 实测只有
/// "单一输入"(单个本地文件)能做到 —— 见 Helpers/DashRemuxer 的实测对照表。
/// 于是短视频的策略是: 先用 DASH 立即开播(零等待), 同时在后台把这两路下到本地、
/// 合成一个 mp4, 好了再切过去; 下次直接播本地文件。
/// </summary>
public class DashStreams
{
    public AudioQuality SelectedAudioQuality {get;set;} = AudioQuality.Auto;
    public string VideoUrl { get; set; } = "";
    public string AudioUrl { get; set; } = "";

    /// <summary>接口给的时长(毫秒), 用于两个判断: 大小估算(决定值不值得下)与 MPD/容器时长</summary>
    public long DurationMs { get; set; }

    public long VideoBandwidth { get; set; }
    public string VideoCodecs { get; set; } = "";
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }

    public long AudioBandwidth { get; set; }
    public string AudioCodecs { get; set; } = "";

    /// <summary>按码率估算的文件总大小(字节)。用来决定"这么大的片子不值得为它占本地缓存"</summary>
    public long EstimatedBytes =>
        DurationMs <= 0 ? 0 : (long)((VideoBandwidth + AudioBandwidth) / 8.0 * (DurationMs / 1000.0));
}

/// <summary>登录用户信息</summary>
public class UserState
{
    public bool IsLogin { get; set; }
    public string Name { get; set; } = "";
    public string Face { get; set; } = "";
    public long Mid { get; set; }
    public int Level { get; set; }

    public double Coins { get; set; }

    /// <summary>当前经验值(nav 的 level_info.current_exp; 「我的」页显示"经验 x/y")</summary>
    public long ExperienceCurrent { get; set; }

    /// <summary>升级所需经验(nav 的 level_info.next_exp; Lv6 时为 0)</summary>
    public long ExperienceNext { get; set; }
}

/// <summary>UP 主空间信息(多接口聚合的结果)</summary>
public class SpaceInfo
{
    public string Name { get; set; } = "";
    public string Face { get; set; } = "";
    public string Sign { get; set; } = "";
    public long Mid { get; set; }
    public int Level { get; set; }
    public long Follower { get; set; }   // 粉丝数
    public long Following { get; set; }  // 关注数
    public long VideoCount { get; set; } // 投稿数
    public bool RelationStatsKnown { get; set; }
    public bool VideoCountKnown { get; set; }
}

/// <summary>
/// 搜索 / 历史的"看哪一类"标签(2026-10-03 新增)。
///
/// 视频与直播间走的是**完全不同的接口和字段**, 但界面上是同一套卡片墙 ——
/// 所以用同一个枚举把"当前这一类"在页面/VM/接口之间传下去, 而不是各传一个 bool。
/// </summary>
public enum ContentKind
{
    /// <summary>视频稿件(搜索 search_type=video / 历史 type=archive)</summary>
    Video,

    /// <summary>直播间(搜索 search_type=live_room / 历史 type=live)</summary>
    Live,

    /// <summary>
    /// 番剧(搜索 search_type=bangumi, 2026-10-11 新增)。
    ///
    /// ★ 它**不是**"视频的一个子类": 番剧按 season_id 寻址、封面竖版、点开是选集页,
    ///   既没有 bvid 也播不了 —— 塞进 VideoItem 会让点击走错路。
    ///   所以搜索结果单独走 <see cref="SearchData.BangumiItems"/>, 页面用海报卡渲染。
    /// </summary>
    Bangumi,

    /// <summary>
    /// 用户 / UP 主(搜索 search_type=bili_user, 2026-11-04 新增)。
    ///
    /// ★ 官方 PC 端搜"影之刃零"第一条就是**官方账号**而不是视频 —— 这是搜索的第一优先级:
    ///   用户搜一个游戏名/作品名, 多半想找的是"谁在做这个"、"官方在哪"。
    ///   按稿件搜只会给一堆搬运和切片。
    /// ★ 与番剧同理, 用户没有 bvid/roomid, 点开要进**个人主页**而不是播放器,
    ///   所以单独走 <see cref="SearchData.UserItems"/>, 页面用用户卡渲染。
    /// </summary>
    User,
}

/// <summary>搜索 API 返回结构</summary>
public class SearchData
{
    public bool Ok { get; set; }
    public bool Blocked { get; set; }    // 被风控拦截
    public string? Error { get; set; }
    public System.Collections.Generic.List<VideoItem> Items { get; set; } = new();

    /// <summary>
    /// 番剧结果(2026-10-11 新增)。与 <see cref="Items"/> **并存但互斥**:
    /// 搜番剧时 <see cref="Items"/> 为空、结果全在这里; 搜视频/直播时反之。
    /// 分开而不是塞进 Items, 是因为两者点开的去向完全不同(见 <see cref="ContentKind.Bangumi"/>)。
    /// </summary>
    public System.Collections.Generic.List<BangumiItem> BangumiItems { get; set; } = new();

    /// <summary>
    /// 用户/UP 主结果(2026-11-04 新增)。与 <see cref="Items"/>、<see cref="BangumiItems"/> 互斥。
    /// 点开的去向是**个人主页**(mid), 不是播放器。
    /// </summary>
    public System.Collections.Generic.List<UserItem> UserItems { get; set; } = new();

    public bool HasMore { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// 搜索里的一条用户/UP 主。
///
/// 字段来自 `search_type=bili_user`(2026-11-04 实测):
///   uname / mid / fans / videos / upic / usign / level / official_verify{type,desc}
///   official_verify.type: 0=个人, 1=机构认证(官方号), 127=UP 主认证
/// 这些足够把"官方账号"从一堆同名号里挑出来 —— 认证标 + 粉丝数就是最强的排序信号。
/// </summary>
public class UserItem
{
    public long Mid { get; set; }
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "";
    public string Sign { get; set; } = "";
    public long Fans { get; set; }
    public long Videos { get; set; }
    public int Level { get; set; }
    /// <summary>0=无认证, 1=机构认证(官方), 127=UP 主认证</summary>
    public int VerifyType { get; set; }
    public string VerifyDesc { get; set; } = "";

    /// <summary>机构认证(官方号) —— 搜索结果的置顶/高亮依据</summary>
    public bool IsOfficial => VerifyType == 1;
    /// <summary>任何认证(官方或 UP 主认证)</summary>
    public bool IsVerified => VerifyType != 0;

    public string FansText => Fans switch
    {
        >= 100_000_000 => (Fans / 100_000_000.0).ToString("0.#") + "亿粉丝",
        >= 10_000 => (Fans / 10_000.0).ToString("0.#") + "万粉丝",
        _ => Fans + " 粉丝"
    };

    public string LevelText => "LV" + Math.Clamp(Level, 0, 6);

    /// <summary>认证文案。★ 官方(1)已经有独立徽章, 这里只给 UP 主认证(127)出文字, 避免重复。</summary>
    public string VerifyLabel => VerifyType == 127 ? "UP主认证" : "";
}

/// <summary>
/// 热搜榜的一条(首页右上角搜索卡展开后的「热搜」区块)。
///
/// 接口的 key 与展示是两回事, 所以这里把两者都留着:
/// keyword 是"点下去要搜什么"(可能很短, 例如 "KC XLG"),
/// show_name 才是给人看的完整说法("KC战胜XLG VCT冠军赛") —— 网页端显示的也是后者。
/// </summary>
public class HotSearchItem
{
    /// <summary>1 起的排名, 直接当序号显示(左栏 1~5, 右栏 6~10)</summary>
    public int Rank { get; set; }

    /// <summary>真正的搜索词 —— 点这一条时用它去搜</summary>
    public string Keyword { get; set; } = "";

    /// <summary>展示名(接口的 show_name; 缺了就退回 keyword)</summary>
    public string ShowName { get; set; } = "";

    /// <summary>徽章文字(新 / 热 / 独家)。空 = 这一条没有徽章, 界面自己隐藏</summary>
    public string Badge { get; set; } = "";

    public bool HasBadge => Badge.Length > 0;

    /// <summary>前三名用强调色显示序号 —— 和 B 站一致, 让"最热的几条"一眼能挑出来</summary>
    public bool IsTopRank => Rank is >= 1 and <= 3;
}

/// <summary>关注列表项(关注的 UP 主)</summary>
public class FollowUser : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public long Mid { get; set; }
    public string Name { get; set; } = "";
    public string Face { get; set; } = "";
    public string Sign { get; set; } = "";
    public int VerifyType { get; set; } = -1;
    public string VerifyDesc { get; set; } = "";

    private bool _isSelected;

    /// <summary>
    /// 动态页左栏的选中高亮(UI 用, 不参与任何接口)。
    /// 必须带通知: 选中是**从 ViewModel 侧**改的, 新旧两项的高亮都要跟着变。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private int _newCount;

    /// <summary>
    /// 「未读动态」条数 —— 左栏头像左侧未读红点的判定依据(0 = 不显示红点)。
    /// 红点本身只表达"有没有新动态", 具体条数放在红点的悬浮提示里。
    ///
    /// ★ 由**本地**判断, 不新增接口(见 DynamicSeenStore): 拿动态流里"该 UP 最新的一条"
    ///   与上次记录的比对, 不一样就是有新动态。
    /// ★ 必须带通知: 红点是刷新后由 ViewModel 侧统一刷的(见 FollowViewModel.UpdateUnreadBadges),
    ///   界面要跟着变。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int NewCount
    {
        get => _newCount;
        set
        {
            if (_newCount == value) return;
            _newCount = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(NewCount)));
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(HasNew)));
        }
    }

    public bool HasNew => NewCount > 0;
}

/// <summary>
/// 评论里的一个表情。
/// 字段名跟着接口走: `url` 是图片地址, `meta.size` 是尺寸档位
/// (1 = 常规小表情, 2 = 偏大的那种, 比如游戏联动表情)。
/// </summary>
public class CommentEmote
{
    public string Url { get; set; } = "";

    /// <summary>接口 meta.size。1 = 小表情(约一行文字高), 2 = 大表情</summary>
    public int Size { get; set; } = 1;
}

/// <summary>
/// 评论里的一张图片(接口 content.pictures 数组的一项)。
/// </summary>
public class CommentPicture
{
    /// <summary>图片地址(img_src)。点击看大图也用它(ImagePreviewWindow 会按 4096 解码)</summary>
    public string Url { get; set; } = "";

    /// <summary>原始宽高(像素)。布局用它算缩放比, 缺了按方形兜底</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>文件大小(字节)。接口可能不给, 0 = 未知, 只用于提示</summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 缩略图显示宽(px)。照 B 站网页版: 缩略图**最高 160px**, 宽按原始宽高比算。
    /// ★ 原始尺寸缺失(0)时按 160 方形兜底 —— 按比例算会得 NaN, 图直接消失。
    /// </summary>
    public double ThumbWidth => Width > 0 && Height > 0
        ? Math.Round(160.0 * Width / Height)
        : 160;
}

/// <summary>评论条目(顶层评论与楼中楼回复共用同一个模型)</summary>
public class CommentItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <summary>评论 id。取楼中楼回复时要拿它当 root 参数</summary>
    public long Rpid { get; set; }

    /// <summary>
    /// 评论者 mid。头像点一下跳他的个人主页要用。
    /// 顶层评论和楼中楼回复的 JSON 里都有 `mid` 字段(不必绕 member.mid)。
    /// </summary>
    public long Mid { get; set; }

    /// <summary>评论者等级(0~6)。0 = 接口没给, 不显示徽章</summary>
    public int Level { get; set; }

    public bool HasLevel => Level > 0;

    public string LevelText => "LV" + Level;

    /// <summary>
    /// 这条评论的作者是不是**本视频的 UP 主** → 昵称后挂 [UP] 标。
    /// 判据是评论者 mid == 顶层 data.upper.mid(接口按"当前稿件"给, 不是按评论本身),
    /// 所以只有取列表时能算, 解析单条时拿不到。
    /// </summary>
    public bool IsUp { get; set; }

    /// <summary>
    /// 这条是不是**置顶评论**(UP 主置顶 / 管理员置顶)。
    ///
    /// ★ 2026-10-03 修"置顶评论不可见": 置顶那条**不在** `data.replies` 里, 而在
    ///   `data.top.upper`(UP 主置顶)或 `data.top.admin`(管理员置顶) —— 只读 replies 的话
    ///   它永远不会出现在列表里。实测: 置顶 rpid=315658667873 在 replies 里**找不到**。
    ///   解析时由调用方置位, 界面据此在最前面挂一个"置顶"标。
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// 被回复的那条评论 id。
    /// 直接回复楼主时它等于线程根(可用它判"这是给楼主的第一层回复"), 回复楼中楼里某个人时
    /// 指向那个人 —— 与 root 不同就说明这是"对话中的再回复"。
    /// </summary>
    public long ParentRpid { get; set; }

    public string UserName { get; set; } = "";
    public string UserFace { get; set; } = "";
    public string Content { get; set; } = "";

    private int _likeCount;
    public int LikeCount
    {
        get => _likeCount;
        set
        {
            if (_likeCount == value) return;
            _likeCount = value;
            Raise(nameof(LikeCount));
            Raise(nameof(LikeText));
        }
    }

    private bool _isLiked;

    /// <summary>
    /// 当前登录用户有没有给这条评论点过赞(接口的 `action` 字段: 1 = 已赞)。
    /// 带 INPC: 点赞是**从代码侧**改这个值的, 没有通知按钮的高亮不会跟着变。
    /// </summary>
    public bool IsLiked
    {
        get => _isLiked;
        set
        {
            if (_isLiked == value) return;
            _isLiked = value;
            Raise(nameof(IsLiked));
        }
    }

    /// <summary>点赞请求进行中(防连点: 连点会造成 赞/取消 交叉, 服务端最终状态就不确定了)</summary>
    private bool _likeBusy;
    public bool LikeBusy
    {
        get => _likeBusy;
        set
        {
            if (_likeBusy == value) return;
            _likeBusy = value;
            Raise(nameof(LikeBusy));
            Raise(nameof(CanLike));
        }
    }

    /// <summary>
    /// 当前用户有没有给这条评论点过**踩**。
    ///
    /// ★ 注意接口层面的实情(2026-09-27 修正): 赞和踩是**两个不同的接口**
    /// (`/x/v2/reply/action` 与 `/x/v2/reply/hate`), 踩的 action 只在 1/0 之间取值。
    /// 评论列表里的 `action` 字段是不是真会下发 2(已踩) 并没有验证过, 所以这个值更准确的
    /// 含义是"**本地已知的踩状态**": 拿不到就当未踩, 由用户点一下来纠正。
    /// 踩**没有计数**: 接口不下发 dislike 总数(网页版也只是一个图标), 所以只有状态、没有数字。
    ///
    /// 赞与踩在服务端是互相覆盖的(点赞会消掉踩、点踩会消掉赞), 所以界面上的两个状态也必须互斥。
    /// </summary>
    private bool _isDisliked;
    public bool IsDisliked
    {
        get => _isDisliked;
        set
        {
            if (_isDisliked == value) return;
            _isDisliked = value;
            Raise(nameof(IsDisliked));
        }
    }

    public bool CanLike => !_likeBusy;

    public string RcTime { get; set; } = "";

    /// <summary>
    /// 评论发布时间的 Unix 秒。界面只用 <see cref="RcTime"/> 显示, 这里留原始值是为了**排序**:
    /// 详情页里切「时间」时要按真实时间重排楼中楼, 拿格式化过的字符串比虽然也能用,
    /// 但一旦哪天改了显示格式就会静默排错。
    /// </summary>
    public long Ctime { get; set; }

    /// <summary>
    /// 正文里出现的表情: 短代码(`[微笑]`) → 图片地址。
    ///
    /// 为什么不能用 Unicode 直接替: B 站的评论区里绝大多数是**自定义表情**
    /// (大会员表情、直播表情、游戏联动表情), 根本没有对应的 Unicode 字符。
    /// 接口在 `content.emote` 里把"这条评论用到的表情"连图片地址一起给了,
    /// key 就是正文里原样出现的短代码, 所以按它替换即可。
    /// </summary>
    public Dictionary<string, CommentEmote> Emotes { get; } = new();

    /// <summary>
    /// IP 属地。来自接口的 `reply_control.location`, 是**已经带前缀的展示串**
    /// (形如 "IP属地：上海"), 所以直接显示、不再自己拼前缀。
    /// 可能为空(用户关了展示 / 接口没给), 空则不显示。
    /// </summary>
    public string Location { get; set; } = "";

    public bool HasLocation => Location.Length > 0;

    // ------------------------------------------------------------ @提及(2026-10-30)

    /// <summary>
    /// 正文里 @ 到的**昵称列表**(按出现顺序, 可重复)。
    ///
    /// ★ 为什么要自己解析: B 站接口的 <c>content</c> 字段是**纯文本**(emote 另给),
    ///   但回复楼中楼里的 "@某某" 是**结构化引用** —— 昵称在正文里,
    ///   而真正的主键(rpid)在 <c>reply_control.reply_id</c>。
    ///   界面只要"把 @ 出来的那几个字染成强调色", 自己扫文本就够了。
    ///
    /// ★ 解析规则(与 B 站网页版一致):
    ///   · <c>@</c> 后面跟一段**非空白**字符, 直到下一个空白或标点为止;
    ///   · 昵称里可能含中文、字母、数字、点、下划线、减号 —— 所以不能只认单词字符;
    ///   · 长度上限 20(超长的多半是误匹配, 例如邮箱)。
    /// </summary>
    public List<string> Mentions { get; } = new();

    /// <summary>正文里有没有 @ 人(用于决定要不要做高亮扫描)</summary>
    public bool HasMentions => Mentions.Count > 0;

    // ------------------------------------------------------------ 评论图片(2026-10-31)

    /// <summary>
    /// 这条评论附带的图片(接口 <c>content.pictures</c>)。带图评论才有内容。
    ///
    /// ★ 接口形态(2026-10-31 实测, /x/v2/reply/main):
    ///   pictures: [ { img_src, img_width, img_height, img_size, img_tag?, img_type? } ]
    ///   · 尺寸是**原始像素**, 网页版按它把图缩到最高 160px 的缩略图;
    ///   · 带图评论的 message 里**不含**图片占位符, 图是"正文下面挂一排缩略图"。
    /// </summary>
    public List<CommentPicture> Pictures { get; } = new();

    /// <summary>有没有图(用于决定图片行的可见性)</summary>
    public bool HasPictures => Pictures.Count > 0;

    public string LikeText => LikeCount > 0 ? VideoItem.FormatCount(LikeCount) : "";

    // ------------------------------------------------------------ 楼中楼

    private int _replyCount;

    /// <summary>这条评论下的回复总数(服务端的 rcount)。
    /// 注意它可能**大于**已取到的条数 —— 首页接口只内联前几条。</summary>
    public int ReplyCount
    {
        get => _replyCount;
        set
        {
            if (_replyCount == value) return;
            _replyCount = value;
            Raise(nameof(ReplyCount));
            Raise(nameof(HasReplies));
            Raise(nameof(HasMoreReplies));
            Raise(nameof(ReplyMoreText));
            Raise(nameof(ReplyBoxText));
        }
    }

    /// <summary>已取到的回复(内联的前几条 + 后续展开补齐的)</summary>
    public ObservableCollection<CommentItem> Replies { get; } = new();

    /// <summary>
    /// 列表页「楼中楼方框」里展示的**预览**回复(最多 3 条)。
    ///
    /// ★ 2026-11-04: 方框以前直接绑 <see cref="Replies"/>, 详情页自动补全后
    ///   同一份集合被撑到上百条 —— 从详情页返回列表时, 那条评论底下突然挂着
    ///   一整屏回复, 不会自己收起来。详情页继续用 <see cref="Replies"/> 看全部,
    ///   列表只认这份短预览, 返回时自然维持原样。
    /// </summary>
    public ObservableCollection<CommentItem> PreviewReplies { get; } = new();

    // ------------------------------------------------------------ 楼中楼预览方框

    /// <summary>方框底部那行提示文字(点整框进详情页)</summary>
    public string ReplyBoxText => ReplyCount > 0 ? $"共 {ReplyCount} 条回复" : "";

    /// <summary>
    /// 方框/详情页里一条回复的**前缀**(用户名那一截)。
    ///
    /// 规则跟 B 站网页版一致:
    ///   · 正文以 "回复 @XXX：" 开头(接口对"回复楼中楼里的人"的正文**自带**这截前缀)
    ///     → 前缀只给"用户名 "一个空格, 免得出现"用户名 回复 @XX 回复 @XX"；
    ///   · 否则(直接回复楼主) → 前缀给"用户名：", 冒号由我们补。
    /// UP 主的评论在用户名后面挂 [UP]。
    /// </summary>
    public string ReplyPrefix
    {
        get
        {
            var name = UserName;
            if (IsUp) name += " [UP]";
            return Content.StartsWith("回复 @", StringComparison.Ordinal) ? name + " " : name + "：";
        }
    }

    /// <summary>
    /// 正文是否以 "回复 @XXX：" 开头 —— 接口对"回复楼中楼里某个人"的正文**自带**这截前缀。
    /// 详情页据此挂"查看对话"入口(直接回复楼主的那条没有对话可追)。
    /// </summary>
    public bool IsReplyToSomeone => Content.StartsWith("回复 @", StringComparison.Ordinal);

    /// <summary>
    /// 楼中楼"展开更多"下一次要请求的页码。
    /// 之前固定 pn=1: 超过 20 条的楼中楼第二页永远拉不到(去重后一条不增,
    /// 按钮却一直显示"展开 N 条回复"点不动)。首次展开前由加载方置为 1, 之后自增。
    /// </summary>
    public int RepliesNextPage { get; set; } = 1;

    /// <summary>还有没取到的回复 → 显示"展开 N 条回复"</summary>
    public bool HasMoreReplies => ReplyCount > Replies.Count;

    /// <summary>
    /// 有没有楼中楼 → 决定"查看详情"入口显不出来。
    /// 没有楼中楼的评论详情页就是它自己, 没有可看的内容, 入口不显示。
    /// </summary>
    public bool HasReplies => ReplyCount > 0;

    public string ReplyMoreText => ReplyCount > Replies.Count
        ? $"展开 {ReplyCount - Replies.Count} 条回复"
        : "";

    private bool _loadingReplies;

    public bool IsLoadingReplies
    {
        get => _loadingReplies;
        set
        {
            if (_loadingReplies == value) return;
            _loadingReplies = value;
            Raise(nameof(IsLoadingReplies));
        }
    }

    /// <summary>
    /// 补齐回复后手动通知一次。
    /// ObservableCollection 的数量变化只会通知集合本身, 不会带上 HasMoreReplies / ReplyMoreText
    /// 这两个依赖 Count 的计算属性, 所以必须显式补一刀。
    /// </summary>
    public void NotifyRepliesChanged()
    {
        Raise(nameof(HasMoreReplies));
        Raise(nameof(ReplyMoreText));
        // 展开后"共 N 条回复"可能跟着变(用实际条数收口时), 一并通知
        Raise(nameof(ReplyBoxText));
    }

    /// <summary>
    /// 把 <see cref="PreviewReplies"/> 同步成 <see cref="Replies"/> 的前几条。
    /// 列表方框只展示预览, 详情页/自动补全只动 <see cref="Replies"/>;
    /// 发表回复后重新拉了第一页时, 用这个方法把预览也刷新一下。
    /// </summary>
    public void SyncPreviewReplies(int max = 3)
    {
        PreviewReplies.Clear();
        foreach (var r in Replies.Take(max)) PreviewReplies.Add(r);
    }
}

/// <summary>云端收藏夹(账号下的一个收藏夹分组)</summary>
public class FavFolder : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; set; }
    public string Title { get; set; } = "";

    private int _mediaCount;

    /// <summary>
    /// 夹内条目数。
    /// 带变更通知是因为"批量移出"之后这个数要跟着减 —— 而**不能**靠"从集合里移除再插回来"
    /// 来触发刷新: 那会重建收藏夹 chip 的容器, 而 RadioButton 的分组选中态是挂在容器上的,
    /// 容器一重建, 高亮就没了(用户看到的是"删了几个之后选中的收藏夹标签突然不亮了")。
    /// </summary>
    public int MediaCount
    {
        get => _mediaCount;
        set
        {
            if (_mediaCount == value) return;
            _mediaCount = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MediaCount)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayText)));
        }
    }

    /// <summary>
    /// 某个**具体视频**是否在这个收藏夹里。
    /// 只有"按 rid 查收藏夹"接口(/x/v3/fav/folder/created/list-all?rid=)才会带上这个语义
    /// (响应里的 fav_state 字段); 普通的收藏夹列表接口不返回它, 此时保持 false。
    /// </summary>
    public bool HasVideo { get; set; }

    private bool _isCurrent;

    /// <summary>
    /// 是否是当前选中的收藏夹(收藏页 chip 的高亮)。
    /// 必须带通知: "从「我的」页点卡片进来"这类**程序切换**不经过用户点击,
    /// RadioButton 的选中态只能靠这里推过去 —— 否则数据换了、高亮还停在旧夹上,
    /// 看起来就像"点了 A 打开了 B"。
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    /// <summary>下拉里显示的 "标题 (数量)"</summary>
    public string DisplayText => MediaCount > 0 ? $"{Title} ({MediaCount})" : Title;

    /// <summary>
    /// 收藏夹封面(「我的」页的文件夹卡片用)。
    /// created/list 接口才下发; 个别夹(比如刚建的空夹)可能为空串, 界面自己放兜底底色。
    /// </summary>
    public string Cover { get; set; } = "";

    /// <summary>隐私状态: 0=公开 1=私密(接口不下发时按 0 处理)</summary>
    public int Privacy { get; set; }

    public string PrivacyText => Privacy == 1 ? "私密" : "公开";
}

/// <summary>
/// 私信会话(消息页左侧列表的一项)。
/// 需要 INotifyPropertyChanged 是因为: 打开会话要清未读角标、发完消息要更新摘要,
/// 这两件事都发生在对象已经绑到界面上之后。
/// </summary>
public class MsgSession : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <summary>对话对象的 UID(群聊时是群 id)</summary>
    public long TalkerId { get; set; }
    public long Timestamp { get; set; }
    public bool IsOfficial { get; set; }
    private bool _selected;
    public bool IsSelected {get=>_selected;set{if(_selected==value)return;_selected=value;Raise(nameof(IsSelected));}}

    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; Raise(nameof(Name)); Raise(nameof(DisplayName)); }
    }

    private string _face = "";
    public string Face
    {
        get => _face;
        set { if (_face == value) return; _face = value; Raise(nameof(Face)); }
    }

    private string _preview = "";
    /// <summary>最后一条消息的摘要</summary>
    public string Preview
    {
        get => _preview;
        set { if (_preview == value) return; _preview = value; Raise(nameof(Preview)); }
    }

    private string _timeText = "";
    /// <summary>最后活跃时间</summary>
    public string TimeText
    {
        get => _timeText;
        set { if (_timeText == value) return; _timeText = value; Raise(nameof(TimeText)); }
    }

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set { if (_unreadCount == value) return; _unreadCount = value; Raise(nameof(UnreadCount)); Raise(nameof(HasUnread)); }
    }

    /// <summary>已读到的序号 / 服务端最新序号(标记已读用)</summary>
    public long AckSeqno { get; set; }
    public long MaxSeqno { get; set; }

    public bool HasUnread => UnreadCount > 0;

    /// <summary>服务端名字拿不到时兜底显示</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? $"UID {TalkerId}" : Name;
}

/// <summary>一条私信</summary>
public enum MsgKind {Text,Image,Video,Article,Link,Notification,Unknown}
public sealed class MsgAction {public string Label {get;set;}="";public string Url {get;set;}="";}
public class MsgItem
{
    public long SenderUid {get;set;}
    public long Seqno {get;set;}
    public bool IsMine {get;set;}
    public string Text {get;set;}="";
    public string ImageUrl {get;set;}="";
    public string TimeText {get;set;}="";
    public MsgKind Kind {get;set;}
    public string Title {get;set;}="";
    public string Description {get;set;}="";
    public string CoverUrl {get;set;}="";
    public string Bvid {get;set;}="";
    public long Aid {get;set;}
    public string TargetUrl {get;set;}="";
    public List<MsgAction> Actions {get;set;}=new();
    public string KindLabel=>Kind switch {MsgKind.Video=>"视频",MsgKind.Article=>"专栏",MsgKind.Notification=>"通知",_=>"分享"};
    public bool IsCard=>Kind is MsgKind.Video or MsgKind.Article or MsgKind.Link or MsgKind.Notification;
    public bool IsImage=>!IsCard && ImageUrl.Length>0;
    public bool IsText=>!IsCard && ImageUrl.Length==0;
    public bool HasCover=>CoverUrl.Length>0;
    public bool CanOpen=>Kind!=MsgKind.Notification && (Bvid.Length>0 || Aid>0 || TargetUrl.Length>0);
}

/// <summary>
/// 离线缓存里的一条本地视频文件。
/// 它对应下载目录里的一个真实文件, 不是从接口拿到的数据。
/// </summary>
public class CacheEntry
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string TimeText { get; set; } = "";

    /// <summary>原始字节数(算总占用用, 不显示)</summary>
    public long Bytes { get; set; }
}

/// <summary>
/// SponsorBlock 的一条片段标注(B 站社区众筹的"空降"数据)。
/// 字段名对齐服务端返回, 见 Services/SponsorBlockService。
/// </summary>
public sealed class SponsorSegment
{
    /// <summary>
    /// 该片段所属**分 P** 的 cid。
    /// ★ 必须和当前正在播的 cid 相同才允许使用 —— 多 P 视频的片段是按分 P 标注的,
    /// 拿错分 P 的片段会把用户跳到完全不相干的位置。
    /// </summary>
    public long Cid { get; set; }

    /// <summary>类别 id(服务端原值, 如 "sponsor"; 中文名见 SponsorCategories)</summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// 动作类型: <c>skip</c>=跳过 / <c>full</c>=整条视频都是该类内容 / <c>mute</c>=静音 / <c>poi</c>=精彩时刻。
    /// ★ 只有 skip 能当"区间"处理: full 的 segment 是 [0, 0], 当成区间跳会直接回到开头。
    /// </summary>
    public string ActionType { get; set; } = "";

    public double StartSec { get; set; }
    public double EndSec { get; set; }

    /// <summary>标注 id。用于"这条已经处理过(跳过/撤回)"的去重</summary>
    public string Uuid { get; set; } = "";

    /// <summary>社区净投票。负数 = 被投下去的存疑标注, 不跳</summary>
    public int Votes { get; set; }

    public long StartMs => (long)(StartSec * 1000);
    public long EndMs => (long)(EndSec * 1000);

    /// <summary>给界面看的中文名</summary>
    public string Label => SponsorCategories.LabelOf(Category);
}

/// <summary>
/// SponsorBlock 的类别表。
///
/// id 是**服务端原值, 不能改**(改一个字母就查不到数据); 中文名只用于界面。
/// 只列 B 站语境下真会出现的几个 —— 服务端还有 music_offtopic(非音乐部分) 之类
/// 面向 YouTube 的类别, 放进设置页只会让人莫名其妙。
/// </summary>
public static class SponsorCategories
{
    public const string Sponsor = "sponsor";
    public const string SelfPromo = "selfpromo";
    public const string Intro = "intro";
    public const string Outro = "outro";
    public const string Preview = "preview";
    public const string PoiHighlight = "poi_highlight";

    // 注: 服务端还有 interaction(一键三连提醒) —— **本应用不做这个类别**(2026-09-27 用户拍板:
    // 用不到)。所以它既不出现在设置页, 也会在读设置时被 Sanitize 丢掉, 避免出现
    // "界面上一个都没勾, 后台却还在按 interaction 跳"这种自相矛盾的状态。

    /// <summary>(id, 界面名) —— 数组顺序 = 设置页里的显示顺序</summary>
    public static readonly (string Id, string Label)[] All =
    {
        (Sponsor, "赞助广告"),
        (SelfPromo, "自我推广"),
        (Intro, "开场动画"),
        (Outro, "结尾动画"),
        (Preview, "预告 / 回顾"),
        (PoiHighlight, "高能时刻")
    };

    /// <summary>id → 中文名; 不认识的 id 原样返回(界面上也不会因此变成空白)</summary>
    public static string LabelOf(string? id)
    {
        foreach (var (i, label) in All)
            if (i == id) return label;
        return id ?? "";
    }

    /// <summary>这个 id 是不是本应用支持的类别</summary>
    public static bool IsKnown(string? id)
    {
        foreach (var (i, _) in All)
            if (i == id) return true;
        return false;
    }

    /// <summary>
    /// 进度条上该类别色块的颜色。
    ///
    /// 和类别表放在一起是有意的: 以后加类别时会被迫在这里选一个颜色,
    /// 不会出现"新类别在进度条上没颜色"的漏网。
    /// 选的都是在深色控制栏上够扎眼的颜色(进度条压在视频上, 底色永远是暗的)。
    /// </summary>
    public static string MarkColorHex(string? id) => id switch
    {
        Sponsor => "#FB7299",        // 恰饭: 直接用 B 站品牌粉, 最醒目
        SelfPromo => "#FFB74D",      // 自我推广: 橙
        Intro => "#4FC3F7",          // 开场: 蓝
        Outro => "#9575CD",          // 结尾: 紫
        Preview => "#4DB6AC",        // 预告回顾: 青
        PoiHighlight => "#FFD54F",   // 高能: 亮黄
        _ => "#BDBDBD"               // 不认识的类别: 中性灰(照旧能看见, 不会消失)
    };

    /// <summary>
    /// 归一化一份"类别串": 只保留本应用认识的 id, 去重、去空白;
    /// 一个都不剩时退回默认的 sponsor(与"清空类别"的处理一致, 不能让功能静默失效)。
    /// 读设置时必须过一遍 —— 用户的配置里可能留着我们已经下掉的 interaction, 也可能被手改坏。
    /// </summary>
    public static string Sanitize(string? csv)
    {
        var kept = new List<string>();
        if (!string.IsNullOrWhiteSpace(csv))
        {
            foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var id = raw.Trim();
                if (id.Length == 0 || kept.Contains(id)) continue;
                if (!IsKnown(id)) continue;
                kept.Add(id);
            }
        }
        return kept.Count > 0 ? string.Join(",", kept) : Sponsor;
    }
}

/// <summary>外观模式</summary>
public enum AppTheme
{
    System,
    Light,
    Dark
}

/// <summary>
/// 窗口背景材质变体(2026-10-06 新增)。
///
/// ★★★ 为什么做这个而不是"模糊度": 系统**没有**任何 API 能改模糊半径
///   (模糊半径是 DWM 写死的内部常量, 唯一能改的办法是注入 dwm.exe hook 私有符号, 普通应用做不到)。
///   而"用哪种材质"是**官方开放**的 —— <c>DWMWA_SYSTEMBACKDROP_TYPE</c> 就是这个选择器。
///   这是唯一既真实、又观感差异明显的可控项, 所以把这一层开放给用户。
///
/// ★ 枚举值**直接等于** DWMWA_SYSTEMBACKDROP_TYPE 的取值(DWMSBT_*), 下发时不做映射 ——
///   少一层转换就少一处能写错的地方。★ 别改这些数字。
/// </summary>
public enum BackdropMaterial
{
    /// <summary>无材质: 窗口保持全透明(不申请任何 DWM 材质, 桌面直接透出、不模糊)。</summary>
    None = 1,

    /// <summary>
    /// 云母(Mica, 2)。★ 它是**不透明**的: 只采样一次桌面壁纸、且**从不透出后面的窗口**。
    /// 所以选它等于放弃"透出桌面"这个效果, 换来的是更沉稳的壁纸色调。
    /// </summary>
    Mica = 2,

    /// <summary>亚克力(Acrylic, 3) —— 默认。真·半透明磨砂, 实时透出桌面内容。</summary>
    Acrylic = 3,

    /// <summary>云母 Alt / 标签页材质(Tabbed, 4)。同样是壁纸基调的不透明材质, 层次比云母更平。</summary>
    MicaAlt = 4
}

/// <summary>
/// 首页「推荐」tab 的算法来源。★ 2026-10-03 起这个选择**已取消**, 固定用网页版。
///
/// 保留枚举只是为了让老 settings.json 里存着的值仍能反序列化(删掉枚举会让老配置解析失败,
/// 那会连带把整个设置文件清空)。实际读取处一律强制 Web —— 原因见 ApiClient.GetRecommendAsync:
/// App 端接口对第三方客户端不提供个性化, 返回的是全站通用热门池。
///
/// 两个值的差别(历史记录, 供以后参考):
///   · App —— app.bilibili.com/x/v2/feed/index, 每次 10 条;
///   · Web —— wbi/index/top/feed/rcmd, 单次最多 30 条, **实测更贴口味**。
/// </summary>
public enum RecommendSource
{
    App = 0,
    Web = 1
}

/// <summary>URL 工具(纯函数, 不依赖 WPF)</summary>
public static class UrlUtil
{
    /// <summary>把 bilibili 返回的图片地址规范为 https 绝对地址</summary>
    public static string Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (url.StartsWith("//")) return "https:" + url;
        if (url.StartsWith("http://")) return "https://" + url[7..];
        return url;
    }
}

/// <summary>会话(登录 Cookie)</summary>
public class Session
{
    public string? SessData { get; set; }
    public string? BiliJct { get; set; }
    public string? DedeUserID { get; set; }
    public string? DedeUserIDCkMd5 { get; set; }
    public string? Buvid3 { get; set; }

    /// <summary>
    /// 设备指纹的第二半(buvid4)。
    ///
    /// ★ 必须持久化, 不能只放内存。推荐系统靠 (buvid3 + buvid4) 这一对认"这是同一台设备",
    ///   而 `x/frontend/finger/spi` **每次调用都发一对全新的** —— 实测: 即使把已有的
    ///   buvid3/buvid4 带上去, 它照样回吐两个新的, 不做 round-trip。所以只要不在本地存下来,
    ///   每次启动都会变成一台"从未见过的设备", 推荐模型每次都要从零冷启动,
    ///   表现就是"推的东西东一榔头西一棒槌"。
    /// 老配置没有这个字段 → 反序列化保持 null → 下次启动补上并落盘。
    /// </summary>
    public string? Buvid4 { get; set; }

    // ------------------------------------------------------------------
    // ★★★ 写操作(点赞/投币/收藏/三连)必需的风控 Cookie(2026-11-12 加)
    //
    // 背景: B 站的**读**接口只看 SESSDATA + buvid3/4 就放行, 但**写**接口还会看
    // 这一组"浏览器会话特征"。实测(探针 bd-probe-like, 登录态 nav 正常、只读
    // has/like 也正常): 缺它们时写接口间歇性返回 -352「被风控拦截」/ -403「账号异常」。
    //
    // ★ 为什么这与"扫码登录"有关: 旧流程从浏览器 Profile 里直接拿整套 Cookie,
    //   天然带着这些; 新登录流程(2026-10 引入扫码/账密)只挑了 6 个存进 session.json,
    //   其余全丢 —— 于是"换了登录方式之后点赞就不行了", 而登录态本身是好的。
    //
    // 值全部可空: 老配置没有这些字段, 缺了就只带能带的(退回到改动前的行为, 不会更坏)。
    public string? BNut { get; set; }
    public string? Sid { get; set; }
    public string? BuvidFp { get; set; }
    public string? Blsid { get; set; }
}

/// <summary>
/// 一条"一键屏蔽 UP 主"规则(落盘用)。
/// Name 只用于面板显示/UID 缺失时的兜底匹配; Mid &gt; 0 时优先按 UID 命中, UP 改名也拦得住。
/// </summary>
public class BlockedUpEntry
{
    public string Name { get; set; } = "";
    public long Mid { get; set; }
}

/// <summary>应用设置</summary>
public class AppSettings
{
    public string FontId {get;set;}="System";
    public bool UseWebPlayerLayout { get; set; }
    public int TouchpadFeedSensitivity { get; set; } = 75;

    public AppTheme ThemeMode { get; set; } = AppTheme.System;
    public string SkinId { get; set; } = "";
    public bool SkinEffects { get; set; } = true;
    public bool SkinUseAccent { get; set; } = true;
    /// <summary>
    /// 播放器音量(0~100)。用户调过一次就记住, 之后打开的每个视频都用它 ——
    /// 这就是"音量继承"(2026-11-12 用户要求"调节音量以后下一个视频的音量也一样")。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public int PlayerVolume { get; set; } = 80;

    /// <summary>
    /// LibVLC 网络缓冲(毫秒)。控制流式播放的预读深度: 越小越省内存, 越大越抗网络抖动。
    /// 老配置没有这个字段 ⇒ 反序列化保持初始化值(1000, 与旧版全局值一致)。
    /// 手改成 0/越界值由 SettingsStore 读取时夹回 300~3000。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public int NetworkCachingMs { get; set; } = 1000;

    /// <summary>
    /// 缓冲策略(true=自动按带宽挑值, false=手动用 NetworkCachingMs)。
    /// ★ 用可空类型接: JSON 反序列化分不清"老配置没这个字段"和"明确写了 false",
    ///   null(=字段不存在)要按"自动"处理 —— 那是新配置的默认(见 SettingsStore.AutoBufferPolicy)。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public bool? AutoBufferPolicy { get; set; }

    /// <summary>
    /// 上次用的搜索类别(见 <see cref="ContentKind"/>: 0=视频 1=直播间 2=番剧)。
    /// 存 int 而不是枚举本身: JSON 里枚举按数字走, 老配置没有这个字段时默认 0(视频),
    /// 与搜索页的初始类别一致。★ 取值范围由 SettingsStore.SearchKind 夹取。
    /// </summary>
    public int LastSearchKind { get; set; }

    /// <summary>
    /// 旧字段(仅用于**读取**老配置, 兼容用)。
    ///
    /// ★★★ 2026-10-06 起"窗口背景材质"合并成了**一个**选择器(无/亚克力/云母/云母Alt),
    ///   真正的开关是 <see cref="AcrylicMaterial"/>(None = 关); 这个 bool 已降级为**派生值**
    ///   (由材质算出来, 落盘时同步写一份, 免得用户回退旧版本时状态丢失)。
    ///   ⇒ 代码里**别再按它判断开关**, 一律用 AcrylicMaterial。
    /// </summary>
    public bool AcrylicBackground { get; set; }

    /// <summary>
    /// 亚克力背景透明度——浅色主题(默认 30, 可调 0~50, 范围见 SettingsStore.AcrylicOpacityLightMin)。
    /// 数值越小越透明, 越大越不透明。只影响"页面底/侧边栏"这两层的透明度, 不影响卡片/浮层等实色块。
    /// ★ 默认值/范围与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public int AcrylicOpacityLight { get; set; } = 30;

    /// <summary>
    /// 亚克力背景透明度——深色主题(默认 50, 可调 30~70, 范围见 SettingsStore.AcrylicOpacityDarkMin)。
    /// 深色底本身就更暗, 默认稍不透明一档。
    /// </summary>
    public int AcrylicOpacityDark { get; set; } = 50;

    /// <summary>
    /// **窗口背景材质**(2026-10-06 起是唯一的开关 + 变体选择, 与云母/云母Alt 平级)。
    ///   · None(1)   = 不用任何材质(普通纯色背景) —— 这就是"关闭"
    ///   · Mica(2)   = 云母(不透明, 只采样壁纸)
    ///   · Acrylic(3)= 亚克力(半透明磨砂, 实时透出桌面)
    ///   · MicaAlt(4)= 云母 Alt
    ///
    /// ★★★ 为什么是**可空**的: 老配置里根本没有这个字段, 而枚举默认值恰好是 None ——
    ///   那样就分不清"老用户(该按旧 bool 推导)"和"新用户显式选了无材质"。
    ///   可空之后: null = 老配置(缺字段, 走迁移), 有值 = 新配置。
    /// ★ 枚举值直接等于 DWMSBT_*, 见 <see cref="BackdropMaterial"/>。
    /// </summary>
    public BackdropMaterial? AcrylicMaterial { get; set; }

    /// <summary>
    /// 卡片页每行显示几列(0 = 自动, 按窗口宽度算)。范围见 Helpers/CardWall.FixedColumnsMin/Max。
    /// ★ 作用于**所有**卡片页(首页/历史/收藏/搜索/分区/稍后再看/关注/个人空间), 它们共用同一套卡片墙。
    /// </summary>
    public int CardColumns { get; set; }

    /// <summary>
    /// 「动态」页右栏的排版: false = 动态卡(单列: 头像 + 发布时间 + 视频), true = 卡片墙(多列网格)。
    ///
    /// ★ 默认 false(动态卡): 用户 2026-10-15 要求"右侧不要全是视频卡片, 要有视频结合时间和
    ///   头像的卡片"; 卡片墙是留给他按需切回去的那一档, 而不是默认档。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public bool FollowCardWall { get; set; }

    /// <summary>
    /// 软件缩放百分比(80~150, 默认 100)。把整个界面按比例放大/缩小, 与系统 DPI 无关。
    /// 范围见 Helpers/UiScale.MinPercent/MaxPercent。
    /// </summary>
    public int UiScalePercent { get; set; } = 100;

    /// <summary>
    /// 自定义**下载/缓存位置**(空串 = 用默认的系统「下载」文件夹)——
    /// 下载的视频(.mp4)与"图片另存为"都存到这里, 设置页那一行与离线缓存页显示的就是它。
    /// ★ 2026-10-11 由 <c>CacheRoot</c> 改名而来: 用户理解的"缓存位置"是"下载的视频存哪",
    ///   而封面图/短视频合流那些内部缓存用户看不见也不需要管, 已固定留在 %LocalAppData%。
    /// ★ 旧配置里的 <c>CacheRoot</c> 值**不迁移**(见 SettingsStore.Load 的兼容读取) ——
    ///   那条值过去指向用户数据目录下的 imgcache/vcache, 与"下载目录"语义完全不同,
    ///   拿来当下载目录会让已下好的视频"凭空消失"在设置页的显示里。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public string DownloadDir { get; set; } = "";

    /// <summary>弹幕开关(全局, 播放器不再提供弹幕按钮)</summary>
    public bool DanmakuEnabled { get; set; } = true;

    /// <summary>弹幕显示区域占比(25~100, 单位: 画面高度百分比)</summary>
    public int DanmakuAreaPercent { get; set; } = 25;

    /// <summary>弹幕智能屏蔽(过滤超长/刷屏弹幕, 降低卡顿)</summary>
    public bool DanmakuSmartFilter { get; set; } = true;

    /// <summary>
    /// 彩色弹幕。接口给的颜色非白时按原色渲染; 关掉之后所有弹幕统一白色。
    /// 默认开 —— 彩色弹幕是 B 站的既定玩法, 不看颜色等于丢信息。
    /// </summary>
    public bool DanmakuColorful { get; set; } = true;

    /// <summary>
    /// 弹幕关键词屏蔽规则。多个词用换行或逗号分隔;
    /// 以 "re:" 开头的行按正则处理(用于 "只要包含日期就屏蔽" 这类模式匹配)。
    /// </summary>
    public string DanmakuBlockKeywords { get; set; } = "";

    /// <summary>
    /// 弹幕类型过滤(只显示勾选的类型)。四个独立开关而不是位标志: 界面上就是四个胶囊按钮,
    /// 位运算每次都要拆合容易写错; 存 JSON 时 bool 可读、老配置缺字段自动拿默认值。
    /// 默认全部 true —— 过滤是减法功能, 默认不该动用户能看到的弹幕。
    /// </summary>
    public bool DanmakuFilterScroll { get; set; } = true;

    /// <summary>固定弹幕(顶部/底部)</summary>
    public bool DanmakuFilterFixed { get; set; } = true;

    /// <summary>彩色弹幕(按颜色过滤; 与 DanmakuColorful 的"是否上色"是两件事)</summary>
    public bool DanmakuFilterColorful { get; set; } = true;

    /// <summary>高级弹幕(mode 7 高级 / 8 代码 / 9 BAS)</summary>
    public bool DanmakuFilterAdvanced { get; set; } = true;

    /// <summary>
    /// 弹幕不透明度(20~100)。★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**,
    /// 两处必须一致 —— 老配置缺这个字段时靠属性初始化值拿到 80, 不需要版本兼容。
    /// </summary>
    public int DanmakuOpacityPercent { get; set; } = 80;

    /// <summary>弹幕字号倍率(50~300)。与 SettingsStore 同名属性是读写契约。</summary>
    public int DanmakuFontScalePercent { get; set; } = 100;

    /// <summary>弹幕速度档位(0 最慢 ~ 4 最快)。与 SettingsStore 同名属性是读写契约。</summary>
    public int DanmakuSpeedLevel { get; set; } = 2;

    // ------------------------------------------------------------ 列表屏蔽(FilterService)

    /// <summary>
    /// 按标题屏蔽的关键词(手打, 包含匹配)。多个词用空格 / 逗号分隔。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public string FilterTitleKeyword { get; set; } = "";

    /// <summary>按 UP 主屏蔽的关键词(手打, 包含匹配)。与 SettingsStore 同名属性是读写契约。</summary>
    public string FilterUpKeyword { get; set; } = "";

    /// <summary>
    /// 卡片右键"屏蔽此 UP 主"加的一键规则。精确同名 + UID(见 FilterService)。
    /// 存 Name/Mid 两个字段而不是一个拼接串: UP 改名后 UID 仍然有效,
    /// 面板里又需要昵称给人看。★ 与 SettingsStore 同名属性是读写契约。
    /// </summary>
    public List<BlockedUpEntry> FilterBlockedUps { get; set; } = new();

    /// <summary>卡片右键"屏蔽此标题"加的一键规则(标题完全相同才命中)。</summary>
    public List<string> FilterBlockedTitles { get; set; } = new();

    /// <summary>
    /// 默认画质(qn)。用户在设置页**开始播放前**预先选定的清晰度档位,
    /// 每次起播都用它取流; 该档拿不到时自动降级(见 ApiClient.GetPlayUrlAsync)。
    /// 默认取 QualityPreference.DefaultQn(1080P) —— 与新增本设置项之前播放器里
    /// 写死的 qn=80 一致, 老用户升级后起播画质不变。
    /// </summary>
    public int PreferredQualityQn { get; set; } = QualityPreference.DefaultQn;

    /// <summary>
    /// 音轨偏好(2026-11-04)。Auto = 有 Hi-Res 用 Hi-Res, 否则最高 AAC。
    /// ★ 与 PreferredQualityQn 分开: 画质 qn 与音质 id 是两套取值空间(见 AudioQuality)。
    /// </summary>
    public AudioQuality PreferredAudioQuality { get; set; } = AudioQuality.Auto;

    /// <summary>
    /// 首页「推荐」tab 的算法来源。★ 2026-10-03 起固定为网页版(字段仅为兼容老配置保留,
    /// 读取时一律强制 Web, 见 SettingsStore.Load)。
    /// </summary>
    public RecommendSource RecommendSource { get; set; } = RecommendSource.Web;

    /// <summary>
    /// 是否启用"跳过赞助片段"(SponsorBlock)。
    ///
    /// ★ **默认关**(用户 2026-09-27 拍板): 开启后每条视频的 BV 号都会被发到第三方服务器
    /// (www.bsbsb.top), 等于把"你在看什么"告诉别人。这是真实的隐私代价, 必须由用户
    /// 明确选择, 不能默认替他送出去。
    ///
    /// (属性初始化就是 false —— 老配置缺这个字段时会保持初始化值, 自动落到"关"。)
    /// </summary>
    public bool SponsorBlockEnabled { get; set; }

    /// <summary>
    /// 要跳过的片段类别(逗号分隔, 存服务端的 category 原值)。
    /// 默认只勾"赞助广告" —— 那是这个功能的立身之本; 片头片尾属于个人口味, 交给用户自己开。
    /// 存字符串而不是位标志: 类别以后可能增删, 位标志会像枚举重排那样错位。
    /// </summary>
    public string SponsorBlockCategories { get; set; } = SponsorCategories.Sponsor;

    /// <summary>
    /// 主题色(强调色)。空串 = 用默认的 B 站粉(见 <see cref="Helpers.ThemeService.DefaultAccent"/>),
    /// 非空时是 `#RRGGBB` 形式。
    ///
    /// 为什么默认空串而不是直接存 "#FB7299": 默认色以后可能会调, 空串表示"跟着默认走",
    /// 老配置(没这个字段 → 空串)自动落到默认值, 用户显式选过的颜色才被记住。
    /// </summary>
    public string AccentColor { get; set; } = "";

    /// <summary>
    /// 关闭主窗口时最小化到托盘(默认 true)。
    /// 关掉之后点右上角关闭键就是真的退出程序, 不再收到托盘里。
    /// 属性初始化就是 true —— 老配置缺这个字段时会保持初始化值, 于是"默认打开"自动成立。
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// 启动后自动检查更新(默认 true)。
    /// 检查是匿名的 GitHub Releases 请求, 不带任何账号信息; 关掉后仍可在设置页手动检查。
    /// 老配置缺这个字段时保持初始化值 ⇒ "默认打开"自动成立, 不需要版本号兼容。
    /// </summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>
    /// 用户点过「跳过此版本」的版本号(如 "1.2.0")。空 = 没跳过任何版本。
    /// 只对**自动**检查生效: 手动点「检查更新」说明用户就是想看, 一样会弹窗。
    /// </summary>
    public string SkippedVersion { get; set; } = "";

    /// <summary>
    /// 免登录 1080P(默认 true)。
    ///
    /// 未登录时在取流请求里补上 try_look + 四个 dm_img_* 设备指纹参数, 把匿名可见的
    /// 清晰度从 360P/480P 提到 720P/1080P —— 做法与参数依据见 ApiClient.ApplyTryLook。
    ///
    /// 默认 true 与 PiliPlus 一致: 只影响"未登录"这一种状态, 已登录用户按账号等级取流,
    /// 这个开关对它们没有任何作用(ApplyTryLook 会直接跳过)。
    /// 老配置缺这个字段时保持初始化值 ⇒ "默认打开"自动成立, 不需要版本号兼容。
    /// </summary>
    public bool NoLogin1080P { get; set; } = true;

    /// <summary>
    /// 视频线路(CDN)选择策略。默认自动测速 —— 实测各家 CDN 同一时刻吞吐能差 4 倍以上,
    /// 且排名随网络/地区/时段变化, 所以默认让程序自己挑。详见 Services/CdnService.cs。
    /// </summary>
    public CdnSelectMode CdnMode { get; set; } = CdnSelectMode.Auto;

    /// <summary>
    /// 手动模式下选定的 CDN Id(见 Models/CdnOption.All)。
    /// 存 Id 而不是 host: B 站换域名时 Id 不变, 老配置才不会失效。
    /// 读配置时用 CdnOption.ById 校验, 已下线的 Id 会被清成空串(退回自动)。
    /// </summary>
    public string CdnManualId { get; set; } = "";

    /// <summary>
    /// 屏蔽 PCDN(点对点分发)。默认开 —— PCDN 拿其他用户的带宽做节点, 速度不稳定;
    /// 实测同一档清晰度常规 CDN 吞吐高一倍以上。
    /// </summary>
    public bool BlockPcdn { get; set; } = true;

    /// <summary>
    /// 是否已同意首次启动的免责声明。
    /// 默认 false —— 老版本升级上来的用户也会被问一次, 这是有意的:
    /// 声明里有"账号风控/数据丢失风险自担"这类实质内容, 让所有人在同一份文本上确认一次更稳妥。
    /// </summary>
    public bool DisclaimerAccepted { get; set; }

    /// <summary>
    /// 用户同意的是**第几版**声明(对应 <see cref="Helpers.DisclaimerText.Version"/>)。
    ///
    /// 单独记版本号而不是只留一个 true/false: 声明正文以后还会改, 改完要再让用户确认一次 ——
    /// 否则"你事后能翻到的, 就是你当初点同意时看到的那一份"这句话就不成立了。
    /// 老配置里没有这个字段(值为 0), 兼容处理见 SettingsStore.Load。
    /// </summary>
    public int DisclaimerVersion { get; set; }
}

/// <summary>
/// 供页面往「视频卡片」右键菜单里追加的条目定义。
///
/// 为什么不用现成的 MenuItem:
/// 直接把 MenuItem 实例放进集合再让 DataTemplate 去渲染, 会丢失"命令参数应该是当前卡片的数据"
/// 这一层语义(MenuItem 自己的 DataContext 是它自身)。用一个纯数据的中转类型,
/// 模板里就能明确地把卡片的 DataContext 作为 CommandParameter 注入。
/// </summary>
public class CardMenuItem
{
    public string Header { get; set; } = "";
    public System.Windows.Input.ICommand? Command { get; set; }

    /// <summary>
    /// 菜单项左侧的图标字形(Segoe Fluent Icons 码位, 形如 "\uE896")。
    /// 留空则图标列收成 0 宽, 不会留下空档。
    /// </summary>
    public string Glyph { get; set; } = "";
}

// ============================================================================
// 番剧(PGC / 追番)
//
// 与 UGC 视频是**两套完全不同的资源体系**, 不能塞进 VideoItem:
//   视频按 bv 号寻址, 封面 16:9; 番剧按 season_id / ep_id 寻址, 封面是竖版海报 3:4,
//   一次拿回的是"一整季 + 它的分集列表"。所以这里单开一套模型。
//
// 接口面(2026-10-11 实测, 均带登录 Cookie):
//   我的追番   x/space/bangumi/follow/list?vmid=&pn=&ps=&type=   type:1 追番 2 追剧
//   番剧推荐   pgc/season/index/result?season_type=1&type=0&page=&ps=
//   剧集详情   pgc/view/web/season?season_id=
// ============================================================================

/// <summary>番剧卡片(追番列表 / 推荐列表共用)。</summary>
public sealed class BangumiItem
{
    public long SeasonId { get; set; }
    public long MediaId { get; set; }
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Subtitle { get; set; } = "";

    /// <summary>分类: 1 = 番剧(日漫), 4 = 国创(国产动画)。接口给的是 season_type。</summary>
    public int SeasonType { get; set; }

    /// <summary>接口自带的分类名("番剧" / "国创"), 解析不到时由 <see cref="SeasonTypeText"/> 兜底。</summary>
    public string SeasonTypeName { get; set; } = "";

    /// <summary>
    /// 分类的中文名。
    /// ★ 不直接用 <see cref="SeasonTypeName"/>: 推荐接口(index/result)那个字段是空的,
    ///   只有"我的追番"接口才给 —— 两个列表显示的却是同一批东西, 所以一律走这里。
    /// </summary>
    public string SeasonTypeText => !string.IsNullOrEmpty(SeasonTypeName)
        ? SeasonTypeName
        : SeasonType == 4 ? "国创" : "番剧";

    /// <summary>是否为国创(推荐列表按它把「番剧推荐」与「国创推荐」分成两栏)</summary>
    public bool IsGuochuang => SeasonType == 4;

    /// <summary>角标: 大会员 / 独播 / 出品。空串表示没有角标, 界面上整个角标不显示。</summary>
    public string Badge { get; set; } = "";

    /// <summary>角标底色(B 站自带, 如 #FB7299 / #00C0FF)。取不到就由界面退回默认色。</summary>
    public string BadgeColor { get; set; } = "";

    /// <summary>总集数</summary>
    public int TotalCount { get; set; }

    /// <summary>是否已完结</summary>
    public bool IsFinish { get; set; }

    /// <summary>更新进度文案("更新至第 195 话" / "全 12 话")</summary>
    public string IndexShow { get; set; } = "";

    /// <summary>
    /// 观看进度文案("看到第 12 话")。
    /// ★ 只有真的看到一半的剧才会给这个值, 其余是空串 —— 所以界面按"有才显示"处理,
    ///   空的时候退回 <see cref="IndexShow"/>, 而不是留一个空行。
    /// </summary>
    public string Progress { get; set; } = "";

    public long FollowCount { get; set; }

    /// <summary>卡片副标题: 优先显示观看进度(用户更关心"我看到哪了"), 没有就显示更新进度。</summary>
    public string CardSubText => !string.IsNullOrWhiteSpace(Progress) ? Progress : IndexShow;

    /// <summary>番剧主页(B 站网页版)</summary>
    public string Url { get; set; } = "";
}

/// <summary>番剧的一集(PGC 的分集, 不同于 UGC 合集)。</summary>
public sealed class BangumiEpisode
{
    public long EpId { get; set; }
    public long Aid { get; set; }

    /// <summary>默认分 P cid。播放走 bvid, 这个是给"定位到具体一集"用的。</summary>
    public long Cid { get; set; }

    /// <summary>这一集对应的普通稿件 bv 号 —— 播放器只认它, 点"播放"就是拿它去播。</summary>
    public string Bvid { get; set; } = "";

    /// <summary>集号("1" / "12")。部分条目是番外名而非数字。</summary>
    public string Title { get; set; } = "";

    /// <summary>本集标题("凡人风起天南1重制版")</summary>
    public string LongTitle { get; set; } = "";

    public string Cover { get; set; } = "";

    /// <summary>时长(秒)</summary>
    public int DurationSec { get; set; }
    public string Duration => VideoItem.FormatSeconds(DurationSec);

    public long PubTime { get; set; }

    /// <summary>是否已播(0 = 预告 / 未播)。没播的集不该给"播放"入口。</summary>
    public bool IsAired { get; set; }

    /// <summary>
    /// 是不是当前选中 / 正在播放的那一集(选集列表据此高亮)。
    ///
    /// ★ 这一集对象本身被 ItemsControl 绑定着, 所以**不能**靠"换引用"来改选中态 ——
    ///   换引用等于换一行, 高亮会跟着闪一下。这里让同一个对象自己报属性变更,
    ///   与 VideoItem.IsSelected 同一套路(Models 这一层刻意不依赖 WPF, 用的就是 INotifyPropertyChanged)。
    /// </summary>
    private bool _isCurrent;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 界面上这一行显示的文案。
    /// 集号 + 本集标题才是用户认得的东西; 只有标题没有集号会分不清"第几集"。
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            var num = string.IsNullOrWhiteSpace(Title) ? "" : "第" + Title + "话";
            if (string.IsNullOrWhiteSpace(LongTitle)) return num;
            return string.IsNullOrEmpty(num) ? LongTitle : num + " " + LongTitle;
        }
    }
}

/// <summary>番剧详情 + 选集列表(pgc/view/web/season 的 result)。</summary>
public sealed class BangumiDetail
{
    public long SeasonId { get; set; }
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Subtitle { get; set; } = "";

    /// <summary>分类名("番剧" / "国创" / "电影"…)</summary>
    public string SeasonTypeName { get; set; } = "";

    /// <summary>简介(pgc 的 evaluate)</summary>
    public string Evaluate { get; set; } = "";

    /// <summary>评分("9.7")。没评分(新番刚开播)时为空。</summary>
    public string Score { get; set; } = "";

    /// <summary>评分人数</summary>
    public long ScoreCount { get; set; }

    /// <summary>地区("中国大陆")</summary>
    public string Area { get; set; } = "";

    /// <summary>风格分类, 逗号连接("热血, 奇幻")</summary>
    public string Styles { get; set; } = "";

    /// <summary>更新时间说明("每日17:00更新一集")</summary>
    public string Renewal { get; set; } = "";

    /// <summary>首播日期("2022-01-01")</summary>
    public string PublishDate { get; set; } = "";

    /// <summary>追番人数</summary>
    public long FollowCount { get; set; }

    /// <summary>是否完结</summary>
    public bool IsFinish { get; set; }

    /// <summary>选集列表(正片, 按官方顺序展平)</summary>
    public List<BangumiEpisode> Episodes { get; set; } = new();

    public int TotalEp => Episodes.Count;

    /// <summary>评分那一行的文案; 没有评分就整段不显示。</summary>
    public string ScoreText => string.IsNullOrEmpty(Score) ? "" : Score + " 分";
}

// ============================================================ 动态混合流(2026-11-04)

/// <summary>
/// 一条动态的**类型**。B 站动态是混合流: 视频投稿、图文、纯文字、转发、专栏、直播开播……
/// 之前 FollowViewModel 只收 type=8(视频), 其余全丢, 于是"动态页只有视频"。
///
/// ★ 值来自接口的 type 字段(实测):
///   1=转发 2=图文 4=纯文字 8=视频投稿 64=专栏 4308=直播开播 等。
///   认不出来的类型统一归 Other, 仍然**照常显示**(有正文就显示正文), 不再丢弃 ——
///   "宁可显示一条信息不完整的动态, 也不要整条消失"是这次重构的核心原则。
/// </summary>
public enum DynamicKind
{
    /// <summary>转发(带原动态引用)</summary>
    Forward,
    /// <summary>图文(正文 + 图片九宫格)</summary>
    ImageText,
    /// <summary>纯文字(没有图片)</summary>
    Text,
    /// <summary>视频投稿(封面 + 标题, 可点开播放)</summary>
    Video,
    /// <summary>专栏文章</summary>
    Article,
    /// <summary>直播开播提醒</summary>
    Live,
    /// <summary>认不出来的类型(仍然显示正文/图片)</summary>
    Other,
}

/// <summary>动态里的一张图片(九宫格用)。</summary>
public class DynamicPicture
{
    public string Url { get; set; } = "";
    /// <summary>原图宽高(接口给, 用来算九宫格里的占位比例; 拿不到时按 1:1)</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public double Aspect => Width > 0 && Height > 0 ? (double)Width / Height : 1.0;
}

/// <summary>
/// 动态正文里的一段富文本(2026-11-04)。
///
/// 为什么不能只留 <see cref="DynamicItem.Text"/>:
///   B 站动态的 desc 同时给两个字段 —— `text` 是**纯文本**(话题、@、表情全部退化成普通字),
///   `rich_text_nodes[]` 才带类型。官方客户端里 #话题# 是蓝色可点、@某人 是蓝色可点、
///   表情是图。只读 text 的话这三样全都变成死字, 而且 # 号还会跟着显示出来。
///   所以解析时把 nodes 一并留下, 渲染层按 Type 上色 / 挂点击。
///
/// 字段含义(照接口原文):
///   · Type = RICH_TEXT_NODE_TYPE_TEXT     普通文字
///   · Type = RICH_TEXT_NODE_TYPE_TOPIC    话题, 带 Rid(话题 id) 与 JumpUrl
///   · Type = RICH_TEXT_NODE_TYPE_AT       提及某人, 带 Rid(被 @ 的 mid)
///   · Type = RICH_TEXT_NODE_TYPE_WEB      网页链接
///   · Type = RICH_TEXT_NODE_TYPE_BV       BV 号
///   · Type = RICH_TEXT_NODE_TYPE_EMOJI    小表情, 带 Emoji(图片地址)
/// </summary>
public class DynamicRichNode
{
    /// <summary>接口原文类型(RICH_TEXT_NODE_TYPE_*), 保留原文便于以后加类型不用改解析</summary>
    public string Type { get; set; } = "";
    /// <summary>显示文本。话题含两侧的 #, @ 含 @ 号 —— 与官方一致</summary>
    public string Text { get; set; } = "";
    /// <summary>不带装饰的原文(话题去掉 #, @ 去掉 @), 用来做搜索词/主页跳转</summary>
    public string OrigText { get; set; } = "";
    /// <summary>话题 id / 被 @ 的 mid / 视频 aid, 按类型解释</summary>
    public string Rid { get; set; } = "";
    /// <summary>跳转地址(接口给的 jump_url, 只用于判断类型, 不在客户端直接打开网页)</summary>
    public string JumpUrl { get; set; } = "";
    /// <summary>小表情图片地址(仅 EMOJI 有值)</summary>
    public string Emoji { get; set; } = "";

    public bool IsTopic => Type.EndsWith("_TOPIC", StringComparison.Ordinal);
    public bool IsMention => Type.EndsWith("_AT", StringComparison.Ordinal);
    public bool IsEmoji => Type.EndsWith("_EMOJI", StringComparison.Ordinal) && Emoji.Length > 0;
    public bool IsLink => Type.EndsWith("_WEB", StringComparison.Ordinal) || Type.EndsWith("_BV", StringComparison.Ordinal);
}

/// <summary>
/// 动态投票(2026-11-04, 按实测结构重写)。
///
/// ★★★ 位置纠正(只读探针实测, 扫 160 条动态 0 条命中 major.vote):
///   投票节点在 <c>modules.module_dynamic.additional.vote</c>, 且
///   <c>additional.type == "ADDITIONAL_TYPE_VOTE"</c>。major.vote **不存在**。
///
/// ★★★ 结构纠正 —— 卡片节点**不含选项数组**:
///   卡片上只有 vote_id / title / desc / join_num / status / end_time / choice_cnt,
///   要拿到 options[{opt_idx, opt_desc, cnt}] 必须再调一次
///   <c>GET /x/vote/vote_info?vote_id=&lt;id&gt;</c>(匿名可读)。
///   所以 <see cref="Options"/> 在卡片首次出现时是空的, 由界面滚动到可见时再拉 ——
///   这也是本类要有 <see cref="OptionsLoaded"/> 的原因。
///
/// 字段语义(实测, 与原猜测有出入, 逐条标注):
///   · <c>vote_id</c>  → Id          (不是 id)
///   · <c>desc</c>     → 是"N人参与"**文案**, 不是投票描述 ← 原实现当成描述, 会显示成
///                        "投票说明: 167人参与" 这种自相矛盾的文字
///   · <c>join_num</c> → TotalCount  (参与人数, 直接给, 不需要自己求和)
///   · <c>status</c>   → '1' 进行中 / '4' 已结束(**不是**"我投过没有")
///   · <c>choice_cnt</c> → MaxChoices (可选几项; '1' = 单选)
///   · <c>end_time</c> → EndTime     (unix 秒)
///   · <c>options[].opt_idx</c> 从 **1** 开始(不是 0) —— 传 0 会被服务端拒
///   · <c>options[].opt_desc</c> 是选项文字(不是 desc)
///
/// ★ 「我投过没有」两个读接口都**不下发**(vote_my_status / my_vote_info 均无),
///   本轮未取到可靠判据。所以界面只在**本次会话里自己投成功之后**标记已投,
///   不假装知道服务端状态 —— 比猜错让用户点两次强。
/// </summary>
public class DynamicVote
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>接口原文的 desc —— 实测是"N人参与"文案, 只在没有 join_num 时兜底显示</summary>
    public string Desc { get; set; } = "";
    /// <summary>参与人数(join_num)。0 表示接口没给</summary>
    public long TotalCount { get; set; }
    /// <summary>可选几项(choice_cnt)。1 = 单选, &gt;1 = 多选</summary>
    public int MaxChoices { get; set; } = 1;
    /// <summary>'1' 进行中 / '4' 已结束</summary>
    public string Status { get; set; } = "";
    public long EndTime { get; set; }
    /// <summary>跳转地址(实测指向 /h5/vote), 只用于判断, 不在客户端直接开网页</summary>
    public string JumpUrl { get; set; } = "";

    public List<DynamicVoteOption> Options { get; set; } = new();
    /// <summary>选项是否已经从 /x/vote/vote_info 拉过(卡片节点本身不带选项)</summary>
    public bool OptionsLoaded { get; set; }
    public bool OptionsLoading { get; set; }
    public bool OptionsFailed { get; set; }
    public string OptionsError { get; set; } = "";

    /// <summary>本次会话里我自己投票成功过(服务端不下发这个状态, 只能本地记)</summary>
    public bool VotedThisSession { get; set; }
    /// <summary>我自己投中的选项编号(用于高亮)</summary>
    public HashSet<int> MyChoices { get; set; } = new();

    public bool IsMulti => MaxChoices > 1;
    public bool HasOptions => Options.Count > 0;
    /// <summary>投票已结束(status=4)。注意: status=4 也可能是"投票不存在", 接口区分不了</summary>
    public bool IsClosed => Status == "4";
    public bool CanVote => !IsClosed && !VotedThisSession;

    /// <summary>
    /// 底部那行参与数。
    /// ★ 优先用 join_num(接口直接给), 没有再退回 desc(实测 desc 就是"N人参与"文案),
    ///   两者都没有才说"还没有人参与" —— 不要凭空造一个数字。
    /// </summary>
    public string TotalText =>
        TotalCount > 0 ? TotalCount.ToString("N0") + " 人参与"
        : Desc.Length > 0 ? Desc
        : "还没有人参与";

    public string StateText => IsClosed ? "已结束" : IsMulti ? $"可多选({MaxChoices} 项)" : "单选";
}

/// <summary>投票里的一项</summary>
public class DynamicVoteOption
{
    public int Index { get; set; }
    public string Text { get; set; } = "";
    public long Count { get; set; }
    public string ImageUrl { get; set; } = "";
    /// <summary>列表里显示成"42%"——分母为 0 时给空串, 避免出现 NaN%</summary>
    public string PercentText { get; set; } = "";
    /// <summary>票数的展示串("13"), 没票时也不显示 0 更干净</summary>
    public string CountText => Count > 0 ? Count.ToString("N0") + " 票" : "";
    /// <summary>
    /// 百分比条的宽度比例 0~1。
    /// ★ 为什么给比例而不是像素: 卡片宽度随窗口/列数变化, 像素值一写死就错。
    ///   界面用 RenderTransform 的 ScaleX 接这个值(横向缩放一条满宽色带),
    ///   父容器多宽都不会算错, 也不需要监听 SizeChanged。
    /// </summary>
    public double Fraction { get; set; }
}

/// <summary>
/// 动态附加卡片(2026-11-04), 对应接口的 modules.module_dynamic.major.common。
///
/// 什么时候会出现: 活动页、带货商品、番剧 / 影视 / 课程 / 直播预告这类"外部内容"。
/// 官方客户端把它画成一张"左封面右文字 + 右上角来源标签 + 右下角行动按钮"的卡。
/// 之前完全没解析 ⇒ 这类动态在时间线上只剩一句正文, 用户以为内容丢了。
///
/// 字段都可能是空串(接口有时只给 title 不给 cover), 所以 HasContent 以
/// "有标题或有封面"为准 —— 两者都空就没有画的必要。
/// </summary>
public class DynamicCommonCard
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>合成后的描述(desc1 · desc2 或老结构的 desc), 界面显示这个</summary>
    public string Desc { get; set; } = "";
    /// <summary>新结构的两段描述原文(实测 additional.common 用 desc1/desc2 拆开给)</summary>
    public string Desc1 { get; set; } = "";
    public string Desc2 { get; set; } = "";
    public string Cover { get; set; } = "";
    public string JumpUrl { get; set; } = "";
    /// <summary>来源标签(接口 badge.text, 例如"活动""番剧"), 没有就是空串</summary>
    public string Badge { get; set; } = "";
    /// <summary>右上角的来源名(接口 head_text / source_name)</summary>
    public string SourceName { get; set; } = "";
    /// <summary>子类型(实测 sub_type, 例如 "game"), 用来把"游戏卡"和"活动卡"画得不一样</summary>
    public string SubType { get; set; } = "";
    /// <summary>行动按钮文案(实测在 button.jump_style.text, 老结构在 button.text)</summary>
    public string ButtonText { get; set; } = "";

    public bool HasContent => Title.Length > 0 || Cover.Length > 0 || Desc.Length > 0;
    public bool HasCover => Cover.Length > 0;
    public bool HasButton => ButtonText.Length > 0;
    public bool IsGame => SubType == "game";
}

/// <summary>
/// 动态混合流里的一条。与 <see cref="VideoItem"/> **并列**而不是替代:
/// 视频动态仍然要能点开播放, 所以内部持有一个 VideoItem(可空)。
///
/// ★ 为什么不把图文硬塞进 VideoItem: VideoItem 的字段全是"视频"语义
///   (bvid/时长/播放量), 图文动态一条都没有, 塞进去会让一半字段永远为空,
///   而且点击逻辑要到处判空。分开之后: 卡片按 Kind 选模板, 点击按 Kind 选去向。
/// </summary>
public class DynamicItem : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>动态 id(字符串, 用于去重与"已读"标记)</summary>
    public string Id { get; set; } = "";

    public DynamicKind Kind { get; set; }
    public string CommentOid { get; set; } = "";
    public int CommentType { get; set; }
    public string DetailUrl { get; set; } = "";
    public string ContentTitle { get; set; } = "";
    public bool IsLiked { get; set; }
    public bool LikeForbidden { get; set; }
    private bool _isLiking;
    public bool IsLiking {get=>_isLiking;set {if(_isLiking==value)return;_isLiking=value;Notify(nameof(CanLike));}}
    public bool CanLike => !IsLiking && !LikeForbidden;

    public bool HasContentTitle => ContentTitle.Length > 0;
    public bool HasVideo => Video != null;

    /// <summary>作者的 mid / 昵称 / 头像</summary>
    public long AuthorMid { get; set; }
    public string AuthorName { get; set; } = "";
    public string AuthorFace { get; set; } = "";

    /// <summary>发布时间戳(秒)与排好版的文本("2小时前")</summary>
    public long PubTs { get; set; }
    public string PubTimeText { get; set; } = "";

    /// <summary>正文(纯文字/图文/转发的评论)。转发时这是"转发理由"。</summary>
    public string Text { get; set; } = "";

    /// <summary>正文里的图片(九宫格)</summary>
    public List<DynamicPicture> Pictures { get; set; } = new();

    /// <summary>视频动态: 可播放对象(非视频动态为 null)</summary>
    public VideoItem? Video { get; set; }

    /// <summary>
    /// 视频封面的**原始宽高比**(2026-11-04 修"封面被裁切")。
    ///
    /// 原来视频附带卡把封面写死 220 高、宽度撑满, 再叠一层 `Stretch=UniformToFill` ——
    /// 那等于**裁掉两侧**: 16:9 的封面塞进更扁的框里, 左右直接被切。
    /// 用户截图里 ED 画面的"Presented by Nintendo"被切、竖版的方舟封面被切,
    /// 都是同一个原因。
    ///
    /// 现在改成: 由接口给的 archive 尺寸算出比例, 让框**自己**按比例定高,
    /// 封面用 Uniform 填满 —— 不裁、不变形。
    /// 拿不到尺寸时退回 16:9(官方客户端的默认预览比)。
    /// </summary>
    public double VideoAspect { get; set; }

    /// <summary>
    /// 视频动态的类型标(2026-11-04)。
    /// 来自 `archive.badge.text` / `module_author.pub_action`, 实测取值:
    ///   "投稿视频" / "投稿了视频" / "直播回放" / "投稿了专栏" ...
    /// 官方客户端在封面左上角显示这个标, 让人一眼分清是视频还是直播回放。
    /// 空 = 不显示(图文/纯文字动态本来就没有)。
    /// </summary>
    public string PubAction { get; set; } = "";

    /// <summary>封面左上角的类型标(优先用 archive.badge.text, 退到 pub_action)</summary>
    public bool HasPubAction => PubAction.Length > 0;

    /// <summary>时长的展示串("06:53")。视频动态才有</summary>
    public string DurationText => Video?.Duration ?? "";

    /// <summary>播放量的展示串("2063" / "4.7万")</summary>
    public string ViewCountText => Video?.ViewCountText ?? "";

    /// <summary>弹幕数的展示串, 与播放量并排显示在封面左下角</summary>
    public string DanmakuCountText => Video?.DanmakuCountText ?? "";

    /// <summary>播放 + 弹幕的合并串(官方是"20播放 · 3弹幕"这种一行)</summary>
    public string StatText
    {
        get
        {
            var play = ViewCountText;
            var danmaku = DanmakuCountText;
            if (play.Length > 0 && danmaku.Length > 0) return play + "播放 · " + danmaku + "弹幕";
            if (play.Length > 0) return play + "播放";
            if (danmaku.Length > 0) return danmaku + "弹幕";
            return "";
        }
    }

    /// <summary>有没有时长角标可显示</summary>
    public bool HasDuration => DurationText.Length > 0;

    /// <summary>专栏: 标题 + 封面 + cv 号</summary>
    public string ArticleTitle { get; set; } = "";
    public string ArticleCover { get; set; } = "";
    public string ArticleId { get; set; } = "";

    /// <summary>直播: 房间号</summary>
    public long LiveRoomId { get; set; }

    /// <summary>
    /// 投票(2026-11-04)。null = 这条不是投票动态。
    /// vote 挂在 major.vote 上, 与正文/图片并存 —— 所以不能靠 Kind 判定,
    /// 必须是"有没有这个对象"。
    /// </summary>
    public DynamicVote? Vote { get; set; }

    /// <summary>
    /// 附加卡片(major.common, 2026-11-04)。
    /// 官方把"活动 / 商品 / 番剧 / 课程 / 小站内链"这类东西统一塞进 common,
    /// 结构: { id, title, desc, cover, jump_url, button{...}, badge{...} }。
    /// 之前没读 ⇒ 这类动态在时间线上只剩一句正文, 看起来像"内容丢了"。
    /// </summary>
    public DynamicCommonCard? Common { get; set; }

    /// <summary>
    /// 正文的富文本节点(话题 / @ / 表情 / 链接)。空 = 接口没给(或纯文字动态)。
    /// 渲染层优先用它上色, 拿不到就退回 <see cref="Text"/>。
    /// </summary>
    public List<DynamicRichNode> RichNodes { get; set; } = new();
    public bool HasRichNodes => RichNodes.Count > 0;

    /// <summary>
    /// 话题(2026-11-04)。来自 <c>module_dynamic.topic{ id, name, jump_url }</c>。
    /// ★ 这是话题的**权威来源**: rich_text_nodes 里的 TOPIC 节点 rid 恒为空串(实测),
    ///   拿不到 topic_id; 只有这里给真 id。有它才能"点话题进话题页"而不是退化成搜索。
    /// </summary>
    public string TopicId { get; set; } = "";
    public string TopicName { get; set; } = "";
    public string TopicJumpUrl { get; set; } = "";
    public bool HasTopic => TopicId.Length > 0 || TopicName.Length > 0;

    /// <summary>转发: 被转发的原动态(可能为 null = 原动态已删除)</summary>
    public DynamicItem? Original { get; set; }
    /// <summary>原动态已删除时的提示</summary>
    public bool OriginalDeleted { get; set; }

    /// <summary>点赞/评论/转发数(拿不到就是 0, 卡片会隐藏)</summary>
    public long LikeCount { get; set; }
    public long CommentCount { get; set; }
    public long ForwardCount { get; set; }

    // ---- 展示辅助 ----

    /// <summary>
    /// 有没有正文可显示。
    /// ★★★ 2026-11-04 修"正文不显示": 判据不能只看 Text。
    ///   富文本节点(话题/@/表情)本身就是正文 —— 有些动态 desc.text 为空但
    ///   rich_text_nodes 有内容(纯话题动态、纯表情动态), 只看 Text 会把整块藏掉。
    ///   ★ 不把 ContentTitle 算进来: 标题有单独的显示位(卡片主体的标题行),
    ///     算进来会让"只有标题"的卡片多出一行空白正文。
    /// </summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Text) || RichNodes.Count > 0;
    /// <summary>有没有图片</summary>
    public bool HasPictures => Pictures.Count > 0;
    /// <summary>九宫格是否只有一张(单图走大图版式)</summary>
    public bool IsSinglePicture => Pictures.Count == 1;
    /// <summary>是不是转发(要画引用框)</summary>
    public bool IsForward => Kind == DynamicKind.Forward;

    /// <summary>视频/专栏/直播/投票/附加卡片这类"有独立内容卡"的类型</summary>
    public bool HasAttachedCard => HasPictures || IsForward || Vote != null || Common != null
        || Kind is DynamicKind.Video or DynamicKind.Article or DynamicKind.Live;

    /// <summary>
    /// 这条动态带投票。
    /// ★ 判据是**有没有 vote 节点**, 不是"有没有选项":
    ///   实测卡片节点压根不带 options, 选项要额外查 /x/vote/vote_info。
    ///   若按 HasOptions 判, 卡片会一直显示成"没有投票", 直到详情页把选项补回来。
    /// </summary>
    public bool HasVote => Vote != null;
    public bool HasCommon => Common != null && Common.HasContent;

    public string LikeText => LikeCount > 0 ? "赞 " + FormatCount(LikeCount) : "赞";
    public string CommentText => CommentCount > 0 ? "评论 " + FormatCount(CommentCount) : "评论";
    public string ForwardText => ForwardCount > 0 ? "转发 " + FormatCount(ForwardCount) : "转发";

    /// <summary>
    /// 互动行用的**纯数字**(2026-11-04 改为图标 + 数字)。
    /// 数字为 0 时返回空串 —— 官方手机版也是"没有数字就只显示图标",
    /// 一排 "0" 只会让界面显得没内容。
    /// 与 <see cref="LikeText"/> 等"文字版"并存: 别的页面(消息页/详情页)还在用文字版。
    /// </summary>
    public string LikeCountText => LikeCount > 0 ? FormatCount(LikeCount) : "";
    public string CommentCountText => CommentCount > 0 ? FormatCount(CommentCount) : "";
    public string ForwardCountText => ForwardCount > 0 ? FormatCount(ForwardCount) : "";

    private static string FormatCount(long n) => n switch
    {
        >= 100_000_000 => (n / 100_000_000.0).ToString("0.#") + "亿",
        >= 10_000 => (n / 10_000.0).ToString("0.#") + "万",
        _ => n.ToString(),
    };

    /// <summary>
    /// 这条动态下的**热门评论**(2026-11-04)。
    ///
    /// B 站 PC 客户端的动态流里, 一条动态下面会直接挂 1~2 条"热评"预览,
    /// 不用点进去就能看到别人怎么说 —— 这是"动态"区别于"视频列表"的关键体验之一。
    /// 空 = 还没拉/没有评论。
    /// </summary>
    public System.Collections.Generic.List<DynamicComment> TopComments { get; set; } = new();

    public bool CommentsPreviewLoaded { get; set; }
    public bool CommentsPreviewLoading { get; set; }
    public bool HasComments => TopComments.Count > 0;

    /// <summary>评论拉回来之后通知界面(DynamicItem 不是 INPC, 用一次性事件最省)。</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void HasCommentsChanged()
    {
        Notify(nameof(TopComments));
        Notify(nameof(HasComments));
    }
    private void Notify(string name) => PropertyChanged?.Invoke(this,
        new System.ComponentModel.PropertyChangedEventArgs(name));
    public void NotifyContentChanged() => Notify("");
    public void RefreshFrom(DynamicItem d)
    {
        Kind = d.Kind; AuthorMid = d.AuthorMid; AuthorName = d.AuthorName; AuthorFace = d.AuthorFace;
        PubTs = d.PubTs; PubTimeText = d.PubTimeText; Text = d.Text; Pictures = d.Pictures; Video = d.Video;
        ContentTitle = d.ContentTitle; ArticleTitle = d.ArticleTitle; ArticleCover = d.ArticleCover; ArticleId = d.ArticleId;
        LiveRoomId = d.LiveRoomId; Original = d.Original; OriginalDeleted = d.OriginalDeleted;
        // ★ 2026-11-04: 投票 / 附加卡片 / 富文本节点也必须跟着刷新, 否则详情页拉回完整数据后
        //   卡片上还是列表接口那份旧快照(投票参与人数不更新、话题不上色)。
        Vote = d.Vote; Common = d.Common; RichNodes = d.RichNodes;
        TopicId = d.TopicId; TopicName = d.TopicName; TopicJumpUrl = d.TopicJumpUrl;
        VideoAspect = d.VideoAspect; PubAction = d.PubAction;
        LikeCount = d.LikeCount; CommentCount = d.CommentCount; ForwardCount = d.ForwardCount;
        IsLiked = d.IsLiked; LikeForbidden = d.LikeForbidden; CommentOid = d.CommentOid; CommentType = d.CommentType;
        NotifyContentChanged();
        // ★ HasText 是计算属性, NotifyContentChanged("") 在 WPF 里刷新全部绑定,
        //   但保险起见把两个最关键的显式点一次 —— 正文这块的可见性完全依赖它们。
        Notify(nameof(Text));
        Notify(nameof(HasText));
    }
    public void SetLiked(bool liked)
    {
        if (IsLiked == liked) return;
        IsLiked = liked;
        LikeCount = Math.Max(0, LikeCount + (liked ? 1 : -1));
        Notify(nameof(IsLiked));
        Notify(nameof(LikeText));
    }

    /// <summary>动态类型标识(接口原文的数字), 评论接口要用它区分 12/17(图文) 与 1(转发)</summary>
    public int RawType { get; set; }

    /// <summary>卡片顶部的"作者 · 时间"那一行的第二部分</summary>
    public string AuthorTimeText =>
        string.IsNullOrEmpty(PubTimeText) ? AuthorName : AuthorName + " · " + PubTimeText;
}
/// <summary>
/// 音轨档位(2026-11-04)。
///
/// B 站 DASH 里音频分三处, 不是一个数组:
///   · dash.audio[]        → AAC, id 30216(64k) / 30232(132k) / 30280(192k)
///   · dash.flac.audio[]   → Hi-Res 无损, id 30251(通常 1.4Mbps), **要大会员且 UP 上传了**
///   · dash.dolby.audio[]  → 杜比全景声, id 30250, **要大会员**
///
/// 之前只读第一处并按带宽取最高, 于是永远拿不到 Hi-Res —— 用户在这台机器上
/// 就算开了大会员也看不到无损选项。
/// </summary>
public enum AudioQuality
{
    /// <summary>跟随视频(默认): 有 Hi-Res 用 Hi-Res, 否则用最高 AAC</summary>
    Auto = 0,
    /// <summary>标准 AAC(30216, 64k)</summary>
    Standard = 30216,
    /// <summary>高品质 AAC(30280, 192k)</summary>
    High = 30280,
    /// <summary>Hi-Res 无损 FLAC(30251)</summary>
    HiRes = 30251,
    /// <summary>杜比全景声(30250)</summary>
    Dolby = 30250,
}

/// <summary>一条可选音轨(界面上列出来的那种)</summary>
public class AudioTrack
{
    public AudioQuality Quality { get; set; }
    public string Label { get; set; } = "";
    /// <summary>码率(kbps, 用于标签上显示"无损 1.4Mbps"这种)</summary>
    public long Bandwidth { get; set; }
    public string Codecs { get; set; } = "";
    /// <summary>这条音轨的真实地址(已选好 CDN)</summary>
    public string Url { get; set; } = "";

    public string DisplayLabel => Bandwidth > 0
        ? $"{Label} ({Bandwidth / 1000} kbps)"
        : Label;
}

/// <summary>
/// 动态下的一条评论预览(2026-11-04)。
/// 只保留卡片上要显示的三样: 谁说的、说了什么、几个赞。
/// </summary>
public class DynamicComment
{
    public long UserMid { get; set; }
    public string UserName { get; set; } = "";
    public string UserFace { get; set; } = "";
    public string Text { get; set; } = "";
    public long LikeCount { get; set; }
    public bool IsUp { get; set; }

    public string LikeText => LikeCount > 0
        ? (LikeCount >= 10_000 ? (LikeCount / 10_000.0).ToString("0.#") + "万" : LikeCount.ToString())
        : "";
}
