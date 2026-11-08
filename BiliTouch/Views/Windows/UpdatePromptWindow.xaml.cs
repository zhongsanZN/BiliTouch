using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BiliTouch.Services;
using BiliTouch.Views;

namespace BiliTouch.Views.Windows;

/// <summary>
/// 「发现新版本」弹窗。自动检查(启动后)与手动检查(设置页「检查更新」按钮)共用这一个窗口。
///
/// 按钮语义(2026-11-08 起「下载并安装」是主按钮):
///   · 下载并安装 —— 程序内下载 → 校验(SHA-256 / 大小) → 静默安装 → 装完自动重开;
///   · 前往 GitHub 下载 —— 跳系统浏览器打开这个 Release 页。**保留为次按钮**:
///     内置下载失败(断网/被墙/杀软拦 UAC/公司策略禁止 exe)时那是唯一还能走的路;
///   · 跳过此版本 —— 把版本号记进设置, **自动**检查对这个版本不再弹窗(手动检查时隐藏);
///   · 以后再说 —— 只关窗口, 下次自动检查还会提醒;
///   · 取消下载 —— 只在下载中出现, 取消后一切恢复成下载前的样子。
///
/// ★ 关于"owner 可能是 null": 自动检查在启动 6 秒后的后台任务里弹窗, 那一刻主窗口不一定
///   已经就绪(或被托盘藏起来了)。所以:
///     · 不用 ShowDialog —— 无 owner 的模态窗会让整个应用"卡"在一个没有父子关系的窗口上;
///     · 位置由 <see cref="ShowFor"/> 按 owner 是否存在决定, 别让 XAML 里的 CenterOwner 生效;
///     · Owner 赋值前先判 null, 赋值本身也可能抛(owner 未显示时 WPF 会拒绝)。
/// </summary>
public partial class UpdatePromptWindow : FluentWindow
{
    /// <summary>
    /// 当前这一版的更新信息。
    /// ★ 不是 readonly: <see cref="OnWindowLoaded"/> 里可能补上安装包附件(见 <see cref="ReplaceInfo"/>),
    ///   而 <see cref="UpdateInfo"/> 的属性是 init 的, 补附件只能换一个新实例。
    /// </summary>
    private UpdateInfo _info;

    /// <summary>每次点「下载并安装」都换一个新的 CTS: 取消过的 token 不能复用</summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>下载中(用来挡住重复点击 + 决定哪些按钮该灰掉)</summary>
    private bool _busy;

    /// <summary>
    /// 窗口是否已经关掉。
    /// ★ 需要它是因为下载是异步的: 用户可以在下载途中直接点右上角关闭(关闭按钮我们没拦,
    ///   也不该拦 —— 强行留住一个"正在下载"的窗口比让他关掉更糟)。那时 await 之后继续跑
    ///   的代码会去改一个已关闭窗口的控件、甚至弹 MessageBox.Show(this, …), 后者会抛
    ///   InvalidOperationException。有这面旗子就能在关窗后只写日志、不碰界面。
    /// </summary>
    private bool _closed;

    public UpdatePromptWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        Headline.Text = $"发现新版本 v{info.Version}";
        Subline.Text = $"当前版本 v{App.AppVersion} · {info.Title}";
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? "(这个版本没有填写发布说明)"
            : info.Notes;

