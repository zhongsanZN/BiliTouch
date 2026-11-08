using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BiliTouch.Helpers;

namespace BiliTouch.Services;

/// <summary>
/// 一次检查更新的结果。没发现新版本时调用方拿到 null(见 <see cref="UpdateChecker.CheckAsync"/>)。
/// </summary>
public sealed class UpdateInfo
{
    /// <summary>最新版本号(tag 名去掉前缀 v/V 后解析, 解析失败的 tag 一律视为"没有新版本")</summary>
    public Version Version { get; init; } = new(0, 0, 0);

    /// <summary>Release 标题(没有就回落到 tag 名)</summary>
    public string Title { get; init; } = "";

    /// <summary>Release 说明(即发布说明正文, 弹窗里展示给用户的那段)</summary>
    public string Notes { get; init; } = "";

    /// <summary>该 Release 的网页地址(「前往 GitHub 下载」按钮的落点; 自动下载失败时的退路)</summary>
    public string ReleaseUrl { get; init; } = "";

    /// <summary>
    /// 安装器附件的下载直链(GitHub 的 browser_download_url)。
    /// 空串 = **这个 Release 没有可自动安装的附件**(老版本只挂了源码归档), 调用方要按"只能手动下载"处理。
    /// ★ <see cref="UpdateChecker.CheckAsync"/> **不填这三个字段**(SetupUrl/SetupSize/SetupSha256):
    ///   它的语义是"只看版本", 老调用点(设置页/自动检查)只想知道"有没有新版", 不该被迫关心附件。
    ///   要附件就显式调 <see cref="UpdateChecker.CheckWithInstallerAsync"/> 或
    ///   <see cref="UpdateChecker.AttachInstallerAsync"/> —— 弹窗走的是后者(见 UpdatePromptWindow)。
    /// </summary>
    public string SetupUrl { get; init; } = "";

    /// <summary>安装器字节数(GitHub 给的 size)。0 = 没拿到, 此时只能靠 sha256 或干脆不校验</summary>
    public long SetupSize { get; init; }

    /// <summary>
    /// 安装器的 SHA-256(小写 hex, 已去掉 GitHub 的 "sha256:" 前缀)。
    /// null = **GitHub 这次没给 digest**(接口字段本身可能为 null), 下载完只能退回"只比大小"。
    /// </summary>
    public string? SetupSha256 { get; init; }

    /// <summary>
    /// 有没有可以自动下载安装的附件。
    /// ★ 界面**不拿它决定按钮显不显示**(见 UpdatePromptWindow.ApplyPendingInstallerState 的说明):
    ///   构造期分不清"这个 Release 没挂安装器"和"这一次没取到附件信息", 摘按钮会误伤后者。
    ///   它在这里的用途是给调用方一个廉价判断: 有就直连下载, 没有就先补一次再决定。
    /// </summary>
    public bool HasSetup => SetupUrl.Length > 0;

    /// <summary>
    /// 安装器落盘用的文件名。格式与 <c>Publish/installer.iss</c> 的
    /// <c>OutputBaseFilename=BiliTouch-{#MyAppVersion}-setup</c> 以及
    /// <c>Publish/github_release.py</c> 校验的 <c>BiliTouch-{version}-setup.exe</c> 完全一致 ——
    /// 三处必须同形, 否则下载下来的包和"发布脚本认定的那个包"会对不上。
    ///
    /// ★ 这里**只取三段**并显式拼, 不用 <c>Version.ToString()</c>: tag 万一是 v0.2.9.0
    ///   (四段, installer.iss 的 VersionInfoVersion 就是这个形状), ToString() 会给出
    ///   "0.2.9.0", 于是本地文件名变成 BiliTouch-0.2.9.0-setup.exe —— 而 GitHub 上的附件
    ///   叫 BiliTouch-0.2.9-setup.exe。名字对不上时排查问题的人会以为是两个不同的包。
    /// </summary>
    public string SetupFileName
    {
        get
        {
            // ★ 写 this.Version 而不是裸 Version: 属性名与类型 System.Version 同名,
            //   裸写虽然能编过(C# 的"Color Color"规则), 但读的人要停顿一下才分得清
            //   那是"取属性"还是"访问类型"。加 this. 没有歧义。
            var v = this.Version;
            return $"BiliTouch-{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}-setup.exe";
        }
    }

