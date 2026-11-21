using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace BiliTouch.Helpers;

/// <summary>
/// 澎湃 OS (HyperOS) 统一实时物理弹簧动效调度器。
/// <para>
/// 所有微动效（卡片按压回弹、导航滑块、弹层弹出、抽屉/子页滑入、Toast/OSD 胶囊）共享单一
/// <see cref="CompositionTarget.Rendering"/> 合成帧时钟：
/// <list type="bullet">
///   <item><description>空闲时自动退订静态渲染事件（零后台开销）；</description></item>
///   <item><description>同通道中断自动继承当前瞬时位置与速度向量（真正物理可打断）；</description></item>
///   <item><description>全程只改复用 <see cref="ScaleTransform"/> / <see cref="TranslateTransform"/> / <see cref="UIElement.Opacity"/>，零 Measure/Arrange 重排。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class HyperMotion
{
    internal readonly record struct SpringSpec(
        double Omega,
        double DampingRatio,
        double PosEpsilon,
        double VelEpsilon);

    /// <summary>导航/标签滑块：丝滑弹性跟手（轻微 2% 呼吸过冲，典型澎湃 OS 指示条手感）。</summary>
    internal static readonly SpringSpec TabIndicator = new(23.0, 0.80, 0.10, 0.6);

    /// <summary>子页/抽屉/面板滑入：稳重顺滑长尾减速。</summary>
    internal static readonly SpringSpec SheetSlide = new(18.5, 0.94, 0.15, 0.9);

    /// <summary>抽屉/面板收起：利落不拖沓。</summary>
    internal static readonly SpringSpec SheetDismiss = new(24.0, 1.0, 0.25, 1.5);

    /// <summary>菜单弹层/浮层卡片弹出：从锚点生长带微弹质感。</summary>
    internal static readonly SpringSpec PopupScale = new(26.5, 0.82, 0.001, 0.015);

    /// <summary>卡片/按钮按下：利落跟手下陷。</summary>
    internal static readonly SpringSpec TactilePress = new(36.0, 0.92, 0.001, 0.02);

    /// <summary>卡片/按钮松手与一镜到底落位回弹：澎湃 OS 标志性轻盈微弹。</summary>
    internal static readonly SpringSpec TactileRelease = new(24.5, 0.72, 0.001, 0.02);

    /// <summary>悬停微浮：柔顺无抖动。</summary>
    internal static readonly SpringSpec HoverLift = new(22.0, 0.90, 0.001, 0.015);

    /// <summary>不透明度淡入淡出：零过冲平滑过渡。</summary>
    internal static readonly SpringSpec SmoothFade = new(22.0, 1.0, 0.003, 0.03);

    private sealed class SpringTrack
    {
        public required object Owner;
        public required string Channel;
        public double Position;
        public double Velocity;
        public double Target;
        public SpringSpec Spec;
        public required Action<double> Apply;
        public Action? OnComplete;
        public double ElapsedSeconds;
    }

    private static readonly List<SpringTrack> _tracks = new();
    private static bool _attached;
    private static long _lastTicks;
    private static TimeSpan _lastRenderingTime = TimeSpan.MinValue;

    /// <summary>当前活跃的弹簧通道数（供测试校验收敛后时钟已彻底释放）。</summary>
    internal static int ActiveTrackCount => _tracks.Count;

    /// <summary>静态合成时钟当前是否处于挂载状态。</summary>
    internal static bool IsClockAttached => _attached;

    /// <summary>
    /// 启动或重定向一条物理弹簧通道。若同 (owner, channel) 正在运行且 <paramref name="inheritIfRunning"/> 为 true，
    /// 则保留其当前位置与速度，实现零跳变物理转向。
    /// </summary>
    internal static void SpringTo(
        object owner,
        string channel,
        double from,
        double to,
        SpringSpec spec,
        Action<double> apply,
        Action? onComplete = null,
        bool inheritIfRunning = true,
        double? initialVelocity = null)
    {
        if (owner == null || string.IsNullOrEmpty(channel) || apply == null) return;

        SpringTrack? existing = null;
        for (int i = 0; i < _tracks.Count; i++)
        {
            var t = _tracks[i];
            if (ReferenceEquals(t.Owner, owner) && string.Equals(t.Channel, channel, StringComparison.Ordinal))
            {
                existing = t;
                break;
            }
        }

        if (existing != null)
        {
            if (!inheritIfRunning)
            {
                existing.Position = from;
                existing.Velocity = initialVelocity ?? 0;
            }
            else if (initialVelocity.HasValue)
            {
                existing.Velocity = initialVelocity.Value;
            }
            existing.Target = to;
            existing.Spec = spec;
            existing.Apply = apply;
            existing.OnComplete = onComplete;
            existing.ElapsedSeconds = 0;
            if (!inheritIfRunning)
                SafeApply(existing, existing.Position);
            EnsureClockAttached();
            return;
        }

        var track = new SpringTrack
        {
            Owner = owner,
            Channel = channel,
            Position = from,
            Velocity = initialVelocity ?? 0,
            Target = to,
            Spec = spec,
            Apply = apply,
            OnComplete = onComplete,
            ElapsedSeconds = 0
        };
        _tracks.Add(track);
        SafeApply(track, from);
        EnsureClockAttached();
    }

    /// <summary>取消指定对象的一条或全部弹簧通道。</summary>
    internal static void Cancel(object? owner, string? channel = null)
    {
        if (owner == null || _tracks.Count == 0) return;
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            var t = _tracks[i];
            if (!ReferenceEquals(t.Owner, owner)) continue;
            if (channel != null && !string.Equals(t.Channel, channel, StringComparison.Ordinal)) continue;
            _tracks.RemoveAt(i);
        }
        DetachIfIdle();
    }

    /// <summary>取消全部活跃弹簧通道（测试清理或退出兜底用）。</summary>
    internal static void CancelAll()
    {
        _tracks.Clear();
        DetachIfIdle();
    }

    /// <summary>确定性推进所有活跃弹簧一帧（供单元测试无延时验证）。</summary>
    internal static void StepAll(double seconds)
    {
        if (_tracks.Count == 0) return;
        var dt = Math.Clamp(seconds, 0.001, 0.05);
        List<Action>? completedCallbacks = null;

        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            var t = _tracks[i];
            if (t.Owner is DependencyObject dep && dep.Dispatcher.HasShutdownStarted)
            {
                _tracks.RemoveAt(i);
                continue;
            }

            t.ElapsedSeconds += dt;
            (t.Position, t.Velocity) = SpringMotion.AdvanceDamped(
                t.Position, t.Velocity, t.Target, dt, t.Spec.Omega, t.Spec.DampingRatio);

            bool settled = (Math.Abs(t.Position - t.Target) <= t.Spec.PosEpsilon
                            && Math.Abs(t.Velocity) <= t.Spec.VelEpsilon)
                           || t.ElapsedSeconds >= 3.5;

            if (settled)
            {
                t.Position = t.Target;
                t.Velocity = 0;
                SafeApply(t, t.Target);
                var cb = t.OnComplete;
                _tracks.RemoveAt(i);
                if (cb != null)
                {
                    completedCallbacks ??= new List<Action>();
                    completedCallbacks.Add(cb);
                }
            }
            else
            {
                SafeApply(t, t.Position);
            }
        }

        DetachIfIdle();

        if (completedCallbacks != null)
        {
            for (int i = 0; i < completedCallbacks.Count; i++)
            {
                try { completedCallbacks[i](); } catch { }
            }
        }
    }

    private static void SafeApply(SpringTrack track, double value)
    {
        try { track.Apply(value); }
        catch { }
    }

    private static void EnsureClockAttached()
    {
        if (_attached || _tracks.Count == 0) return;
        _attached = true;
        _lastTicks = Stopwatch.GetTimestamp();
        _lastRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void DetachIfIdle()
    {
        if (!_attached || _tracks.Count > 0) return;
        _attached = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (_tracks.Count == 0)
        {
            DetachIfIdle();
            return;
        }
        if (e is RenderingEventArgs frame)
        {
            if (_lastRenderingTime == frame.RenderingTime) return;
            _lastRenderingTime = frame.RenderingTime;
        }
        var now = Stopwatch.GetTimestamp();
        var dt = Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds;
        _lastTicks = now;
        StepAll(Math.Clamp(dt, 0.001, 0.05));
    }

    // ------------------------------------------------------------ 高层场景动效原语

    /// <summary>
    /// 确保元素挂载了可复用的 <c>TransformGroup(ScaleTransform, TranslateTransform)</c>，
    /// 避免每次动效覆盖或重分配 Transform 对象。
    /// </summary>
    internal static (ScaleTransform Scale, TranslateTransform Translate) EnsureScaleTranslate(
        FrameworkElement element, Point? origin = null)
    {
        if (origin.HasValue)
            element.RenderTransformOrigin = origin.Value;

        if (element.RenderTransform is TransformGroup { IsFrozen: false } tg
            && tg.Children.Count >= 2
            && tg.Children[0] is ScaleTransform { IsFrozen: false } st
            && tg.Children[1] is TranslateTransform { IsFrozen: false } tt)
        {
            return (st, tt);
        }

        var scale = element.RenderTransform is ScaleTransform { IsFrozen: false } existingScale
            ? existingScale
            : new ScaleTransform(1, 1);
        var translate = element.RenderTransform is TranslateTransform { IsFrozen: false } existingTranslate
            ? existingTranslate
            : new TranslateTransform(0, 0);

        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        element.RenderTransform = group;
        return (scale, translate);
    }

    /// <summary>纯等比弹性缩放（卡片按压/悬停/落位回弹/按钮微震）。</summary>
    internal static void AnimateUniformScale(
        FrameworkElement element,
        double targetScale,
        SpringSpec spec,
        double? startScale = null,
        Point? origin = null,
        Action? onComplete = null)
    {
        if (element == null) return;
        var (scale, _) = EnsureScaleTranslate(element, origin ?? new Point(0.5, 0.5));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        var from = startScale ?? scale.ScaleX;
        SpringTo(
            element,
            "UniformScale",
            from,
            targetScale,
            spec,
            v =>
            {
                var clamped = Math.Max(0.05, v);
                scale.ScaleX = clamped;
                scale.ScaleY = clamped;
            },
            onComplete,
            inheritIfRunning: !startScale.HasValue);
    }

    /// <summary>为 Popup 内部根容器播放澎湃 OS 风格锚点生长弹出动效（微缩放 + 轻上浮 + 淡入）。</summary>
    internal static void AnimatePopupEntrance(
        UIElement? child,
        double startScale = 0.91,
        double offsetY = 10.0,
        Point? origin = null)
    {
        if (child is not FrameworkElement fe) return;
        var (scale, translate) = EnsureScaleTranslate(fe, origin ?? new Point(0.5, 1.0));
        fe.BeginAnimation(UIElement.OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);

        SpringTo(fe, "PopupScale", startScale, 1.0, PopupScale, v =>
        {
            var s = Math.Max(0.1, v);
            scale.ScaleX = s;
            scale.ScaleY = s;
        }, inheritIfRunning: false);

        SpringTo(fe, "PopupY", offsetY, 0.0, SheetSlide, v => translate.Y = v, inheritIfRunning: false);
        SpringTo(fe, "PopupOpacity", 0.0, 1.0, SmoothFade, v => fe.Opacity = Math.Clamp(v, 0, 1), inheritIfRunning: false);
    }

    /// <summary>子页 / 详情页 / 抽屉空间滑入（支持纯位移无透明度模式，兼容 PageHost 驱动限制）。</summary>
    internal static void AnimateSheetIn(
        FrameworkElement element,
        double fromX = 0,
        double fromY = 0,
        double fromScale = 1.0,
        bool animateOpacity = true,
        double fromOpacity = 0.0,
        Point? origin = null,
        Action? onComplete = null)
    {
        if (element == null) return;
        var (scale, translate) = EnsureScaleTranslate(element, origin ?? new Point(0.5, 0.5));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);

        Action? primaryDone = onComplete;
        if (Math.Abs(fromX) > 0.01)
        {
            SpringTo(element, "SheetX", fromX, 0, SheetSlide, v => translate.X = v, primaryDone, inheritIfRunning: false);
            primaryDone = null;
        }
        else
        {
            Cancel(element, "SheetX");
            translate.X = 0;
        }

        if (Math.Abs(fromY) > 0.01)
        {
            SpringTo(element, "SheetY", fromY, 0, SheetSlide, v => translate.Y = v, primaryDone, inheritIfRunning: false);
            primaryDone = null;
        }
        else
        {
            Cancel(element, "SheetY");
            translate.Y = 0;
        }

        if (Math.Abs(fromScale - 1.0) > 0.001)
        {
            SpringTo(element, "SheetScale", fromScale, 1.0, SheetSlide, v =>
            {
                var s = Math.Max(0.1, v);
                scale.ScaleX = s;
                scale.ScaleY = s;
            }, primaryDone, inheritIfRunning: false);
            primaryDone = null;
        }
        else
        {
            Cancel(element, "SheetScale");
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }

        if (animateOpacity)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            SpringTo(element, "SheetOpacity", Math.Clamp(fromOpacity, 0, 1), 1.0, SmoothFade,
                v => element.Opacity = Math.Clamp(v, 0, 1), primaryDone, inheritIfRunning: false);
        }
    }

    /// <summary>胶囊提示（Toast / 音量 OSD / 倍速 OSD）澎湃 OS 弹性浮出。</summary>
    internal static void AnimateCapsuleIn(
        FrameworkElement element,
        double fromY = -10,
        double fromScale = 0.90,
        Point? origin = null)
    {
        if (element == null) return;
        var (scale, translate) = EnsureScaleTranslate(element, origin ?? new Point(0.5, 0.0));
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Visibility = Visibility.Visible;

        var startOpacity = element.Opacity > 0.05 && element.Opacity < 0.99 ? element.Opacity : 0.0;
        var alreadyVisible = element.Opacity >= 0.95;

        if (!alreadyVisible)
        {
            SpringTo(element, "CapsuleScale", fromScale, 1.0, PopupScale, v =>
            {
                var s = Math.Max(0.1, v);
                scale.ScaleX = s;
                scale.ScaleY = s;
            }, inheritIfRunning: true);
            SpringTo(element, "CapsuleY", fromY, 0.0, SheetSlide, v => translate.Y = v, inheritIfRunning: true);
        }
        else
        {
            // 已展示时刷新文案：给一个极轻微的 1.0 -> 1.03 -> 1.0 呼吸脉动，确认反馈
            SpringTo(element, "CapsuleScale", 1.03, 1.0, TactileRelease, v =>
            {
                var s = Math.Max(0.1, v);
                scale.ScaleX = s;
                scale.ScaleY = s;
            }, inheritIfRunning: false);
        }

        SpringTo(element, "CapsuleOpacity", startOpacity, 1.0, SmoothFade,
            v => element.Opacity = Math.Clamp(v, 0, 1), inheritIfRunning: true);
    }

    /// <summary>胶囊提示柔和收起。</summary>
    internal static void AnimateCapsuleOut(
        FrameworkElement element,
        double toY = -8,
        double toScale = 0.94,
        Action? onComplete = null)
    {
        if (element == null) return;
        var (scale, translate) = EnsureScaleTranslate(element);
        element.BeginAnimation(UIElement.OpacityProperty, null);

        SpringTo(element, "CapsuleScale", scale.ScaleX, toScale, SheetDismiss, v =>
        {
            var s = Math.Max(0.1, v);
            scale.ScaleX = s;
            scale.ScaleY = s;
        }, inheritIfRunning: true);
        SpringTo(element, "CapsuleY", translate.Y, toY, SheetDismiss, v => translate.Y = v, inheritIfRunning: true);
        SpringTo(element, "CapsuleOpacity", element.Opacity, 0.0, SmoothFade,
            v => element.Opacity = Math.Clamp(v, 0, 1),
            onComplete,
            inheritIfRunning: true);
    }
}
