using System.Windows;
using System.Windows.Input;

namespace BiliDesk.Views;

public partial class PlayerWindow
{
    private async void OnSendDanmakuClick(object sender, RoutedEventArgs e) => await SendCurrentDanmakuAsync();
    private async void OnDanmakuInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        await SendCurrentDanmakuAsync();
    }
    private async Task SendCurrentDanmakuAsync()
    {
        if (!BtnSendDanmaku.IsEnabled || _closing) return;
        if (_isLive || _isLocalPlayback) { Svc.Toast.Show("直播和本地文件暂不支持发送视频弹幕"); return; }
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("请先登录后再发送弹幕"); return; }
        var text = DanmakuInput.Text.Trim();
        if (text.Length == 0) { DanmakuInput.Focus(); return; }
        var cid = _currentCid; var bvid = _currentBvid; var generation = _loadGen;
        long progress = 0; try { progress = _mp.Time; } catch { }
        BtnSendDanmaku.IsEnabled = false;
        try
        {
            var result = await Svc.Api.SendDanmakuAsync(cid, bvid, text, progress);
            if (_closing || cid != _currentCid || generation != _loadGen || bvid != _currentBvid) return;
            if (!result.ok) { Svc.Toast.Show(result.err ?? "弹幕发送失败"); return; }
            if (DanmakuInput.Text.Trim() == text) DanmakuInput.Clear();
            if (_danmakuOn) AddDanmaku(text, 1, Math.Max(0, progress) / 1000.0);
            Svc.Toast.Show("弹幕已发送");
        }
        finally { if (!_closing) BtnSendDanmaku.IsEnabled = true; }
    }
}
