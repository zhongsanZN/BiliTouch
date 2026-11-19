using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliTouch.Helpers;
using BiliTouch.Models;

namespace BiliTouch.Services;

public partial class ApiClient
{
    /// <summary>
    /// 短视频的"本地合流"时长阈值(毫秒)。**只对极短片**(≤60 秒)生效。
    ///
    /// 为什么短视频要特殊处理: B 站的 DASH 把画面和声音分成两个文件, 播放器只能用
    /// LibVLC 的 `input-slave` 把音轨挂上去 —— 那是**两个各自独立的输入**(各自解复用、
    /// 各自时钟、各自 EOF)。实测(2026-09-25, 同一个 76.5 秒视频跳到 60s):
    ///   · 画面恢复要 1383ms(单个本地文件只要 470ms);
    ///   · 音轨先 EOF 会把**整个 input** 提前结束, 结尾固定少 1911ms(没声音 + 进度条提前满格);
    ///   · 而且这与"音轨在本地还是远端"无关(把音轨换成本地文件, 结尾照样少 1911ms),
    ///     是这个机制的固有缺陷。
    /// 但"下完再播"本身会**把起播变成下载**: 3 分钟 1080P 约 20MB, 慢线路要十几秒,
    /// 用户看到的就是"点开一直转圈"(2026-10-30 用户报"视频加载慢/卡住")。
    /// ⇒ 只保留最划算的那一档: ≤60 秒且 ≤8MB(见 ShortVideoCache.WorthCaching)才预下,
    ///   1~2 秒就能下完; 超过就**直接远端双流起播**, 不为"跳转更准"牺牲起播速度。
    /// </summary>
    private const long ShortVideoRemuxThresholdMs = 60_000;


