using System.Threading;
using LibVLCSharp.Shared;

namespace BiliTouch.Views;

public partial class PlayerWindow
{
    private readonly SemaphoreSlim _nativeInputGate = new(1, 1);
    private int _nativeInputVersion;

    /// <summary>Keep native Stop off the dispatcher and serialize its following commit.</summary>
    private async Task ReplaceNativeInputAsync(Action? commit)
    {
        var version = ++_nativeInputVersion;
        var generation = _loadGen;
        var player = _mp;
        await _nativeInputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool Current() => ReferenceEquals(player, _mp) &&
                (commit == null || (!_closing && version == _nativeInputVersion && generation == _loadGen));
            if (!Current()) return;
            await Task.Run(() => { try { player.Stop(); } catch { } }).ConfigureAwait(false);
            if (commit != null)
                await Dispatcher.InvokeAsync(() => { if (Current()) commit(); });
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_closing && version == _nativeInputVersion) ShowFail("切换播放失败：" + ex.Message);
            });
        }
        finally { _nativeInputGate.Release(); }
    }
}
