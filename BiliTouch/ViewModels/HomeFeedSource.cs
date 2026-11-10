using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.ViewModels;

internal interface IHomeFeedSource
{
    Task<(bool ok, string? err, List<VideoItem>? items)> Recommend(bool reset);
    Task<(bool ok, string? err, List<VideoItem>? items)> Popular(int page);
    Task<(bool ok, string? err, List<VideoItem>? items)> Ranking();
    Task<(bool ok, string? err, List<VideoItem>? items)> Live(int page);
    Task<(bool ok, string? err, List<BangumiItem>? items, bool hasNext)> Bangumi(int page);
}

internal sealed class ApiHomeFeedSource : IHomeFeedSource
{
    public Task<(bool ok, string? err, List<VideoItem>? items)> Recommend(bool reset) => Svc.Api.GetRecommendAsync(reset);
    public Task<(bool ok, string? err, List<VideoItem>? items)> Popular(int page) => Svc.Api.GetPopularAsync(page);
    public Task<(bool ok, string? err, List<VideoItem>? items)> Ranking() => Svc.Api.GetRankingAsync();
    public Task<(bool ok, string? err, List<VideoItem>? items)> Live(int page) => Svc.Api.GetLiveListAsync(page);
    public Task<(bool ok, string? err, List<BangumiItem>? items, bool hasNext)> Bangumi(int page) => Svc.Api.GetBangumiRecommendAsync(page);
}