    /// <summary>获取视频播放地址。
    /// 优先 DASH 流(最高可用清晰度), 视频/音频分离——播放器用 input-slave 合并音轨;
    /// DASH 完全不可用时回退 durl 单流。
    /// 返回 (ok, err, 主 url、备选 urls、时长、可用清晰度列表、实际返回的清晰度、
    /// 音轨 url(仅 DASH)、以及**短视频的本地合流规格**(非 null 表示播放器可以去下它并
    /// 合成本地文件, 见 ShortVideoCache))。
    ///
    /// ★ 2026-10-01: 前面补上了 `ok`/`err`。以前这个元组里**一个能说明成败的字段都没有**,
    ///   调用方只能靠 `url == null` 猜, 于是播放器在失败时只能弹一句"该清晰度暂时不可用" ——
    ///   而真正的理由(网络失败 / 大会员专属 / 该清晰度无 DASH 也无 durl)在这一层就被丢掉了。
    ///   现在与本类其它方法统一为 `(bool ok, string? err, ...)` 开头, 失败原因能直接透给用户。
    /// </summary>
    public async Task<(bool ok, string? err, string? url, List<string> backups, long durationMs,
        List<(int qn, string label)> qualities, int actualQn, string? audioUrl, DashStreams? remux,
        List<AudioTrack> audioTracks, AudioQuality actualAudio)>
        GetPlayUrlAsync(string bvid, long cid, int qn = 80, AudioQuality audioQuality = AudioQuality.Auto)
    {
        var qualities = new List<(int qn, string label)>();
        // 音轨结果在所有分支上都要有值(调用方解构时不能少项)
        var audioTracksOut = new List<AudioTrack>();
        try
        {
            await EnsureBuvidAsync();

            // ★★★ 2026-10-17 播放器手术第 1 步: durl 合流单流**按需**优先(仅 ≤720P)。
            //
            //   背景(全部实测, 探针 bd-probe-muxed / muxed2 / muxed5):
            //   DASH 双流要用 `:input-slave` 把音轨挂到视频流上, 而 libvlc 3.0.20 在
            //   **带 input-slave 的组合**下, 对 frag-MP4 的远距离 seek 会**内部失败**
            //   (cannot seek ⇒ 位置掉回开头), 这就是"拖过去又跳回去、画面卡死"的根因。
            //   durl(fnval=1) 是**音视频已合流的单个 MP4**, 不需要 input-slave; 配
            //   `:demux=avformat` 后实测来回 7 跳全部准确(偏差 0.9~2.2s)且画面持续推进。
            //
            //   ★ 代价: durl **封顶 720P**(实测带不带 ApplyTryLook 都一样, bd-probe-muxed3),
            //     而 DASH 能到 1080P ⇒ 只有用户真的要 ≤720P 时才值得走 durl。
            //
            // ★★★ 两个必须避开的坑(都实测踩过, 用户报"几乎所有视频都是 720p"):
            //   ① **"无条件先试 durl"是错的**: 默认档 1080P 时 durl 只返回 [64,16],
            //      `actualQn` 变 64, 而播放器用 actualQn 覆盖 `_currentQn`
            //      (见 FillQualityMenu) ⇒ 之后每条视频都被钉死在 720P;
            //      画质菜单也被换成 durl 的列表 ⇒ **1080P 那档直接消失**, 用户再也选不回去。
            //   ② **AutoQn(=0) 不是最低画质, 而是"自动(最高可用)"**(见 QualityPreference,
            //      内部换算成 127)。所以判"是否 ≤720P"必须**先排除它** ——
            //      直接写 `qn <= 64` 会把 0 也吃进去, 于是用户默认就落进 durl 那条路。
            var isAuto = qn == QualityPreference.AutoQn;
            if (!isAuto && qn <= 64 && audioQuality == AudioQuality.Standard)
            {
                var (mUrl, mBackups, mQn, mQualities, durMux) = await FetchMuxedAsync(bvid, cid, qn);
                if (!string.IsNullOrEmpty(mUrl))
                {
                    if (mQualities.Count > 0) qualities = mQualities;
                    return (true, null, mUrl, mBackups, durMux, qualities, mQn, null, null, new List<AudioTrack>(), AudioQuality.Auto);
                }
                // durl 取不到(多段切片/大会员/该档无源) → 落到下面的 DASH, 行为与手术前一致
            }

            // ---- DASH: fnval=4048 = 全特性位 ----
            // 16=DASH 64=HDR 128=4K 256=杜比音频 512=杜比视界 1024=8K 2048=AV1
            // ★ 2026-11-04: 原来是 fnval=16, 只开 DASH —— 于是**杜比音轨和 Hi-Res 根本不会下发**,
            //   前端再怎么解析也拿不到。改成 4048 全开, 服务端才会把 dash.dolby / dash.flac 填上
            //   (当然最终还得看账号权限与 UP 是否上传)。
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "4048",
                ["fnver"] = "0",
                ["fourk"] = "1"
            };
            ApplyTryLook(ps);
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code == 0 && data != null &&
                data.Value.TryGetProperty("dash", out var dash) && dash.ValueKind == JsonValueKind.Object)
            {
                long dur = data.Value.TryGetProperty("timelength", out var tl) && tl.ValueKind == JsonValueKind.Number
                    ? tl.GetInt64() : 0;

                string? videoUrl = null, audioUrl = null;
                var backups = new List<string>();
                int chosenQn = 0;
                // 短视频做本地合流要用到这几项(码率用于估算体积, 其余写进容器)
                JsonElement? chosen = null;
                JsonElement? chosenAudio = null;

                if (dash.TryGetProperty("video", out var videos) && videos.ValueKind == JsonValueKind.Array)
                {
                    // 收集可用清晰度(按 id 去重, 从高到低)
                    var seen = new HashSet<int>();
                    foreach (var v in videos.EnumerateArray())
                    {
                        var id = GetInt(v, "id");
                        if (id > 0 && seen.Add(id))
                            qualities.Add((id, QnToLabel(id)));
                    }
                    qualities.Sort((a, b) => b.qn.CompareTo(a.qn));

                    // 选流: 不超过请求 qn 的最高清晰度; **同档优先 HEVC**。
                    //
                    // ★ 为什么同档优先 hevc 而不是 avc(2026-09-28 实测, 见下):
                    //   同一个视频(限免 4K 样片 BV1NGZtBwELa)同一档清晰度, 服务端同时给
                    //   avc 与 hevc 两条流, 声明码率差一倍:
                    //       id=120(4K)    avc 12.60Mbps   hevc 7.03Mbps   (hevc 省 44%)
                    //       id=112(1080P+) avc  4.17Mbps   hevc 2.63Mbps   (省 37%)
                    //       id=80 (1080P)  avc  2.67Mbps   hevc 1.34Mbps   (省 50%)
                    //   同画质(同一档 qn)下 hevc 只要一半左右的数据量。原来"同档优先 avc"等于
                    //   每次起播/跳转都要多搬 40%~100% 的字节 —— 4K 档最明显, 这正是
                    //   "缓冲特别久"的主因之一。硬解(VLC 默认 avcodec-hw=any)对 hevc 与 avc
                    //   同样支持, 所以这不是"拿兼容性换速度"。
                    //   av01(若有)排在最后: 体积更小但硬解支持面窄, 不主动选。
                    JsonElement best = default; int bestId = 0; int bestRank = -1;
                    foreach (var v in videos.EnumerateArray())
                    {
                        var id = GetInt(v, "id");
                        var codec = GetStr(v, "codecs");
                        if (id <= 0 || id > qn) continue;
                        var rank = CodecRank(codec);
                        if (bestId == 0 || id > bestId || (id == bestId && rank > bestRank))
                        {
                            best = v; bestId = id; bestRank = rank;
                        }
                    }
                    // 请求的 qn 不可用时取最高档(同档仍按上面的编码优先级挑)
                    if (bestId == 0 && videos.GetArrayLength() > 0)
                    {
                        foreach (var v in videos.EnumerateArray())
                        {
                            var id = GetInt(v, "id");
                            var rank = CodecRank(GetStr(v, "codecs"));
                            if (id > bestId || (id == bestId && rank > bestRank))
                            {
                                best = v; bestId = id; bestRank = rank;
                            }
                        }
                    }
                    if (bestId > 0)
                    {
                        // ★ 线路选择交给 CdnService(2026-10-03 新增, 设置页可配)。
                        //   它负责三件事: 按用户策略(自动测速/手动指定/跟随服务端)选线路、
                        //   剔除 PCDN、全部失败时回退。详细依据见 Services/CdnService.cs。
                        //
                        //   以前这里只有一条静态规则"优先非 mcdn", 问题是:
                        //     · 无法利用"各家 CDN 同一时刻吞吐差 4 倍"这件事(实测 2.5~11MB/s);
                        //     · 认不出**伪装域名** —— 实测 B 站给过
                        //       `mv0bz14m.edge.mountaintoys.cn`(query 里 os=mcdn), 域名看不出
                        //       PCDN 特征, 旧规则会把它当成正常线路用。
                        var cands = new List<string>();
                        var baseUrl = GetStr(best, "baseUrl");
                        if(baseUrl.Length==0)baseUrl=GetStr(best,"base_url");
                        if (baseUrl.Length > 0) cands.Add(baseUrl);
                        if ((best.TryGetProperty("backupUrl", out var bu) || best.TryGetProperty("backup_url",out bu)) && bu.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var u in bu.EnumerateArray())
                            {
                                var s = u.GetString();
                                if (!string.IsNullOrEmpty(s)) cands.Add(s);
                            }
                        }
                        if (cands.Count > 0)
                        {
                            var picked = await CdnService.Instance.SelectUrlAsync(cands[0], cands);
                            videoUrl = picked;
                            // 备用线路 = 其余候选(去重、且不包含已选中的那条)
                            foreach (var c in cands)
                                if (!string.Equals(c, picked, StringComparison.Ordinal) && !backups.Contains(c))
                                    backups.Add(c);
                            chosenQn = bestId;
                            chosen = best;
                        }
                    }
                }

