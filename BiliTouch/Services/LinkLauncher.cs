using System;
using System.Diagnostics;

namespace BiliTouch.Services;

/// <summary>
/// 外部网页链接校验与系统浏览器唤起工具。
/// </summary>
public static class LinkLauncher
{
    /// <summary>只放行 http/https，避免外部页面脚本把 file:/javascript:/ms-* 传给 ShellExecute。</summary>
    public static bool IsWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>用系统默认浏览器打开指定网页 URL。</summary>
    public static bool OpenInSystemBrowser(string? url)
    {
        if (!IsWebUrl(url)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url!)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
