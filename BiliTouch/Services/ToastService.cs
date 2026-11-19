using System;
using System.Windows;

namespace BiliTouch.Services;

/// <summary>轻量 Toast 通知(主窗口右上角浮动提示，线程安全自动切回 UI 线程)</summary>
public class ToastService
{
    public static ToastService Instance { get; } = new();

    public event Action<string>? Shown;

    public void Show(string message)
    {
        var handler = Shown;
        if (handler == null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            dispatcher.BeginInvoke(new Action(() => Shown?.Invoke(message)));
            return;
        }
        handler.Invoke(message);
    }
}