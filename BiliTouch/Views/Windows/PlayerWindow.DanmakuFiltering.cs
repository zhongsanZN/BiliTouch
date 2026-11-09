using BiliTouch.Helpers;

namespace BiliTouch.Views;

public partial class PlayerWindow
{
    private sealed record DanmakuFilterSnapshot(bool Smart, string Keywords, bool Scroll, bool Fixed, bool Colorful, bool Advanced);
    private static DanmakuFilterSnapshot CaptureDanmakuFilter()
    {
        var s = Svc.Settings;
        return new(s.DanmakuSmartFilter, s.DanmakuBlockKeywords, s.DanmakuFilterScroll,
            s.DanmakuFilterFixed, s.DanmakuFilterColorful, s.DanmakuFilterAdvanced);
    }

    private CancellationTokenSource? _danmakuFilterCancellation;
    private DanmakuFilterSnapshot? _requestedDanmakuFilter;
    private int _danmakuFilterRevision;

    private void CancelDanmakuFiltering()
    {
        ++_danmakuFilterRevision;
        _requestedDanmakuFilter = null;
        var previous = _danmakuFilterCancellation;
        _danmakuFilterCancellation = null;
        previous?.Cancel();
    }

    /// <summary>Snapshot on UI; compile rules/filter off UI; publish only the latest media/settings revision.</summary>
    private async Task RefilterDanmakuAsync(bool force = false)
    {
        if (_closing) return;
        var snapshot = CaptureDanmakuFilter();
        // Opacity, speed, font and area updates do not change filter membership.
        if (!force && snapshot == _requestedDanmakuFilter) return;
        CancelDanmakuFiltering();
        _requestedDanmakuFilter = snapshot;
        if (_danmakuRaw.Count == 0) return;

        var cancellation = _danmakuFilterCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var revision = _danmakuFilterRevision;
        var generation = _loadGen;
        var cid = _currentCid;
        var raw = _danmakuRaw.ToArray();
        try
        {
            var visible = await Task.Run(() => FilterDanmakuSnapshot(raw, snapshot, token).ToArray(), token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing || token.IsCancellationRequested || revision != _danmakuFilterRevision ||
                    generation != _loadGen || cid != _currentCid) return;
                _danmaku.Clear();
                _danmaku.AddRange(visible);
                _indexInLib = LowerBound(_danmaku, Math.Max(0, _mp?.Time / 1000d ?? 0));
                ResetLanes();
                WriteLibVLCDebug($"[Danmaku] cid={cid} fetched={raw.Length} visible={visible.Length}\n");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { WriteLibVLCDebug($"[Danmaku] filter failed: {ex.GetType().Name}\n"); }
        finally
        {
            if (ReferenceEquals(_danmakuFilterCancellation, cancellation)) _danmakuFilterCancellation = null;
            cancellation.Dispose();
        }
    }
}