        // 有附件字段就直接可用; 没有就先**置灰**等 OnWindowLoaded 去补(见 ApplyPendingInstallerState)。
        // ★ 两条路都保留主按钮: 「这个 Release 没挂安装器」与「这一次没取到附件信息」在构造期
        //   分不出来, 而后者(网络抖动)占绝大多数 —— 为一次抖动永久拿走自动更新入口是过度反应。
        //   真没有安装器时, 点下去会再补一次并明确提示改走浏览器(见 OnInstallClick)。
        if (info.HasSetup) ApplyInstallerState();
        else ApplyPendingInstallerState(enabled: false);

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    /// <summary>
    /// 手动检查也走这个窗口 —— 但手动检查时用户是主动来的, 「跳过此版本」没意义, 会由调用方隐藏。
    ///
    /// ★ 必须有一个**独立的字段**存它, 不能像以前那样拿 BtnSkip.Visibility 当值:
    ///   下载期间 SetBusy 会把「跳过此版本」收起来, 若 getter 读的是控件可见性, 那一刻它
    ///   就变成 false 了 —— 于是"取消下载"之后这个按钮**再也回不来**(一次下载把按钮吃掉)。
    /// </summary>
    public bool AllowSkip
    {
        get => _allowSkip;
        set
        {
            _allowSkip = value;
            // 下载中不要抢 SetBusy 的控制权: 那时按钮本就该收着, 等 SetBusy(false) 再放出来
            if (!_busy) BtnSkip.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private bool _allowSkip = true;

    /// <summary>
    /// 统一入口: 建窗、定位置、显示。自动检查(owner 可能为 null)与手动检查(owner 是主窗口)
    /// 都走这里, 免得两处各写一遍 Owner 逻辑然后其中一处忘了判空。
    ///
    /// ★ <paramref name="owner"/> 传 null 时**不设 Owner**, 并把位置改成 CenterScreen:
    ///   设了 Owner = null 的窗口再写 WindowStartupLocation = CenterOwner, WPF 会退化成
    ///   "左上角(0,0)" —— 弹窗跑到屏幕左上角, 看起来像 bug。
    /// </summary>
    public static UpdatePromptWindow ShowFor(UpdateInfo info, Window? owner, bool allowSkip)
    {
        var win = new UpdatePromptWindow(info) { AllowSkip = allowSkip };

        // ★ Owner 赋值本身会抛: owner 还没 Show 过时 WPF 会报
        //   "Cannot set Owner property to a Window that has not been shown previously"。
        //   自动检查那一刻主窗口理论上已显示, 但"理论上"不足以支撑一个必崩的赋值。
        if (owner != null)
        {
            try
            {
                win.Owner = owner;
                // ★ 全名限定: 本类自己有一个同名属性 WindowStartupLocation, 静态方法里
                //   写短名要靠 C# 的"Color Color"规则才解析成类型 —— 那条规则很容易在
                //   以后有人加个局部变量时悄悄变味。写全名没有歧义。
                win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            }
            catch (InvalidOperationException)
            {
                win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            }
        }
        else
        {
            win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        }

        win.Show();
        return win;
    }

    /// <summary>
    /// 窗口加载后补一次"安装包在哪"。
    ///
    /// ★ 为什么要补: 检查(CheckAsync)与"能不能自动装"是两件事 —— 设置页/自动检查拿到的
    ///   <see cref="UpdateInfo"/> 里没有附件字段(它们只关心"有没有新版")。在这里补,
    ///   就不用把所有调用点都改成 CheckWithInstallerAsync。
    /// ★ 补不到(断网/附件还没传)只是把按钮切成"可点会重试"的样子, **不打扰用户**:
    ///   他本来就没点下载, 这时弹一个"获取安装包信息失败"纯属噪音。
    /// </summary>
    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            CenterIfOwnerless();

            // 顺手清掉历史版本的安装包(只保留当前这一版) —— 每次更新会留下 ~30MB, 而
            // 这个目录没有任何界面能删, 不清就会一直涨。
            // ★ 丢到后台线程: 这一步要枚举目录 + 删几十 MB, 在机械盘/杀软实时扫描下能到几十毫秒,
            //   落在窗口首帧上就是"弹窗先白一下"。清理失败无所谓(CleanupOldInstallers 内部
            //   已经吞掉异常), 所以不需要 await, 也不需要处理结果。
            _ = Task.Run(() => UpdateInstaller.CleanupOldInstallers(_info.Version));

            if (_info.HasSetup) return;

            await ResolveInstallerAsync();
        }
        catch (Exception ex)
        {
            // 自动补齐失败是静默的(理由见方法注释), 但必须留日志: 这是"为什么下载按钮没出现"的唯一线索
            System.Diagnostics.Trace.WriteLine("[Update] 弹窗补齐安装包信息失败: " + ex.Message);
            if (!_closed) ApplyPendingInstallerState(enabled: true);
        }
    }

