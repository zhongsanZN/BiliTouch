using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.Views.Controls;

namespace BiliTouch.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ 长按点赞 = 一键三连

    /// <summary>长按总时长(ms)：按住满 2 秒触发一键三连（前 500ms 静默确认，后 1500ms 进度环扫满）。</summary>
    internal const int LikeLongPressMs = 2000;

    /// <summary>长按确认时长(ms)：不足此值松手为普通点击，超过则进入进度环动画。</summary>
    internal const int LikeConfirmMs = 500;

    /// <summary>进度环扫满时长(ms)。</summary>
    internal const int LikeProgressMs = LikeLongPressMs - LikeConfirmMs;

    /// <summary>取消长按的点击抑制阈值(ms)：与确认时长对齐，消除无反馈死区。</summary>
    internal const int LikeClickMaxMs = LikeConfirmMs;

    internal const int TripleComboSuppressMs = 1500;
    internal const int LikeCancelSuppressMs = 1000;

    private static readonly TimeSpan LikePressTick = TimeSpan.FromMilliseconds(16);

    private long _tripleComboAt;
    private bool _tripleComboBusy;
    private long _likeDownTick;
    private long _likePressCancelAt;
    private long _likePressStartTick;
    private bool _likePressCommitted;
    private bool _likeConfirmed;
    private int _activeLikeTouchId = -1;

    private DispatcherTimer? _likePressProgressTimer;
    private PressRingAdorner? _likePressAdorner;

    internal static bool IsTripleComboSuppressActive(long comboAt, long nowTick)
        => comboAt != 0 && nowTick - comboAt < TripleComboSuppressMs;

    internal static bool TryConsumeCancelTimestamp(ref long cancelAt, long nowTick)
    {
        if (cancelAt == 0) return false;
        var pending = nowTick - cancelAt < LikeCancelSuppressMs;
        cancelAt = 0;
        return pending;
    }

    private bool ShouldSuppressLikeClick(object sender)
    {
        // 只抑制来自 BtnLike 本身的 Click；快捷键 L（sender 为 PlayerWindow）不受长按残留窗口影响。
        if (!ReferenceEquals(sender, BtnLike)) return false;
        var now = Environment.TickCount64;
        if (IsTripleComboSuppressActive(_tripleComboAt, now))
        {
            _tripleComboAt = 0;
            _likePressCancelAt = 0;
            return true;
        }
        return TryConsumeCancelTimestamp(ref _likePressCancelAt, now);
    }

    private void OnTripleButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e) || _activeLikeTouchId >= 0) return;
        BeginTriplePress(sender);
    }

    private void OnTripleButtonPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e) || _activeLikeTouchId >= 0) return;
        EndTriplePressUp();
    }

    private void OnTripleButtonMouseLeave(object sender, MouseEventArgs e)
    {
        if (_activeLikeTouchId >= 0) return;
        EndTriplePressLeave();
    }

    private void OnTripleButtonLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e) || _activeLikeTouchId >= 0) return;
        EndTriplePressLeave();
    }

    private void OnTripleButtonPreviewTouchDown(object sender, TouchEventArgs e)
    {
        if (_activeLikeTouchId >= 0) return;
        if (BeginTriplePress(sender))
            _activeLikeTouchId = e.TouchDevice.Id;
    }

    private void OnTripleButtonPreviewTouchMove(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id != _activeLikeTouchId || _likePressCommitted || BtnLike == null) return;
        var pos = e.GetTouchPoint(BtnLike).Position;
        const double slop = 16;
        if (pos.X < -slop || pos.Y < -slop || pos.X > BtnLike.ActualWidth + slop || pos.Y > BtnLike.ActualHeight + slop)
        {
            _activeLikeTouchId = -1;
            EndTriplePressLeave();
        }
    }

    private void OnTripleButtonPreviewTouchUp(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id != _activeLikeTouchId) return;
        _activeLikeTouchId = -1;
        EndTriplePressUp();
    }

    private void OnTripleButtonLostTouchCapture(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id != _activeLikeTouchId) return;
        _activeLikeTouchId = -1;
        EndTriplePressLeave();
    }

    private bool BeginTriplePress(object sender)
    {
        var now = Environment.TickCount64;
        _likeDownTick = now;
        _likePressCancelAt = 0;
        if (_closing || _tripleComboBusy) return false;
        if (sender is not Button btn) return false;
        if (!Svc.Session.HasLogin || _isLocalPlayback || _isLive) return false;
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0) return false;

        StartLikePressProgress(btn, now);
        return true;
    }

    private void EndTriplePressUp()
    {
        var now = Environment.TickCount64;
        if (_likeDownTick != 0 && now - _likeDownTick >= LikeClickMaxMs)
            _likePressCancelAt = now;
        _likeDownTick = 0;
        CancelLikePressProgress();
    }

    private void EndTriplePressLeave()
    {
        var now = Environment.TickCount64;
        if (_likeDownTick != 0 && !_likePressCommitted && now - _likeDownTick >= LikeClickMaxMs)
            _likePressCancelAt = now;
        _likeDownTick = 0;
        CancelLikePressProgress();
    }

    private void StartLikePressProgress(Button btn, long startTick)
    {
        EnsureLikePressAdorner(btn);
        if (_likePressAdorner == null) return;

        _likePressCommitted = false;
        _likeConfirmed = false;
        _likePressStartTick = startTick;

        _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, null);
        _likePressAdorner.Opacity = 0;
        _likePressAdorner.Progress = 0;
        _likePressAdorner.InvalidateVisual();

        _likePressProgressTimer ??= new DispatcherTimer { Interval = LikePressTick };
        _likePressProgressTimer.Tick -= OnLikePressProgressTick;
        _likePressProgressTimer.Tick += OnLikePressProgressTick;
        _likePressProgressTimer.Start();
    }

    private void EnsureLikePressAdorner(Button? btn)
    {
        if (btn == null) return;
        if (_likePressAdorner != null && ReferenceEquals(_likePressAdorner.AdornedElement, btn)) return;

        if (_likePressAdorner != null)
        {
            AdornerLayer.GetAdornerLayer(_likePressAdorner.AdornedElement)?.Remove(_likePressAdorner);
            _likePressAdorner = null;
        }

        var layer = AdornerLayer.GetAdornerLayer(btn);
        if (layer == null) return;
        _likePressAdorner = new PressRingAdorner(btn);
        layer.Add(_likePressAdorner);
    }

    private void OnLikePressProgressTick(object? sender, EventArgs e)
    {
        if (_closing) { StopLikePressProgress(); return; }
        var elapsed = Environment.TickCount64 - _likePressStartTick;

        if (elapsed < LikeConfirmMs) return;

        if (!_likeConfirmed)
        {
            _likeConfirmed = true;
            if (_likePressAdorner != null)
            {
                var brush = new SolidColorBrush(ActionLikeColor);
                if (brush.CanFreeze) brush.Freeze();
                _likePressAdorner.Stroke = brush;
                _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, null);
                _likePressAdorner.Opacity = 0.95;
            }
        }

        var p = Math.Clamp((elapsed - LikeConfirmMs) / (double)LikeProgressMs, 0, 1);
        UpdateLikePressRing(p);
        if (p >= 1)
        {
            StopLikePressProgress();
            FireTripleCombo();
        }
    }

    private void FireTripleCombo()
    {
        _tripleComboAt = Environment.TickCount64;
        _likeDownTick = 0;
        _activeLikeTouchId = -1;
        CommitLikePressProgress();
        _ = PlayTripleComboShowAsync();
        _ = RunTripleComboAsync();
    }

    private void UpdateLikePressRing(double p)
    {
        if (_likePressAdorner == null) return;
        _likePressAdorner.Progress = p;
        _likePressAdorner.InvalidateVisual();
    }

    private void StopLikePressProgress() => _likePressProgressTimer?.Stop();

    private void CommitLikePressProgress()
    {
        StopLikePressProgress();
        UpdateLikePressRing(1);
        _likePressCommitted = true;
    }

    private void CancelLikePressProgress()
    {
        if (_likePressCommitted) return;
        StopLikePressProgress();
        _likeConfirmed = false;
        FadeOutLikePressProgress();
    }

    private void SetLikePressProgressColor(Color color)
    {
        if (_likePressAdorner == null) return;
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze) brush.Freeze();
        _likePressAdorner.Stroke = brush;
        _likePressAdorner.InvalidateVisual();
    }

    private void FinishLikePressProgress()
    {
        _likePressCommitted = false;
        StopLikePressProgress();
        FadeOutLikePressProgress();
    }

    private void ResetLikePressProgress()
    {
        _likePressCommitted = false;
        _likeConfirmed = false;
        _likeDownTick = 0;
        _likePressStartTick = 0;
        _activeLikeTouchId = -1;
        StopLikePressProgress();
        if (_likePressAdorner != null)
        {
            _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, null);
            _likePressAdorner.Opacity = 0;
            _likePressAdorner.Progress = 0;
            _likePressAdorner.InvalidateVisual();
        }
        if (TripleComboBadge != null)
        {
            TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, null);
            TripleComboBadge.Opacity = 0;
        }
        if (TripleComboBadgeScale != null)
        {
            TripleComboBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TripleComboBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            TripleComboBadgeScale.ScaleX = 0.8;
            TripleComboBadgeScale.ScaleY = 0.8;
        }
    }

    private void FadeOutLikePressProgress()
    {
        if (_likePressAdorner == null) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, null);
            _likePressAdorner.Opacity = 0;
            _likePressAdorner.Progress = 0;
            _likePressAdorner.InvalidateVisual();
            return;
        }
        var adorner = _likePressAdorner;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        anim.Completed += (_, _) =>
        {
            adorner.BeginAnimation(UIElement.OpacityProperty, null);
            adorner.Opacity = 0;
            adorner.Progress = 0;
            adorner.InvalidateVisual();
        };
        adorner.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private async Task PlayTripleComboShowAsync()
    {
        if (_closing || _likePressAdorner == null) return;
        SetLikePressProgressColor(ActionLikeColor);
        PulseLikePressProgress();
        PlayButtonPop(BtnLike);
        PlayActionPop(LikeIconBox);
        PulseTripleSkinGlow(ThemedLikeIcon, playAudio: true);
        try
        {
            await Task.Delay(350);
            if (_closing) return;
            SetLikePressProgressColor(ActionCoinColor);
            PlayButtonPop(BtnCoin);
            PlayActionPop(CoinIconBox);
            FlashButtonRing(BtnCoin, ActionCoinColor);
            PulseTripleSkinGlow(ThemedCoinIcon);

            await Task.Delay(350);
            if (_closing) return;
            SetLikePressProgressColor(ActionSaveColor);
            PlayButtonPop(BtnFavorite);
            PlayActionPop(FavIconBox);
            FlashButtonRing(BtnFavorite, ActionSaveColor);
            PulseTripleSkinGlow(ThemedFavoriteIcon);

            await Task.Delay(350);
            if (_closing) return;
            ShowTripleComboBadge();
            await Task.Delay(600);
        }
        catch
        {
            // 窗口关闭时演出直接结束。
        }
        finally
        {
            if (!_closing)
            {
                HideTripleComboBadge();
                FinishLikePressProgress();
            }
        }
    }

    private void PulseTripleSkinGlow(SkinActionIcon? icon, bool playAudio = false)
    {
        if (_closing || !SkinService.Instance.IsPhantomBlade || !SkinService.Instance.EffectsEnabled) return;
        if (playAudio) ThemeAudio.PlayLike();
        icon?.PulseGlow();
    }

    private void PulseLikePressProgress()
    {
        if (_likePressAdorner == null || !SystemParameters.ClientAreaAnimation) return;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.55, KeyTime.FromPercent(0.4)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromPercent(1)));
        _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void ShowTripleComboBadge()
    {
        if (TripleComboBadge == null || TripleComboBadgeScale == null) return;
        TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, null);
        TripleComboBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TripleComboBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (!SystemParameters.ClientAreaAnimation)
        {
            TripleComboBadge.Opacity = 1;
            TripleComboBadgeScale.ScaleX = 1;
            TripleComboBadgeScale.ScaleY = 1;
            return;
        }

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        var scale = new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, fade);
        TripleComboBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        TripleComboBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void HideTripleComboBadge()
    {
        if (TripleComboBadge == null) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, null);
            TripleComboBadge.Opacity = 0;
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, null);
            TripleComboBadge.Opacity = 0;
        };
        TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void PlayButtonPop(Button? btn)
    {
        if (btn == null || _closing || !SystemParameters.ClientAreaAnimation) return;
        var scale = new ScaleTransform(1, 1);
        btn.RenderTransformOrigin = new Point(0.5, 0.5);
        btn.RenderTransform = scale;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromPercent(0.4))
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

    private void FlashButtonRing(Button? btn, Color color)
    {
        if (btn == null || _closing || !SystemParameters.ClientAreaAnimation) return;
        var layer = AdornerLayer.GetAdornerLayer(btn);
        if (layer == null) return;

        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze) brush.Freeze();
        var ring = new PressRingAdorner(btn) { Progress = 1, Stroke = brush };
        layer.Add(ring);

        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(450));
        anim.Completed += (_, _) =>
        {
            try { layer.Remove(ring); } catch { /* 窗口已关 */ }
        };
        ring.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>将三连 API 响应合并到本地计数与状态（无网络单测可调用）。</summary>
    internal void ApplyTripleLikeSuccess(ApiClient.TripleLikeResult res)
    {
        var wasLiked = _isLiked;
        var wasCoined = _isCoined;
        var wasFav = _isFavorited;

        if (res.Like)
        {
            _isLiked = true;
            if (!wasLiked) _likeCount = Math.Max(0, _likeCount + 1);
        }

        if (res.Coin || res.Multiply > 0)
        {
            var addedCoins = res.Multiply > 0 ? res.Multiply : (wasCoined ? 0 : 2);
            _isCoined = true;
            _coinCount = Math.Max(0, _coinCount + addedCoins);
            if (ThemedCoinIcon != null)
            {
                var prior = wasCoined ? Math.Max(ThemedCoinIcon.Count, 1) : 0;
                ThemedCoinIcon.Count = Math.Clamp(prior + (addedCoins > 0 ? addedCoins : (res.Coin ? 2 : 0)), 1, 2);
            }
        }

        if (res.Fav)
        {
            _isFavorited = true;
            if (!wasFav) _favCount = Math.Max(0, _favCount + 1);
        }
    }

    internal static string FormatTripleToast(bool liked, bool coined, bool favorited)
    {
        if (liked && coined && favorited) return "一键三连成功";
        var missing = new System.Collections.Generic.List<string>(3);
        if (!liked) missing.Add("点赞");
        if (!coined) missing.Add("投币");
        if (!favorited) missing.Add("收藏");
        return missing.Count == 3
            ? "三连请求已返回，请检查登录或硬币余额"
            : "部分完成（未完成：" + string.Join("、", missing) + "）";
    }

    private async Task RunTripleComboAsync()
    {
        if (_closing || _tripleComboBusy) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("三连需要先登录"); return; }
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0 || _isLocalPlayback || _isLive)
        {
            Svc.Toast.Show("当前内容不支持三连");
            return;
        }
        if (_isLiked && _isCoined && _isFavorited)
        {
            Svc.Toast.Show("这个视频已经三连过了");
            return;
        }

        var bvid = _currentBvid;
        var aid = _aid;
        var generation = _loadGen;
        _tripleComboBusy = true;
        try
        {
            Svc.Toast.Show("一键三连中…");
            var res = await Svc.Api.TripleLikeAsync(bvid);
            if (_closing || bvid != _currentBvid || aid != _aid || generation != _loadGen) return;

            if (!res.Ok)
            {
                var m = res.Error ?? "未知错误";
                Svc.Toast.Show(m.StartsWith("B 站风控", StringComparison.Ordinal)
                    ? m : "三连失败: " + m);
                return;
            }

            ApplyTripleLikeSuccess(res);
            if (_isFavorited) Svc.UserState.PublishFavorite(aid, bvid, true);
            UpdateActionCounts();

            // 若接口返回某些位为 false（可能原先已在另一端完成或受限），回查以对齐真实状态。
            var syncTasks = new System.Collections.Generic.List<Task>(3);
            if (!_isLiked) syncTasks.Add(RefreshLikeStateAsync());
            if (!_isCoined) syncTasks.Add(RefreshCoinStateAsync());
            if (!_isFavorited) syncTasks.Add(RefreshFavStateAsync());
            if (syncTasks.Count > 0)
            {
                await Task.WhenAll(syncTasks);
                if (_closing || bvid != _currentBvid || aid != _aid || generation != _loadGen) return;
                UpdateActionCounts();
            }

            Svc.Toast.Show(FormatTripleToast(_isLiked, _isCoined, _isFavorited));
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            Svc.Toast.Show("三连失败: " + ex.Message);
        }
        finally
        {
            _tripleComboBusy = false;
        }
    }
}
