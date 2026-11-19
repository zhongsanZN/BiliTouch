using System;
using System.Collections.Generic;
using System.Windows;
using BiliTouch.Models;

namespace BiliTouch.Services;

/// <summary>
/// 全站用户状态联动总线：把点赞/收藏/稍后再看/关注/历史/动态变更统一在一个单一出口广播，
/// 消除播放器、直播间、首页卡片、关注页、我的页、UP 主空间之间的状态割裂。
///
/// 设计兼顾两点：
///   1. 体验强联动：任意入口完成操作，所有已缓存的页面与窗口即时感知或标脏刷新；
///   2. 架构弱耦合与零泄漏：订阅方只依赖轻量事件负载（不直接互相引用页面/窗口），
///      内部按弱引用自动回收已卸载/释放的订阅者，并统一派发到 UI 线程。
/// </summary>
public sealed class UserStateBus
{
    public static UserStateBus Instance { get; } = new();

    private readonly WeakChannel<FollowStateChange> _follow = new();
    private readonly WeakChannel<FavoriteStateChange> _favorite = new();
    private readonly WeakChannel<WatchLaterStateChange> _watchLater = new();
    private readonly WeakChannel<HistoryRecordChange> _history = new();
    private readonly WeakChannel<DynamicItem> _dynamicPublished = new();
    private readonly WeakChannel<DynamicMutationChange> _dynamicMutated = new();

    public void SubscribeFollow(object owner, Action<FollowStateChange> handler) => _follow.Subscribe(owner, handler);
    public void SubscribeFavorite(object owner, Action<FavoriteStateChange> handler) => _favorite.Subscribe(owner, handler);
    public void SubscribeWatchLater(object owner, Action<WatchLaterStateChange> handler) => _watchLater.Subscribe(owner, handler);
    public void SubscribeHistory(object owner, Action<HistoryRecordChange> handler) => _history.Subscribe(owner, handler);
    public void SubscribeDynamicPublished(object owner, Action<DynamicItem> handler) => _dynamicPublished.Subscribe(owner, handler);
    public void SubscribeDynamicMutated(object owner, Action<DynamicMutationChange> handler) => _dynamicMutated.Subscribe(owner, handler);

    public void PublishFollow(long mid, bool followed)
    {
        if (mid <= 0) return;
        Dispatch(() => _follow.Publish(new FollowStateChange(mid, followed)));
    }

    public void PublishFavorite(long aid, string? bvid, bool favorited)
    {
        if (aid <= 0 && string.IsNullOrEmpty(bvid)) return;
        Dispatch(() => _favorite.Publish(new FavoriteStateChange(aid, bvid ?? "", favorited)));
    }

    public void PublishWatchLater(long aid, string? bvid, bool added)
    {
        if (aid <= 0 && string.IsNullOrEmpty(bvid)) return;
        Dispatch(() => _watchLater.Publish(new WatchLaterStateChange(aid, bvid ?? "", added)));
    }

    public void PublishHistoryRecorded(long aid, string? bvid = null, bool isLive = false)
    {
        if (aid <= 0 && string.IsNullOrEmpty(bvid)) return;
        Dispatch(() => _history.Publish(new HistoryRecordChange(aid, bvid ?? "", isLive)));
    }

    public void PublishDynamicCreated(DynamicItem item)
    {
        if (item == null) return;
        Dispatch(() => _dynamicPublished.Publish(item));
    }

    public void PublishDynamicMutated(string? dynamicId, bool? liked = null, int commentDelta = 0, int repostDelta = 0, DynamicItem? alreadyAppliedTo = null)
    {
        if (string.IsNullOrWhiteSpace(dynamicId)) return;
        Dispatch(() => _dynamicMutated.Publish(new DynamicMutationChange(dynamicId, liked, commentDelta, repostDelta, alreadyAppliedTo)));
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        dispatcher.BeginInvoke(action);
    }

    private sealed class WeakChannel<T>
    {
        private readonly object _gate = new();
        private readonly List<(WeakReference Owner, Action<T> Handler)> _subs = new();

        public void Subscribe(object owner, Action<T> handler)
        {
            lock (_gate)
            {
                _subs.RemoveAll(s => !s.Owner.IsAlive);
                _subs.Add((new WeakReference(owner), handler));
            }
        }

        public void Publish(T payload)
        {
            Action<T>[] snapshot;
            lock (_gate)
            {
                var list = new List<Action<T>>(_subs.Count);
                for (var i = _subs.Count - 1; i >= 0; i--)
                {
                    if (!_subs[i].Owner.IsAlive)
                    {
                        _subs.RemoveAt(i);
                        continue;
                    }
                    list.Add(_subs[i].Handler);
                }
                list.Reverse();
                snapshot = list.ToArray();
            }
            foreach (var handler in snapshot)
            {
                try { handler(payload); }
                catch { /* 单个订阅者异常不影响其他页面同步 */ }
            }
        }
    }
}

public readonly record struct FollowStateChange(long Mid, bool Followed);
public readonly record struct FavoriteStateChange(long Aid, string Bvid, bool Favorited);
public readonly record struct WatchLaterStateChange(long Aid, string Bvid, bool Added);
public readonly record struct HistoryRecordChange(long Aid, string Bvid, bool IsLive);
public readonly record struct DynamicMutationChange(string DynamicId, bool? Liked, int CommentDelta, int RepostDelta, DynamicItem? AlreadyAppliedTo = null);
