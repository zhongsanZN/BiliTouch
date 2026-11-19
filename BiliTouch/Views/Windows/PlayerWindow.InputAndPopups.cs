using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Views.Controls;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliTouch.Views;

public partial class PlayerWindow
{
    // ------------------------------------------------------------ 音量弹层(2026-11-05 与直播间对齐)

    /// <summary>
    /// 喇叭按钮: 收起态 = 静音开关; 展开态 = 静音切换 + 弹层留着让用户拖。
    /// ★ 触屏没有 hover, 所以**第一下点按固定是"展开"**: 让手指够得到滑杆;
    ///   展开之后再点才是静音切换。鼠标用户可以完全不点 —— 悬停就展开了。
    /// ★ 为什么收起态保留静音语义: 静音是最高频的音量动作, 藏进弹层等于多两次点击。
    /// </summary>
    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_closing || VolumePopup == null) return;
        if (!VolumePopup.IsOpen)
        {
            _volumePinned = true;
            ShowVolumePopup();
            return;
        }
        SetVolume(_volumeCache == 0 ? 80 : 0);
        HideVolumePopupSoon();
    }

    private void OnVolumeHoverEnter(object sender, MouseEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e)) return;   // 触屏由点击处理, 忽略合成鼠标事件
        _volumeHover = true;
        ShowVolumePopup();
    }

    private void OnVolumeHoverLeave(object sender, MouseEventArgs e)
    {
        _volumeHover = false;
        HideVolumePopupSoon();
    }

    private void ShowVolumePopup()
    {
        if (_closing || VolumePopup == null || VolumeSlider == null) return;
        _volumeCloseTimer?.Stop();
        ShowControls();
        VolumePopup.PlacementTarget = BtnVolume;
        VolumePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        if (VolumePopupText != null) VolumePopupText.Text = _volumeCache + "%";
        VolumePopup.IsOpen = true;
        // 正在操作就别让自动隐藏把控制栏(连带弹层)收走
        _controlsHideTimer?.Stop();
    }

    /// <summary>
    /// 延迟一轮再决定收不收: 从喇叭挪到弹层的那一瞬间两者都不在 IsMouseOver,
    /// 立刻收会把"想拖滑杆"的用户挡在门外。
    /// </summary>
    private void HideVolumePopupSoon()
    {
        if (_closing || _volumePinned || _volumeHover) return;
        _volumeCloseTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _volumeCloseTimer.Tick -= OnVolumeCloseTick;
        _volumeCloseTimer.Tick += OnVolumeCloseTick;
        _volumeCloseTimer.Stop();
        _volumeCloseTimer.Start();
    }

    private void OnVolumeCloseTick(object? sender, EventArgs e)
    {
        _volumeCloseTimer?.Stop();
        if (_closing || _volumeHover || _volumePinned || BtnVolume.IsMouseOver ||
            VolumePopup.Child is { IsMouseOver: true } or { IsMouseCaptureWithin: true }) return;
        VolumePopup.IsOpen = false;
    }

    private void CloseVolumePopup()
    {
        _volumeHover = false; _volumePinned = false;
        _volumeCloseTimer?.Stop();
        if (VolumePopup != null) VolumePopup.IsOpen = false;
    }

    /// <summary>一次音量调节的步进。与方向键上下保持一致, 滚轮手感不至于一格跳太多。</summary>
    private const int VolumeStep = 5;

    /// <summary>音量提示浮层的停留时长</summary>
    private static readonly TimeSpan VolumeOsdHold = TimeSpan.FromMilliseconds(1100);

    private DispatcherTimer? _volumeOsdTimer;

    // ---- 音量弹层(2026-11-05 与直播间对齐) ----
    // ★ 为什么改成"悬停/点一下才向上展开": 控制栏里常驻一条 78px 的滑杆, 窗口一窄就把
    //   倍速/清晰度/弹幕按钮往右挤, 甚至挤到全屏按钮外面去。翻上去只借用画面空白。
    // ★ 收起态那颗喇叭仍然是静音开关 —— 那是最高频的动作, 不该藏进弹层。
    /// <summary>鼠标是否压在喇叭上(悬停展开的存活条件)</summary>
    private bool _volumeHover;
    /// <summary>用户主动点开过弹层(触屏没有 hover, 只能靠点击), 点开后在点别处前不自动收</summary>
    private bool _volumePinned;
    private DispatcherTimer? _volumeCloseTimer;

    /// <summary>倍速提示的淡出计时器(松手后"已恢复 1.0x"亮一下就靠它收场)</summary>
    private DispatcherTimer? _speedOsdTimer;

    /// <summary>
    /// 滚轮调音量: 上滑 +5, 下滑 -5。
    ///
    /// 为什么用 PreviewMouseWheel 而不是 MouseWheel:
    /// 滚轮是**隧道**事件, Preview 那条从根往叶走, 挂了就能在底部控制栏的 Slider、
    /// 清晰度 Popup 的 ListBox 之前拿到事件; 否则鼠标压在控制栏上滚轮就没反应。
    /// 只挂视频区(和覆盖它的那层浮动窗口, 见 HookOverlayKeyboard) ——
    /// 鼠标在右侧信息面板/评论区时, 滚轮该滚那个列表。
    ///
    /// ⚠ **必须同时挂到 VideoView 的 ForegroundWindow**: 视频画面是被那层独立的顶层窗口
    /// (airspace)盖住的, 鼠标压在画面上时滚轮事件根本进不了主窗口的 WPF 树 ——
    /// 用户报的"滚轮调音量不灵敏"就是这个: 只有压在控制栏那一小条上才有效。
    /// 与键盘失灵、鼠标侧键失灵是同一个坑。
    ///
    /// 步进用**累计**而不是"每个事件至少一步": 标准鼠标一格 ±120, 而触控板/精密滚轮会给
    /// 一串 ±1~±40 的小值 —— 后者按"每事件至少一步"算, 轻轻一划就跳十几个百分点,
    /// 反过来显得"太灵/乱跳"。累计到一整格才走一步, 两种设备的手感就都对了。
    /// </summary>
    private void OnVideoAreaMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mp == null || _closing || e.Delta == 0) return;

        // 触控板捏合经 Ctrl+滚轮进入，与触屏一样只控制全屏状态。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            HandleTrackpadPinch(e.Delta);
            e.Handled = true;
            return;
        }

        if (!IsTouchControl(e.OriginalSource as DependencyObject) && HandleWebVideoWheel(e.Delta)) { e.Handled = true; return; }
        var wheelSource = e.OriginalSource as DependencyObject;
        if (IsInPopup(wheelSource, SpeedPopup) || IsInPopup(wheelSource, QualityPopup) || IsInPopup(wheelSource, DanmakuAreaPopup))
            return;
        // 反向滚动时先清掉上次攒的余量, 否则要先"抵消"掉才动
        if (Math.Sign(e.Delta) != Math.Sign(_wheelAccum)) _wheelAccum = 0;
        _wheelAccum += e.Delta;

        var notches = _wheelAccum / 120;
        if (notches != 0)
        {
            _wheelAccum -= notches * 120;
            // 基数用 _volumeCache 而不是 _mp.Volume: 见 _volumeCache 的说明(aout 未就绪时原生读回是 -1/旧值)
            SetVolume(_volumeCache + notches * VolumeStep);
        }
        // 不够一格也要吃掉: 否则事件继续冒泡给别的可滚动控件(视频区里其实没有, 但别留隐患)
        e.Handled = true;
    }

    // 一次触控板缩放流只执行一次；小 delta 累计到一格后再触发。
    private int _pinchWheelAccum;
    private bool _pinchWheelConsumed;
    private DispatcherTimer? _pinchWheelIdle;
    private void HandleTrackpadPinch(int delta)
    {
        if (_closing || delta == 0) return;
        _wheelAccum = 0;
        _pinchWheelIdle ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _pinchWheelIdle.Tick -= OnPinchWheelIdle;
        _pinchWheelIdle.Tick += OnPinchWheelIdle;
        _pinchWheelIdle.Stop();
        _pinchWheelIdle.Start();
        if (_pinchWheelConsumed) return;
        if (Math.Sign(delta) != Math.Sign(_pinchWheelAccum)) _pinchWheelAccum = 0;
        _pinchWheelAccum += delta;
        if (Math.Abs(_pinchWheelAccum) < 120) return;
        var enter = _pinchWheelAccum > 0;
        _pinchWheelConsumed = true;
        _pinchWheelAccum = 0;
        if (_isFullscreen != enter) ToggleFullscreen();
    }
    private void OnPinchWheelIdle(object? sender, EventArgs e)
    {
        _pinchWheelIdle?.Stop();
        _pinchWheelConsumed = false;
        _pinchWheelAccum = 0;
    }

    /// <summary>滚轮累计的余量(±120 为一格)。见 OnVideoAreaMouseWheel 的说明</summary>
    private int _wheelAccum;

    /// <summary>
    /// 应用侧的音量权威值(0~100)。
    ///
    /// 为什么不直接读 _mp.Volume(反编译 LibVLCSharp 3.8.0 确认):
    /// getter 是纯原生调用 libvlc_audio_get_volume, **没有 aout(音频输出)时返回 -1**;
    /// setter 在无 aout 时也是静默丢弃。aout 的生命周期跟媒体流走 ——
    /// 长视频走远端 DASH 双流, 起播/缓冲/拖动/换清晰度期间 aout 可能尚未就绪或刚被重建,
    /// 这时拿 _mp.Volume 当滚轮基数, 基数就是 -1/旧值, 步进全乱(用户报的"长视频滚轮调音量不灵敏")。
    /// 短视频是本地合流文件, aout 几乎秒建, 所以从来感觉不到 —— 时长相关性由此而来。
    /// 滚轮/键盘/滑块一律以本字段为基数, 原生侧只在 SetVolume 里单向写、在 Playing(aout 就绪)时补写。
    /// </summary>
    private int _volumeCache = 80;

    /// <summary>
    /// 设置音量: 同步 LibVLC 与滑块, 并弹出音量提示。
    /// 统一入口是必要的 —— 之前 ↑↓/静音键各自改一遍 _mp.Volume 和 VolumeSlider.Value,
    /// 再加一条滚轮路径就会变成三份容易走偏的代码。
    /// </summary>
    private void SetVolume(int volume, bool showHint = true)
    {
        if (_closing || _mp == null) return;
        var v = Math.Max(0, Math.Min(100, volume));
        // 权威值先落账: 之后所有路径(滚轮/键盘)都以它为基数, 不依赖原生读回(见 _volumeCache 的说明)
        _volumeCache = v;
        UpdateVolumeChrome(v);
        // ★ 音量继承: 用户每调一次就落盘, 下一个视频/下一次打开都用它。
        //   放在这里而不是各处调用点 —— SetVolume 是滚轮/键盘/滑块的唯一入口。
        //   SetPlayerVolume 内部有"值没变就不写"的短路, 连滚到同一值不会反复写盘。
        Svc.Settings.SetPlayerVolume(v);
        // seek 静音期间用户调音量: 把新值记为"恢复目标", 声音仍保持静音直到画面就绪 ——
        // 若直接写 _mp.Volume 会立刻出声(又变成画面没到声音先到), 而且恢复时还会覆盖用户的值。
        if (_seekMuteSavedVolume >= 0)
        {
            _seekMuteSavedVolume = v;
            try { _mp.Volume = 0; } catch { }
            if (VolumeSlider != null) VolumeSlider.Value = v;
            if (showHint) ShowVolumeHint(v);
            return;
        }
        try { _mp.Volume = v; } catch { return; }
        if (VolumeSlider != null) VolumeSlider.Value = v;
        if (showHint) ShowVolumeHint(v);
    }

    /// <summary>
    /// 把音量同步到控制栏的两处可见反馈: 喇叭图标(有声/静音)与弹层里的百分比。
    /// ★ 之前音量只体现在那条常驻滑杆的位置上, 现在滑杆收进弹层了, 图标必须自己会变,
    ///   否则收起状态下用户完全看不出自己是不是被静音了。
    /// </summary>
    private void UpdateVolumeChrome(int volume)
    {
        if (VolumeIcon != null) VolumeIcon.Text = PlayerGlyphs.VolumeGlyph(volume <= 0);
        if (VolumePopupText != null) VolumePopupText.Text = Math.Clamp(volume, 0, 100) + "%";
    }

    /// <summary>
    /// 浮出音量提示。调音量时画面之外没有任何反馈(音量条在底部偶尔还是自动隐藏的),
    /// 所以给一个"音量 65%"的短提示; 连续调节时只重置计时器, 不重播淡入动画,
    /// 否则每滚一格都闪一下。
    /// </summary>
    /// <summary>
    /// 浮出倍速提示(长按快进开始 / 恢复 1x 时调用)。
    ///
    /// ★ 与音量提示的差别(所以没有复用 ShowVolumeHint):
    ///   音量是"调一下、闪一下、自己淡出"; 倍速提示必须在**整个长按期间一直挂着** ——
    ///   用户按住的那几秒里要始终知道"现在是 2 倍速, 松手会回去"。
    ///   松手时再调一次本方法(<c>hold=false</c>), 文案换成"已恢复 1.0x"并淡出。
    /// </summary>
    /// <param name="text">提示文案, 如 "2.0x 快进中" / "已恢复 1.0x"</param>
    /// <param name="hold">true = 常显不自动淡出(长按期间); false = 显示一下自己淡出</param>
    private void ShowSpeedHint(string text, bool hold)
    {
        if (_closing || SpeedOsd == null) return;

        SpeedOsdText.Text = text;
        _speedOsdTimer?.Stop();
        _speedOsdTimer = null;

        // 每次都从"当前透明度"补到 1(同 ShowVolumeHint 的理由:
        // 上一次的淡出可能还没跑完, 从当前值补最稳, 不会出现"提示淡到一半卡住")
        SpeedOsd.Visibility = Visibility.Visible;
        SpeedOsd.BeginAnimation(OpacityProperty, null);
        // ★★★ 淡入动画**绝不能写 FillBehavior=Stop**(2026-10-14 修"缓存中长按 2x,
        //   缓存结束后倍速提示消失"): XAML 里 SpeedOsd 的基准值是 `Opacity="0"`,
        //   而 Stop 语义是"播完动画把属性还原成**基准值**" —— 于是 120ms 淡入一结束,
        //   Opacity 立刻掉回 0, 提示虽然 Visibility=Visible 却整块透明。
        //   探针 bd-probe-speedosd 实测: 第一次显示 +180ms 后 Opacity=0.00。
        //   ★ 这解释了为什么"第二次显示是好的": 上一次淡出的 Completed 里写了
        //     `Opacity = 1`, 把基准值改成了 1, 之后所有淡入的 Stop 都还原到 1。
        //     ⇒ 只有**每个进程的第一次显示**中招, 与"缓存期间长按"完全对得上
        //       (缓存期间恰好是打开视频后的第一次长按)。
        //   ★ 与 ShowVolumeHint 保持一致: 它的淡入也没写 FillBehavior(默认 Hold)。
        SpeedOsd.BeginAnimation(OpacityProperty,
            new DoubleAnimation(Math.Min(1.0, SpeedOsd.Opacity), 1, TimeSpan.FromMilliseconds(120)));

        if (hold) return;   // 长按期间: 挂着等松手

        // 松手了: 亮一下"已恢复"再淡出
        _speedOsdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _speedOsdTimer.Tick += (_, _) =>
        {
            _speedOsdTimer?.Stop();
            _speedOsdTimer = null;
            if (_closing) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            fade.Completed += (_, _) =>
            {
                // 淡出期间用户可能又按住了(ShowSpeedHint 已重开): 那时不能藏
                if (_closing || _speedOsdTimer != null) return;
                // ★ 摘动画 + 归位(2026-10-13 清理动画 bug 时的同款问题):
                //   DoubleAnimation 默认 Hold 会把 Opacity 钉在 0, 下次常显就整块透明。
                SpeedOsd.BeginAnimation(OpacityProperty, null);
                SpeedOsd.Opacity = 1;
                SpeedOsd.Visibility = Visibility.Collapsed;
            };
            SpeedOsd.BeginAnimation(OpacityProperty, fade);
        };
        _speedOsdTimer.Start();
    }

    private void ShowVolumeHint(int volume)
    {
        if (_closing || VolumeOsd == null) return;

        VolumeOsdText.Text = volume + "%";
        // 0 用静音图标, 其余用喇叭: 一眼就能看出"是没声还是声音小"
        VolumeOsdIcon.Text = volume == 0 ? "\uE74F" : "\uE767";

        // 每次都从"当前透明度"补到 1。
        // 不要在"已经 Visible 就跳过"上做判断 —— 淡出动画还没跑完时(计时器已被置空)
        // 用户再滚一下, 浮层仍是 Visible 于是不重播, 结果它继续淡到 0 卡住,
        // 用户反而看不到任何音量反馈。从当前值补到 1 在这两种情况下都正确:
        // 已经在 1 上就是原地不动(连滚不闪), 淡出中途则接着淡回来。
        VolumeOsd.Visibility = Visibility.Visible;
        VolumeOsd.BeginAnimation(OpacityProperty,
            new DoubleAnimation(Math.Min(1.0, VolumeOsd.Opacity), 1, TimeSpan.FromMilliseconds(120)));

        _volumeOsdTimer?.Stop();
        _volumeOsdTimer = new DispatcherTimer { Interval = VolumeOsdHold };
        _volumeOsdTimer.Tick += (_, _) =>
        {
            _volumeOsdTimer?.Stop();
            _volumeOsdTimer = null;
            if (_closing) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            fade.Completed += (_, _) =>
            {
                // 淡出期间用户又调了一次 -> ShowVolumeHint 已经重开计时器并重播淡入,
                // 这时绝对不能把浮层藏掉(表现就是"滚轮调音量提示闪一下就不见了")
                if (_closing || _volumeOsdTimer != null) return;
                // ★ 摘动画 + 归位(2026-10-13 修): 原来这里是裸 Collapsed,
                //   而 DoubleAnimation 默认 FillBehavior=Hold ⇒ Opacity 被钉死在 0,
                //   下次滚轮时浮层虽 Visible 却整块透明 —— 提示像"消失"了一样。
                VolumeOsd.BeginAnimation(OpacityProperty, null);
                VolumeOsd.Opacity = 1;
                VolumeOsd.Visibility = Visibility.Collapsed;
            };
            VolumeOsd.BeginAnimation(OpacityProperty, fade);
        };
        _volumeOsdTimer.Start();
    }

    /// <summary>隐藏音量提示并停掉它的计时器(关闭流程用)</summary>
    private void StopVolumeOsd()
    {
        _volumeOsdTimer?.Stop();
        _volumeOsdTimer = null;
    }

    /// <summary>隐藏倍速提示并停掉它的计时器(关闭流程用)</summary>
    private void StopSpeedOsd()
    {
        _speedOsdTimer?.Stop();
        _speedOsdTimer = null;
    }

    /// <summary>允许的最低/最高倍速。范围写在这里一处, 输入夹取与预设列表都引用它。</summary>
    private const float MinPlaybackRate = 0.25f;
    private const float MaxPlaybackRate = 4.0f;

    /// <summary>常用预设档位(顺序即弹层里的顺序)。</summary>
    private static readonly float[] SpeedPresets =
    {
        0.25f, 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f, 2.5f, 3.0f, 4.0f
    };

    /// <summary>填充倍速列表时抑制 SelectionChanged, 避免"打开弹层就自动改一次倍速"。</summary>
    private bool _fillingSpeedMenu;

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        if (_mp == null) return;
        TogglePlayerPopup(SpeedPopup, FillSpeedMenu);
    }

    /// <summary>
    /// 填充预设倍速列表并高亮当前倍速, 同时把当前值预填进自定义输入框。
    /// </summary>
    private void FillSpeedMenu()
    {
        if (SpeedList == null) return;
        _fillingSpeedMenu = true;
        try
        {
            SpeedList.Items.Clear();
            foreach (var rate in SpeedPresets)
                SpeedList.Items.Add(new ListBoxItem { Tag = rate, Content = FormatRate(rate) });

            var cur = _mp?.Rate ?? 1.0f;
            if (cur <= 0) cur = 1.0f;
            if (SpeedCustomText != null) SpeedCustomText.Text = FormatRateValue(cur);
            if (SpeedCustomHint != null) SpeedCustomHint.Text = "";

            // 只有与某个预设一致才高亮; 自定义值(如 1.15x)保持无选中
            var idx = Array.FindIndex(SpeedPresets, r => Math.Abs(r - cur) < 0.005f);
            SpeedList.SelectedIndex = idx;
        }
        finally
        {
            _fillingSpeedMenu = false;
        }
    }

    private void OnSpeedSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSpeedMenu || SpeedPopup == null) return;
        if (SpeedList.SelectedItem is not ListBoxItem li || li.Tag is not float rate) return;
        ApplyPlaybackRate(rate, closePopup: true);
    }

    private void OnSpeedCustomApply(object sender, RoutedEventArgs e) => ApplyCustomSpeed();

    /// <summary>自定义输入框: Enter 应用, Esc 关闭弹层(不改变当前倍速)。</summary>
    private void OnSpeedCustomKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyCustomSpeed();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SpeedPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void ApplyCustomSpeed()
    {
        if (_mp == null) return;
        if (!TryParseSpeed(SpeedCustomText?.Text, out var rate))
        {
            ShowSpeedCustomHint($"请输入 {FormatRate(MinPlaybackRate)} – {FormatRate(MaxPlaybackRate)} 之间的数字, 例如 1.25");
            return;
        }
        // 超出范围自动夹到边界(而不是拒绝): 用户输入 10x 时给他 4x 比什么都不做更符合预期
        ApplyPlaybackRate(Math.Clamp(rate, MinPlaybackRate, MaxPlaybackRate), closePopup: true);
    }

    /// <summary>
    /// 唯一的倍速应用入口(预设 / 自定义共用)。
    /// ★ 顺序: 先让弹幕时钟按新倍速落定当前值, 再真正 SetRate ——
    ///   反过来先 SetRate 的话, SyncDanmakuClockRate 内部那句 DanmakuClock() 会
    ///   用**旧速率**外推(而媒体已经按新速率跑了), 那一下就是用户看到的"顿一下"。
    /// </summary>
    private void ApplyPlaybackRate(float rate, bool closePopup)
    {
        if (_mp == null) return;
        // 保留两位小数: 避免自定义输入留下 1.1499999 这种值, 显示与存储都不好看
        rate = Math.Clamp(MathF.Round(rate, 2), MinPlaybackRate, MaxPlaybackRate);

        SyncDanmakuClockRate(rate);
        try { _mp.SetRate(rate); }
        catch
        {
            ShowSpeedCustomHint("设置倍速失败, 播放器可能正在切换片源");
            return;
        }

        UpdateSpeedText(rate);
        if (SpeedCustomHint != null) SpeedCustomHint.Text = "";
        if (closePopup && SpeedPopup != null) SpeedPopup.IsOpen = false;
    }

    /// <summary>把控制栏上的倍速文字同步成实际速率(不传 = 读播放器当前值)。</summary>
    private void UpdateSpeedText(float? rate = null)
    {
        var r = rate ?? _mp?.Rate ?? 1.0f;
        if (r <= 0) r = 1.0f;
        if (SpeedText != null) SpeedText.Text = FormatRate(r);
    }

    /// <summary>带 x 后缀的显示文字, 如 1.0x / 1.25x / 4.0x。</summary>
    private static string FormatRate(float rate)
        => FormatRateValue(rate) + "x";

    /// <summary>纯数字显示(用于自定义输入框预填), 如 1.0 / 1.25。</summary>
    private static string FormatRateValue(float rate)
        => rate.ToString("0.0##", CultureInfo.InvariantCulture);

    /// <summary>解析用户输入的倍速: 允许 "1.25"、"1.25x"、逗号小数点。</summary>
    private static bool TryParseSpeed(string? text, out float rate)
    {
        rate = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.EndsWith("x", StringComparison.OrdinalIgnoreCase)) s = s[..^1].Trim();
        s = s.Replace(',', '.');
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out rate)
               && float.IsFinite(rate);
    }

    private void ShowSpeedCustomHint(string message)
    {
        if (SpeedCustomHint != null) SpeedCustomHint.Text = message;
    }

    /// <summary>把弹层的右缘与目标按钮的右缘对齐(放在按钮正上方)。</summary>
    private void RightAlignPopup(System.Windows.Controls.Primitives.Popup popup,
        System.Windows.FrameworkElement target)
    {
        popup.PlacementTarget = target;
        popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
        popup.HorizontalOffset = 0;
        popup.CustomPopupPlacementCallback = (size, anchor, offset) => new[] {
            new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(anchor.Width - size.Width, -size.Height - 4), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal) };

    }


    // ------------------------------------------------------------ 弹幕显示区域

    private void OnDanmakuAreaClick(object sender, RoutedEventArgs e)
    {
        if (DanmakuAreaPopup == null) return;
        if (DanmakuAreaPopup.IsOpen)
        {
            CloseDanmakuAreaPopup();
            return;
        }
        DanmakuAreaPopup.PlacementTarget = BtnDanmakuArea;
        RightAlignPopup(DanmakuAreaPopup, BtnDanmakuArea);
        DanmakuAreaPopup.IsOpen = true;
    }

    private void CloseDanmakuAreaPopup()
    {
        if (DanmakuAreaPopup != null && DanmakuAreaPopup.IsOpen)
        {
            if (Mouse.Captured is DependencyObject captured && IsInPopup(captured, DanmakuAreaPopup))
                Mouse.Capture(null);
            DanmakuAreaPopup.IsOpen = false;
        }
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject? ancestor)
    {
        if (ancestor == null) return false;
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null)
                   ?? LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private bool IsInPopup(DependencyObject? source, Popup? popup)
        => source != null && popup != null &&
           (ReferenceEquals(source, popup) || (popup.Child != null && IsDescendantOf(source, popup.Child)));

    private bool IsInAnyPlayerPopup(DependencyObject? source)
        => IsInPopup(source, DanmakuAreaPopup)
           || IsInPopup(source, SpeedPopup)
           || IsInPopup(source, QualityPopup)
           || IsInPopup(source, VolumePopup);

    /// <summary>调整弹幕显示区域占比(弹幕只在画面上方 ratio 比例内滚动)</summary>
    private void OnDanmakuAreaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var pct = (int)e.NewValue;
        _danmakuAreaRatio = pct / 100.0;
        if (DanmakuAreaValue != null) DanmakuAreaValue.Text = pct + "%";
        // ★ 初始化/程序同步期间只刷文字, 不落库 —— 否则 XAML 里的初始 Value=25
        //   会在 InitializeComponent() 时把用户存的值覆写成默认(见 _syncingDanmakuLook 的说明)。
        if (_syncingDanmakuLook) return;
        // 持久化到全局设置(设置页与播放器双向同步)
        Svc.Settings.UpdateDanmakuSettings(Svc.Settings.DanmakuEnabled, pct, Svc.Settings.DanmakuSmartFilter);
    }

    // ------------------------------------------------------------ 弹幕外观三项(2026-10-12)

    /// <summary>
    /// 三个外观滑杆的统一入口。
    ///
    /// ★ 为什么不直接写 Svc.Settings.UpdateDanmakuSettings 之后各改各的:
    ///   这三项**都要重算运行时状态**(ApplyDanmakuLook), 而重算的第一步就是比较新旧值。
    ///   于是最省事也最不容易漏的写法就是: 先落设置(它会广播 OnDanmakuSettingsChanged,
    ///   那里已经调了 ApplyDanmakuLook), 自己只负责把"这一行的数值文字"刷新掉。
    ///   两条路径(设置页改 / 播放器改)因此走的是同一段重算逻辑, 不会出现两边算法漂移。
    ///
    /// ★ 必须防回灌: 滑杆赋值也会触发 ValueChanged。同步控件时那个标志是必需的,
    ///   否则一次同步 → 一次事件 → 一次保存 → 一次广播 → 又同步, 虽然值同值不会死循环,
    ///   但每次开窗都会白白写一遍配置文件。
    /// </summary>
    private void OnDanmakuOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DanmakuOpacityValue != null) DanmakuOpacityValue.Text = ((int)e.NewValue) + "%";
        if (_syncingDanmakuLook) return;
        Svc.Settings.UpdateDanmakuSettings(
            Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
            opacityPercent: (int)e.NewValue);
    }

    private void OnDanmakuFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DanmakuFontValue != null) DanmakuFontValue.Text = ((int)e.NewValue) + "%";
        if (_syncingDanmakuLook) return;
        Svc.Settings.UpdateDanmakuSettings(
            Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
            fontScalePercent: (int)e.NewValue);
    }

    private void OnDanmakuSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var level = (int)e.NewValue;
        // 显示的是"适中/较快"这类人话, 不是数字 —— 与用户给的截图一致
        if (DanmakuSpeedValue != null) DanmakuSpeedValue.Text = Helpers.DanmakuMotion.SpeedLabel(level);
        if (_syncingDanmakuLook) return;
        Svc.Settings.UpdateDanmakuSettings(
            Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
            speedLevel: level);
    }

    private void OnDanmakuOutlineRowClick(object sender, MouseButtonEventArgs e)
    {
        if (DanmakuOutlineCheck == null || IsDescendantOf(e.OriginalSource as DependencyObject, DanmakuOutlineCheck))
            return;
        DanmakuOutlineCheck.IsChecked = DanmakuOutlineCheck.IsChecked != true;
        e.Handled = true;
    }

    private void OnDanmakuOutlineRowTouchDown(object? sender, TouchEventArgs e)
    {
        if (DanmakuOutlineCheck == null) return;
        e.Handled = true;
    }

    private void OnDanmakuOutlineRowTouchUp(object? sender, TouchEventArgs e)
    {
        if (DanmakuOutlineCheck == null) return;
        DanmakuOutlineCheck.IsChecked = DanmakuOutlineCheck.IsChecked != true;
        TouchSupport.CancelPendingTap();
        e.Handled = true;
    }

    private void OnDanmakuOutlineChanged(object sender, RoutedEventArgs e)
    {
        var enabled = DanmakuOutlineCheck?.IsChecked == true;
        if (DanmakuOutlineStateText != null)
            DanmakuOutlineStateText.Text = enabled ? "已开启" : "已关闭";
        if (_syncingDanmakuLook) return;
        Svc.Settings.UpdateDanmakuSettings(
            Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
            outline: enabled);
    }

    /// <summary>把外观三项及描边开关同步到当前设置(开窗与设置页改动后各调一次)</summary>
    private void UpdateDanmakuLookTexts()
    {
        var s = Svc.Settings;
        // 回灌保护: 下面的赋值会触发 ValueChanged, 那三个处理器会写设置。
        // ★ 保存/恢复上一个值而不是无条件 false —— 本方法可能在"窗口初始化"的大闸门里被调用,
        //   直接置 false 会提前把闸门打开(2026-10-30)。
        var prev = _syncingDanmakuLook;
        _syncingDanmakuLook = true;
        try
        {
            if (DanmakuOpacitySlider != null) DanmakuOpacitySlider.Value = s.DanmakuOpacityPercent;
            if (DanmakuOpacityValue != null) DanmakuOpacityValue.Text = s.DanmakuOpacityPercent + "%";
            if (DanmakuFontSlider != null) DanmakuFontSlider.Value = s.DanmakuFontScalePercent;
            if (DanmakuFontValue != null) DanmakuFontValue.Text = s.DanmakuFontScalePercent + "%";
            if (DanmakuSpeedSlider != null) DanmakuSpeedSlider.Value = s.DanmakuSpeedLevel;
            if (DanmakuSpeedValue != null)
                DanmakuSpeedValue.Text = Helpers.DanmakuMotion.SpeedLabel(s.DanmakuSpeedLevel);
            if (DanmakuOutlineCheck != null) DanmakuOutlineCheck.IsChecked = s.DanmakuOutline;
            if (DanmakuOutlineStateText != null)
                DanmakuOutlineStateText.Text = s.DanmakuOutline ? "已开启" : "已关闭";
        }
        finally
        {
            _syncingDanmakuLook = prev;
        }
    }


    // ------------------------------------------------------------ 视频区手势

    /// <summary>
    /// 视频区左键按下。
    /// 手势优先级: 双击(全屏) > 长按(2x 快进) > 单击(播放/暂停)。
    ///
    /// WPF 的 ClickCount 在 Down 阶段就已经是 2, 但 Up 阶段同样是 2,
    /// 所以"双击"必须在 Down 里判定并置抑制标记, 让后面两次 Up 都不再排队单击动作。
    /// </summary>
    private void OnVideoAreaMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e)) return;
        _lastPlayerInputWasTouch = false;
        // 点击控制栏或播放器设置弹层(播放/清晰度/倍速/音量/弹幕设置等)不作为视频区手势,
        // 必须在边缘缩放判定与焦点抢回之前先行放行, 否则既会干扰弹层内 CheckBox/Slider 的鼠标捕获,
        // 也会把随后的 PreviewMouseLeftButtonUp 误当成无配对余波吞掉。
        var source = e.OriginalSource as DependencyObject;
        if (IsInAnyPlayerPopup(source)) { _videoGestureActive = false; return; }
        if (ControlBar != null && (ControlBar.IsMouseOver || IsDescendantOf(source, ControlBar))) { _videoGestureActive = false; return; }
        // 落在窗口边缘缩放带里的按下是"拖边缘改大小", 不算视频区手势(见 TryBeginEdgeResize)
        if (TryBeginEdgeResize()) { e.Handled = true; return; }
        if (_mp == null) return;
        // 点视频区会把焦点交给那层浮动覆盖窗口/原生视频窗口, 顺手把键盘焦点收回 WPF 树
        RestoreKeyboardFocus();
        if (VolumePopup.IsOpen)
        {
            CloseVolumePopup();
            _videoGestureActive = false;
            e.Handled = true;
            return;
        }
        if (DanmakuAreaPopup?.IsOpen == true)
        {
            CloseDanmakuAreaPopup();
            _videoGestureActive = false;
            e.Handled = true;
            return;
        }
        // 跳过提示条上的点击(「撤回」按钮)同样不算视频区手势 ——
        // 它是覆盖层里除了控制栏之外唯一会吃掉左键的自绘元素, 不挡住就会"点撤回顺带切一次播放/暂停"。
        if (SkipNoticeBorder != null && SkipNoticeBorder.IsMouseOver) return;
        // ★ 弹层开着 ⇒ 这次按下属于浮层, 视频区手势**不起步**。
        //   这是整套防护的地基(见 _videoGestureActive 的说明): 不起步, 随后漏进来的
        //   那个孤立 Up 才会因为"没有配对 Down"而被丢掉。
        if (AnyPlayerPopupOpen) { _videoGestureActive = false; return; }
        // 记下"这一下是我起的" —— Up 里靠它识别弹层余波
        _videoGestureActive = true;

        // 双击: 播放/暂停(与手机端一致)。★ 不再切全屏 —— 用户明确要求"不需要双击全屏"。
        // 立即执行, 取消所有挂起的单击/长按动作, 并抑制后续 Up 的单击排队。
        if (e.ClickCount >= 2)
        {
            _suppressSingleClick = true;
            _videoGestureActive = false;   // 这一下已被双击消费, 不再等 Up 配对
            _singleClickTimer?.Stop();
            _longPressTimer?.Stop();
            _longPressFired = false;
            OnPlayPauseClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // 单击: 启动长按计时器, 单击的"播放/暂停"在 MouseUp 时延迟触发
        _suppressSingleClick = false;
        _longPressFired = false;
        _longPressTimer?.Stop();
        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer!.Stop();
            _longPressFired = true;
            // ★ 长按快进: 改倍速(SetRate)。为什么不用 seek 式、以及松手那点静音为何
            //   不值得用花招去治 —— 见 FastForwardRate 那段(含三个实测更糟的方案)。
            StartFastForward();
            // ★ 浮出提示(2026-10-13 用户要求): 长按快进是**无入口手势**,
            //   不提示的话用户只看到画面变快, 不知道发生了什么、也不知道松手会恢复。
            //   hold=true: 整个长按期间一直挂着, 松手时由 Up 那边换成"已恢复"并淡出。
            ShowSpeedHint(FormatRate(_fastForwardTarget) + " 快进中", hold: true);
            // 长按触发了, 不再触发单击
            _singleClickTimer?.Stop();
        };
        _longPressTimer.Start();
    }

    /// <summary>视频区左键松开: 若是长按 → 恢复 1x; 否则延迟触发播放/暂停(给双击留窗口)</summary>
    private void OnVideoAreaMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e)) return;
        // ★ 与 Down 严格对称: 弹层内(DanmakuAreaPopup/SpeedPopup/QualityPopup/VolumePopup)及控制栏上的
        //   MouseLeftButtonUp 必须原样放行! 之前这里漏判了 IsInAnyPlayerPopup 和 IsDescendantOf(ControlBar),
        //   导致在弹幕设置弹层里点「弹幕描边」时, PreviewMouseLeftButtonUp 被下面的 `!_videoGestureActive`
        //   置为 e.Handled = true 吞掉 —— 既导致描边开关点不动, 又让 CheckBox 在 Down 时拿到的
        //   Mouse.Capture 永远得不到 Up 释放而死锁整窗鼠标焦点(必须按 Esc 关弹层才解开)。
        var source = e.OriginalSource as DependencyObject;
        if (IsInAnyPlayerPopup(source)) return;
        if (ControlBar != null && (ControlBar.IsMouseOver || IsDescendantOf(source, ControlBar))) return;
        // ★ 与 Down 对称: 跳过提示条上的「撤回」点击也不触发单击暂停
        if (SkipNoticeBorder != null && SkipNoticeBorder.IsMouseOver) return;
        // ★★ 弹层余波(2026-10-13 第三轮修正, 前两轮都没修对):
        //   关闭清晰度/弹幕弹层那一次点击, Down 被弹层 HWND 吃掉, 只有 Up 漏进视频区。
        //   实测它到达时 IsOpen 已 false、Mouse.LeftButton 已 Released、Closed 还没来
        //   —— 三个"当前状态"信号全不可用(完整时间戳见 _videoGestureActive 的说明)。
        //   唯一可靠判据: **没有配对 Down 的 Up**。视频区手势由 Down 起步,
        //   没起步就来的 Up 必然是浮层漏过来的, 直接丢。
        //   不丢的后果有两个: 排一个"单击暂停"(收起列表视频自己暂停了);
        //   万一 Down 也漏进来, 长按计时器 500ms 后 SetRate(2.0) 无故快进。
        if (!_videoGestureActive) { e.Handled = true; return; }
        _videoGestureActive = false;

        _longPressTimer?.Stop();

        // 双击已被 Down 处理过(播放/暂停), 这里直接吞掉, 绝不能再排一次播放/暂停
        if (_suppressSingleClick)
        {
            _suppressSingleClick = false;
            e.Handled = true;
            return;
        }

        // 长按结束: 恢复按下前的速率
        if (_longPressFired)
        {
            _longPressFired = false;
            // 恢复速率。★ 恢复到**按下前那个速率**(见 _rateBeforeFastForward), 不是写死 1x ——
            //   用户可能先用倍速按钮设了 1.5x。
            //   也**不要**去写 SpeedText —— 那个指示归控制栏的倍速按钮管, 与长按无关。
            StopFastForward();
            // ★ 提示收场: 亮一下再淡出 —— 手势有始有终,
            //   用户才知道刚才那阵快进是自己按出来的、现在结束了。
            ShowSpeedHint("已结束快进", hold: false);
            e.Handled = true;
            return;
        }

        // 单击延迟触发: 等一个双击判定窗口再执行播放/暂停。
        // 若这期间来了第二次点击, Down 里的 ClickCount>=2 分支会置 _suppressSingleClick
        // 并停掉本定时器, 因此双击不会同时触发暂停。
        _singleClickTimer?.Stop();
        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer!.Stop();
            if (_suppressSingleClick) return;
            // ★★★ 2026-11-04 用户要求: 「单击应该改为和晃动鼠标一样唤出或让状态栏消失」。
            //
            //   原来单击 = 播放/暂停, 而双击**也是**播放/暂停 —— 两个手势做同一件事,
            //   等于单击这个交互完全浪费了。官方 PC 客户端(以及几乎所有播放器)的约定是:
            //     · 单击画面 = 显示/隐藏控制栏(与"晃动鼠标唤出控制栏"同一个开关);
            //     · 双击画面 = 播放/暂停(或全屏, 这里按用户要求保持播放/暂停)。
            //   这样单击与"鼠标移动唤出"语义一致, 也不会误暂停。
            ToggleControlsByClick();
        };
        _singleClickTimer.Start();
    }

    /// <summary>
    /// 视频区右键按下: 记下"这一下是我起的", 按住 **500ms** 起长按快进(2026-10-31 新增)。
    ///
    /// ★ 原来这里只有"右键 = 铺满窗口"一个直接切换, 现在改为:
    ///   · **短右键**(按住 < 500ms) = 铺满窗口 —— 原行为不变;
    ///   · **长右键**(按住 ≥ 500ms) = 2x 快进, 与左键长按完全同款 ——
    ///     长视频里左手拖进度条、右手按住快进, 两手不打架(左键长按要占用双击全屏的手)。
    /// ★ 与左键长按共用 <see cref="_longPressFired"/>/<see cref="_isFastForwarding"/>
    ///   那套状态与 <see cref="StartFastForward"/>/StopFastForward 收尾 ——
    ///   两只手同时按住时是同一个快进态, 谁先松谁收, 不会互相顶。
    /// ★ 与左键 Down 同款的成对判定(弹层余波防护)在这里照抄:
    ///   没有配对 Down 的 Up 一律丢弃, 这是唯一可靠的判据(见 AnyPlayerPopupOpen)。
    /// </summary>
    private void OnVideoAreaMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e)) return;
        // 控制栏 / 播放器设置弹层 / 跳过提示条上的右键不算视频区手势(与左键同款理由)
        var source = e.OriginalSource as DependencyObject;
        if (IsInAnyPlayerPopup(source)) { _rightGestureActive = false; return; }
        if (ControlBar != null && (ControlBar.IsMouseOver || IsDescendantOf(source, ControlBar))) { _rightGestureActive = false; return; }
        // ★ 落在窗口边缘缩放带里就**不要**当成视频区手势, 但**也不能**调 TryBeginEdgeResize():
        //   它内部发的是 `WM_NCLBUTTONDOWN`(**左键**消息), 用右键去调等于"假装用户按了左键拖边框",
        //   会让系统进入模态缩放循环 —— 而那个循环等的是左键松开, 右键根本不会给它。
        //   ⇒ 这里只判断"在不在缩放带", 在就放行(交给窗口默认行为), 不启动任何东西。
        if (IsOnResizeEdge()) return;
        if (_mp == null) return;
        RestoreKeyboardFocus();
        if (SkipNoticeBorder != null && SkipNoticeBorder.IsMouseOver) return;
        if (DanmakuAreaPopup?.IsOpen == true) { CloseDanmakuAreaPopup(); _rightGestureActive = false; e.Handled = true; return; }
        // 弹层开着 ⇒ 这一下属于浮层, 不起步(地基, 见方法说明)
        if (AnyPlayerPopupOpen) { _rightGestureActive = false; return; }
        _rightGestureActive = true;
        e.Handled = true;   // 别让它冒泡成系统菜单
    }

    /// <summary>左右键长按共用的判定时长(毫秒)。与左键长按原阈值保持一致。</summary>
    private const int LongPressThresholdMs = 500;

    /// <summary>指针是否落在窗口边缘的缩放带上(只判断, 不启动缩放 —— 见 OnVideoAreaMouseRightDown)。</summary>
    private bool IsOnResizeEdge()
    {
        if (_isFullscreen || WindowState != WindowState.Normal) return false;
        return HitTestResizeEdge() != 0;
    }

    /// <summary>
    /// 视频区右键抬起: 按住了长按(快进)就收快进, 否则是"短右键"→ 切「铺满窗口」。
    /// 没有配对 Down 的 Up 一律丢弃(那是弹层余波, 见 Down 的说明)。
    /// </summary>
    private void OnVideoAreaMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e)) return;
        var source = e.OriginalSource as DependencyObject;
        if (IsInAnyPlayerPopup(source)) return;
        if (ControlBar != null && (ControlBar.IsMouseOver || IsDescendantOf(source, ControlBar))) return;
        if (SkipNoticeBorder != null && SkipNoticeBorder.IsMouseOver) return;
        if (!_rightGestureActive) { e.Handled = true; return; }
        _rightGestureActive = false;
        if (_closing) return;
        // ★ 2026-11-04: 右键不再有长按分支(见 OnVideoAreaMouseRightDown 的说明) ——
        //   每一次右键都只做"铺满/收起详情栏"这一件事。
        ToggleFillWindow();
        e.Handled = true;
    }

    /// <summary>鼠标离开视频区: 兜底, 防止按住时鼠标移出导致状态卡住</summary>
    private void OnVideoAreaMouseLeave(object sender, MouseEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e) || _videoTouches.Count > 0) return;
        _longPressTimer?.Stop();
        _singleClickTimer?.Stop();
        _suppressSingleClick = false;
        // ★ 手势状态一并复位: 鼠标移出去了, 这一次按下就不再属于视频区,
        //   之后即使漏一个 Up 进来也会因为"没有配对 Down"被正确丢掉。
        _videoGestureActive = false;
        // 右键那一路同样复位: 按下后拖到视频区外再松手, Up 不会再来 ——
        // 不复位的话下一次孤立的 Up 会被误当成"配对成功"而切一次铺满。
        _rightGestureActive = false;
        if (_longPressFired)
        {
            _longPressFired = false;
            // 同 Up 那条路: 恢复按下前的速率(见 _rateBeforeFastForward)
            StopFastForward();
            // ★ 兜底路径也要收掉提示: 鼠标移出时 Up 不会再来了,
            //   不在这里收的话提示会一直挂在屏幕上(用户报过同类"提示不消失")。
            ShowSpeedHint("已结束快进", hold: false);
        }
        // 兜底: 万一手势态已经乱了(比如 _longPressFired 被别处清掉), 快进态也不能留着
        StopFastForward();
        // 离开视频区: 播放中直接藏(倒计时都不用等), 暂停时保持常显
        if (ShouldHideControls()) HideControls();
    }


    // ------------------------------------------------------------ 缓存当前视频

    private void OnCacheClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _isLocalPlayback || _isLive || string.IsNullOrEmpty(_currentBvid)) return;
        // 与卡片右键"下载视频"同一条链路: 弹独立的下载进度对话框。
        // ★ 下载是**一次性**动作(不像点赞可来回切), 所以这里等结果、成功后把按钮染成绿色
        //   并换成"已下载" —— 设计稿 v4 第 07 节把下载归为"拿走"这一组, 色相是绿。
        //   失败/取消不动按钮(保持中性), 有对话框自己的提示兜底。
        var bvid = _currentBvid;
        _ = DownloadFromPlayerAsync(bvid);
    }

    /// <summary>下载并收尾按钮状态(见 OnCacheClick)。</summary>
    private async Task DownloadFromPlayerAsync(string bvid)
    {
        try
        {
            var saved = await VideoDownloader.DownloadAsync(bvid, VideoTitleText.Text, this, _currentCid);
            if (_closing) return;
            // 期间用户可能已经换片: 只给"还是当前这条"的按钮点绿
            if (saved != null && string.Equals(bvid, _currentBvid, StringComparison.Ordinal))
                MarkCacheDownloaded();
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
    }

    /// <summary>
    /// 把下载按钮标成"已下载"(绿)。
    /// ★ 走 UpdateActionCounts 收口, 不在这里自己刷颜色 —— 否则"绿色"会有两个出口,
    ///   迟早出现"这里绿了那里又灰回去"。
    /// </summary>
    private void MarkCacheDownloaded()
    {
        _cacheDownloaded = true;
        UpdateActionCounts();
        if (BtnCache != null) BtnCache.ToolTip = "已下载";
    }

    // ------------------------------------------------------------ 键盘

    internal static bool ShouldBypassShortcutModifiers(ModifierKeys modifiers)
        => (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 只在焦点位于"可编辑"输入框时让按键走默认路径; 只读展示框不拦截空格
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false }) return;
        // 放行带 Ctrl / Alt 的系统与文本组合键(如 Ctrl+C 复制), 避免被裸字母快捷键吞掉
        if (ShouldBypassShortcutModifiers(Keyboard.Modifiers)) return;
        switch (e.Key)
        {
            case Key.Space: OnPlayPauseClick(this, new RoutedEventArgs()); e.Handled = true; return;
            // ★ 2026-10-31 长视频快进体验: 普通方向键 ±5s(原样), **Shift+方向键 ±30s** ——
            //   长视频里 5s 一跳要按十几下才够用, Shift 档一下抵六下。走同一条 SeekRelative,
            //   所有 seek 防护(看门狗/静音/进度钉制)自动继承。
            case Key.Left: SeekRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -30 : -5); e.Handled = true; return;
            case Key.Right: SeekRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 30 : 5); e.Handled = true; return;
            // 音量: ↑↓ 走统一入口(顺带弹音量提示), M 在静音与 80 之间切换
            // 基数用 _volumeCache: aout 未就绪时 _mp.Volume 原生读回是 -1/旧值(见 _volumeCache 的说明)
            case Key.Up: SetVolume(_volumeCache + VolumeStep); e.Handled = true; return;
            case Key.Down: SetVolume(_volumeCache - VolumeStep); e.Handled = true; return;
            case Key.M: SetVolume(_volumeCache == 0 ? 80 : 0); e.Handled = true; return;
            case Key.F: ToggleFullscreen(); e.Handled = true; return;
            case Key.L: OnLikeClick(this, new RoutedEventArgs()); e.Handled = true; return;
            case Key.U: OnUpPageClick(this, new RoutedEventArgs()); e.Handled = true; return;
            case Key.D: ToggleDanmaku(); e.Handled = true; return;
            case Key.Escape: HandleBackRequest(); e.Handled = true; return;
        }
    }

    /// <summary>
    /// "返回上一层"的统一处理(键盘 Esc / 鼠标侧键共用)。
    ///
    /// 三层语义, **由内到外**逐级退, 与所有播放器一致:
    ///   全屏中     → 先退全屏(而不是直接关窗口, 否则用户按一下就"连界面一起没了");
    ///   已铺满窗口 → 再退铺满窗口(信息栏回来);
    ///   都没有     → 关闭播放器回到主界面。
    /// ★ 铺满窗口必须夹在中间: 它是最"内层"的一个显示模式(窗口尺寸都没变), 用户按 Esc 的
    ///   预期是"先把我刚展开的这个视图收回去", 而不是"直接关掉播放器"。
    ///   少了这一级, 用户在铺满窗口状态下按 Esc 会直接关窗 —— 与真全屏下按 Esc 的行为不一致。
    /// 两个入口必须走同一个方法, 不然很容易出现"Esc 是对的、侧键直接关窗"这种不一致。
    /// </summary>
    private void HandleBackRequest()
    {
        if (DanmakuAreaPopup?.IsOpen == true)
        {
            CloseDanmakuAreaPopup();
            return;
        }
        if (_isFullscreen)
        {
            _isFullscreen = false;
            ApplyFullscreen(false);
            return;
        }
        if (_isFillWindow)
        {
            _isFillWindow = false;
            ApplyInfoPanelState();
            UpdateFillWindowButton();
            return;
        }
        Close();
    }

    /// <summary>
    /// 鼠标侧键(XButton1/XButton2) = 返回; 同时把点击弹幕设置弹层外部时非模态收起。
    ///
    /// 为什么两个侧键都当返回: 本应用没有"前进"这种概念, 而各家的鼠标把拇指键映射成
    /// 哪个 XButton 并不统一(有的只给一个键, 有的是前/后两个), 只认 XButton1 的话
    /// 相当一部分鼠标按了没反应。既然没有前进语义可用, 统一当返回最实用。
    ///
    /// 为什么用 PreviewMouseDown: 底部的按钮/滑块会吃掉普通 MouseDown, 而隧道事件从
    /// 根往下走, 挂 Preview 就一定先经过我们这里。
    /// </summary>
    private void DismissDanmakuAreaIfOutside(DependencyObject? source)
    {
        if (DanmakuAreaPopup?.IsOpen != true) return;
        if (IsInPopup(source, DanmakuAreaPopup)) return;
        if (IsDescendantOf(source, BtnDanmakuArea)) return;
        if (IsDescendantOf(source, VideoAreaRoot) && !IsDescendantOf(source, ControlBar)) return;
        CloseDanmakuAreaPopup();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        if (e.ChangedButton == MouseButton.XButton1 || e.ChangedButton == MouseButton.XButton2)
        {
            e.Handled = true;
            HandleBackRequest();
            return;
        }
        DismissDanmakuAreaIfOutside(e.OriginalSource as DependencyObject);
    }

    private void OnPreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (_closing) return;
        DismissDanmakuAreaIfOutside(e.OriginalSource as DependencyObject);
    }

    private void SeekRelative(double seconds)
    {
        if (_mp.Length <= 0) return;
        // ★ 走统一入口(分段跳 / 重建后跳), 见 SeekTo 的说明
        SeekTo((long)(_mp.Time + seconds * 1000));
    }


    // ------------------------------------------------------------ 按钮事件

    private void UpdateButtonState()
    {
        if (_closing) return;
        // 本地文件没有点赞/投币/收藏/关注这些在线操作, 一律禁用而不是让它点了报错
        var canInteract = Svc.Session.HasLogin && !_isLocalPlayback;
        if (BtnLike != null) BtnLike.IsEnabled = canInteract;
        if (BtnCoin != null) BtnCoin.IsEnabled = canInteract;
        if (BtnFavorite != null) BtnFavorite.IsEnabled = canInteract;
        if (BtnWatchLaterTop != null) BtnWatchLaterTop.IsEnabled = canInteract && !_isLive;
        if (BtnFollow != null) BtnFollow.IsEnabled = canInteract && _ownerMid > 0;
        // 评论输入框的可用性跟着同一套前提走(已登录 + 在线点播)
        UpdateCommentComposer();
    }

    /// <summary>查询当前视频是否已点赞, 用于按钮状态切换</summary>
    private async Task RefreshLikeStateAsync()
    {
        if (_aid <= 0 || !Svc.Session.HasLogin) return;
        var (ok, _, liked) = await Svc.Api.HasLikedAsync(_aid);
        // 查不到就保持原状(别把按钮图标刷成"未赞"), 但把原因记一笔便于排查
        if (!ok) WriteLibVLCDebug($"\n[HasLiked] 查询失败 aid={_aid}\n");
        else _isLiked = liked;
        if (!_closing) UpdateActionCounts();
    }

    /// <summary>
    /// 查询真实的投币状态并刷新按钮点亮态。
    ///
    /// ★ 为什么必须单独查一次: 详情接口(view)**不给**"本人是否投过币"这个信息
    ///   (见 <see cref="ApiClient.GetCoinCountAsync"/> 里那段实测记录), 所以换片之后
    ///   `_isCoined` 不能继续取自详情 —— 那正是"投过币、重开视频后键还是灰的"的根因。
    /// ★ 与 <see cref="RefreshLikeStateAsync"/> 同构: 失败时保持原状(不把已点亮的刷成灰),
    ///   只记一笔便于排查。
    /// ★ 只在已登录且是点播时查: 本地文件/直播没有投币这回事, 查了也是白问一次接口。
    /// </summary>
    private async Task RefreshCoinStateAsync()
    {
        if (_aid <= 0 || !Svc.Session.HasLogin || _isLocalPlayback || _isLive) return;
        var (ok, _, coins) = await Svc.Api.GetCoinCountAsync(_aid);
        if (!ok) { WriteLibVLCDebug($"\n[Coined] 查询失败 aid={_aid}\n"); return; }
        _isCoined = coins > 0;
        ThemedCoinIcon.Count = coins;
        if (!_closing) UpdateActionCounts();
    }

    private async void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (ShouldSuppressLikeClick(sender)) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("点赞需要先登录"); return; }
        if (string.IsNullOrEmpty(_currentBvid)) return;
        // 按当前状态切换: 已赞 -> 取消, 未赞 -> 点赞
        var bvid = _currentBvid;
        var generation = _loadGen;
        var target = !_isLiked;
        if (!BtnLike.IsEnabled) return;
        BtnLike.IsEnabled = false;
        bool ok; string? err;
        try { (ok, err) = await Svc.Api.LikeVideoAsync(bvid, target); }
        finally { if (!_closing) BtnLike.IsEnabled = true; }
        if (_closing || bvid != _currentBvid || generation != _loadGen) return;
        if (ok)
        {
            _isLiked = target;
            _likeCount = Math.Max(0, _likeCount + (_isLiked ? 1 : -1));
            UpdateActionCounts();
            // 弹一下(设计稿 v4: 点赞是全栏最高频的动作, 反馈比别的按钮重一档)
            PlayThemedAction(ThemeReaction.Like, BtnLike, LikeIconBox, target);
            // 取消点赞后锁住 hover 预览: 否则鼠标还停在按钮上, 它会当场又变红
            if (!_isLiked) LockActionHoverPreview();

        }
        else
        {
            // ★ 风控提示已经是一整句可照做的指引(见 ApiClient.FriendlyRiskMessage),
            //   再套一层"操作失败: "只会把话拉长、把重点埋掉。这里按前缀判断, 只在
            //   普通错误上加动作名。
            var m = err ?? "未知错误";
            Svc.Toast.Show(m.StartsWith("B 站风控", StringComparison.Ordinal)
                ? m : "操作失败: " + m);
        }
    }

    /// <summary>
    /// 投币按钮: 弹出数量选择对话框(形态照 B 站官方那个投币弹窗, 见 <see cref="CoinWindow"/>)。
    ///
    /// ★ 为什么要有"选数量"这一步: 投币**不可撤销**, 而单视频上限是 2 枚 —— 原来写死投 1 枚,
    ///   想投 2 枚的用户只能去官方端补。
    /// ★ 用模态窗而不是 Popup: 播放器视频区有单击暂停 / 双击全屏手势, Popup 的"点外部关闭"
    ///   会把那一次点击漏进视频区(与收藏夹选择窗同一个理由)。
    /// ★ 已投过就不再弹: 投币没有"取消"语义, 让用户进到对话框里再撞服务端的"超过投币上限"
    ///   只会白跑一趟。
    /// </summary>
    private void OnCoinClick(object sender, RoutedEventArgs e)
    {
        if (!BtnCoin.IsEnabled) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("投币需要先登录"); return; }
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0) return;
        if (_isCoined) { Svc.Toast.Show("这个视频已经投过币了"); return; }

        var dlg = new CoinWindow { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Confirmed) return;
        _ = CoinAsync(dlg.Count, dlg.AlsoLike);
    }

    /// <summary>
    /// 投币(count = 1 或 2)。成功后点亮按钮, 计数按实际枚数累加(不是固定 +1)。
    /// alsoLike 走接口的 select_like, 点赞由服务端在同一次请求里完成。
    /// </summary>
    private async Task CoinAsync(int count, bool alsoLike)
    {
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0) return;
        if (!BtnCoin.IsEnabled) return;
        // 已经赞过就不带 select_like: 没有必要, 也免得撞服务端"已经点赞"那条分支
        var wantLike = alsoLike && !_isLiked;
        var generation = _loadGen; var aid = _aid; var bvid = _currentBvid;
        BtnCoin.IsEnabled = false;
        bool ok; string? err;
        try { (ok, err) = await Svc.Api.CoinVideoAsync(aid, count, bvid, wantLike); }
        finally { if (!_closing) BtnCoin.IsEnabled = true; }
        if (_closing || aid != _aid || bvid != _currentBvid || generation != _loadGen) return;
        if (ok)
        {
            // 同一个视频最多投 2 枚, 本地只记"已投过"的点亮态(下次进来以详情接口为准)
            _isCoined = true;
            _coinCount += count;
            ThemedCoinIcon.Count = count;
            // 赞是服务端顺带点的, 本地状态要跟着走 —— 否则按钮还显示"未赞"、计数也少 1
            if (wantLike) { _isLiked = true; _likeCount += 1; }
            UpdateActionCounts();
            PlayThemedAction(ThemeReaction.Coin, BtnCoin, CoinIconBox, true);
        }
        else
        {
            // 同上: 风控提示自成一句, 不再套"投币失败: "前缀
            var m = err ?? "未知错误";
            Svc.Toast.Show(m.StartsWith("B 站风控", StringComparison.Ordinal)
                ? m : "投币失败: " + m);
        }
    }

    /// <summary>
    /// 操作栏的"图标 + 数量/文字"刷新。**唯一决定这六个按钮颜色与图标形态的地方。**
    ///
    /// 2026-10-06 按设计稿「播放页_v4」重构, 规则:
    ///   · **未激活一律是灰**(TextSecondaryBrush), 颜色只在用户动过之后才出现;
    ///   · 每个动作有**固定色相**, 不跟主题强调色走(红=情绪 / 金=货币 / 蓝=收进我的库 / 绿=拿走)。
    ///     用户把主题色改成绿时, "点赞"跟着变绿会立刻毁掉这套分工 —— 所以色值写死在这里。
    ///   · 数字与文字 TextBlock 的前景**必须显式绑**到按钮 Foreground: 全局隐式 TextBlock 样式
    ///     带 Foreground setter(优先级高于继承), 不绑的话变色的只有图标(项目里反复踩的坑)。
    ///   · 「稍后再看」的两层文字在这里一处切换 —— 散在触发器里改必然出现
    ///     "图标点亮了文字没换"这种半状态(2026-10-06 改回两行布局时恢复)。
    /// </summary>
    private void UpdateActionCounts()
    {
        if (_closing) return;
        LikeLabel.Text = VideoItem.FormatCount(_likeCount);
        ThemedLikeIcon.IsActive = _isLiked;
        ThemedFavoriteIcon.IsActive = _isFavorited;
        CoinLabel.Text = VideoItem.FormatCount(_coinCount);
        FavLabel.Text = VideoItem.FormatCount(_favCount);
        ApplyActionVisual(BtnLike, _isLiked, ActionLikeColor);
        ApplyActionVisual(BtnFavorite, _isFavorited, ActionSaveColor);

        // 「稍后再看」的两层文字随点亮态互换(稍后再看 ↔ 已添加)。
        // ★ 宽度由 XAML 里的双层叠放兜住(两层叠在同一格, 宽度取最大值), 切换只做透明度过渡,
        //   所以整排按钮不会被推着走 —— 两行布局下这套是必需的(2026-10-06 改回两行时恢复)。
        // ★ 2026-11-04: 顶部 bar 那颗「稍后再看」也要同步(用户要求它和「听视频」放一起)。
        //   两处是**同一个状态**的两个视图, 必须一起刷 —— 否则点完上面那颗、下面那颗不亮,
        //   用户会以为没生效又点一次, 结果把它取消了。
        if (LaterTopLabelOff != null && LaterTopLabelOn != null)
        {
            LaterTopLabelOff.Opacity = _isInWatchLater ? 0 : 1;
            LaterTopLabelOn.Opacity = _isInWatchLater ? 1 : 0;
        }
        if (LaterTopIcon != null)
        {
            LaterTopIcon.Stroke = _isInWatchLater
                ? new SolidColorBrush(ActionSaveColor)
                : (Brush)(TryFindResource("TextSecondaryBrush") ?? Brushes.Gray);
        }
        // 未点过的三个按钮也要走一次: 换片后要把上一次残留的色相清回灰。
        // ★ 下载的绿色终态由 _cacheDownloaded 决定(它只在换片时复位, 见 ResetForNewMedia)——
        //   不能在这里无条件刷成灰: 本方法会被点赞/收藏等**同一片内**的操作反复调用,
        //   那样用户一下载完、随手点个赞, 绿色就没了。
        ApplyActionVisual(BtnCoin, _isCoined, ActionCoinColor);
        ApplyActionVisual(BtnShare, false, ActionNeutralColor);
        ApplyActionVisual(BtnCache, _cacheDownloaded, ActionDownloadColor);
    }

    /// <summary>当前这条视频是否已下载完成(只用于下载按钮的绿色终态)</summary>
    private bool _cacheDownloaded;

    // ---- 操作栏色相(设计稿 v4: 固定色, 不跟主题强调色) ----

    /// <summary>点赞红(情绪)</summary>
    private static readonly Color ActionLikeColor = Color.FromRgb(0xF4, 0x3F, 0x5E);
    /// <summary>投币琥珀(货币)</summary>
    private static readonly Color ActionCoinColor = Color.FromRgb(0xF5, 0x9E, 0x0B);
    /// <summary>收藏 / 稍后再看蓝(收进我的库, 两者同族同色)</summary>
    private static readonly Color ActionSaveColor = Color.FromRgb(0x0E, 0xA5, 0xE9);
    /// <summary>下载绿(拿走)。只在"已下载"这一个终态出现, 不像其它色相有"未点亮/点亮"两态</summary>
    private static readonly Color ActionDownloadColor = Color.FromRgb(0x10, 0xB9, 0x81);
    /// <summary>中性(分享 / 下载): 走主题的次要文字色, 不做色相区分</summary>
    private static readonly Color? ActionNeutralColor = null;

    /// <summary>
    /// 把一个操作按钮刷成"点亮(用该动作的色相)"或"未点亮(灰)"。
    ///
    /// ★ 这里**直接操作控件**而不是靠 Style 触发器: hover 预览(.v4)需要在鼠标悬停时
    ///   临时借用色相, 而"取消点赞后鼠标还悬停着 → 按钮当场又变红"这个坑(v4 稿第 04 节)
    ///   要求"取消后短暂锁住 hover"。这套时序逃不出代码, 所以统一放在这里。
    /// </summary>
    private void ApplyActionVisual(Button? btn, bool on, Color? accent)
    {
        if (btn == null) return;
        btn.Foreground = on && accent.HasValue
            ? new SolidColorBrush(accent.Value)
            : (Brush)FindResource("TextSecondaryBrush");
        // Tag 驱动模板里的常驻浅底(见 PlayerActionBtn 的 DataTrigger)
        btn.Tag = on ? "on" : "off";
        if (!on) return;
        // 冻结笔刷: 这几个色相是常量, 冻结后 WPF 渲染时可以少走一次可变检查
        if (btn.Foreground is SolidColorBrush scb && scb.CanFreeze) scb.Freeze();
    }

    /// <summary>该动作的色相(按名字取, 点亮/hover 预览共用一套来源)</summary>
    private Color? ActionAccentOf(Button btn)
    {
        if (ReferenceEquals(btn, BtnLike)) return ActionLikeColor;
        if (ReferenceEquals(btn, BtnCoin)) return ActionCoinColor;
        if (ReferenceEquals(btn, BtnFavorite) || ReferenceEquals(btn, BtnWatchLaterTop)) return ActionSaveColor;
        // 下载: 只有"已下载"这一个终态是绿的, 没有"未点亮→点亮"的来回切换
        if (ReferenceEquals(btn, BtnCache)) return ActionDownloadColor;
        return ActionNeutralColor;
    }

    /// <summary>
    /// 这个按钮有没有"hover 预览"。
    /// ★ 下载(和分享)没有: 下载的结果是**一次性**的, 鼠标扫过去就预先把按钮染绿等于
    ///   谎报"已经下载好了"。设计稿 v4 里 .act.dl 也刻意没有 hover 预览。
    /// </summary>
    private bool HasHoverPreview(Button btn)
        => !ReferenceEquals(btn, BtnCache) && !ReferenceEquals(btn, BtnShare);

    /// <summary>该按钮当前是不是"已点亮"态(取消后要用它判断该不该回灰)</summary>
    private bool IsActionOn(Button btn)
    {
        if (ReferenceEquals(btn, BtnLike)) return _isLiked;
        if (ReferenceEquals(btn, BtnFavorite)) return _isFavorited;
        if (ReferenceEquals(btn, BtnWatchLaterTop)) return _isInWatchLater;
        if (ReferenceEquals(btn, BtnCoin)) return _isCoined;
        // 下载的"已下载"也是终态, 有它就说明不该回灰
        if (ReferenceEquals(btn, BtnCache)) return _cacheDownloaded;
        return false;
    }

    /// <summary>
    /// 操作按钮的 hover 预览(设计稿 v4 第 03 节): 鼠标上来先把该动作的色相透出来,
    /// 让用户"不用点就知道点了会怎样"; 底色由模板的 DataTrigger 负责(同色相浅底)。
    ///
    /// ★ 已在点亮态的按钮不需要预览 —— 它本来就是那个颜色, 变过去等于没变。
    /// ★★ 取消后 750ms 内不预览(hover 锁, 见 _actionHoverLockUntil): 取消的那一刻鼠标
    ///    必然还停在这个按钮上, 不给锁的话按钮会当场又变回红色, 用户以为自己没取消成功,
    ///    于是再点一次(这就是设计稿里 v3 的那个隐藏 bug)。
    /// </summary>
    private void OnActionButtonHover(object sender, MouseEventArgs e)
    {
        if (_closing || sender is not Button btn) return;
        if (DateTime.UtcNow < _actionHoverLockUntil) return;
        if (!HasHoverPreview(btn)) return;
        var accent = ActionAccentOf(btn);
        if (accent.HasValue && !IsActionOn(btn))
            btn.Foreground = new SolidColorBrush(accent.Value);
    }

    /// <summary>鼠标离开: 回到该按钮的"真实态"颜色(点亮保持色相, 未点亮回灰)</summary>
    private void OnActionButtonLeave(object sender, MouseEventArgs e)
    {
        if (_closing || sender is not Button btn) return;
        ApplyActionVisual(btn, IsActionOn(btn), ActionAccentOf(btn));
    }

    /// <summary>取消操作后锁住 hover 预览到这个时刻(UTC)。750ms 与设计稿一致。</summary>
    private DateTime _actionHoverLockUntil = DateTime.MinValue;

    /// <summary>锁住 hover 预览 750ms(取消点赞/收藏/稍后再看之后调)</summary>
    private void LockActionHoverPreview() => _actionHoverLockUntil = DateTime.UtcNow.AddMilliseconds(750);

    /// <summary>
    /// 给一个操作按钮播"弹一下"的反馈(设计稿 v4 第 03 节②)。
    /// 作用在图标盒子上而不是内部 Path: Path 自身没有 ScaleTransform, 直接缩放会和
    /// 别的动画/布局打架; 图标盒子是纯容器, 缩放它最干净。
    ///
    /// ★★★ 每次都必须**新建** ScaleTransform, 不能复用现有的那一个。
    ///   原因见 XAML 里 ActionIconBox 的注释: 样式 Setter 里的 Freezable 会被 WPF **冻结**,
    ///   对冻结对象 BeginAnimation 会直接抛
    ///     "无法在 System.Windows.Media.ScaleTransform 上激活 ScaleX 属性, 因为该对象已密封或已冻结"
    ///   —— 用户看到的就是"点一下点赞就弹错误框"(2026-10-06 实测踩到)。
    ///   新建一个 ScaleTransform 的开销可以忽略(一次点击一次), 而它从根上避开了冻不冻的问题。
    /// </summary>
    private void PlayThemedAction(ThemeReaction action, FrameworkElement anchor, FrameworkElement iconBox, bool celebrate)
    {
        if (SkinService.Instance.IsPhantomBlade)
        {
            if (celebrate && !_closing && SkinService.Instance.EffectsEnabled)
            {
                if (action == ThemeReaction.Like) ThemeAudio.PlayLike();
                if (iconBox is Panel panel) panel.Children.OfType<SkinActionIcon>().FirstOrDefault()?.PulseGlow();
            }
            return;
        }
        if (celebrate && !_closing && !_isFullscreen && !_isFillWindow && ThemeReactions.Show(action, anchor)) return;
        PlayActionPop(iconBox);
    }

    private void PlayActionPop(FrameworkElement? iconBox)
    {
        if (iconBox == null || _closing || !SystemParameters.ClientAreaAnimation) return;
        var scale = new ScaleTransform(1, 1);
        iconBox.RenderTransform = scale;
        iconBox.RenderTransformOrigin = new Point(0.5,0.5);
        // 关键帧: 1 → 1.3 → 1(与设计稿的 40% 处 1.3 一致), 总时长 420ms
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.3, KeyTime.FromPercent(0.4))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }


    // ------------------------------------------------------------ 稍后再看

    /// <summary>稍后再看的点亮态只有这一个入口(和收藏一样, 别在多处直接改字段)</summary>
    private void SetInWatchLater(bool inList)
    {
        _isInWatchLater = inList;
        if (BtnWatchLaterTop != null)
            BtnWatchLaterTop.ToolTip = inList ? "已在稍后再看（点击移出）" : "加入稍后再看";
        UpdateActionCounts();
    }

    /// <summary>
    /// 查询该视频是否已在「稍后再看」里。
    /// 服务端没有单条查询接口, 走的是"拉列表 + 本地缓存 aid 集合"(见 ApiClient.IsInWatchLaterAsync):
    /// 60 秒内复用, 所以连续点开视频不会每次都拉一遍两百多条。
    /// 返回 null(未登录/查询失败)时**不点亮也不熄灭**, 保持未知态 —— 谎报状态比不显示更糟。
    /// </summary>
    private async Task RefreshWatchLaterStateAsync()
    {
        if (_aid <= 0 || _closing) return;
        var aid = _aid; var gen = _loadGen;
        var (inOk, _, inList) = await Svc.Api.IsInWatchLaterAsync(aid);
        if (_closing || !inOk || aid != _aid || gen != _loadGen) return;
        SetInWatchLater(inList);
    }

    private async void OnWatchLaterClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("稍后再看需要先登录"); return; }
        if (_isLive || _isLocalPlayback) { Svc.Toast.Show("直播和本地文件不支持稍后再看"); return; }
        if (_aid <= 0 || _watchLaterBusy) return;

        var aid = _aid; var bvid = _currentBvid; var gen = _loadGen;
        var target = !_isInWatchLater;
        _watchLaterBusy = true;
        try
        {
            var (ok, err) = target
                ? await Svc.Api.AddWatchLaterAsync(aid)
                : await Svc.Api.RemoveWatchLaterAsync(aid);
            if (_closing || aid != _aid || gen != _loadGen) return;
            if (!ok)
            {
                Svc.Toast.Show((target ? "加入失败: " : "移出失败: ") + (err ?? "未知错误"));
                return;
            }
            SetInWatchLater(target);
            Svc.UserState.PublishWatchLater(aid, bvid, target);
            PlayActionPop(LaterTopIcon);
            if (!target) LockActionHoverPreview();
        }
        catch (Exception ex) { if (!_closing) Svc.Toast.Show("操作失败: " + ex.Message); }
        finally
        {
            if (aid == _aid && gen == _loadGen) _watchLaterBusy = false;
        }
    }

    private bool _favoriteHeld;
    private System.Windows.Threading.DispatcherTimer? _favoriteHoldTimer;
    private void FavoritePress(object sender, InputEventArgs e)
    {
        _favoriteHeld = false;
        _favoriteHoldTimer?.Stop();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        timer.Tick += (_, _) => {
            timer.Stop();
            if (!ReferenceEquals(_favoriteHoldTimer, timer)) return;
            _favoriteHeld = true;
            OnFavoritePicker(sender, new RoutedEventArgs());
        };
        _favoriteHoldTimer = timer;
        timer.Start();
    }
    private void FavoriteRelease(object sender, InputEventArgs e) => _favoriteHoldTimer?.Stop();
    private async void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        _favoriteHoldTimer?.Stop();
        if (_favoriteHeld) { _favoriteHeld = false; return; }
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("收藏需要先登录"); return; }
        if (_aid <= 0 || _favBusy) return;
        _favBusy = true;
        var aid = _aid; var bvid = _currentBvid; var generation = _loadGen;
        try {
            var folders = await Svc.Api.GetVideoFavFoldersAsync(aid);
            if (_closing || aid != _aid || generation != _loadGen) return;
            if (!folders.ok || folders.folders == null) { Svc.Toast.Show(folders.err ?? "收藏夹读取失败"); return; }
            var target = folders.folders.FirstOrDefault(f => f.IsDefault) ?? folders.folders.OrderBy(f => f.Id).FirstOrDefault();
            if (target == null) { Svc.Toast.Show("请先创建默认收藏夹"); return; }
            var existing = folders.folders.Where(f => f.HasVideo).Select(f => f.Id).ToArray();
            var remove = existing.Length > 0;
            var result = await Svc.Api.DealFavoriteAsync(aid, addIds: remove ? null : new[] { target.Id }, delIds: remove ? existing : null);
            if (_closing || aid != _aid || generation != _loadGen) return;
            if (!result.ok) { Svc.Toast.Show(result.err ?? "收藏失败"); return; }
            SetFavorited(!remove);
            Svc.UserState.PublishFavorite(aid, bvid, !remove);
            PlayThemedAction(ThemeReaction.Favorite, BtnFavorite, FavIconBox, !remove);
        }
        catch (Exception ex) { if (!_closing) Svc.Toast.Show("收藏失败: " + ex.Message); }
        finally { if (aid == _aid && generation == _loadGen) _favBusy = false; }
    }

    private async void OnFavoritePicker(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("收藏需要先登录"); return; }
        if (_aid <= 0 || _favBusy) return;

        _favBusy = true;
        var aid = _aid; var bvid = _currentBvid; var generation = _loadGen;
        try
        {
            var (ok, err, folders) = await Svc.Api.GetVideoFavFoldersAsync(aid);
            if (_closing || aid != _aid || generation != _loadGen) return;
            if (!ok || folders == null)
            {
                Svc.Toast.Show("收藏状态获取失败: " + (err ?? "未知错误"));
                return;
            }
            if (folders.Count == 0)
            {
                Svc.Toast.Show("账号下还没有收藏夹, 请先去 B 站创建一个");
                return;
            }

            if (_closing) return;
            var picker = new FavFolderPickerWindow(folders, _videoTitle) { Owner = this };
            picker.ShowDialog();
            if (_closing || aid != _aid || generation != _loadGen || !picker.Confirmed) return;

            var addIds = picker.AddedIds;
            var delIds = picker.RemovedIds;
            if (addIds.Count == 0 && delIds.Count == 0) return;

            var (ok2, err2) = await Svc.Api.DealFavoriteAsync(aid,
                addIds.Count > 0 ? addIds : null,
                delIds.Count > 0 ? delIds : null);
            if (_closing || aid != _aid || generation != _loadGen) return;
            if (!ok2) { Svc.Toast.Show("收藏失败: " + (err2 ?? "未知错误")); return; }

            var final = picker.FinalCheckedIds;
            SetFavorited(final.Count > 0);
            Svc.UserState.PublishFavorite(aid, bvid, final.Count > 0);
            PlayThemedAction(ThemeReaction.Favorite, BtnFavorite, FavIconBox, addIds.Count > 0);
            if (final.Count == 0) LockActionHoverPreview();
        }
        catch (Exception ex) { if (!_closing) Svc.Toast.Show("收藏失败: " + ex.Message); }
        finally
        {
            if (aid == _aid && generation == _loadGen) _favBusy = false;
        }
    }

    private bool _followBusy;

    /// <summary>查询当前 UP 主关注状态, 用于关注按钮切换显示</summary>
    private async Task RefreshFollowStateAsync()
    {
        if (_ownerMid <= 0 || _closing) return;
        var mid = _ownerMid; var gen = _loadGen;
        var (relOk, _, followed) = await Svc.Api.GetRelationAsync(mid);
        if (!relOk || _closing || mid != _ownerMid || gen != _loadGen) return;
        _isFollowed = followed;
        UpdateFollowButton();
    }

    private void UpdateFollowButton()
    {
        if (FollowLabel == null) return;
        FollowLabel.Text = _isFollowed ? "已关注" : "+ 关注";
    }

    private async void OnFollowClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin || _ownerMid <= 0) { Svc.Toast.Show("关注需要先登录"); return; }
        if (_followBusy) return;
        _followBusy = true;
        var mid = _ownerMid; var gen = _loadGen; var target = !_isFollowed;
        try
        {
            var (ok, err) = await Svc.Api.FollowUpAsync(mid, target);
            if (_closing || mid != _ownerMid || gen != _loadGen) return;
            if (ok)
            {
                _isFollowed = target;
                UpdateFollowButton();
                Svc.UserState.PublishFollow(mid, target);
            }
            else
            {
                Svc.Toast.Show("操作失败: " + (err ?? "未知错误"));
            }
        }
        catch (Exception ex) { if (!_closing) Svc.Toast.Show("操作失败: " + ex.Message); }
        finally { if (mid == _ownerMid && gen == _loadGen) _followBusy = false; }
    }

    private void OnUpPageClick(object sender, RoutedEventArgs e)
    {
        if (_ownerMid <= 0) return;
        if (Svc.Navigate != null) Svc.Navigate.OpenUserSpace(_ownerMid.ToString());
        else new NavigationDispatcher(this).OpenUserSpace(_ownerMid.ToString());
    }

    /// <summary>
    /// 左键点 UP 头像/名字 → UP 主主页。
    /// 原"UP 主页"文字入口已删(2026-09-26 用户要求), 这就是 UP 入口的唯一落点;
    /// 头像的右键菜单(看大图/下载)走 ContextMenu, 与左键互不干扰。
    /// </summary>
    private void OnUpAvatarClick(object sender, MouseButtonEventArgs e) => OnUpPageClick(sender, e);

    /// <summary>右键 UP 主头像 → 查看大图</summary>
    private void OnViewUpAvatar(object sender, RoutedEventArgs e)
        => MediaActions.ShowImage(_ownerFace,
            string.IsNullOrEmpty(_ownerName) ? "UP 主头像" : _ownerName + " 的头像", this);

    /// <summary>右键 UP 主头像 → 保存到本地</summary>
    private async void OnDownloadUpAvatar(object sender, RoutedEventArgs e)
        => await MediaActions.SaveImageAsync(_ownerFace,
            string.IsNullOrEmpty(_ownerName) ? "头像" : _ownerName + "_头像");

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentBvid)) return;
        try
        {
            Clipboard.SetText("https://www.bilibili.com/video/" + _currentBvid);
            Svc.Toast.Show("链接已复制");
        }
        catch
        {
            // 剪贴板被别的进程占着时 SetText 会抛(系统级竞争, 重试也可能再失败)。
            // 静默吞掉即可: 复制失败不该弹框打断播放, 而且没有"已复制"的提示用户自己会再点一次。
        }
    }

    /// <summary>
    /// "网页版"入口已随第二行小字链接一起删除(2026-09-26 用户要求)。
    /// 别处要"跳系统浏览器"时, 用 Process.Start(url) { UseShellExecute = true } 即可。
    /// </summary>

    // ------------------------------------------------------------ Toast

    private System.Windows.Threading.DispatcherTimer? _localToastTimer;
    private void OnToast(string msg) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closing) return;
        LocalToastText.Text = msg;
        LocalToastBorder.Visibility = Visibility.Visible;
        // ★ 淡入前必须先摘掉上一次的动画 + 归位: 连发两条 Toast 时(投币成功又来一条提示),
        //   上一次的淡出动画可能还挂着。不摘的话新淡入的起点读到的是"动画当前值"
        //   (可能已经是 0), 于是这条 Toast 是从 0 淡到 0.95 —— **等于看不见**。
        LocalToastBorder.BeginAnimation(OpacityProperty, null);
        LocalToastBorder.Opacity = 0.95;
        LocalToastBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(160))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        _localToastTimer?.Stop();
        _localToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _localToastTimer.Tick += (_, _) =>
        {
            _localToastTimer.Stop();
            var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            a.Completed += (_, _) =>
            {
                // ★ 淡出结束后把动画摘掉并归位: 不摘的话 Opacity 被动画钉在 0,
                //   下一条 Toast 即使 Visible 也完全不显示(整块透明)。
                LocalToastBorder.BeginAnimation(OpacityProperty, null);
                LocalToastBorder.Opacity = 0.95;
                LocalToastBorder.Visibility = Visibility.Collapsed;
            };
            LocalToastBorder.BeginAnimation(OpacityProperty, a);
        };
        _localToastTimer.Start();
    }));

}