    /// <summary>
    /// 补一次安装包信息: 成功就点亮主按钮, 失败就保持"可点会重试"。
    /// ★ 抽成一个方法是因为有**两个**调用点: 窗口 Loaded 时补一次(无感), 以及用户
    ///   点了「下载并安装」但手上还没有附件信息时**再补一次**(那时他明确要下载,
    ///   再失败就必须给提示了)。
    /// </summary>
    private async Task<bool> ResolveInstallerAsync()
    {
        var withSetup = await UpdateChecker.AttachInstallerAsync(_info);
        if (_closed) return false;
        if (withSetup == null)
        {
            ApplyPendingInstallerState(enabled: true);
            return false;
        }

        ReplaceInfo(withSetup);
        return true;
    }

    /// <summary>
    /// 没有 owner 时把窗口摆到屏幕中央。
    ///
    /// ★ 为什么不在 XAML 里解决: 那里写的是 CenterOwner, 而 <see cref="ShowFor"/> 只在
    ///   "自己创建窗口"时才会把它改成 CenterScreen。**设置页手动检查**那条路是直接
    ///   <c>new UpdatePromptWindow(...) { Owner = owner }</c> 然后 ShowDialog(见
    ///   SettingsViewModel.CheckUpdateAsync), 而 owner 在极少数情况下也是 null
    ///   (主窗口还没建好就点了检查) —— 那时 WPF 把 CenterOwner 退化成左上角 (0,0),
    ///   弹窗会贴在屏幕左上角, 看起来像程序出了 bug。
    /// ★ 在 Loaded 里手动摆位(而不是改 WindowStartupLocation): 位置在 Show 那一刻就定了,
    ///   Loaded 时再改枚举不会有第二次机会, 只有直接写 Left/Top 才有效。
    /// </summary>
    private void CenterIfOwnerless()
    {
        if (Owner != null) return;
        // ★ 这里必须写全名 System.Windows.WindowStartupLocation: 本窗口有一个同名属性
        //   (WindowStartupLocation), 只写短名时编译器要靠"Color Color"规则才分得清,
        //   而那条规则在以后有人加个局部变量时就会静默变成另一种解释。写全名没有歧义。
        if (WindowStartupLocation != System.Windows.WindowStartupLocation.CenterOwner) return;
        try
        {
            var work = SystemParameters.WorkArea;
            var w = ActualWidth > 0 ? ActualWidth : Width;
            var h = ActualHeight > 0 ? ActualHeight : Height;
            Left = work.Left + (work.Width - w) / 2;
            Top = work.Top + (work.Height - h) / 2;
        }
        catch
        {
            // 拿不到工作区就保持原位置, 不影响功能
        }
    }

    /// <summary>
    /// 把补到的安装包信息换进窗口。
    /// ★ <see cref="UpdateInfo"/> 的属性是 init 的(不可变), 所以补附件只能换一个新实例;
    ///   字段也必须跟着换 —— 只换界面不换字段的话, 点「下载并安装」用的还是那个
    ///   SetupUrl 为空的旧对象, 会在下载第一步就抛"没有可自动安装的安装包"。
    /// </summary>
    private void ReplaceInfo(UpdateInfo info)
    {
        _info = info;
        ApplyInstallerState();
    }

    /// <summary>
    /// 有安装包: 主按钮可用, 提示文案说清会发生什么
    /// </summary>
    private void ApplyInstallerState()
    {
        BtnInstall.IsEnabled = true;
        BtnInstall.Visibility = Visibility.Visible;
        BtnInstall.Content = "下载并安装";
        BtnInstall.ToolTip = _info.SetupSize > 0
            ? $"自动下载安装包({FormatSize(_info.SetupSize)})并静默安装, 完成后重新打开 BiliTouch"
            : "自动下载安装包并静默安装, 完成后重新打开 BiliTouch";
    }

