using BiliTouch.Services;

/// <summary>
/// 锁死「更新下载只有一个 URL、遇到 504 就直接失败」这个真机问题。
///
/// 实测：GitHub 的 browser_download_url 会整站 504（所有资产、直连/代理都一样），
/// 而 api.github.com/.../releases/assets/{id} + Accept: application/octet-stream
/// 同一时刻仍然 206。所以下载器必须知道备选端点怎么拼。
/// </summary>
internal static class UpdateFallbackTests
{
    public static void Run(Action<bool, string> check)
    {
        var url = UpdateChecker.AssetDownloadUrl(726401809);
        check(url == "https://api.github.com/repos/zhongsanZN/BiliTouch/releases/assets/726401809",
            "asset fallback url points at the official API endpoint: " + url);

        var info = new UpdateInfo { SetupUrl = "https://github.com/zhongsanZN/BiliTouch/releases/download/v0.2.13/x.exe" };
        check(info.SetupAssetId == 0,
            "a plain UpdateInfo without asset id still exposes the property (default 0)");

        var withId = info.WithInstaller("https://github.com/a/b/releases/download/v1/x.exe", 123, null, 42);
        check(withId.SetupAssetId == 42,
            "WithInstaller carries the asset id through to the downloader");
        check(withId.SetupSize == 123 && withId.SetupUrl.EndsWith("x.exe"),
            "WithInstaller keeps the existing fields intact");

        var noId = info.WithInstaller("https://github.com/a/b/releases/download/v1/x.exe", 123, null);
        check(noId.SetupAssetId == 0,
            "omitting the asset id keeps backwards compatibility (default 0)");
    }
}