                // ---- 音轨: 三处都要看(2026-11-04) ----
                //   dash.audio[]       AAC(30216/30232/30280)
                //   dash.flac.audio[]  Hi-Res 无损(30251) —— 大会员且 UP 上传了才有
                //   dash.dolby.audio[] 杜比全景声(30250) —— 大会员
                // ★ 原来只读第一处并按带宽取最高, 于是 Hi-Res 永远拿不到。
                //   现在把三处全收进 audioTracks, 再按用户选的档位挑一条。
                var dashAudioTracks = new List<AudioTrack>();
                var dashActualAudio = AudioQuality.Auto;
                // AudioTrack → 原始 JsonElement: 短视频合流要用码率/编码写进容器
                var audioElements = new Dictionary<AudioTrack, JsonElement>();
                void AddAudioCandidates(JsonElement a, AudioQuality q, string label, int fallbackTrackId = 0)
                {
                    if(a.ValueKind!=JsonValueKind.Object)return;
                    var acands = new List<string>();
                    var abase = GetStr(a, "baseUrl");
                    if(abase.Length==0)abase=GetStr(a,"base_url");
                    if (abase.Length > 0) acands.Add(abase);
                    if ((a.TryGetProperty("backupUrl", out var abu) || a.TryGetProperty("backup_url",out abu)) && abu.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var u in abu.EnumerateArray())
                        {
                            var s = u.GetString();
                            if (!string.IsNullOrEmpty(s)) acands.Add(s);
                        }
                    }
                    if (acands.Count == 0) return;
                    var rawId = (int)GetLong(a, "id");
                    var track = new AudioTrack
                    {
                        Quality = q,
                        TrackId = rawId > 0 ? rawId : fallbackTrackId,
                        Label = label,
                        Bandwidth = GetLong(a, "bandwidth"),
                        Codecs = GetStr(a, "codecs"),
                        // 音轨复用视频那次测速的结果, 不单独测速(见 SelectSecondaryUrl 的说明)
                        Url = CdnService.Instance.SelectSecondaryUrl(acands[0], acands),
                    };
                    dashAudioTracks.Add(track);
                    audioElements[track] = a.Clone();
                }

                if (dash.TryGetProperty("audio", out var audios) && audios.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in audios.EnumerateArray())
                    {
                        var id = (int)GetLong(a, "id");
                        var q = id switch
                        {
                            30216 => AudioQuality.Standard,
                            30280 => AudioQuality.High,
                            _ => AudioQuality.High,   // 30232 等归到"高品质"一档显示
                        };
                        var label = id switch
                        {
                            30216 => "标准音质",
                            30280 => "高品质",
                            30232 => "较高音质",
                            _ => "音质 " + id,
                        };
                        AddAudioCandidates(a, q, label, id);
                    }
                }

                void AddPremium(string name,AudioQuality quality,string label,int fallbackTrackId)
                {
                    if(!dash.TryGetProperty(name,out var group) || group.ValueKind!=JsonValueKind.Object || !group.TryGetProperty("audio",out var tracks))return;
                    if(tracks.ValueKind==JsonValueKind.Object)AddAudioCandidates(tracks,quality,label,fallbackTrackId);
                    else if(tracks.ValueKind==JsonValueKind.Array)
                        foreach(var track in tracks.EnumerateArray())AddAudioCandidates(track,quality,label,fallbackTrackId);
                }
                AddPremium("flac",AudioQuality.HiRes,"Hi-Res 无损",30251);
                AddPremium("dolby",AudioQuality.Dolby,"杜比全景声",30250);
                // 按码率降序排列，码率相同或为 0 时按 TrackId 降序（30280 > 30232 > 30216）
                dashAudioTracks.Sort((left, right) =>
                {
                    var bw = right.Bandwidth.CompareTo(left.Bandwidth);
                    return bw != 0 ? bw : right.TrackId.CompareTo(left.TrackId);
                });

                AudioTrack? BestStereoAac() =>
                    dashAudioTracks
                        .Where(t => t.Quality is AudioQuality.High or AudioQuality.Standard)
                        .OrderByDescending(t => t.Bandwidth)
                        .ThenByDescending(t => t.TrackId)
                        .FirstOrDefault();

                // 按档位挑一条（杜比采用可选方案，默认不强选杜比下混）：
                //   Auto  → Hi-Res > 最高立体声 AAC（仅当无 AAC 时兜底杜比）
                //   HiRes → Hi-Res > 最高立体声 AAC（不自动落到杜比）
                //   Dolby → 杜比 > Hi-Res > 最高立体声 AAC
                //   High/Standard → 对应立体声 AAC 档
                AudioTrack? pick = null;
                if (audioQuality == AudioQuality.Auto)
                {
                    pick = dashAudioTracks.FirstOrDefault(t => t.Quality == AudioQuality.HiRes)
                        ?? BestStereoAac()
                        ?? dashAudioTracks.FirstOrDefault(t => t.Quality == AudioQuality.Dolby)
                        ?? dashAudioTracks.FirstOrDefault();
                }
                else
                {
                    pick = dashAudioTracks.FirstOrDefault(t => t.Quality == audioQuality);
                    if (pick == null && audioQuality == AudioQuality.Dolby)
                    {
                        pick = dashAudioTracks.FirstOrDefault(t => t.Quality == AudioQuality.HiRes)
                            ?? BestStereoAac();
                    }
                    else if (pick == null && audioQuality == AudioQuality.HiRes)
                    {
                        pick = BestStereoAac();
                    }
                    else if (pick == null && audioQuality is AudioQuality.High or AudioQuality.Standard)
                    {
                        pick = BestStereoAac();
                    }
                    pick ??= BestStereoAac() ?? dashAudioTracks.FirstOrDefault();
                }
                if (pick != null)
                {
                    audioUrl = pick.Url;
                    dashActualAudio = pick.Quality;
                    if (audioElements.TryGetValue(pick, out var el)) chosenAudio = el;
                }

                if (!string.IsNullOrEmpty(videoUrl))
                {
                    // ---- 短视频: 顺带给出"本地合流"所需的规格(播放器后台下载并合成本地文件) ----
                    // 注意这里**不改播放地址**: 起播仍然用远端 DASH(零等待), 合流是后台在做,
                    // 就绪后播放器再切过去。见 ShortVideoRemuxThresholdMs 的说明。
                    DashStreams? remux = null;
                    if (dur > 0 && dur <= ShortVideoRemuxThresholdMs &&
                        !string.IsNullOrEmpty(audioUrl) && chosen != null && chosenAudio != null)
                    {
                        var v = chosen.Value;
                        var au = chosenAudio.Value;
                        remux = new DashStreams
                        {
                            VideoUrl = videoUrl!,
                            AudioUrl = audioUrl!,
                            DurationMs = dur,
                            VideoBandwidth = GetLong(v, "bandwidth"),
                            VideoCodecs = GetStr(v, "codecs"),
                            VideoWidth = GetInt(v, "width"),
                            VideoHeight = GetInt(v, "height"),
                            AudioBandwidth = GetLong(au, "bandwidth"),
                            AudioCodecs = GetStr(au, "codecs"),
                            SelectedAudioQuality = dashActualAudio
                        };
                    }
                    return (true, null, videoUrl, backups, dur, qualities, chosenQn, audioUrl, remux, dashAudioTracks, dashActualAudio);
                }
            }

            // ---- 2. 回退 durl 单流 ----
            // 手术前 durl 只在这条"兜底"路径上被用到; 现在它同时也是 ≤720P 的**主路**。
            // 这里仍然保留兜底: DASH 取不到(付费/大会员/该档无源)时, 单流往往还能救回来。
            var (mUrl2, mBackups2, mQn2, mQualities2, dur2) = await FetchMuxedAsync(bvid, cid, qn);
            if (mUrl2 == null)
                return (false, "DASH 与 durl 都没能取到可播放的地址(可能是大会员/付费专属, 或该清晰度无源)",
                    null, new List<string>(), 0, qualities, 0, null, null,
                    new List<AudioTrack>(), AudioQuality.Auto);
            if (mQualities2.Count > 0) qualities = mQualities2;
            return (true, null, mUrl2, mBackups2, dur2, qualities, mQn2, null, null, new List<AudioTrack>(), AudioQuality.Auto);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取播放地址失败: " + ex.Message,
                null, new List<string>(), 0, qualities, 0, null, null,
                new List<AudioTrack>(), AudioQuality.Auto);
        }
    }

    /// <summary>
    /// 请求 durl(单流)并挑出可直接播放的那一条, 返回 (url, 备用线路, 实际清晰度, 清晰度列表, 时长)。
    ///
    /// 两个必须注意的地方:
    ///   1. **不要带 `platform=html5`**。带上它现在一律返回 `-404 啥都木有`(实测),
    ///      回退分支以前就是因为这个参数常年跑不通。去掉之后 qn=80 在 pc 平台能正常拿到 durl。
    ///   2. durl 可能是**多段**(长视频被切片, 每段带 order)。播放器只会播第一条,
    ///      所以这里要求"必须只有一段"才算可用 —— 宁可让上面回退到 DASH, 也不要给用户
    ///      一条"播到一半就没了"的地址。
    /// </summary>
    private async Task<(string? url, List<string> backups, int qn,
        List<(int qn, string label)> qualities, long durationMs)>
        FetchMuxedAsync(string bvid, long cid, int qn)
    {
        var qualities = new List<(int qn, string label)>();
        var backups = new List<string>();
        try
        {
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "1",
                ["fnver"] = "0",
                ["fourk"] = "1"
            };
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code != 0 || data == null) return (null, backups, 0, qualities, 0);

            long dur = data.Value.TryGetProperty("timelength", out var tl) && tl.ValueKind == JsonValueKind.Number
                ? tl.GetInt64() : 0;

            var segs = new List<string>();
            if (data.Value.TryGetProperty("durl", out var durl) && durl.ValueKind == JsonValueKind.Array)
            {
                foreach (var seg in durl.EnumerateArray())
                {
                    var u = GetStr(seg, "url");
                    if (!string.IsNullOrEmpty(u)) segs.Add(u);
                }
            }
            // 多段没法播(播放器不会自动续下一段), 当作拿不到
            if (segs.Count != 1) return (null, backups, 0, qualities, dur);

            var actualQn = GetInt(data.Value, "quality");
            if (data.Value.TryGetProperty("accept_quality", out var aq) && aq.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in aq.EnumerateArray())
                    if (q.ValueKind == JsonValueKind.Number)
                        qualities.Add((q.GetInt32(), QnToLabel(q.GetInt32())));
            }
            if (qualities.Count == 0 && actualQn > 0)
                qualities.Add((actualQn, QnToLabel(actualQn)));
            qualities.Sort((a, b) => b.qn.CompareTo(a.qn));

            return (segs[0], backups, actualQn, qualities, dur);
        }
        catch
        {
            // 解析失败按"没有可用地址"处理, 交给调用方走降级(报错/回落 durl) ——
            // 这里刻意不 ReportError: 拿到了 200 但结构不认识属于内容问题, 不该弹窗打扰用户。
            return (null, backups, 0, qualities, 0);
        }
    }

    /// <summary>
    /// 未登录时补上"免登录 1080P"参数(仿 PiliPlus 的做法)。
    ///
    /// ★★ 这里有一个**只加 try_look 会完全无效**的坑, 实测结论(2026-10-03, 见 .probes/bd-probe-trylook):
    ///
    ///   必要性矩阵(未登录, 同一视频, 每格重复 2 轮结果一致):
    ///     都不加                       -> dash.video id = [16,32]           只有 360P/480P
    ///     **只加 try_look=1**          -> dash.video id = [16,32]           依然只有 360P/480P  ← 无效!
    ///     **只加 dm_img_* 四个**        -> dash.video id = [16,32]           依然只有 360P/480P  ← 无效!
    ///     try_look=1 + dm_img_* 四个   -> dash.video id = [16,32,64,80]     ★ 720P + 1080P 解锁
    ///
    ///   也就是说这是**两要素同时成立**才放行: try_look 触发"试看"通道, 四个 dm_img_*
    ///   是被校验的设备指纹。少任何一个都退回 360P/480P —— 而且**四个一个都不能少**
    ///   (单独抽掉 dm_img_inter 就会立刻退回 [16,32])。
    ///
    ///   为什么别的项目里"只发 try_look"也能看到 1080P 的说法靠不住:
    ///   PiliPlus 是 2026-05-08 才补上 dm_img_* 的(此前只发 try_look), 那次补丁正是为了修
    ///   它自己 issue #2020「免登录1080p看不了」。照抄它的旧版参数就会复现那个 bug。
    ///
    /// 顺带实测确认**不需要**的东西(避免以后有人"顺手加上去"):
    ///   · 不需要 Cookie(完全无 session 也能解锁)
    ///   · 不需要改 UA / Referer / Origin / env / app-key / x-bili-aurora-zone
    ///   · 不需要把 fnval 从 16 改成 4048, 也不需要动 fourk / fnver
    ///   · qn 不决定返回哪些流(qn=16 照样返回 [16,32,64,80]), 它只决定哪一条带可播放地址
    ///
    /// 能力上限: 只到 1080P(id=80)。1080P+(112) / 60帧(116) / 4K(120) / HDR 匿名拿不到,
    /// 那是账号等级限制, 不是参数问题。番剧(PGC)也不适用 —— 那条接口匿名返回空 dash。
    ///
    /// ⚠️ data.quality 在 qn=80 时会返回 64(看起来像只给了 720P), 但 dash.video 里
    ///    **确实有** id=80 的 1920x1080 流。判断清晰度必须看 dash.video[].id, 不能看它。
    ///
    /// ⚠️ 这是一套设备指纹启发式校验, B 站随时可能改判据(历史上已失效过两次)。
    ///    所以失败要能静默回退 —— 这里只是"多加几个参数", 最坏情况就是退回现在的 360P/480P,
    ///    不会让播放本身出错。
    /// </summary>
    private static void ApplyTryLook(Dictionary<string, string> ps)
    {
        // 只在"未登录 + 用户开着该开关"时发。
        // 已登录时 B 站按账号等级给流, 发这个参数没有意义(还可能干扰), 所以直接不发。
        if (SessionManager.Instance.HasLogin) return;
        if (!SettingsStore.Instance.NoLogin1080P) return;

        ps["try_look"] = "1";
        // 四个设备指纹参数必须**成组**出现, 缺一不可(见上方必要性矩阵)。
        // ★★ 2026-10-17 修「退出登录后无法播放」: 必须是**进程内固定的一组**, 不能每次随机。
        //   上一版用 RandomBase64 每次请求都生成新值 —— 这正是"长驻进程才出问题、
        //   探针复现不了"的原因: 探针只发一两次请求, 而产品每看一个视频、每切一次清晰度
        //   都打一次 playurl ⇒ **同一个 buvid 却每次带着一组全新的设备指纹**,
        //   服务端眼里就是"一台设备在不断更换自己的硬件特征" ⇒ 判为爬虫 ⇒ -412。
        //   (重新登录后正常, 因为 SESSDATA 给设备特征不一致背了书。)
        //   取值本身几乎不被校验(形状像指纹即可), 所以固定下来的成本为零。
        ps["dm_img_list"] = "[]";
        ps["dm_img_str"] = StableDeviceFingerprint(16, 64);
        ps["dm_cover_img_str"] = StableDeviceFingerprint(32, 128);
        ps["dm_img_inter"] = "{\"ds\":[],\"wh\":[0,0,0],\"of\":[0,0,0]}";
    }

    /// <summary>dm_img_str 的进程内缓存(一台设备一套, 见 StableDeviceFingerprint)</summary>
    private static string? _imgStrCache;

    /// <summary>dm_cover_img_str 的进程内缓存(与上面那份相互独立)</summary>
    private static string? _coverImgStrCache;

    /// <summary>
    /// 进程内固定的"设备指纹"随机串: 同一字段每次调用返回**同一个值**。
    ///
    /// ★ 为什么不能每次都随机(2026-10-17 实测教训, 见 ApplyTryLook):
    ///   同一 buvid 每次请求都换一组硬件特征, 在服务端看来就是"设备特征不稳定的爬虫",
    ///   于是稳定 -412。而**探针复现不了**这个 bug —— 它只发一两次请求,
    ///   只有长驻进程连续取流才会暴露。这与 MEMORY 里
    ///   "探针的 ApiClient 是全新实例, 用户跑的是长驻进程"那条教训是同一个模式。
    /// </summary>
    private static string StableDeviceFingerprint(int minLen, int maxLen)
    {
        // 用 maxLen 区分两个字段, 各缓存一份(彼此独立, 但各自稳定)
        if (maxLen <= 64)
            return _imgStrCache ??= RandomBase64(minLen, maxLen);
        return _coverImgStrCache ??= RandomBase64(minLen, maxLen);
    }

    /// <summary>生成指定长度范围内的随机 base64 串(用于 dm_img_str / dm_cover_img_str)</summary>
    private static string RandomBase64(int minLen, int maxLen)
    {
        var n = Random.Shared.Next(minLen, maxLen + 1);
        var bytes = new byte[n];
        Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// 取一组"带签名的真实媒体 URL", 专门给 CDN 测速当样本用(设置页「重新测速」)。
    ///
    /// 为什么单独一个方法、而不是复用 GetPlayUrlAsync:
    ///   GetPlayUrlAsync 返回的是**已经按用户策略选好线路**的那一条, 拿它测速等于
    ///   "只测了当前用的这条", 测不出别的线路更快。测速需要的是**原始候选集**
    ///   (baseUrl + 所有 backupUrl), 所以这里返回全部候选。
    ///
    /// 返回空列表 = 没有可用样本(取流失败/付费视频), 调用方据此提示用户。
    /// </summary>
    public async Task<List<string>> GetCdnSampleUrlsAsync(string bvid, long cid, int qn = 80)
    {
        var urls = new List<string>();
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "16",
                ["fnver"] = "0",
                ["fourk"] = "1"
            };
            ApplyTryLook(ps);
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code != 0 || data == null) return urls;

            if (!data.Value.TryGetProperty("dash", out var dash) ||
                dash.ValueKind != JsonValueKind.Object ||
                !dash.TryGetProperty("video", out var videos) ||
                videos.ValueKind != JsonValueKind.Array ||
                videos.GetArrayLength() == 0)
                return urls;

            // 用最高那段视频当样本(它最吃带宽, 测出来的差距最有代表性)
            JsonElement best = default;
            var bestId = 0;
            foreach (var v in videos.EnumerateArray())
            {
                var id = GetInt(v, "id");
                if (id > bestId) { bestId = id; best = v; }
            }
            if (bestId <= 0) return urls;

            var baseUrl = GetStr(best, "baseUrl");
            if (baseUrl.Length > 0) urls.Add(baseUrl);
            if (best.TryGetProperty("backupUrl", out var bu) && bu.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in bu.EnumerateArray())
                {
                    var s = u.GetString();
                    if (!string.IsNullOrEmpty(s) && !urls.Contains(s)) urls.Add(s);
                }
            }
        }
        catch (Exception ex)
        {
            // 测速样本拿不到不该弹错(那是设置页的一个辅助功能), 但要在日志里留痕
            App.ReportError(ex);
        }
        return urls;
    }

    /// <summary>清晰度 id 转显示名</summary>
    public static string QnToLabel(int qn) => qn switch
    {
        127 => "8K",
        126 => "杜比视界",
        125 => "HDR",
        120 => "4K",
        116 => "1080P60",
        112 => "1080P+",
        100 => "智能修复",
        80 => "1080P",
        74 => "720P60",
        64 => "720P",
        32 => "480P",
        16 => "360P",
        _ => qn + "P"
    };


    /// <summary>
    /// 获取视频弹幕(protobuf 段接口)。
    ///
    /// 为什么换成这个接口: 旧实现走的 /x/v1/dm/list.so 早已被 B 站废弃,
    /// 多数视频返回 404 或空 XML, 属于"代码在跑但永远没数据"。
    /// 现行方案是 /x/v2/dm/web/seg.so —— 弹幕按 6 分钟一段(segment)存储,
    /// 每段独立返回一份 protobuf 二进制, 需要按段号依次拉取再合并。
    ///
    /// 拉取策略:
    ///   - 已知视频总时长时只拉需要的前 N 段(6 分钟一段), 避免长视频白拉几十个请求;
    ///   - 各段并发请求(默认 4 并发), 明显快于串行;
    ///   - 单段失败不影响其它段, 尽力而为。
    /// </summary>
    /// <param name="cid">视频 cid</param>
    /// <param name="durationSec">视频总时长(秒), &lt;=0 时按最多 20 段拉</param>
    /// <param name="max">条数上限</param>
    /// <returns>按时间排序的弹幕(时间秒 / 文本 / 模式 / 颜色)</returns>
    public async Task<List<RawDanmaku>>
        GetDanmakuAsync(long cid, int durationSec = 0, int max = 60000, CancellationToken cancellationToken = default)
    {
        var result = new List<RawDanmaku>();

        // 启动硬超时: 单 SocketsHttpHandler 实例共用连接池, 超时设 15s 就够,
        // 但外层再兜一个, 防止某个段卡住时整页干等。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            const int segLen = 360; // B 站弹幕固定 6 分钟一段
            var segCount = durationSec > 0
                ? Math.Max(1, (int)Math.Ceiling(durationSec / (double)segLen))
                : 20;
            segCount = Math.Min(segCount, 120); // 上限保护(12 小时)

            var segments = new List<RawDanmaku>[segCount];
            using var sem = new SemaphoreSlim(4); // 并发闸门: 同时最多 4 个段请求

            var tasks = new List<Task>(segCount);
            for (var i = 0; i < segCount; i++)
            {
                var index = i; // 闭包捕获
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync(cts.Token).ConfigureAwait(false);
                    try
                    {
                        List<RawDanmaku>? segment=null;
                        for(var attempt=0;attempt<2 && segment==null;attempt++)
                        {
                            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(12));
                            segment=await FetchDanmakuSegmentAsync(cid,index+1,timeout.Token).ConfigureAwait(false);
                            if(segment==null && !cts.IsCancellationRequested)await Task.Delay(150,cts.Token).ConfigureAwait(false);
                        }
                        segments[index]=segment ?? new();
                    }
                    catch
                    {
                        segments[index] = new List<RawDanmaku>();
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, cts.Token));
            }

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch { /* 个别段失败无所谓 */ }

            // 合并所有段并按时间排序
            var merged = new List<RawDanmaku>();
            foreach (var seg in segments)
                if (seg is { Count: > 0 }) merged.AddRange(seg);

            if (cancellationToken.IsCancellationRequested) return result;
            if (merged.Count == 0) return await FetchLegacyDanmakuAsync(cid, max).ConfigureAwait(false);
            merged.Sort((a, b) => a.Time.CompareTo(b.Time));

            result.AddRange(LimitDanmakuTimeline(merged.Where(d=>!string.IsNullOrWhiteSpace(d.Text)).ToList(),max));
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
        return result;
    }

    // Bound memory without dropping the latter half of a dense video's timeline.
    internal static IEnumerable<RawDanmaku> LimitDanmakuTimeline(List<RawDanmaku> items,int max)
    {
        if(max<=0)yield break;
        if(items.Count<=max){foreach(var item in items)yield return item;yield break;}
        if(max==1){yield return items[0];yield break;}
        for(var i=0;i<max;i++)yield return items[(int)((long)i*(items.Count-1)/(max-1))];
    }

    private async Task<List<RawDanmaku>> FetchLegacyDanmakuAsync(long cid, int max)
    {
        var items = new List<RawDanmaku>();
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://comment.bilibili.com/{cid}.xml");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            request.Headers.TryAddWithoutValidation("User-Agent", Ua);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await SendThrottledAsync(request, timeout.Token).ConfigureAwait(false);
            if (response == null) return new List<RawDanmaku>();
            response.EnsureSuccessStatusCode();
            var xml = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            foreach (var node in xml.Descendants("d")) {
                var fields = ((string?)node.Attribute("p") ?? "").Split(',');
                if (fields.Length < 4 || !double.TryParse(fields[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var time) || !int.TryParse(fields[1], out var mode)) continue;
                uint.TryParse(fields[3], out var color);
                items.Add(new RawDanmaku(time, node.Value, mode, color));

            }
            items.Sort((a, b) => a.Time.CompareTo(b.Time));
        } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Danmaku XML fallback: " + ex.Message); }
        return LimitDanmakuTimeline(items,max).ToList();
    }

    /// <summary>拉取单个弹幕段并解析为 RawDanmaku 列表(失败返回 null)</summary>
    private async Task<List<RawDanmaku>?> FetchDanmakuSegmentAsync(
        long cid, int segmentIndex, CancellationToken ct)
    {
        try
        {
            var url = "https://api.bilibili.com/x/v2/dm/web/seg.so" +
                      $"?type=1&oid={cid}&segment_index={segmentIndex}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var cookie = AppendFingerprint(SessionManager.Instance.BuildCookieHeader());
            if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("User-Agent", Ua);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");

            using var resp = await SendThrottledAsync(req, ct).ConfigureAwait(false);
            if (resp == null) return null;

            // 该时间段没有弹幕时, B 站返回 HTTP 304 + 头 "Bili-Status-Code: -304"
            // (不是 200 带空 body)。这属于正常情况, 返回空列表而不是当失败处理。
            if ((int)resp.StatusCode == 304) return new List<RawDanmaku>();
            if (resp.Headers.TryGetValues("Bili-Status-Code", out var codes) &&
                codes.Any(c => c.Contains("-304")))
                return new List<RawDanmaku>();
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0) return new List<RawDanmaku>();

            return DanmakuParser.ParseSegReply(bytes);
        }
        catch
        {
            return null;
        }
    }


    // ---------------------------------------------------------------- 视频下载

    /// <summary>
    /// 取"可直接保存为单个 MP4 文件"的播放地址(durl 单流)。
    ///
    /// 为什么下载不用播放那套 DASH 地址:
    /// DASH 是音视频**分离**的两条流, 直接存下来会是"有画面没声音"的残缺文件,
    /// 要合并必须依赖 ffmpeg 做 remux —— 本项目不带 ffmpeg, 引入它会让安装包暴涨几十兆。
    /// durl 单流本身就是一个完整 MP4(音视频已封装在一起), 保存下来就能直接播,
    /// 代价是清晰度上限 720P。对"下载到本地看"这个场景, 能播 >> 高清晰度。
    /// </summary>
    public async Task<(bool ok, string? err, string? url, string? audioUrl)> GetDownloadUrlAsync(string bvid,long cid,int qn=64)
    {
        var (ok,err,url,audio,_) = await GetOfflineDownloadSourceAsync(bvid,cid,qn);
        return (ok,err,url,audio);
    }

    public async Task<(bool ok, string? err, string? url, string? audioUrl, int quality)> GetOfflineDownloadSourceAsync(
        string bvid, long cid, int qn = 64)
    {
        try
        {
            if (string.IsNullOrEmpty(bvid) || cid <= 0) return (false, "参数不完整", null, null, qn);
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "1",      // 1 = durl 单流(音视频合一的 MP4)
                ["fnver"] = "0",
                ["fourk"] = "1",
                // ★ 不要带 platform=html5。FetchMuxedAsync 的实测结论(同接口、同 fnval=1):
                //   带上它一律返回 -404"啥都木有"。这里以前偏偏带了, 于是下载走的是一条
                //   已知跑不通的参数组合 —— 表现是"该视频没有可下载的单流地址"或 code -404,
                //   很容易被误判成"付费/大会员专属"。改接口参数前先看上面 FetchMuxedAsync 的说明。
                ["high_quality"] = "1"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code != 0 || data == null)
                return (false, msg ?? $"获取下载地址失败 (code {code})", null, null, qn);

            if (data.Value.TryGetProperty("durl", out var durl) &&
                durl.ValueKind == JsonValueKind.Array && durl.GetArrayLength() > 0)
            {
                if(durl.GetArrayLength()>1)return (false,"当前视频返回多段源，暂不支持完整缓存",null,null,qn);
                var url = GetStr(durl[0], "url");
                if (!string.IsNullOrEmpty(url)) return (true, null, url, null, GetInt(data.Value, "quality") is > 0 and var actual ? actual : qn);
            }
            return (false, "该视频没有可下载的单流地址(可能是付费/大会员专属)", null, null, qn);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, null, qn);
        }
    }

}