    /// <summary>
    /// 还没有安装包信息(走 CheckAsync 拿到的 info 没有附件字段), 但按钮**保持可点**:
    /// 点下去会再补一次(见 <see cref="OnInstallClick"/> 开头的重试)。
    ///
    /// ★ 为什么补失败时不把按钮摘掉: 补失败的原因分不清 —— 可能是"这个 Release 真的没挂
    ///   安装器", 也可能只是**这一次**请求超时/被墙。后者占绝大多数, 而摘掉按钮等于
    ///   因为一次网络抖动就永久拿走用户的自动更新入口, 他只能自己去 GitHub 找包。
    ///   保留可点 + 点下去重试, 两种情况都能给出正确答案。
    /// ★ 为什么先置灰(构造期)再点亮(Loaded 后): 构造期窗口还没上屏, 此时若已经知道有安装包
    ///   就直接点亮(ApplyInstallerState), 否则给个"正在获取…"的灰态, 避免用户点在一个
    ///   还没准备好的按钮上。Loaded 里补失败后回到本方法 → 变成"可点 + 会重试"。
    /// </summary>
    private void ApplyPendingInstallerState(bool enabled)
    {
        BtnInstall.IsEnabled = enabled;
        BtnInstall.Visibility = Visibility.Visible;
        BtnInstall.Content = "下载并安装";
        BtnInstall.ToolTip = enabled
            ? "点击后先获取安装包信息, 再自动下载并静默安装"
            : "正在获取安装包信息…";
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        Svc.Settings.SetSkippedVersion(_info.Version.ToString());
        Close();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => Close();

    private void OnGithubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // ReleaseUrl 理论上永远非空(UpdateChecker 会回落到 ReleasePageUrl),
            // 但空串会让 Process.Start 抛一个"找不到文件"的诡异异常, 这里挡一下。
            var url = string.IsNullOrWhiteSpace(_info.ReleaseUrl)
                ? UpdateChecker.ReleasePageUrl
                : _info.ReleaseUrl;
            UpdateChecker.OpenInBrowser(url);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            Svc.Toast.Show("打不开浏览器, 请手动访问 GitHub Releases 页");
        }
    }

    /// <summary>
    /// 主按钮: 下载 → 校验 → 静默安装 → 关自己 → 装完自动重开。
    /// ★ 必须是 async void(事件处理器), 所以整个方法体包在 try/catch 里 —— 漏一处
    ///   就是"点下载按钮应用直接闪退", 而且错误不会进日志。
    /// </summary>
    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // ★ 手上还没有安装包信息(窗口刚打开时补失败/还在补): 用户已经明确点了下载,
        //   这时再补一次, 失败就**必须**给提示 —— 与 Loaded 时的静默完全不同。
        if (!_info.HasSetup)
        {
            // ★ 这里不能用 SetBusy: 它会亮出「取消下载」, 而这一步没有可取消的东西
            //   (补信息走的是 UpdateChecker 里那个 10 秒超时的 HttpClient, 没有 token 可传)。
            //   给一个点了没反应的取消按钮, 比不给更糟。
            // ★ 但仍要占住 _busy: 补信息这几百毫秒里用户完全可能再点两下,
            //   不挡就会出现两条并行的 ResolveInstallerAsync 互相覆盖 _info。
            _busy = true;
            SetStatus("正在获取安装包信息…");
            BtnInstall.IsEnabled = false;

            bool ok;
            try
            {
                ok = await ResolveInstallerAsync();
            }
            catch (Exception ex)
            {
                ok = false;
                App.ReportError(ex);
            }

            _busy = false;
            if (_closed) return;
            ProgressArea.Visibility = Visibility.Collapsed;
            BtnInstall.IsEnabled = true;

            if (!ok)
            {
                Svc.Toast.Show("获取安装包信息失败, 请稍后重试或改用「前往 GitHub 下载」");
                return;
            }
            // 补到了就停在这里, 让用户再点一次: 直接接着下载的话, 他刚看到的是
            // "获取信息"的提示, 然后进度条突然从 0 开始跑 —— 因果不清, 还容易误以为
            // 第一次点击被吞了。再点一次是明确的二次确认。
            return;
        }

        // ★ 每次点击都新建 CTS: 上一次取消过的 token 已经是 Canceled 状态, 复用会立刻抛
        //   OperationCanceledException(表现是"第二次点下载毫无反应")。
        _downloadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        SetBusy(true, "准备下载…");

        // ★ 进度回调一定要回 UI 线程: Progress<double> 捕获的是**创建它时**的同步上下文,
        //   这里在 UI 线程创建, 所以 Report 会自动派发回来。别改成 Action<double> ——
        //   那会在下载线程上直接改控件, 抛"调用线程无法访问此对象"。
        var progress = new Progress<double>(p => OnDownloadProgress(p));

        try
        {
            await UpdateInstaller.InstallAsync(_info, progress, cts.Token);

            // ★ InstallAsync 成功时已经把 Application.Shutdown() **排进消息队列**了
            //   (见它里面的说明: 不能在 ShowDialog 的模态帧里同步 Shutdown)。
            //   所以这里**绝对不能再补一次 Shutdown** —— 那等于把刚绕开的那个问题又做一遍。
            //   本方法干净返回, 让当前这一帧先结束, 队列里的 Shutdown 紧接着就会执行。
            return;
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消: 这不是错误, 不写日志不弹框, 只是回到下载前的样子。
            // IsExiting 一律置回 false: 取消可能发生在"已置位、还没启动安装器"的窗口期。
            App.IsExiting = false;
            if (_closed) return;
            SetBusy(false, null);
            // 取消后进度区要收起来(SetBusy(false) 刻意不收起它, 因为失败提示要留在屏幕上)
            ProgressArea.Visibility = Visibility.Collapsed;
            Svc.Toast.Show("已取消下载");
        }
        catch (Exception ex)
        {
            // ★ 真正的失败(下载/校验/启动安装器): 必须提示 + 留日志。
            //   这里**不能**静默 —— 用户是主动点的按钮, 什么都不发生等于"按钮坏了"。
            // ★ 先回滚 IsExiting: InstallAsync 内部也会回滚, 但"启动安装器成功后 Shutdown 失败"
            //   这条路上它不会(那时它认为已经成功了), 留在这里兜住 —— 否则这次更新失败之后,
            //   用户点关闭键会**真的退出**, 而设置里明明写着"关闭时最小化到托盘"。
            App.IsExiting = false;
            App.ReportError(ex);
            // 窗口已经被用户关掉时不要再碰界面: MessageBox.Show(owner, …) 对着已关闭的窗口
            // 会抛 InvalidOperationException, 那会把"更新失败"变成"应用崩了"。
            if (_closed) return;
            SetBusy(false, null);
            ShowDownloadFailure(ex);
        }
    }

    /// <summary>取消按钮: 只取消下载, 不关窗口(用户可能想改走浏览器)</summary>
    private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _downloadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 下载刚好结束并释放了 CTS —— 此时取消已经没有意义, 忽略即可
        }
    }

    /// <summary>
    /// 进度回调。除了推进度条, 还顺带把标题换成"正在下载 vX.Y.Z…",
    /// 让用户在窗口被挡住一半时也能一眼看出"在干活"。
    /// </summary>
    private void OnDownloadProgress(double value)
    {
        // 窗口已经关掉: 控件对象还在(下载链持有引用), 但用户看不到, 更新它们只是白费
        if (_closed) return;

        var percent = (int)Math.Round(Math.Clamp(value, 0, 1) * 100);
        DownloadProgress.Value = percent;

        var total = _info.SetupSize > 0
            ? $" / {FormatSize(_info.SetupSize)}"
            : "";
        ProgressText.Text = percent >= 100
            ? "下载完成, 正在校验并启动安装程序…"
            : $"正在下载 v{_info.Version} · {percent}%{total}";
    }

    /// <summary>
    /// 只显示一行状态(不进入"下载中"状态)。
    /// ★ 给"获取安装包信息"这一步用: 那时没有进度可报, 也没有可取消的东西,
    ///   走 <see cref="SetBusy"/> 会连带亮出「取消下载」—— 一个点了没反应的按钮。
    /// </summary>
    private void SetStatus(string status)
    {
        ProgressArea.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        ProgressText.Text = status;
    }

    /// <summary>
    /// 进入/退出"下载中"状态。
    /// ★ 关掉的不只是主按钮: 「前往 GitHub」也要禁 —— 下载中开浏览器会让用户以为
    ///   内置下载已经放弃了, 于是自己去下第二个包; 而「跳过此版本」「以后再说」在下载中
    ///   直接**隐藏**(不是禁用): 它们在这个阶段没有意义, 留着只会让底部挤成四个按钮。
    ///   留下「取消下载」, 因为那是此刻唯一有意义的动作。
    /// </summary>
    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;

        BtnInstall.IsEnabled = !busy;
        BtnGithub.IsEnabled = !busy;

        // 下载中: 跳过/以后再说 收起, 取消下载 出现(两者互斥, 位置不变)
        BtnSkip.Visibility = busy ? Visibility.Collapsed : (AllowSkip ? Visibility.Visible : Visibility.Collapsed);
        BtnLater.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        BtnCancelDownload.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        // 进度区只在"真的在下载"和"失败后留结论"这两种情况下显示(后者见 ShowDownloadFailure)。
        // 这里 busy=false 时**不强行收起** —— 失败提示要留在屏幕上, 由 ShowDownloadFailure
        // 自己设成 Visible; 成功路径根本走不到这儿(进程已经在退出了)。
        if (busy) ProgressArea.Visibility = Visibility.Visible;

        if (busy)
        {
            DownloadProgress.Value = 0;
            ProgressText.Text = status ?? "正在下载…";
            BtnInstall.Content = "下载中…";
        }
        else
        {
            BtnInstall.Content = "下载并安装";
        }
    }

    /// <summary>
    /// 失败提示。
    /// ★ 用 MessageBox 而不是只发 Toast: Toast 挂在**主窗口**右上角(见 MainWindow 的 ShowToast),
    ///   而这个弹窗是独立窗口、还可能是模态的 —— 用户盯着弹窗时根本看不到主窗口的角标,
    ///   表现就是"点了下载, 什么都没发生"。同时补一条 Toast, 让关掉弹窗后仍能看到结论。
    /// ★ 文案必须给出下一步(「前往 GitHub 下载」), 不能只说"失败"。
    /// </summary>
    private void ShowDownloadFailure(Exception ex)
    {
        var reason = ex switch
        {
            System.IO.InvalidDataException => "安装包校验失败(下载内容不完整或被篡改)",
            // ★ FileNotFoundException 必须排在 IOException 前面: 它派生自 IOException,
            //   排在后面就永远走不到 —— 用户会看到"写入安装包失败"这种和实际情况
            //   (安装器文件不见了)完全对不上的提示。
            System.IO.FileNotFoundException => "安装包文件丢失, 请重新下载",
            System.Net.Http.HttpRequestException => "下载失败, 请检查网络后重试",
            System.IO.IOException => "写入安装包失败, 请检查磁盘空间与杀毒软件",
            UnauthorizedAccessException => "没有权限写入安装包目录, 请检查杀毒软件设置",
            _ => "更新失败: " + ex.Message
        };

        var text = $"{reason}\n\n可以重试, 或点「前往 GitHub 下载」手动安装。";

        // ★ 失败后进度区必须**留在屏幕上**并写明原因: 收起后窗口看起来和"什么都没发生过"
        //   一样, 用户会怀疑自己到底点没点上。进度条清零是因为这次下载已经没有进度可言。
        //   (SetBusy(false) 刻意不收起进度区, 正是为了这里能直接写文案。)
        DownloadProgress.Value = 0;
        ProgressText.Text = reason;
        ProgressArea.Visibility = Visibility.Visible;

        Svc.Toast.Show(reason);
        MessageBox.Show(this, text, "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>人类可读的字节数(只用在这一个窗口的提示里, 不值得为它建一个工具类)</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:0.0} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.0} MB";
        return $"{bytes / 1024.0:0} KB";
    }

    /// <summary>
    /// 关窗时取消还没结束的下载。
    /// ★ 不取消的话下载线程会继续跑完 30MB(用户已经不要了), 然后在一个已关闭的窗口上
    ///   改控件 —— 那时控件还在内存里(CTS 持有引用), 不崩但白耗流量与时间。
    /// ★ 只 Cancel 不 Dispose: Dispose 会让还在 await 的那条链上的
    ///   <see cref="CancellationToken"/> 在检查 IsCancellationRequested 时抛
    ///   ObjectDisposedException(而不是干净的 OperationCanceledException), 于是"用户关窗"
    ///   会变成一条写着"ObjectDisposedException"的错误日志。真正释放交给 GC ——
    ///   CTS 不持有非托管资源, 为它纠结 Dispose 时机会得不偿失。
    /// </summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        try
        {
            _downloadCts?.Cancel();
        }
        catch
        {
            // 关窗路径上的清理失败不该再抛(Closed 里抛异常会变成未处理异常)
        }
    }
}
