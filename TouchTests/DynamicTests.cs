using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliDesk;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using BiliDesk.Views.Controls;
using BiliDesk.Views.Pages;

internal static class DynamicTests
{
    static DynamicItem Parse(string json) { using var doc = JsonDocument.Parse(json); return DynamicParser.Parse(doc.RootElement)!; }

    /// <summary>
    /// 解析 /x/vote/vote_info 的返回体(2026-11-04)。
    /// 走的是生产代码同一个入口 —— 不做平行实现, 否则测试能过而线上失效。
    /// </summary>
    static DynamicVote ParseVoteInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var info = doc.RootElement.GetProperty("data").GetProperty("vote_info");
        var vote = new DynamicVote
        {
            Id = info.GetProperty("vote_id").ToString(),
            Title = info.GetProperty("title").GetString() ?? "",
            Status = info.GetProperty("status").ToString(),
            TotalCount = info.GetProperty("join_num").GetInt64(),
            MaxChoices = info.GetProperty("choice_cnt").GetInt32(),
            EndTime = info.GetProperty("end_time").GetInt64(),
        };
        DynamicParser.ReadVoteOptions(info, vote);
        vote.OptionsLoaded = vote.Options.Count > 0;
        return vote;
    }
    const string Draw = """
    {"id_str":"9001","type":"DYNAMIC_TYPE_DRAW","basic":{"comment_id_str":"7001","comment_type":11},"modules":{"module_author":{"mid":"42","name":"示例 UP","pub_time":"刚刚"},"module_dynamic":{"major":{"opus":{"title":"今天的记录","summary":{"text":"正文不会因为图片字段缺失而消失。"},"pics":[{"url":"","width":800,"height":600}]}}},"module_stat":{"comment":{"count":2},"like":{"count":3,"status":true}}}}
    """;

    /// <summary>
    /// 投票动态(2026-11-04, 按**只读探针实测结构**写的夹具)。
    ///
    /// ★ 关键差异(与上一版猜测不同, 这里必须钉住, 否则解析器一改就悄悄失效):
    ///   节点在 <c>modules.module_dynamic.additional.vote</c>, 不是 major.vote;
    ///   卡片节点**不含 options** —— 只有 vote_id/title/desc/join_num/status/choice_cnt/end_time。
    ///   desc 是"N人参与"文案, status 是 '1' 进行中 / '4' 已结束。
    /// </summary>
    const string Vote = """
    {"id_str":"9101","type":"DYNAMIC_TYPE_WORD","text":"投一下","basic":{"comment_id_str":"7101","comment_type":17},"modules":{"module_author":{"mid":"42","name":"投票 UP","pub_time":"刚刚"},"module_dynamic":{"desc":{"text":"划水，大家觉得【几点更新】专栏比较好？？"},"additional":{"type":"ADDITIONAL_TYPE_VOTE","vote":{"vote_id":"22043414","choice_cnt":"1","default_share":"1","title":"划水，大家觉得【几点更新】专栏比较好？？","desc":"167人参与","end_time":"1793861094","join_num":"167","status":"1","type":"0","uid":"1643718","jump_url":"","upower_action_state":0,"upower_level":"0"}}}},"module_stat":{"comment":{"count":9},"like":{"count":12}}}
    """;

    /// <summary>
    /// 投票详情接口(/x/vote/vote_info)的返回体夹具。
    /// ★ 字段名实测是 <c>opt_desc</c>(不是 desc), <c>opt_idx</c> 从 **1** 开始。
    /// </summary>
    const string VoteInfo = """
    {"code":0,"message":"0","ttl":1,"data":{"vote_info":{"vote_id":22043414,"title":"划水，大家觉得【几点更新】专栏比较好？？","join_num":167,"choice_cnt":1,"end_time":1793861094,"status":1,"default_share":1,"ctime":1793000000,"options":[{"opt_idx":1,"opt_desc":"早上！6-9点","cnt":13},{"opt_idx":2,"opt_desc":"中午！11-13点","cnt":24},{"opt_idx":3,"opt_desc":"傍晚！16-19点","cnt":70},{"opt_idx":4,"opt_desc":"晚上！20-23点","cnt":60}],"options_cnt":4,"voter_level":0,"face":"","name":""}}}
    """;

    /// <summary>已结束的投票(status='4')—— 用来验证 IsClosed/CanVote。</summary>
    const string VoteClosed = """
    {"id_str":"9102","type":"DYNAMIC_TYPE_WORD","text":"结束了的投票","modules":{"module_author":{"mid":"42","name":"投票 UP"},"module_dynamic":{"additional":{"type":"ADDITIONAL_TYPE_VOTE","vote":{"vote_id":"22043415","title":"已经结束的投票","desc":"9人参与","join_num":"9","status":"4","choice_cnt":"1","end_time":"1700000000"}}}}}
    """;

    /// <summary>
    /// **只有 opus.summary 有正文**的图文动态(2026-11-04 用户报"有些文字不显示")。
    ///
    /// 实测: feed/all 不带 features=itemOpusStyle 时, 新版图文动态只下发 major.draw(纯图),
    /// 正文的 major.opus.summary 整块缺失 → 卡片上没字。
    /// 带上之后同一条变成:
    ///   major.opus.summary.text = 正文全文
    ///   major.opus.summary.rich_text_nodes[] = 带话题/@ 的节点
    /// 这条夹具钉住"从 summary 里把正文和节点都读出来"。
    /// </summary>
    const string OpusSummaryOnly = """
    {"id_str":"9501","type":"DYNAMIC_TYPE_DRAW","modules":{"module_author":{"mid":"42","name":"图文 UP","pub_time":"刚刚"},"module_dynamic":{"major":{"type":"MAJOR_TYPE_OPUS","opus":{"title":"这是标题","summary":{"text":"主要是汇总了更新内容。\n注意破坏性更新：最低 Node 提到 24.9。","rich_text_nodes":[{"type":"RICH_TEXT_NODE_TYPE_TEXT","text":"主要是汇总了更新内容。"},{"type":"RICH_TEXT_NODE_TYPE_TOPIC","text":"#更新日志#","orig_text":"更新日志","rid":"1234"}]},"pics":[{"url":"https://example.invalid/p1.jpg","width":800,"height":600}]}}}}}
    """;

    /// <summary>"完全没有正文"的视频动态(不该被当成错误, 它本来就只有封面标题)。</summary>
    const string VideoNoText = """
    {"id_str":"9601","type":"DYNAMIC_TYPE_AV","modules":{"module_author":{"mid":"42","name":"视频 UP","pub_time":"刚刚"},"module_dynamic":{"major":{"type":"MAJOR_TYPE_ARCHIVE","archive":{"aid":"100","bvid":"BV1234567890","title":"视频标题","cover":"https://example.invalid/c.jpg"}}}}}
    """;

    /// <summary>附加卡片夹具: major.common(活动) —— 带封面/标题/徽章/按钮。</summary>
    const string Common = """
    {"id_str":"9201","type":"DYNAMIC_TYPE_WORD","text":"活动来了","modules":{"module_author":{"mid":"42","name":"活动 UP"},"module_dynamic":{"major":{"common":{"id":"88001","title":"活动标题","desc":"活动说明文字","cover":"https://example.invalid/c.png","jump_url":"https://example.invalid/a","badge":{"text":"活动"},"button":{"text":"去看看","jump_style":1}}}}}}
    """;

    /// <summary>
    /// 附加卡片的新结构(2026-11-04 只读探针实测): additional.common 用
    /// id_str / desc1 / desc2 / sub_type / head_text, button 文字在 button.jump_style.text。
    /// 老结构(major.common)则是 id / desc / badge —— 两套都要认。
    /// </summary>
    const string AdditionalCommon = """
    {"id_str":"9202","type":"DYNAMIC_TYPE_AV","modules":{"module_author":{"mid":"42","name":"游戏 UP"},"module_dynamic":{"additional":{"type":"ADDITIONAL_TYPE_COMMON","common":{"button":{"type":1,"jump_style":{"text":"下载游戏","interactive":null}},"sub_type":"game","head_text":"相关游戏","jump_url":"https://www.biligame.com/detail?id=107079","id_str":"107079","cover":"https://i0.hdslb.com/bfs/game/x.png","style":1,"title":"洛克王国：世界","desc1":"角色扮演/宠物/回合制","desc2":"现已正式上线"}}}}}
    """;

    /// <summary>
    /// 话题夹具(2026-11-04 实测): 权威来源是 module_dynamic.topic{id,name,jump_url}。
    /// ★ rich_text_nodes 里的 TOPIC 节点 rid 恒为空, 只有这里给真 topic_id。
    /// </summary>
    const string Topic = """
    {"id_str":"9401","type":"DYNAMIC_TYPE_WORD","modules":{"module_author":{"mid":"42","name":"话题 UP"},"module_dynamic":{"topic":{"id":"492306","name":"phigros投票","jump_url":"//m.bilibili.com/topic-detail?topic_id=492306"},"desc":{"text":"#phigros投票# 一起来","rich_text_nodes":[{"type":"RICH_TEXT_NODE_TYPE_TOPIC","text":"#phigros投票#","rid":"","jump_url":"//search?keyword=phigros投票"}]}}}}
    """;

    /// <summary>
    /// 富文本夹具: desc 同时给 text(纯文本) 与 rich_text_nodes[](带类型)。
    /// 顺序: 文字 → 话题 → 文字 → @提及 → 表情。前两段文字 + 话题 + @ + 表情 = 5 个节点。
    /// </summary>
    const string Rich = """
    {"id_str":"9301","type":"DYNAMIC_TYPE_WORD","modules":{"module_author":{"mid":"42","name":"话题 UP"},"module_dynamic":{"desc":{"text":"今天聊聊 #测试话题# 顺便 @示例用户 [微笑]","rich_text_nodes":[{"type":"RICH_TEXT_NODE_TYPE_TEXT","text":"今天聊聊 ","orig_text":"今天聊聊 "},{"type":"RICH_TEXT_NODE_TYPE_TOPIC","text":"#测试话题#","orig_text":"测试话题","rid":"9001","jump_url":"//search?keyword=测试话题"},{"type":"RICH_TEXT_NODE_TYPE_TEXT","text":" 顺便 ","orig_text":" 顺便 "},{"type":"RICH_TEXT_NODE_TYPE_AT","text":"@示例用户","orig_text":"示例用户","rid":"42","jump_url":"//space.bilibili.com/42"},{"type":"RICH_TEXT_NODE_TYPE_EMOJI","text":"[微笑]","emoji":{"icon_url":"https://example.invalid/emoji/smile.png"}}]}}}}
    """;
    public static void Run(Action<bool,string> check, string output)
    {
        var draw = Parse(Draw);
        check(draw.Kind == DynamicKind.ImageText, "string dynamic kind");
        check(draw.CommentOid == "7001" && draw.CommentType == 11, "server comment identity");
        check(draw.AuthorMid == 42 && draw.ContentTitle == "今天的记录" && draw.HasText, "opus title and summary");
        check(draw.IsLiked && draw.LikeCount == 3, "server like state");
        foreach (var (type, kind) in new[] {("DYNAMIC_TYPE_AV",DynamicKind.Video),("DYNAMIC_TYPE_WORD",DynamicKind.Text),("DYNAMIC_TYPE_FORWARD",DynamicKind.Forward),("DYNAMIC_TYPE_ARTICLE",DynamicKind.Article),("DYNAMIC_TYPE_LIVE_RCMD",DynamicKind.Live)})
            check(Parse($$"""{"id_str":"1","type":"{{type}}","text":"content","rid":"2"}""").Kind == kind, type);
        check(Parse("""{"id":123,"type":4,"text":"legacy"}""").Id == "123", "numeric legacy id");
        var opus = Parse(Draw.Replace("\"url\":\"\"", "\"url\":\"https://example.invalid/p.png\""));
        check(opus.HasAttachedCard && opus.HasPictures, "pictures are visible");
        var forward = Parse("""{"id_str":"2","type":"DYNAMIC_TYPE_FORWARD","orig":{"id_str":"3","type":"DYNAMIC_TYPE_WORD","text":"original"}}""");
        check(forward.HasAttachedCard && forward.Original?.Text == "original", "forward visible");
        var deleted = Parse("""{"id_str":"2","type":"DYNAMIC_TYPE_FORWARD","orig":{"type":"DYNAMIC_TYPE_NONE"}}""");
        check(deleted.OriginalDeleted, "deleted original");
        var live = Parse("""{"id_str":"2","type":"DYNAMIC_TYPE_LIVE_RCMD","modules":{"module_dynamic":{"major":{"live_rcmd":{"content":"{\"live_play_info\":{\"room_id\":123,\"title\":\"直播标题\"}}"}}}}}""");
        check(live.LiveRoomId == 123 && live.Text == "直播标题", "live recommendation nested JSON");
        var video = Parse("""{"id_str":"2","type":"DYNAMIC_TYPE_AV","modules":{"module_author":{"mid":42,"name":"UP"},"module_dynamic":{"major":{"archive":{"aid":"100","bvid":"BV1234567890","title":"video","stat":{"play":"1.2万","danmaku":"1.3万"}}}}}}""");
        check(video.Video?.Aid == 100 && video.Video.ViewCount == 12000 && video.Video.DanmakuCount == 13000, "video numeric strings");
        check(video.Video?.Author == "UP" && video.Video.OwnerMid == 42, "video author propagated");
        var events = new List<string>(); draw.PropertyChanged += (_,e) => events.Add(e.PropertyName!);
        draw.TopComments = new() { new() { UserName="观众", Text="热评测试" } }; draw.HasCommentsChanged();
        check(events.Contains("TopComments") && events.Contains("HasComments"), "comment bindings notified");
        draw.SetLiked(false); check(draw.LikeCount == 2 && !draw.IsLiked, "unlike updates count");
        draw.SetLiked(false); check(draw.LikeCount == 2, "same state idempotent");

        var api = Svc.Api;
        var handler = new FakeHandler();
        typeof(ApiClient).GetField("_http", BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api, new HttpClient(handler));
        foreach (var name in new[]{"_buvid3","_buvid4"}) typeof(ApiClient).GetField(name, BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api,"test");
        Svc.Session.Current.SessData = "test-only";
        Wait(api.GetDynamicFeedFullAsync());
        check(handler.Paths.Last().Contains("/feed/all?"), "full feed endpoint");
        var comments = api.GetDynamicCommentsAsync("7001",11); Wait(comments);
        check(handler.Paths.Last().Contains("oid=7001") && handler.Paths.Last().Contains("type=11"), "comment query object");
        check(comments.Result?[0].LikeCount == 5, "comment numeric likes");
        var vm = new FollowViewModel();
        var first = vm.SelectUpAsync(new FollowUser { Mid=10, Name="slow" });
        var second = vm.SelectUpAsync(new FollowUser { Mid=20, Name="fast" });
        Wait(second); Wait(first);
        check(vm.Dynamics.Count == 1 && vm.Dynamics[0].Id == "20", "rapid selection latest result wins");
        check(!vm.Loading && vm.SelectionTitle == "fast 的动态", "selection title and loading");
        check(!vm.HasMore, "server terminal cursor respected");
        Wait(vm.CheckNewDynamicsAsync());
        check(handler.Paths.Last().Contains("host_mid=20"), "new posts poll selected author");

        var page = new FollowPage();
        var window = new Window { Content=page, Width=1100, Height=780, ShowInTaskbar=false };
        window.Show(); Pump();
        for (int i=0;i<300;i++) vm.Dynamics.Add(new DynamicItem { Id=$"fixture{i}", Kind=DynamicKind.Text, AuthorName="示例 UP", Text="混合动态时间线 · 正文示例", PubTimeText="刚刚" });
        page.DataContext = vm; Pump();
        var list = (ListBox)page.FindName("DynamicList"); list.UpdateLayout();
        int realized = Enumerable.Range(0,vm.Dynamics.Count).Count(i=>list.ItemContainerGenerator.ContainerFromIndex(i)!=null);
        check(realized > 0 && realized < 40, $"virtualized containers: {realized}/{vm.Dynamics.Count}");
        var card = new DynamicFeedCard { DataContext=draw, Width=640 };
        window.Content=card; Pump();
        var texts = Descendants<TextBlock>(card).Select(t=>t.Text).ToList();
        check(texts.Contains("今天的记录") && texts.Contains("热评测试"), "rendered title and hot comment");
        check(!texts.Any(t=>t=="BiliDesk.Models.DynamicItem"), "no fallback type name");

        // ============ 2026-11-04 新增: 投票 / 附加卡片 / 富文本节点 ============
        // 这三块此前完全没解析 —— 投票动态只剩一句正文、活动卡只剩标题、
        // 话题与 @ 是死字。下面每条都钉住"真实接口形状 → 模型字段"的映射。

        // ---- 视频卡的三类角标必须**真的落在卡片内**(2026-11-04) ----
        // 起因: 我一开始靠"看截图"判断角标有没有显示, 而模型读图能力有限, 连续误判两次
        // (一次说封面被裁、一次说时长没显示, 实际都是好的)。
        // 改成量几何: 元素可见 + 尺寸>0 + 包围盒落在卡片内。这才是可断言的判据。
        var geoItem = new DynamicItem
        {
            Id = "geo", Kind = DynamicKind.Video, AuthorName = "几何 UP", PubAction = "投稿视频",
            VideoAspect = 16.0 / 9.0,
            Video = new VideoItem
            {
                Bvid = "BV1geotest01", Title = "几何验证用视频", Cover = "",
                Duration = "13:13", ViewCount = 2063, DanmakuCount = 22,
            },
        };
        var geoCard = new DynamicFeedCard { DataContext = geoItem };
        window.Content = geoCard; Pump();
        geoCard.Measure(new Size(760, double.PositiveInfinity));
        geoCard.Arrange(new Rect(new Point(), geoCard.DesiredSize)); geoCard.UpdateLayout();

        var aspectPanel = Descendants<BiliDesk.Views.Controls.AspectPanel>(geoCard).FirstOrDefault();
        check(aspectPanel != null, "video card uses AspectPanel for the cover");
        check(aspectPanel!.ActualHeight > 100 && Math.Abs(aspectPanel.ActualWidth / aspectPanel.ActualHeight - 16.0/9.0) < 0.05,
            $"cover keeps 16:9 (actual {aspectPanel.ActualWidth:0}x{aspectPanel.ActualHeight:0})");

        bool InsideCard(FrameworkElement? el, out string detail)
        {
            if (el == null) { detail = "missing"; return false; }
            if (el.Visibility != Visibility.Visible || el.ActualWidth <= 0) { detail = "hidden/zero"; return false; }
            var box = el.TransformToAncestor(geoCard).TransformBounds(
                new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            var inside = box.X >= -1 && box.Y >= -1 &&
                         box.Right <= geoCard.ActualWidth + 1 && box.Bottom <= geoCard.ActualHeight + 1;
            detail = $"({box.X:0},{box.Y:0}) {el.ActualWidth:0}x{el.ActualHeight:0}";
            return inside;
        }
        var durEl = Descendants<TextBlock>(geoCard).FirstOrDefault(t => t.Text == "13:13");
        check(InsideCard(durEl, out var durDetail), $"duration badge sits inside the card {durDetail}");
        var viewEl = Descendants<TextBlock>(geoCard).FirstOrDefault(t => t.Text == geoItem.ViewCountText);
        check(InsideCard(viewEl, out var viewDetail), $"view count badge sits inside the card {viewDetail}");
        var actionEl = Descendants<TextBlock>(geoCard).FirstOrDefault(t => t.Text == "投稿视频");
        check(InsideCard(actionEl, out var actionDetail), $"pub-action badge sits inside the card {actionDetail}");

        var summaryOnly = Parse(OpusSummaryOnly);
        check(summaryOnly.Text.Length > 20, $"body read from opus.summary ({summaryOnly.Text.Length} chars)");
        check(summaryOnly.Text.StartsWith("主要是汇总了更新内容"), "opus summary keeps the real text");
        check(summaryOnly.RichNodes.Count == 2, "opus summary rich nodes parsed");
        check(summaryOnly.HasText, "summary-only body counts as having text");
        check(summaryOnly.ContentTitle == "这是标题", "opus title still parsed separately");
        check(summaryOnly.Pictures.Count == 1, "opus pics still parsed");
        // 视频动态本来就没有正文 —— HasText=false 是正确状态, 不能被误判成"解析失败"
        var videoOnly = Parse(VideoNoText);
        check(!videoOnly.HasText && videoOnly.Video != null, "video dynamic has no body by design");
        check(videoOnly.Video!.Title == "视频标题", "video dynamic still shows its title");

        // ---- 投票: 节点位置、字段语义、缺 options ----
        var voteItem = Parse(Vote);
        check(voteItem.Vote is {Id:"22043414"}, "vote found under additional.vote (not major.vote)");
        check(voteItem.Vote!.Title.Length>0 && voteItem.Vote.TotalCount==167, "vote title and join_num parsed");
        check(voteItem.Vote.MaxChoices==1 && !voteItem.Vote.IsMulti, "vote choice_cnt means single select");
        check(!voteItem.Vote.HasOptions && !voteItem.Vote.OptionsLoaded, "card node carries no options");
        check(voteItem.Vote.Status=="1" && !voteItem.Vote.IsClosed && voteItem.Vote.CanVote, "vote status 1 means open");
        check(voteItem.Vote.TotalText=="167 人参与", "vote total text prefers join_num");
        var closedItem = Parse(VoteClosed);
        check(closedItem.Vote!.IsClosed && !closedItem.Vote.CanVote, "vote status 4 means closed");
        check(voteItem.HasAttachedCard && voteItem.HasVote, "vote counts as attached content");

        // ---- 投票详情: /x/vote/vote_info 的 opt_desc / opt_idx 从 1 开始 ----
        var voteInfo = ParseVoteInfo(VoteInfo);
        check(voteInfo.Options.Count==4, "vote_info options parsed");
        check(voteInfo.Options[0].Index==1 && voteInfo.Options[0].Text=="早上！6-9点", "opt_idx starts at 1 and opt_desc is the label");
        check(voteInfo.Options[0].Count==13, "opt cnt parsed");
        var sum = voteInfo.Options.Sum(o=>o.Count);
        check(sum==167, $"option counts sum to join_num ({sum})");
        check(voteInfo.Options[2].Fraction>0.4 && voteInfo.Options[2].Fraction<0.43, "percent fraction computed from option sum");

        // ---- 投票渲染: 选项按 vote_info 拉回来之后才能渲染 ----
        var voteCard = new DynamicFeedCard { DataContext=voteItem };
        window.Content=voteCard; Pump();
        // 卡片节点没有 options, 所以此刻应显示"正在读取投票选项…"而不是选项按钮
        check(Descendants<Button>(voteCard).Count(b=>b.DataContext is DynamicVoteOption)==0, "no options rendered before vote_info arrives");

        // ---- 附加卡片 ----
        var commonItem = Parse(Common);
        check(commonItem.Common is {Title:"活动标题",Badge:"活动",ButtonText:"去看看"}, "common card fields parsed");
        check(commonItem.Common!.HasCover && commonItem.Common.HasContent, "common card usable");
        check(commonItem.HasAttachedCard, "common card counts as attached content");
        var barecommon = Parse(Common.Replace("\"cover\":\"https://example.invalid/c.png\",",""));
        check(barecommon.Common is {HasCover:false,HasContent:true}, "common card without cover still renders");

        // 新结构 additional.common: id_str / desc1+desc2 / sub_type / head_text / button.jump_style.text
        var additionalCommon = Parse(AdditionalCommon);
        check(additionalCommon.HasCommon, "common card also read from additional node");
        check(additionalCommon.Common is {Title:"洛克王国：世界",Id:"107079"}, "additional common uses id_str");
        check(additionalCommon.Common!.Desc=="角色扮演/宠物/回合制 · 现已正式上线", "additional common joins desc1 and desc2");
        check(additionalCommon.Common.ButtonText=="下载游戏", "additional common reads button.jump_style.text");
        check(additionalCommon.Common.SourceName=="相关游戏", "additional common reads head_text");
        check(additionalCommon.Common.IsGame, "additional common keeps sub_type");

        // ---- 话题: 权威来源是 module_dynamic.topic, rich 节点 rid 为空 ----
        var topicItem = Parse(Topic);
        check(topicItem.TopicId=="492306" && topicItem.TopicName=="phigros投票", "topic node gives real topic id");
        check(topicItem.HasTopic, "topic counts as present");
        check(topicItem.RichNodes.Count==1 && topicItem.RichNodes[0].IsTopic, "rich topic node still parsed");
        check(topicItem.RichNodes[0].Rid=="", "rich topic node carries no rid (matches probe)");

        // ---- 富文本节点 ----
        var rich = Parse(Rich);
        check(rich.RichNodes.Count==5, "rich text nodes parsed");
        check(rich.RichNodes.Count(n=>n.IsTopic)==1 && rich.RichNodes.First(n=>n.IsTopic).Rid=="9001", "topic node keeps rid");
        check(rich.RichNodes.Count(n=>n.IsMention)==1 && rich.RichNodes.First(n=>n.IsMention).Rid=="42", "mention node keeps mid");
        check(rich.RichNodes.First(n=>n.IsEmoji).Emoji.Contains("emoji"), "emoji node keeps icon url");
        check(rich.RichNodes.Count(n=>n.Type=="RICH_TEXT_NODE_TYPE_TEXT")==2, "plain text nodes kept in order");

        // ---- 附加卡片渲染 ----
        var commonCard = new DynamicFeedCard { DataContext=commonItem };
        window.Content=commonCard; Pump();
        var commonTexts=Descendants<TextBlock>(commonCard).Select(t=>t.Text).ToList();
        check(commonTexts.Contains("活动标题") && commonTexts.Contains("去看看"), "common card rendered");

        // ---- 富文本: 话题与 @ 必须变成 Hyperlink(可点), 而不是整段纯文本 ----
        var richCard = new DynamicFeedCard { DataContext=rich };
        window.Content=richCard; Pump();
        var richBlock=Descendants<DynamicRichText>(richCard).FirstOrDefault();
        var links=richBlock==null?0:richBlock.Inlines.OfType<System.Windows.Documents.Hyperlink>().Count();
        check(links==2, $"topic and mention render as clickable links ({links})");
        card.Measure(new Size(640,double.PositiveInfinity));
        card.Arrange(new Rect(new Point(),card.DesiredSize)); card.UpdateLayout();
        var bitmap = new RenderTargetBitmap(640,(int)Math.Ceiling(card.DesiredSize.Height),96,96,PixelFormats.Pbgra32); bitmap.Render(card);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(Path.Combine(output,"dynamic-card.png"))) encoder.Save(stream);
        foreach (var count in new[]{1,2,4,9})
        {
            var imageItem = new DynamicItem { Id="images", Kind=DynamicKind.ImageText, AuthorName="image fixture" };
            for (int i=0;i<count;i++) imageItem.Pictures.Add(new DynamicPicture());
            window.Content = new DynamicFeedCard { DataContext=imageItem }; Pump();
            check(Descendants<DynamicPicturePanel>(window).Any(), $"render picture panel {count}");
        }
        // Exercise the native page with a full detail response and real WPF templates.
        var detail=new DynamicDetailPage(draw);
        window.Content=detail;Pump();Wait(detail.ViewModel.InitializeAsync());
        while(detail.ViewModel.Loading)Pump();
        check(detail.ViewModel.Item.CommentOid=="7001", "native detail keeps server comment target");
        check(detail.ViewModel.Comments.Count==1, "native comments rendered");
        check(!Descendants<Microsoft.Web.WebView2.Wpf.WebView2>(detail).Any(), "dynamic detail has no WebView");
        var comment=detail.ViewModel.Comments[0];
        Wait(detail.ViewModel.LoadRepliesAsync(comment));
        check(comment.Replies.Count==2 && !comment.HasMoreReplies, "native replies expand and finish");
        Svc.Session.Current.BiliJct="test-csrf";
        typeof(ApiClient).GetField("_writeReady",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api,true);
        detail.ViewModel.ComposeReply(comment);detail.ViewModel.Draft="native reply";
        handler.FailPost=true;Wait(detail.ViewModel.SendAsync());
        check(detail.ViewModel.Draft=="native reply" && detail.ViewModel.Error.Length>0,"failed send preserves draft");
        handler.FailPost=false;Wait(detail.ViewModel.SendAsync());
        check(detail.ViewModel.Draft.Length==0 && handler.Forms.Any(f=>f.Contains("type=11") && f.Contains("root=8001")),"native reply targets correct comment type and root");
        detail.ViewModel.ComposeRepost();detail.ViewModel.Draft="native repost";Wait(detail.ViewModel.SendAsync());
        check(handler.Forms.Any(f=>f.Contains("dynamic_id=9001") && f.Contains("content=native+repost")),"native repost form");
        Wait(detail.ViewModel.LikeCommentAsync(comment));
        check(comment.IsLiked && handler.Forms.Any(f=>f.Contains("rpid=8001") && f.Contains("type=11")),"native comment like type");
        foreach(var mode in new[]{AppTheme.Light,AppTheme.Dark})
        {
            Application.Current.Resources.MergedDictionaries[0]=new ResourceDictionary {Source=new Uri($"/BiliDesk;component/Themes/Colors.{mode}.xaml",UriKind.Relative)};Pump();
            var shot=new RenderTargetBitmap((int)detail.ActualWidth,(int)detail.ActualHeight,96,96,PixelFormats.Pbgra32);shot.Render(detail);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(shot));
            using(var stream=File.Create(Path.Combine(output,$"dynamic-detail-{mode}.png")))png.Save(stream);
        }
        var home=new HomeViewModel {IsPopularTab=true,IsRecommendTab=false};
        typeof(HomeViewModel).GetField("_popularPage",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(home,1);
        handler.FailPopular=true;Wait(home.LoadMoreAsync());
        check((int)typeof(HomeViewModel).GetField("_popularPage",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(home)! == 1,"home failed page retains cursor");
        handler.FailPopular=false;Wait(home.LoadMoreAsync());
        check(handler.Paths.Last().Contains("pn=2") && home.PopularItems.Count==1,"home retries same page successfully");
        var gallery=new BiliDesk.Views.Windows.ImagePreviewWindow();gallery.Show();
        Wait(gallery.LoadGalleryAsync(new[]{"",""}));
        check(((TextBlock)gallery.FindName("GalleryIndex")).Text=="1 / 2","gallery starts at selected image");
        ((Button)gallery.FindName("NextImage")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        check(((TextBlock)gallery.FindName("GalleryIndex")).Text=="2 / 2" && !((Button)gallery.FindName("NextImage")).IsEnabled,"gallery next and end boundary");
        gallery.Close();
        var switchingHome=new HomeViewModel();switchingHome.RecommendItems.Add(new VideoItem {Bvid="cached"});
        handler.FailPopular=true;handler.DelayPopular=true;
        var slowPopular=switchingHome.SwitchToPopularAsync();Wait(switchingHome.SwitchToRecommendAsync());Wait(slowPopular);
        check(switchingHome.IsRecommendTab && !switchingHome.Loading && switchingHome.Error.Length==0,"inactive home request cannot overwrite active state");
        handler.FailPopular=false;handler.DelayPopular=false;
        var wallItems=new System.Collections.ObjectModel.ObservableCollection<VideoItem>(Enumerable.Range(0,600).Select(i=>new VideoItem {Bvid=$"BV{i:D10}",Title=$"首页虚拟化验证 {i} · 测试较长的标题文字与换行布局",Author="示例 UP"}));
        var wallRoot=new FrameworkElementFactory(typeof(BiliDesk.Helpers.VirtualizingCardWallPanel));
        wallRoot.SetValue(BiliDesk.Helpers.CardWall.EnableProperty,true);
        var wallList=new ItemsControl {ItemsSource=wallItems,ItemsPanel=new ItemsPanelTemplate(wallRoot),ItemTemplate=new DataTemplate {VisualTree=new FrameworkElementFactory(typeof(VideoCard))}};
        var content=new StackPanel();content.Children.Add(new TextBlock {Text="首页测试标题",Height=50});content.Children.Add(wallList);content.Children.Add(new Border {Height=250});
        var wallScroll=new ScrollViewer {Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        window.Content=wallScroll;Pump();Pump();
        var wall=Descendants<BiliDesk.Helpers.VirtualizingCardWallPanel>(window).First();
        check(wall.RealizedCount>0 && wall.RealizedCount<60,$"home viewport containers {wall.RealizedCount}/600");
        wallScroll.ScrollToVerticalOffset(wallScroll.ScrollableHeight/2);Pump();Pump();
        var visibleItems=Descendants<VideoCard>(wall).Select(c=>(VideoItem)c.DataContext).ToList();
        check(visibleItems.Count>0 && visibleItems.All(c=>int.Parse(c.Bvid[2..])>200),"home middle scroll realizes correct rows");
        check(wall.RealizedCount<60,"home long scroll remains bounded");
        var anchor=visibleItems[visibleItems.Count/2];window.Width=850;Pump();Pump();
        check(Descendants<VideoCard>(wall).Any(c=>ReferenceEquals(c.DataContext,anchor)),"home resize retains viewport anchor");
        wallScroll.ScrollToBottom();Pump();Pump();
        Console.WriteLine($"HOME bottom offset={wallScroll.VerticalOffset:0} extent={wallScroll.ExtentHeight:0} realized={string.Join(',',Descendants<VideoCard>(wall).Select(c=>((VideoItem)c.DataContext).Bvid))}");
        check(Descendants<VideoCard>(wall).Any(c=>ReferenceEquals(c.DataContext,wallItems.Last())),"home last item reachable with footer");
        var generatedCount=Enumerable.Range(0,wallItems.Count).Count(i=>wallList.ItemContainerGenerator.ContainerFromIndex(i)!=null);
        check(generatedCount<60,$"home generator retains {generatedCount}/600 containers after long scroll");
        wallItems.Clear();for(int i=0;i<12;i++)wallItems.Add(new VideoItem {Bvid=$"BV{i:D10}",Title="筛选后的内容"});Pump();Pump();
        check(wall.RealizedCount<=12 && wall.RealizedCount>0,"home collection reset clamps viewport");
        BiliDesk.Helpers.CardWall.SetFixedColumns(2);Pump();Pump();
        check(BiliDesk.Helpers.CardWall.GetColumns(wall)==2,"home honors fixed column setting");
        BiliDesk.Helpers.CardWall.SetFixedColumns(0);Pump();Pump();
        wallItems.RemoveAt(0);Pump();Pump();
        check(Descendants<VideoCard>(wall).All(c=>wallItems.Contains((VideoItem)c.DataContext)),"home removal does not retain stale cards");
        var blankCover=new Border {Background=Brushes.Red};BiliDesk.Helpers.Cover.SetSource(blankCover," ");
        check(blankCover.Background==null,"recycled empty cover clears previous image");
        var mine=new MinePage {SectionFactory=key=>new System.Windows.Controls.TextBox {Text=key.ToString()}};
        window.Content=mine;Pump();
        check(mine.CurrentTabIndex==0,"mine starts on overview");
        mine.SelectSection(PageKey.History);Pump();
        var historySection=((Grid)mine.FindName("SectionHost")).Children.OfType<FrameworkElement>().First(x=>x.IsVisible);
        mine.SwipeDown(new Point(500,300),mine);mine.SwipeMove(new Point(150,305));mine.SwipeUp();for(int i=0;i<5;i++)Pump();
        check(mine.CurrentTabIndex==2,"mine horizontal gesture advances one section");
        mine.SwipeDown(new Point(400,300),mine);mine.SwipeMove(new Point(402,550));mine.SwipeUp();Pump();
        check(mine.CurrentTabIndex==2,"mine vertical gesture does not change section");
        mine.HandleHorizontalWheel(-1);for(int i=0;i<5;i++)Pump();
        check(mine.CurrentTabIndex==1 && ReferenceEquals(historySection,((Grid)mine.FindName("SectionHost")).Children.OfType<FrameworkElement>().First(x=>x.IsVisible)),"mine restores cached section");
        mine.SelectSection(PageKey.Mine);mine.SwipeDown(new Point(100,300),mine);mine.SwipeMove(new Point(600,300));mine.SwipeUp();for(int i=0;i<5;i++)Pump();
        check(mine.CurrentTabIndex==0,"mine edge swipe stays in range");
        // Mixed, variable-height timeline: the original plain-text-only fixture missed the freeze.
        var mixedPage=new FollowPage();window.Content=mixedPage;Pump();
        var mixedVm=new FollowViewModel();mixedVm.Users.Add(new FollowUser {Mid=42,Name="UP"});
        for(int i=0;i<100;i++)
        {
            var d=new DynamicItem {Id=$"mixed-{i}",Kind=i%2==0?DynamicKind.ImageText:DynamicKind.Text,Text=new string('字',i%5*100+10),AuthorName="混合流"};
            if(d.Kind==DynamicKind.ImageText)for(int k=0;k<i%9+1;k++)d.Pictures.Add(new DynamicPicture {Width=1600,Height=k%2==0?900:2400});
            mixedVm.Dynamics.Add(d);
        }
        var mount=System.Diagnostics.Stopwatch.StartNew();mixedPage.DataContext=mixedVm;Pump();Pump();
        check(mount.ElapsedMilliseconds<3000,"mixed timeline mounting completes without layout loop");
        var mixedList=(ListBox)mixedPage.FindName("DynamicList");
        for(int i=0;i<100;i+=7){mixedList.ScrollIntoView(mixedVm.Dynamics[i]);Pump();}
        check(Enumerable.Range(0,100).Count(i=>mixedList.ItemContainerGenerator.ContainerFromIndex(i)!=null)<30,"mixed timeline scroll remains virtualized");
        var nav=new NavigationDispatcher(window);Svc.Navigate=nav;
        DynamicActions.OpenDetail(draw);Pump();
        check(Application.Current.Windows.OfType<Window>().Any(w=>w.Content is DynamicDetailPage),"card routes to native dynamic page");
        foreach(var w in Application.Current.Windows.OfType<Window>().Where(w=>w!=window).ToList())w.Close();
        window.Close(); Svc.Session.Current.SessData="";
    }
    static IEnumerable<T> Descendants<T>(DependencyObject obj) where T:DependencyObject
    { for(int i=0;i<VisualTreeHelper.GetChildrenCount(obj);i++) { var child=VisualTreeHelper.GetChild(obj,i); if(child is T match) yield return match; foreach(var nested in Descendants<T>(child)) yield return nested; } }
    static void Wait(Task task) { var limit=DateTime.UtcNow.AddSeconds(10); while(!task.IsCompleted && DateTime.UtcNow<limit) Pump(); task.GetAwaiter().GetResult(); }
    static void Pump() { var frame=new DispatcherFrame(); var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)}; timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;}; timer.Start();Dispatcher.PushFrame(frame); }
    sealed class FakeHandler:HttpMessageHandler
    {
        public List<string> Paths {get;}=new();
        public List<string> Forms {get;}=new();
        public bool FailPost {get;set;}
        public bool FailPopular {get;set;}
        public bool DelayPopular {get;set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var url=request.RequestUri!.ToString(); Paths.Add(url);
            if(url.Contains("/popular?") && DelayPopular)await Task.Delay(200,token);
            if(url.Contains("host_mid=10")) await Task.Delay(200,token);
            var id=url.Contains("host_mid=20")?"20":"10";
            string json;
            if(request.Method==HttpMethod.Post)
            {
                Forms.Add(await request.Content!.ReadAsStringAsync(token));
                json=FailPost?"{\"code\":-400,\"message\":\"test failure\"}":"{\"code\":0}";
            }
            else if(url.Contains("/popular?")) json=FailPopular?"{\"code\":-400,\"message\":\"failed\"}":"""{"code":0,"data":{"list":[{"bvid":"BV1234567890","title":"home test","owner":{"mid":1,"name":"UP"}}]}}""";
            else if(url.Contains("/detail?"))json="{\"code\":0,\"data\":{\"item\":"+Draw+"}}";
            else if(url.Contains("/reply/reply?"))json="""{"code":0,"data":{"replies":[{"rpid":8101,"content":{"message":"reply one"},"member":{"uname":"first"}},{"rpid":8102,"content":{"message":"reply two"},"member":{"uname":"second"}}]}}""";
            else if(url.Contains("/reply?"))json="""{"code":0,"data":{"page":{"count":1},"replies":[{"rpid":8001,"rcount":2,"content":{"message":"这是原生评论区，可选择文字、回复和点赞。"},"member":{"uname":"viewer"},"like":5}]}}""";
            else json="""{"code":0,"data":{"has_more":false,"offset":"terminal","items":[{"id_str":"PLACEHOLDER","type":"DYNAMIC_TYPE_WORD","text":"fixture"}]}}""".Replace("PLACEHOLDER",id);
            return new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(json)};
        }
    }
}
