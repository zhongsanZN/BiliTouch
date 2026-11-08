using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BiliTouch.Helpers;

namespace BiliTouch.Services;

/// <summary>
/// 内置更新的"下载 → 校验 → 启动安装器"这一段。
///
/// ★ 为什么单独一个类, 而不是塞进 <see cref="UpdateChecker"/>:
///   两段的失败面完全不同 —— 检查更新失败是"网络不通"(用户断网是常态, 必须静默);
///   下载失败是"文件不对"(哈希/大小对不上, 说明包坏了或被人换了, 必须**硬失败**并删文件);
///   启动安装器失败是"权限/杀软"(要提示用户还能走浏览器)。三类错误混在一个类里,
///   迟早出现"校验失败被 catch 成网络异常然后静默"这种把坏包当成功的事故。
///
/// ★ 为什么用**独立** HttpClient 而不是 UpdateChecker 里那个:
///   那个的 Timeout 是 10 秒(为"别拖住界面"设的), 而安装器有 30MB 上下, 慢速网络上
///   10 秒连响应头都拿不到。用同一个实例就只能二选一: 要么检查变慢, 要么下载必超时。
///
/// ★ 关于"静默安装 + 自动重启"的实现路线(读之前先看这段, 否则会以为下面的 cmd 是乱写的):
///   installer.iss 里 <c>[Run]</c> 那一行带 <c>skipifsilent</c>, 也就是说**静默安装时它不会
///   自动把程序拉起来**。所以"装完自动回来"这件事只能由我们自己安排:
///   用一条独立的 cmd 命令"等安装器退出 → 再启动 BiliTouch"。
///   为什么不是自己 WaitForExit: 安装器要 UAC 提权, 提权后的进程**不是我们的子进程**
///   (中间隔着 AppInfo 服务), 我们拿不到可等待的句柄。cmd 的 <c>start /wait</c> 走
///   ShellExecuteEx + SEE_MASK_NOCLOSEPROCESS, 它能拿到那个句柄 —— 只有它能等。
/// </summary>
public static class UpdateInstaller
{
    /// <summary>
    /// 安装器落盘目录: **<see cref="AppPaths.DataDir"/> 下的 <c>updates\</c>**。
    ///
    /// ★★ 为什么不写死 <c>%LOCALAPPDATA%\BiliTouch\updates</c>(这是硬约束, 别改回去):
    ///   发布校验探针会把 <see cref="AppPaths.DataDir"/> 重定向到隔离目录, 好让整条更新链路
    ///   (检查 → 下载 → 校验 → 安装)在**不碰用户真实数据**的前提下被跑一遍。
    ///   一旦这里自己拼 %LOCALAPPDATA%, 探针就会把 30MB 的安装包和日志写进用户真实目录 ——
    ///   校验越"成功", 污染越严重。所以路径**只能**从 AppPaths 派生。
    /// ★ 放在数据目录而不是临时目录: 下载失败/校验失败时要留下痕迹给用户排查,
    ///   而且用户点"重试"时可以直接复用已下完的包(不必为一次 UAC 取消重下 30MB)。
    /// ★ 为什么不放 <see cref="AppPaths.DownloadDir"/>(用户可见的下载目录):
    ///   那是用户**成品**的地盘(下载的视频), 程序自己的临时安装包混进去只会让人困惑。
    /// </summary>
    public static string InstallersDir
    {
        get
        {
            // AppPaths.DataDir 由 App.OnStartup 里的 AppPaths.Init() 初始化。正常运行时它一定非空;
            // 只有"没走启动流程就被直接调用"(单元测试 / 探针提前摸到这条路径)时才可能是空串。
            // 那时**不要**去猜 %LOCALAPPDATA%(理由见上), 而是落到系统临时目录下的同名子目录 ——
            // 那里天生是"可随意写、坏了也不用负责"的地方, 与 DataDir 的语义最接近。
            var root = AppPaths.DataDir;
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Path.GetTempPath(), "BiliTouch");
            return Path.Combine(root, "updates");
        }
    }

    /// <summary>
    /// 这个版本对应的安装器本地路径(<c>BiliTouch-&lt;版本&gt;-setup.exe</c>)。
    /// 文件名与 GitHub 上的 asset 名、installer.iss 的 OutputBaseFilename 三处同形 ——
    /// 出问题时用户能在两个地方按同一个名字对上号。
    /// </summary>
    public static string PathFor(UpdateInfo info) => Path.Combine(InstallersDir, info.SetupFileName);

    /// <summary>
    /// Inno Setup 的静默安装参数(依据 <c>Publish/installer.iss</c> 实测确认):
    ///
    ///   /SILENT              静默安装: 不显示向导, 只留一个进度窗(比 /VERYSILENT 好 ——
    ///                        用户至少看得见"正在装", 不会以为点了没反应)。
    ///   /SUPPRESSMSGBOXES    静默期间不弹任何消息框, 一律按默认答案走。必须带:
    ///                        installer.iss 的 <c>InitializeSetup</c> 在**没装 .NET 10 运行时**时
    ///                        会 MsgBox 问"要不要去下载运行时" —— 没人点它, 安装就永远卡在那一步。
    ///                        (真命中那条分支时 Setup 会以"中止"收场, 因为 InitializeSetup 返回 False;
    ///                         但本程序正跑在 .NET 10+ 运行时上, 这个分支实际不可能命中 ——
    ///                         带上它只是防"未来 installer.iss 又加了别的 MsgBox"。)
    ///   /CLOSEAPPLICATIONS   让 Setup 用 Restart Manager 关掉占用目标文件的程序。
    ///                        我们自己已经先退出了, 但用户可能开着**另一个** BiliTouch(托盘残留/
    ///                        双开没拦住), 不关它 Setup 会卡在"文件被占用, 请重试"。
    ///   /RESTARTAPPLICATIONS 配合上一条: 被 Restart Manager 关掉的那些程序装完自动重启。
    ///                        (注意: 它**不会**把我们自己拉起来 —— 我们在 Setup 启动前就退了,
    ///                        Restart Manager 没有"关过我们"这回事。自动重启由 <see cref="InstallAsync"/>
    ///                        里那条 cmd 负责, 别把这两件事混为一谈。)
    ///   /NORESTART           装完不要重启电脑。安装器只是替换文件, 没有任何需要重启才生效的东西;
    ///                        少了它, 某些情况下 Setup 会弹"需要重启"并真的重启机器。
    ///   /SP-                 不显示开头那句"This will install ... Do you wish to continue?"。
    ///                        (installer.iss 没设 DisableStartupPrompt, 所以这句是真会出现的。)
    ///
    /// ★ AppId 是 <c>{F4A7C086-EF18-4BD6-B709-C75A83B62D91}</c>(installer.iss 里为了转义写成
    ///   <c>{{F4A7...</c>), 那是**升级识别**用的, 不需要也不该由我们传参 ——
    ///   Setup 靠它判断"这是同一个应用的升级"从而原地覆盖, 传参反而没有这个开关。
    /// ★ 安装器是 <c>PrivilegesRequired=admin</c>, 所以启动时会弹 UAC。这是**预期行为**,
    ///   不是我们能绕过的: 它要写 <c>{autopf}</c>(Program Files)。用户取消 UAC = 放弃这次更新,
    ///   程序必须照常回来(见 InstallAsync 的 relaunch)。
    /// </summary>
    public const string SilentArguments =
        "/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART /SP-";

    /// <summary>进度回调的最小间隔: 每 256KB 报一次(小包时再叠加"每 1%"的限制)</summary>
    private const long ProgressBytesStep = 256 * 1024;

    /// <summary>
    /// 下载安装器到 <see cref="InstallersDir"/>, 完成后做完整性校验, 返回本地路径。
    ///
    /// 校验策略(优先级从高到低, 依据是"我们手上到底有什么可信信息"):
    ///   1. GitHub 给了 <c>digest</c>(形如 <c>sha256:abcd…</c>) ⇒ 逐字节比 SHA-256, 最强;
    ///   2. 没给 digest 但有 <c>size</c> ⇒ 只比字节数, 并在日志里写明"仅校验大小";
    ///   3. 两个都没有 ⇒ 什么都不比, 但日志里留一条 —— 这种情况只会出现在 GitHub 接口变了,
    ///      不阻断更新(用户点的是"更新", 卡在这儿只会让他永远升不上去)。
    ///
    /// ★ 为什么校验失败要**删文件**: 留在磁盘上的坏包下次启动会被当成"已下载"复用,
    ///   于是同一个坏包会被反复拿去安装, 每次都以"安装器闪一下就没了"告终 —— 极难排查。
    /// ★ 为什么先写 <c>.part</c> 再改名: 下载到一半进程被杀(用户强退/断电)时, 目标文件名
    ///   下会留一个长度不对的半包; 有了 .part 中间态, 只有**校验通过**的文件才会出现在正式名字下。
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        if (info == null) throw new ArgumentNullException(nameof(info));
        if (!info.HasSetup) throw new InvalidOperationException("这个版本没有可自动安装的安装包");

        var target = PathFor(info);
        var temp = target + ".part";
        Directory.CreateDirectory(InstallersDir);

        // ★ Timeout 交给调用方的 CancellationToken 管(无限): 30MB 的包在慢速网络上要几分钟,
        //   写死一个总超时等于给慢网用户判死刑; 而"要不要放弃"由界面的取消按钮决定。
        //   ★ 写全名 System.Threading.Timeout: 对象初始化器里 `Timeout = Timeout.…` 虽然能编译,
        //     但同名属性/类型挨在一起时读代码的人要停顿一下才分得清, 不值得省这几个字符。
        using var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);

        try
        {
            using var resp = await http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"下载安装包失败: HTTP {(int)resp.StatusCode}");

            // 服务端能报总长度就以它为准(比 GitHub 接口里的 size 更贴近这次实际传输),
            // 报不出来再退回接口给的 size —— 只是用来算百分比, 不参与校验。
            var total = resp.Content.Headers.ContentLength ?? (info.SetupSize > 0 ? info.SetupSize : 0);

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                long lastReported = 0;
                int lastPercent = -1;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;

                    // ★ 别每读一块就回调: 80KB 一块 × 30MB ≈ 380 次, 每次都往 UI 线程派发
                    //   等于给下载线程加一层无谓的锁竞争。1% 或 256KB, 取"先到者"。
                    if (total <= 0) continue;
                    var percent = (int)(received * 100 / total);
                    if (percent == lastPercent && received - lastReported < ProgressBytesStep) continue;
                    lastPercent = percent;
                    lastReported = received;
                    progress?.Report(Math.Clamp(percent, 0, 100) / 100.0);
                }
            }

            VerifyOrThrow(temp, info);

            // ★ File.Move 带 overwrite: 上一版留下的同名文件要能直接替换,
            //   否则用户第二次点更新会得到一个 IOException(文件已存在)。
            File.Move(temp, target, overwrite: true);
            progress?.Report(1.0);
            return target;
        }
        catch
        {
            // 任何失败(含取消)都不留半成品 —— 见方法注释里"为什么校验失败要删文件"
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// 校验一个已下完的文件。不通过就抛 <see cref="InvalidDataException"/>。
    /// ★ 单独抽出来是为了让"校验规则"只有一份: 将来若加"重试一次"或"复用已下载的包",
    ///   都会先走这里, 不会出现两条路上各写一半校验。
    /// </summary>
    private static void VerifyOrThrow(string path, UpdateInfo info)
    {
        var actualSize = new FileInfo(path).Length;

        if (!string.IsNullOrEmpty(info.SetupSha256))
        {
            string actualHash;
            using (var fs = File.OpenRead(path))
                actualHash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();

            if (!string.Equals(actualHash, info.SetupSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"安装包校验失败(SHA-256 不匹配): 期望 {info.SetupSha256}, 实际 {actualHash}");
            }
            return;
        }

        if (info.SetupSize > 0)
        {
            // 无 digest 是 GitHub 老数据/字段为 null 时的正常情况, 不是错误 —— 但必须留下记录,
            // 否则以后排查"为什么装了个坏包"时看不出当时其实只校验了长度。
            System.Diagnostics.Trace.WriteLine(
                $"[Update] 无 digest, 仅校验大小: 期望 {info.SetupSize} 实际 {actualSize}");
            if (actualSize != info.SetupSize)
            {
                throw new InvalidDataException(
                    $"安装包校验失败(大小不匹配): 期望 {info.SetupSize} 字节, 实际 {actualSize} 字节");
            }
            return;
        }

        System.Diagnostics.Trace.WriteLine("[Update] 既无 digest 也无 size, 本次跳过完整性校验");
    }

    /// <summary>
    /// 启动安装器(静默), **不等它结束**, 也不关自己。
    /// ★ 用 UseShellExecute=true: 安装器是 <c>PrivilegesRequired=admin</c>, 必须由 Shell 拉起
    ///   才会弹 UAC 提权; 用 false 直接 CreateProcess 会以当前权限跑起来, 然后在写
    ///   Program Files 那一步失败, 而且失败得很晚(装到一半)。
    /// </summary>
    public static Task LaunchAsync(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            throw new FileNotFoundException("安装包不存在, 请重新下载", installerPath);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = SilentArguments,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 完整流程: 下载 → 校验 → **先退自己** → 静默安装 → 装完自动重启。
    /// 方法正常返回时, 退出指令已经排进 UI 消息队列(下一个消息循环就会关停进程),
    /// 安装器也已经在跑 —— 调用方什么都不用再做, 干净返回即可。
    ///
    /// ★ 顺序为什么必须是"置 IsExiting → 启动安装器 → 排 Shutdown", 一步都不能换:
    ///   · 先置 <see cref="App.IsExiting"/>: MainWindow.OnClosing 里那道"关闭时最小化到托盘"的
    ///     拦截靠它放行。不置的话 Shutdown 会被自己拦下来, 窗口藏进托盘, 进程活着,
    ///     安装器在"请关闭 BiliTouch"那一步永远等不到 —— 用户看到的是"更新装不上"。
    ///   · 再启动安装器: 它要在我们**还活着**的时候被拉起来(靠 Shell 弹 UAC);
    ///     反过来先 Shutdown 再启动, 这段代码根本不会被执行到。
    ///   · 最后排 Shutdown: 安装器已经独立跑起来了, 我们退出正好把 exe 的文件锁放开。
    ///
    /// ★ 失败回滚: 任何一步抛异常(下载失败/校验失败/找不到文件/UAC 被拒)都要把 IsExiting
    ///   置回 false。否则这次更新失败后, 用户点关闭键就**真的退出**了(托盘设置被绕过),
    ///   而界面上还写着"关闭时最小化到托盘" —— 一个开关失效比更新失败更让人困惑。
    /// </summary>
    public static async Task InstallAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var installer = await DownloadAsync(info, progress, ct);

        App.IsExiting = true;
        try
        {
            LaunchInstallerWithRelaunch(installer);
        }
        catch
        {
            App.IsExiting = false;
            throw;
        }

        // ★ 用 Dispatcher.InvokeAsync 排进消息队列, 而不是直接调 Shutdown:
        //   这个调用栈里可能正挂着 ShowDialog 的模态帧(设置页手动检查走的就是 ShowDialog),
        //   直接 Shutdown 会在**当前帧内部**把窗口全关掉 —— 模态帧被同步拆掉之后,
        //   调用方剩下的 await 续体会在一个已经关停的 Dispatcher 上继续跑,
        //   轻则静默不执行, 重则把异常抛进无人接的 async void 里。
        //   排到下一个消息循环再关, 让当前这一帧先干净地返回。
        //   (InvokeAsync 可以从任意线程调, 不要求先在 UI 线程上。)
        // ★ 用 `_ =` 显式丢弃返回值: DispatcherOperation 是可 await 的, 在 async 方法里
        //   直接丢掉会触发 CS4014(未 await 的调用), 而那正是我们**故意**不等的操作。
        var app = System.Windows.Application.Current;
        if (app == null) return;
        _ = app.Dispatcher.InvokeAsync(new Action(app.Shutdown));
    }

    /// <summary>
    /// 拉起"等安装器结束 → 重新启动本程序"的守护命令。
    ///
    /// ★ 为什么需要它(而不是只 Process.Start 安装器): installer.iss 的 <c>[Run]</c> 带
    ///   <c>skipifsilent</c>, 静默安装时 Setup **不会**帮我们把程序拉起来; 而 /RESTARTAPPLICATIONS
    ///   只重启"被 Setup 关掉的"程序, 我们是在 Setup 启动前自己退出的, 不在那个名单里。
    ///   两件事都不成立, 结果就是"更新装完, 程序没了, 用户以为崩了"。
    /// ★ 为什么用 cmd 而不是自己 WaitForExit: 安装器要提权, 提权进程**不是我们的子进程**
    ///   (中间隔着 AppInfo 服务), Process.Start 拿不到可等待的句柄 —— 这里等不了。
    ///   而 cmd 的 <c>start /wait</c> 是走 ShellExecuteEx + SEE_MASK_NOCLOSEPROCESS 的,
    ///   它能拿到那个提权后的句柄, 所以"装完再启动"这件事交给 cmd 才成立。
    /// ★ 为什么用 <c>&amp;</c> 而不是 <c>&amp;&amp;</c>: 用户在 UAC 上点了"否"时安装器会返回非 0
    ///   退出码, 但那时**更要**把程序拉回来(否则用户等于把自己的应用弄丢了)。
    /// ★ 万一 <c>start /wait</c> 在某个系统上没等住(命令提前返回), 后果是自愈的:
    ///   我们会被立刻重新拉起, 然后安装器的 /CLOSEAPPLICATIONS 用 Restart Manager 把它关掉,
    ///   /RESTARTAPPLICATIONS 再把它重新拉起来 —— 也就是"早启动一次"而不是"启动不了"。
    ///   这就是那两个参数必须一起带的另一个理由。
    /// </summary>
    private static void LaunchInstallerWithRelaunch(string installerPath)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || !File.Exists(self))
            self = Path.Combine(AppContext.BaseDirectory, "BiliTouch.exe");

        // 引号必须自己加: cmd 的 /c 后面整条是一个字符串, 路径里的空格会把它拆成多个参数。
        // start 的语法是 `start "标题" /wait "程序" 参数…`, 第一个引号参数永远是窗口标题 ——
        // 少了那个空 "" 时, 带空格的 exe 路径会被当成标题, 命令直接失效。
        //
        // ★ 中间那 2 秒(ping -n 3 127.0.0.1)不是凑数: 单实例守卫(SingleInstance)在
        //   新进程启动时抢命名 Mutex, 抢不到就**直接退出**。如果安装器瞬间结束(用户在 UAC
        //   上点了"否"), 我们自己的退出流程可能还没走完 —— 新进程会以为"已经有一个实例在跑",
        //   于是安静地把自己关掉, 用户看到的是"点了更新, 程序没了"。
        //   等两秒让旧进程彻底消失。用 ping 而不是 timeout: timeout 在无控制台的隐藏窗口里
        //   会报"输入重定向不受支持"并立刻返回, 起不到等待作用。
        var command =
            $"/c start \"\" /wait \"{installerPath}\" {SilentArguments} & ping -n 3 127.0.0.1 >nul & start \"\" \"{self}\"";

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = command,
                // 不弹黑框: 这条命令只在我们退出后干活, 用户不该看见它
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex)
        {
            // cmd 拉不起来是极端情况(被组策略禁掉/杀软拦)。此时**退回**直接启动安装器:
            // 装完不会自动回来, 但至少这次更新能装上 —— 总比"什么都发生"好。
            System.Diagnostics.Trace.WriteLine("[Update] 守护命令启动失败, 退回直接启动安装器: " + ex.Message);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = SilentArguments,
                UseShellExecute = true
            });
        }
    }

    /// <summary>
    /// 清掉旧版本的安装包(只保留 <paramref name="keepVersion"/> 那一份)。
    ///
    /// ★ 为什么要清: 每个安装器 ~30MB, 每升一次版就在 <see cref="InstallersDir"/> 里躺一个,
    ///   而且**没有任何界面能删它们**(用户根本不知道有这个目录)。
    /// ★ 为什么不在装完立刻删: 那时文件正被安装器读着, 删不掉; 而且装失败还要靠它重试。
    ///   放在**下次启动/下次检查更新**时清, 时机最安全。
    /// ★ 失败一律忽略: 清理是维护性动作, 清不掉只是多占几十 MB, 不该让任何流程失败。
    /// ★ 线程安全: 本方法可能在后台线程被调用(弹窗丢到 Task.Run 里跑), 而它只做
    ///   "枚举 + 删文件", 不碰任何 WPF/共享状态, 所以不需要 Dispatcher。
    /// </summary>
    public static void CleanupOldInstallers(Version? keepVersion)
    {
        try
        {
            if (!Directory.Exists(InstallersDir)) return;
            // ★ 名字必须与 <see cref="UpdateInfo.SetupFileName"/> 用**同一个规则**(三段, 不带
            //   Revision): 只取 Version.ToString() 的话, 四段 tag 会让"要保留的那份"和
            //   "实际落盘的名字"对不上 —— 结果是刚下好的包被自己删掉。
            var keepName = keepVersion == null
                ? ""
                : $"BiliTouch-{keepVersion.Major}.{keepVersion.Minor}.{Math.Max(keepVersion.Build, 0)}-setup.exe";
            foreach (var file in Directory.EnumerateFiles(InstallersDir))
            {
                var name = Path.GetFileName(file);
                // ★ 用"取回全部文件名再自己判"而不是给 EnumerateFiles 传通配符:
                //   Windows 的通配符对 "*-setup.exe*" 这种**两个星号**的写法有历史怪癖
                //   (它还会按 8.3 短名去匹配), 判据写在代码里最清楚也最好测。
                if (!name.StartsWith("BiliTouch-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.Contains("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;

                // ★ 绝不碰 .part: 那是**正在下载**的中间文件。清理与下载可能同时发生
                //   (清理在窗口打开时后台跑, 用户可能立刻点了下载), 删掉它会让这次下载
                //   在 File.Move 那一步报"找不到文件"。
                if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, keepName, StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("[Update] 清理旧安装包失败: " + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉(被杀软占用/权限不足)就算了: 下次 CleanupOldInstallers 还会再试一次
        }
    }
}
