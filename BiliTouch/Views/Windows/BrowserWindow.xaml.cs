using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BiliTouch.Helpers;
using BiliTouch.Services;
using Microsoft.Web.WebView2.Core;

namespace BiliTouch.Views;

/// <summary>通用 WebView2 浏览窗口: 用户主动触发的回退入口(排行榜网页版、网页搜索等)
/// 内部跳转不再自动拦截, 由用户主动关闭此窗口回主界面</summary>
public partial class BrowserWindow : FluentWindow
{
    private readonly string _initialUrl;
    private string _currentUrl;
    private long _lastExternalOpen;
    private const string OpenExternalMessage = "bd-open-external";
    private const long OpenExternalThrottleMs = 500;

    public BrowserWindow(string url, string title)
    {
        InitializeComponent();
        _initialUrl = url;
        _currentUrl = url;
        Title = title;
        Loaded += OnLoaded;
        Closed += OnClosed;
        ReleaseStrayCapture();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Svc.WebView2.InitAsync(Wv);
            var core = Wv.CoreWebView2;
            core.SourceChanged += OnSourceChanged;
            core.WebMessageReceived += OnWebMessage;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildOpenExternalScript());
            core.Navigate(_initialUrl);
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("网页加载失败: " + ex.Message);
            Close();
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        try
        {
            var src = Wv.CoreWebView2?.Source;
            if (string.IsNullOrWhiteSpace(src)) return;
            _currentUrl = src;
            Title = src;
        }
        catch { }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }
        if (!string.Equals(message, OpenExternalMessage, StringComparison.Ordinal)) return;

        var now = Environment.TickCount64;
        if (now - _lastExternalOpen < OpenExternalThrottleMs) return;
        _lastExternalOpen = now;

        string? target = null;
        try { target = Wv.CoreWebView2?.Source; } catch { }
        if (string.IsNullOrWhiteSpace(target)) target = _currentUrl;
        if (!LinkLauncher.OpenInSystemBrowser(target))
            Svc.Toast.Show("无法使用系统默认浏览器打开当前网页");
    }

    private const string OpenExternalScriptTemplate = """
(function () {
  if (window.__bdOpenExternalReady) return;
  window.__bdOpenExternalReady = true;

  var ID = '__bd-open-external';

  function ensure() {
    try {
      if (window.top !== window) return;
      if (!/^https?:$/i.test(location.protocol)) return;
      if (!document.body) return;
      if (document.getElementById(ID)) return;

      var btn = document.createElement('button');
      btn.id = ID;
      btn.type = 'button';
      btn.textContent = '在浏览器中打开 ↗';
      btn.title = '用系统的默认浏览器打开当前页面';
      btn.style.cssText = [
        'position:fixed', 'right:16px', 'bottom:16px', 'z-index:2147483647',
        'margin:0', 'padding:8px 15px', 'border:0', 'border-radius:999px',
        'font:600 13px/1.4 "Microsoft YaHei",system-ui,sans-serif',
        'color:__BD_ON_ACCENT__', 'background:__BD_ACCENT__',
        'box-shadow:0 0 0 1px rgba(255,255,255,.55), 0 4px 14px rgba(0,0,0,.45)',
        'cursor:pointer', 'opacity:.92', 'transition:opacity .15s,transform .15s'
      ].join(';');
      btn.onmouseenter = function () { btn.style.opacity = '1'; btn.style.transform = 'translateY(-1px)'; };
      btn.onmouseleave = function () { btn.style.opacity = '.92'; btn.style.transform = 'none'; };
      btn.onclick = function (ev) {
        ev.preventDefault();
        ev.stopPropagation();
        try { window.chrome.webview.postMessage('bd-open-external'); } catch (e) { }
      };
      document.body.appendChild(btn);
    } catch (e) { }
  }

  if (document.readyState === 'loading')
    document.addEventListener('DOMContentLoaded', ensure, { once: true });
  else ensure();
  setInterval(ensure, 2000);
})();
""";

    private string BuildOpenExternalScript()
    {
        var accent = (TryFindResource("AccentBrush") as SolidColorBrush)?.Color
                     ?? Color.FromRgb(0xFB, 0x72, 0x99);
        var onAccent = (TryFindResource("TextOnAccentBrush") as SolidColorBrush)?.Color
                       ?? Colors.White;
        return OpenExternalScriptTemplate
            .Replace("__BD_ACCENT__", ToCss(accent))
            .Replace("__BD_ON_ACCENT__", ToCss(onAccent));
    }

    private static string ToCss(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void ReleaseStrayCapture()
    {
        try
        {
            if (Mouse.Captured is FrameworkElement fe && Window.GetWindow(fe) != this)
                Mouse.Capture(null);
        }
        catch { }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ReleaseStrayCapture();
        try
        {
            if (Wv?.CoreWebView2 is { } core)
            {
                core.SourceChanged -= OnSourceChanged;
                core.WebMessageReceived -= OnWebMessage;
            }
        }
        catch { }
        try { Wv?.Dispose(); } catch { }
    }
}