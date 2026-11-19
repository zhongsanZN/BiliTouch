using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        check(UpdateChecker.IsNewerVersion("v0.3.2", "0.3.2-test"),
            "test prerelease detects the official release of the same version");
        check(UpdateChecker.IsNewerVersion("v0.3.2-fix1", "0.3.2-test"),
            "test prerelease detects a hotfix release of the same version");
        check(UpdateChecker.IsNewerVersion("v0.3.3", "0.3.2-test"),
            "test prerelease detects a higher version");
        check(!UpdateChecker.IsNewerVersion("v0.3.2-test", "0.3.2-test"),
            "identical test version does not trigger a duplicate update");
        check(!UpdateChecker.IsNewerVersion("v0.3.2-test", "0.3.2"),
            "official release does not downgrade to a prerelease tag");
        check(UpdateChecker.IsNewerVersion("v0.3.2-test2", "0.3.2-test1"),
            "higher prerelease suffix is recognized as newer");

        // 双架构（x64 + ARM64）LibVLC 目录与更新资产路由
        check(VlcCore.LibVlcRidFor(Architecture.X64) == "win-x64",
            "VlcCore maps x64 process to libvlc/win-x64");
        check(VlcCore.LibVlcRidFor(Architecture.Arm64) == "win-arm64",
            "VlcCore maps ARM64 process to libvlc/win-arm64");

        var v036 = UpdateChecker.ParseVersion("v0.3.6")!;
        check(UpdateChecker.SetupFileNameFor(v036, Architecture.X64) == "BiliTouch-0.3.6-setup.exe",
            "x64 installer filename preserves legacy BiliTouch-<ver>-setup.exe format");
        check(UpdateChecker.SetupFileNameFor(v036, Architecture.Arm64) == "BiliTouch-0.3.6-setup-arm64.exe",
            "ARM64 installer filename uses BiliTouch-<ver>-setup-arm64.exe");

        // 验证老版 x64 客户端正则绝不会把 ARM64 安装包误匹配为 x64
        var legacyX64Regex = new Regex(@"^BiliTouch-(?<version>\d+\.\d+\.\d+(?:[-.][A-Za-z0-9.]+)?)-setup\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        check(!legacyX64Regex.IsMatch("BiliTouch-0.3.6-setup-arm64.exe"),
            "legacy v0.3.5 x64 regex never accidentally matches BiliTouch-<ver>-setup-arm64.exe");
        check(legacyX64Regex.IsMatch("BiliTouch-0.3.6-setup.exe"),
            "legacy v0.3.5 x64 regex matches BiliTouch-<ver>-setup.exe cleanly");

        using var dualRelease = JsonDocument.Parse("""
            {"assets":[
             {"name":"BiliTouch-0.3.6-setup-arm64.exe","browser_download_url":"https://example.invalid/arm64","size":21,"id":101},
             {"name":"BiliTouch-0.3.6-setup.exe","browser_download_url":"https://example.invalid/x64","size":20,"id":100}]}
            """);
        var x64Asset = UpdateChecker.FindInstallerAsset(dualRelease.RootElement, v036, Architecture.X64);
        var armAsset = UpdateChecker.FindInstallerAsset(dualRelease.RootElement, v036, Architecture.Arm64);
        check(x64Asset != null && x64Asset.Url == "https://example.invalid/x64" && x64Asset.Id == 100,
            "x64 client selects BiliTouch-<ver>-setup.exe even when arm64 asset appears first");
        check(armAsset != null && armAsset.Url == "https://example.invalid/arm64" && armAsset.Id == 101,
            "ARM64 client selects BiliTouch-<ver>-setup-arm64.exe");
    }
}