    /// <summary>
    /// 复制一份并补上安装器信息。
    /// ★ 属性是 init 的(构造后不可变, 别处不用担心被谁改了一半), 所以"补附件"只能是**换一个新实例**;
    ///   弹窗拿到新实例后整体替换引用即可, 不会出现"版本号是新的、下载地址还是旧版本"这种撕裂状态。
    /// </summary>
    public UpdateInfo WithInstaller(string setupUrl, long setupSize, string? setupSha256) => new()
    {
        // ★ 写 this.Version: 对象初始化器里裸写 `Version = Version` 虽然合法(左边是新对象的成员,
        //   右边解析到外层实例的属性), 但同一行里出现两个同名标识符极易被后续改动读错 ——
        //   有人把它"简化"成 Version = release.Version 就会静默换成另一个版本号。
        Version = this.Version,
        Title = this.Title,
        Notes = this.Notes,
        ReleaseUrl = this.ReleaseUrl,
        SetupUrl = setupUrl,
        SetupSize = setupSize,
        SetupSha256 = setupSha256
    };
}

/// <summary>
/// 检查更新: 数据源 = GitHub Releases(公开仓库的 releases/latest 接口, 匿名可访问)。
///
/// ★ 职责边界(2026-11-08 更新, 与之前不同, 别再按旧注释理解):
///   本类只负责"**问清楚有没有新版、新版的安装包在哪**", 不负责下载, 也不负责安装 ——
///   下载/校验/启动安装器在 <see cref="UpdateInstaller"/>, 交互在 UpdatePromptWindow。
///   这样切的原因是三者失败面完全不同(网络 / 磁盘与哈希 / 进程与 UAC), 混在一起时
///   一个"校验失败"会淹没在 HTTP 异常里, 用户只看到"更新失败"。
///
/// ★ 历史(别删, 是上一轮的设计理由, 现在仍然部分成立):
///   以前这里只"检查 + 跳浏览器", 理由是 GitHub Release 页上同时挂着便携版与安装器,
///   客户端替用户决定升级方式反而多出错面。现在发布脚本已经把 Release 收敛成
///   **只挂一个 BiliTouch-&lt;版本&gt;-setup.exe**(见 Publish/github_release.py 的
///   "Release must contain exactly one uploaded installer"), 所以"替用户决定"这件事不再有歧义 ——
///   附件只有一个, 装的就是它。但**「前往 GitHub 下载」按钮保留**: 内置下载失败(断网/被墙/杀软)
///   时那是用户唯一还能走的路。
///
/// 网络上的独立性: 这不是 B 站接口, **不走 ApiClient**(那里带 WBI/appkey 签名语义和风控
/// 相关的默认值, 混进来只会互相牵连), 单独一个 HttpClient, 失败一律不影响主功能。
/// </summary>
public static class UpdateChecker
{
    // 只从中三维护的独立仓库检查更新，禁止把上游发行作为本分支更新。
    public static bool IsConfigured => true;

    /// <summary>Release 数据所在的仓库(owner/repo)。改名要连着 ReleasePageUrl 一起改。</summary>
    public const string RepoOwner = "zhongsanZN";
    public const string RepoName = "BiliTouch";

    /// <summary>releases/latest 网页地址(弹窗里「前往 GitHub 下载」按钮的落点)。</summary>
    public const string ReleasePageUrl =
        $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    // ★ 这里曾经有一个"微云高速下载"镜像按钮(WeiYunShareUrl / WeiYunSharePwd 两个常量)。
    //   2026-10-01 用户要求**下载全部走 GitHub**, 两个常量与那个按钮一起删掉了。
    //   别再按"国内下载慢"的直觉加回来: 镜像链接要人工维护, 忘了换新版就是给用户一个旧包;
    //   国内访问 Releases 页/附件本来也只需要浏览器能开 GitHub。

