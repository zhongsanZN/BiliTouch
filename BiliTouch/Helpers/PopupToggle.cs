using System;

namespace BiliTouch.Helpers;

/// <summary>
/// 播放器弹层「点一下展开, 再点一下收起」的纯判定逻辑(不依赖 WPF)。
///
/// ★ StaysOpen="False" 的弹层在再点入口按钮时, WPF 在 MouseDown 阶段就将其收起,
///   Click 到达时 IsOpen 已为 false。若只判 !IsOpen 会立刻重新打开。
/// </summary>
public static class PopupToggle
{
    /// <summary>抑制窗口(毫秒): 刚被入口按钮点击收起后的保护时间窗。</summary>
    public const long SuppressReopenMs = 350;

    /// <summary>入口按钮这一下该执行的动作</summary>
    public enum Action
    {
        /// <summary>展开弹层</summary>
        Open,
        /// <summary>收起弹层(此刻它还开着)</summary>
        Close,
        /// <summary>保持收起: 这一下就是收起它的点击, WPF 已在 MouseDown 阶段代劳</summary>
        StayClosed,
    }

    /// <summary>
    /// 按"当前是否开着 + 最近一次由入口按压触发的收起时刻"判定入口按钮动作。
    /// </summary>
    public static Action Decide(bool isOpen, long? closedAtTick, long nowTick, long suppressMs = SuppressReopenMs)
    {
        if (isOpen) return Action.Close;
        if (closedAtTick.HasValue && nowTick - closedAtTick.Value < suppressMs) return Action.StayClosed;
        return Action.Open;
    }
}
