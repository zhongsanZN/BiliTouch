using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 动态发布窗口的视图模型(2026-11-04)。
///
/// 状态很薄, 但有三件事必须在 VM 里而不是 code-behind:
///   1. <see cref="CanPublish"/> —— 正文空、图片还在上传、正在发布, 三种情况都不能点;
///   2. 每张图的上传状态 —— 选图立刻上传, 失败可单独重试;
///   3. 发布完成后把新动态 id 带回去, 让上层能立刻插进时间线(不用等轮询)。
///
/// ★ 图片为什么"选完就传"而不是"点发布才传":
///   官方就是这个手感 —— 用户选完图能马上看到"已经传好了", 点发布很快。
///   等到点发布再传的话, 9 张图要等几十秒, 中途超时还得从头来。
/// </summary>
public sealed class DynamicPublishViewModel : ObservableObject
{
    public ObservableCollection<PublishImage> Images { get; } = new();

    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value)) return;
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(CanPublish));
        }
    }

    private string _error = "";
    public string Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }
    public bool HasError => _error.Length > 0;

    private bool _publishing;
    public bool Publishing
    {
        get => _publishing;
        private set
        {
            if (!SetProperty(ref _publishing, value)) return;
            OnPropertyChanged(nameof(CanPublish));
        }
    }

    /// <summary>发布成功后的动态 id(空 = 还没发成功)</summary>
    public string PublishedId { get; private set; } = "";

    /// <summary>头像与昵称从登录态取, 取不到就不显示(不编一个假的)</summary>
    public string UserName { get; } = "";
    public string Face { get; } = "";

    public string CountText => $"{Text.Length} / 2000";

    /// <summary>
    /// 能不能点发布。
    /// ★ 图片还在传时**必须**禁用: 服务端要 img_src, 传一半的图在 ctrl 里是空的,
    ///   发出去就是"只有文字、图片全丢", 而且用户以为发出去了。
    /// </summary>
    public bool CanPublish =>
        !Publishing &&
        (Text.Trim().Length > 0 || Images.Any(i => i.Uploaded)) &&
        Images.All(i => !i.Uploading && !i.Failed);

    public DynamicPublishViewModel(string userName = "", string face = "")
    {
        UserName = userName;
        Face = face;
    }

    /// <summary>加一张本地图片并立刻开始上传。超过 9 张或已存在则忽略。</summary>
    public async Task AddImageAsync(string filePath)
    {
        if (Images.Count >= 9) { Error = "最多 9 张图片"; return; }
        if (Images.Any(i => string.Equals(i.LocalPath, filePath, StringComparison.OrdinalIgnoreCase))) return;

        var item = new PublishImage { LocalPath = filePath, Uploading = true };
        Images.Add(item);
        OnPropertyChanged(nameof(CanPublish));
        await LoadPreviewAsync(item);
        await UploadAsync(item);
    }

    /// <summary>重试一张失败的图片</summary>
    public async Task RetryAsync(PublishImage item)
    {
        if (item.Uploading) return;
        item.Failed = false;
        item.Uploading = true;
        OnPropertyChanged(nameof(CanPublish));
        await UploadAsync(item);
    }

    /// <summary>
    /// 本地预览图。
    /// ★ 为什么不复用 Cover.Source: 那个附加属性走的是 CoverLoader(HTTP 下载 + 磁盘缓存),
    ///   对本地路径会直接失败。这里自己从文件解一份小图并 Freeze, 后台线程做, 不卡 UI。
    /// </summary>
    private static async Task LoadPreviewAsync(PublishImage item)
    {
        try
        {
            if (!File.Exists(item.LocalPath)) return;
            var path = item.LocalPath;
            var image = await Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // 预览格只有 96px, 按 2 倍 DPI 解 192 就够, 别把 8MB 原图整张解进内存
                bitmap.DecodePixelWidth = 192;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return (ImageSource)bitmap;
            });
            item.Preview = image;
        }
        catch { /* 预览失败不影响上传; 格子会显示占位底色 */ }
    }

    private async Task UploadAsync(PublishImage item)
    {
        try
        {
            var result = await Svc.Api.UploadDynamicImageAsync(item.LocalPath);
            if (result.ok && result.image != null)
            {
                item.Upload = result.image;
                item.Uploaded = true;
                item.Failed = false;
            }
            else
            {
                item.Failed = true;
                item.FailReason = result.err ?? "上传失败";
                Error = item.FailReason;
            }
        }
        catch (Exception ex)
        {
            item.Failed = true;
            item.FailReason = ex.Message;
            Error = "上传失败：" + ex.Message;
        }
        finally
        {
            item.Uploading = false;
            OnPropertyChanged(nameof(CanPublish));
        }
    }

    public void Remove(PublishImage item)
    {
        Images.Remove(item);
        OnPropertyChanged(nameof(CanPublish));
    }

    /// <summary>
    /// 发布。成功后清空草稿并记下 id; 失败**保留全部内容**让用户改完再试 ——
    /// 这一点与详情页的评论发送一致, 失败就清空是上一版最容易惹火用户的行为。
    /// </summary>
    public async Task<bool> PublishAsync()
    {
        if (!CanPublish) return false;
        Publishing = true;
        Error = "";
        try
        {
            var uploads = Images.Where(i => i.Uploaded && i.Upload != null)
                                .Select(i => i.Upload!)
                                .ToList();
            var result = await Svc.Api.CreateDynamicAsync(Text.Trim(), uploads);
            if (!result.ok)
            {
                Error = result.err ?? "发布失败，请重试";
                return false;
            }
            PublishedId = result.dynamicId;
            Text = "";
            Images.Clear();
            OnPropertyChanged(nameof(CanPublish));
            return true;
        }
        catch (Exception ex)
        {
            Error = "发布失败：" + ex.Message;
            return false;
        }
        finally { Publishing = false; }
    }
}

/// <summary>发布窗里的一张待发图片(2026-11-04)。</summary>
public sealed class PublishImage : ObservableObject
{
    public string LocalPath { get; set; } = "";

    private ImageSource? _preview;
    /// <summary>本地预览图。null = 还在解(格子显示占位底色)</summary>
    public ImageSource? Preview { get => _preview; set => SetProperty(ref _preview, value); }

    private bool _uploading;
    public bool Uploading { get => _uploading; set => SetProperty(ref _uploading, value); }

    private bool _uploaded;
    public bool Uploaded { get => _uploaded; set => SetProperty(ref _uploaded, value); }

    private bool _failed;
    public bool Failed { get => _failed; set => SetProperty(ref _failed, value); }

    private string _failReason = "";
    public string FailReason { get => _failReason; set => SetProperty(ref _failReason, value); }

    /// <summary>上传成功后的服务端结果(含 img_src / img_url / 宽高)</summary>
    public DynamicImageUpload? Upload { get; set; }
}