    /// <summary>
    /// 安装器附件名的**唯一**判据。三处必须同形(见 <see cref="UpdateInfo.SetupFileName"/>):
    /// installer.iss 的 OutputBaseFilename、github_release.py 的 expected、以及这里的正则。
    ///
    /// ★ 为什么用正则而不是 EndsWith(".exe"): Release 里以后可能同时挂便携 zip、符号包、
    ///   checksum 文件; 名字不带版本号就无法确认"这个附件是这一版" —— 拿到旧包会装出
    ///   一个版本号倒退的程序, 那比"没有更新"糟得多。
    /// ★ 版本段允许 "-beta"/".1" 这类后缀(github_release.py 的版本校验允许),
    ///   但解析出来的**数字版本必须等于 tag 解析出的版本**, 后缀本身不参与比较。
    /// </summary>
    private static readonly Regex SetupNamePattern = new(
        @"^BiliTouch-(?<version>\d+\.\d+\.\d+(?:[-.][A-Za-z0-9.]+)?)-setup\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HttpClient Http = new()
    {
        // 检查更新不该拖住任何界面: 10 秒拿不到结果就当这次失败(启动后的静默检查会下次再试)。
        Timeout = TimeSpan.FromSeconds(10)
    };

    static UpdateChecker()
    {
        // GitHub API 强制要求 User-Agent 头(不带直接 403); GitHub 不是 B 站,
        // 沿用 HttpDefaults 的纯 Chrome UA 没有副作用(它本来也只是个普通浏览器 UA)。
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>最新 Release 的 API 地址(每次现拼, 常量拼接没有循环依赖问题)</summary>
    private static string LatestApiUrl =>
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>
    /// 查一次最新版本并与当前版本比较。
    /// 返回 null 的三种含义要分清: **没有 Release**(仓库还没发过版, 404)、**已是最新**、
    /// **tag 解析失败**(Tag 不是 vX.Y.Z 形状, 宁可不提示也不给用户一个装不上的"新版本")。
    /// 网络失败会抛异常, 由调用方决定"静默忽略"(自动检查)还是"提示用户"(手动检查)。
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        if (!IsConfigured) throw new InvalidOperationException("BiliTouch 独立发布渠道尚未配置");
        var release = await FetchLatestAsync();
        if (release == null) return null;
        return new UpdateInfo
        {
            Version = release.Version,
            Title = release.Title,
            Notes = release.Notes,
            ReleaseUrl = release.ReleaseUrl
        };
    }

    /// <summary>
    /// 查一次最新版本, 并且**要求这个 Release 带安装器附件**; 拿不到可用安装包就返回 null。
    ///
    /// ★ 与 <see cref="CheckAsync"/> 的差别只有一点: 附件缺失也算"没有可用更新"。
    ///   调用方(自动/手动检查的"下载并安装"入口)据此决定要不要显示下载按钮。
    /// ★ 这个方法**不向 UI 抛异常**: 断网、GitHub 抽风、JSON 结构变了、附件还没传上去 ——
    ///   全部收敛成 null(打一行 Trace)。理由: 它的返回值语义就是"这次有没有能自动装的更新",
    ///   而"没有"本来就是常态, 让调用方到处 try/catch 只会漏掉某一处然后弹一个看不懂的异常框。
    /// </summary>
    public static async Task<UpdateInfo?> CheckWithInstallerAsync()
    {
        if (!IsConfigured) return null;
        try
        {
            var release = await FetchLatestAsync();
            if (release == null || !release.HasSetup) return null;
            return release.ToInfo();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("[Update] 查询安装包附件失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 给一个**已经拿到的** <paramref name="info"/> 补上安装器附件信息(补不到返回 null)。
    ///
    /// ★ 为什么需要它: 设置页的手动检查走的是 <see cref="CheckAsync"/>(只认版本),
    ///   弹窗拿到的是"没有附件字段"的 info; 弹窗在 Loaded 时调本方法补一次,
    ///   就不用逼着所有调用点都换成 CheckWithInstallerAsync —— 检查与"能不能自动装"
    ///   解耦, 老调用点一行都不用改。
    /// ★ 版本必须与当前 release **一致**才认: 极端情况下(检查完的几百毫秒里作者又发了一版)
    ///   releases/latest 已经指向下一个版本, 那时把新版本的附件贴到旧版本的 info 上,
    ///   用户点"下载并安装"会装出一个版本号跟弹窗对不上的程序。
    /// </summary>
    public static async Task<UpdateInfo?> AttachInstallerAsync(UpdateInfo? info)
    {
        if (!IsConfigured || info == null) return null;
        try
        {
            var release = await FetchLatestAsync();
            if (release == null || !release.HasSetup) return null;
            if (!SameVersion(release.Version, info.Version)) return null;
            return info.WithInstaller(release.SetupUrl, release.SetupSize, release.SetupSha256);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("[Update] 补齐安装包信息失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 请求 releases/latest 并解析成内部结构。
    /// 返回 null = 没有 Release(404) / tag 解析失败 / 已是最新 —— 三种都当作"没有新版本"。
    /// 网络与 HTTP 错误**照旧抛**(CheckAsync 的既有语义, 调用方各自决定静默还是提示)。
    /// </summary>
    private static async Task<LatestRelease?> FetchLatestAsync()
    {
        using var resp = await Http.GetAsync(LatestApiUrl);
        if (!resp.IsSuccessStatusCode)
        {
            // 404 = 还没发过任何 Release, 这是正常状态(新仓库/刚建仓), 不当错误抛
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            throw new HttpRequestException($"检查更新失败: HTTP {(int)resp.StatusCode}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        var latest = ParseVersion(root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null);
        if (latest == null || latest <= ParseVersion(App.AppVersion)) return null;

        var asset = FindInstallerAsset(root, latest);
        return new LatestRelease
        {
            Version = latest,
            Title = root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? ""
                : "",
            // body 是 Markdown 源文本; 弹窗里按纯文本展示, markdown 标记(# * -)读起来也不碍事,
            // 没必要为它引一个渲染库
            Notes = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
                ? bodyEl.GetString() ?? ""
                : "",
            ReleaseUrl = root.TryGetProperty("html_url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString() ?? ReleasePageUrl
                : ReleasePageUrl,
            SetupUrl = asset?.Url ?? "",
            SetupSize = asset?.Size ?? 0,
            SetupSha256 = asset?.Sha256
        };
    }

    /// <summary>
    /// 在 assets 数组里找这一版的安装器。
    /// 找不到(或结构不认识)返回 null —— 调用方按"没有可自动安装的更新"处理, 不抛。
    /// </summary>
    private static InstallerAsset? FindInstallerAsset(JsonElement root, Version expected)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            if (!asset.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;

            var match = SetupNamePattern.Match(nameEl.GetString() ?? "");
            if (!match.Success) continue;

            // ★ 名字里的版本必须与 tag 一致: Release 里可能还挂着历史附件(手动上传/改名遗漏),
            //   只认名字不认版本的话, 用户会被引导去装一个**更旧**的包。
            var assetVersion = ParseVersion(match.Groups["version"].Value);
            if (assetVersion == null || !SameVersion(assetVersion, expected)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(url)) continue;

            var size = asset.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var bytes) ? bytes : 0;
            var sha256 = asset.TryGetProperty("digest", out var digestEl) && digestEl.ValueKind == JsonValueKind.String
                ? NormalizeSha256(digestEl.GetString())
                : null;

            return new InstallerAsset { Url = url!, Size = size, Sha256 = sha256 };
        }
        return null;
    }

    /// <summary>
    /// GitHub 的 asset.digest 形如 <c>"sha256:abcd…"</c>, 也可能整个字段是 null。
    /// 这里只认 sha256; 将来若换成 sha512/别的算法, 返回 null 让下载端退回"只比大小",
    /// 而不是拿一个长度不对的串去比对然后永远失败。
    /// </summary>
    private static string? NormalizeSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var s = digest.Trim();
        const string prefix = "sha256:";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        s = s.Trim();
        if (s.Length != 64) return null;
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return null;
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// 版本比较只比三段(Major.Minor.Build)。
    ///
    /// ★ 为什么不用 <see cref="Version"/> 的 ==: 它把"没写第三段"和"写了 0"当成不同值
    ///   (0.2.9 的 Build=9/Revision=-1, 0.2.9.0 的 Revision=0), 于是 tag 写成 v0.2.9.0、
    ///   附件名写成 BiliTouch-0.2.9-setup.exe 时会被判成"不是同一版", 白白丢掉一次更新。
    ///   本仓库的版本号只有三段, 四段那位是 installer.iss 里的 VersionInfoVersion, 不参与比较。
    /// </summary>
    private static bool SameVersion(Version a, Version b)
        => a.Major == b.Major
           && a.Minor == b.Minor
           && Math.Max(a.Build, 0) == Math.Max(b.Build, 0);

    /// <summary>tag → 版本号。容忍 "v"/"V" 前缀与 "-beta" 这类后缀(取 '-' 前的部分); 解析不出返回 null。</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>
    /// 启动后的自动检查: 设置里开着才查; 用户对**这个版本**点过「跳过此版本」就不再打扰
    /// (跳过只对当次发版有效, 下一个新版本照常弹)。任何失败都静默 —— 自动检查不该在
    /// 断网时给用户报错。
    ///
    /// ★ 弹窗统一走 <c>UpdatePromptWindow.ShowFor</c>: 它自己处理"owner 可能是 null / 还没 Loaded"
    ///   的情况(自动检查在后台任务里跑, 那一刻主窗口不一定就绪), 这里不再手写 Owner 赋值。
    /// </summary>
    public static async Task AutoCheckAsync()
    {
        if (!IsConfigured || !Svc.Settings.AutoCheckUpdate) return;
        try
        {
            var info = await CheckAsync();
            if (info == null) return;
            if (string.Equals(Svc.Settings.SkippedVersion, info.Version.ToString(),
                    StringComparison.Ordinal)) return;

            var app = App.Current;
            if (app == null) return;

            // 从线程池回到 UI 线程再弹窗 —— 弹窗是 UI 对象
            await app.Dispatcher.InvokeAsync(() =>
            {
                Views.Windows.UpdatePromptWindow.ShowFor(info, app.MainWindow, allowSkip: true);
            });
        }
        catch
        {
            // 静默: 断网/GitHub 连不上是常态, 自动检查失败不该打扰用户
        }
    }

    /// <summary>用系统浏览器打开链接(弹窗的次按钮与自动下载失败后的退路)</summary>
    public static void OpenInBrowser(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }

    /// <summary>
    /// 解析出来的安装器附件。用一个小类而不是元组: 三个字段里两个是可空的"可能没有"
    /// (size 可能拿不到、digest 可能就是 null), 元组在调用点会变成
    /// <c>asset?.Sha256</c> 这种既难读又容易写错的下标访问。
    /// </summary>
    private sealed class InstallerAsset
    {
        public string Url { get; init; } = "";
        public long Size { get; init; }
        public string? Sha256 { get; init; }
    }

    /// <summary>
    /// releases/latest 的内部解析结果。存在的意义是让"只看版本"与"要安装包"两条路
    /// **共用同一个请求**(GitHub 匿名接口有速率限制, 一次弹窗查两遍纯属浪费, 也让两边
    /// 有机会读到不同时刻的数据)。
    /// </summary>
    private sealed class LatestRelease
    {
        public Version Version { get; init; } = new(0, 0, 0);
        public string Title { get; init; } = "";
        public string Notes { get; init; } = "";
        public string ReleaseUrl { get; init; } = "";
        public string SetupUrl { get; init; } = "";
        public long SetupSize { get; init; }
        public string? SetupSha256 { get; init; }

        public bool HasSetup => SetupUrl.Length > 0;

        public UpdateInfo ToInfo() => new()
        {
            // ★ 同样写 this.: 这里左边是 UpdateInfo.Version、右边是 LatestRelease.Version,
            //   两个同名成员挤在一行, 加 this. 才能一眼看出"抄的是自己那份解析结果"。
            Version = this.Version,
            Title = this.Title,
            Notes = this.Notes,
            ReleaseUrl = this.ReleaseUrl,
            SetupUrl = this.SetupUrl,
            SetupSize = this.SetupSize,
            SetupSha256 = this.SetupSha256
        };
    }
}
