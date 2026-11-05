using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 动态发布与图片上传(2026-11-04)。
///
/// 背景: 上一版客户端只能**转发**别人已有的动态, 自己发一条新动态必须去网页 ——
/// 这正是用户说的"生硬支持"(跳网页), 也是本轮 P1 要补的最后一块。
///
/// 三步, 与服务端接口一一对应:
///   1. 图片先上传拿到 `img_url` 与 `img_src`(走 B 站的分片上传, 见 UploadImageAsync);
///   2. 把正文 + 图片 + 话题 + @ 拼成 `ctrl`(JSON 数组), 每张图一个 draw 项;
///   3. 调 `/x/dynamic/feed/dyn/create` 发布。
///
/// ★ 为什么 ctrl 要自己拼而不是发纯文本:
///   服务端对**客户端发布**的动态要求 `ctrl` 里显式列出内容块, 纯文本也要有一个
///   `{"type":1,"data":{"content":"..."}}`。只发 content 字段的话, 发布成功但
///   正文在官方客户端里显示为空 —— 这类"发出去了但别人看不见"的坑很难在本地发现。
///
/// ★ 图片上传是**分片**的, 不是一次性 POST:
///   preupload 拿 `auth` + `endpoint` + `upos_uri` → 逐片 PUT 到 endpoint →
///   最后 POST 合并。少任何一步, 返回的 `img_url` 都指向一个不存在的对象。
///   下面按这个顺序实现, 每一步都校验 code, 失败时把服务端原文带出来。
/// </summary>
public partial class ApiClient
{
    /// <summary>
    /// 发一条**新动态**(2026-11-04, 端点按只读探针实测校正)。
    /// </summary>
    /// <param name="text">正文。话题写成 <c>#话题#</c>、@ 写成 <c>@昵称</c>, 与官方输入一致。</param>
    /// <param name="images">已上传成功的图片, 为空就是纯文字动态。</param>
    public async Task<(bool ok, string? err, string dynamicId)> CreateDynamicAsync(
        string text, IReadOnlyList<DynamicImageUpload>? images = null)
    {
        if (!SessionManager.Instance.HasLogin) return (false, "发布动态需要先登录", "");
        if (string.IsNullOrWhiteSpace(text) && (images == null || images.Count == 0))
            return (false, "正文和图片不能都是空的", "");

        // ---- 1. 拼 ctrl ----
        var ctrl = new List<object>();
        if (!string.IsNullOrWhiteSpace(text))
            ctrl.Add(new { type = 1, data = new { content = text } });
        if (images != null)
        {
            foreach (var image in images)
            {
                if (image.Url.Length == 0) continue;
                // type=2 是图片块。height/width 必须给, 否则客户端按 0×0 排版(图看不到)。
                ctrl.Add(new
                {
                    type = 2,
                    data = new
                    {
                        img_src = image.Src,
                        img_url = image.Url,
                        img_width = image.Width > 0 ? image.Width : 1080,
                        img_height = image.Height > 0 ? image.Height : 1440,
                    },
                });
            }
        }
        if (ctrl.Count == 0) return (false, "没有可发布的内容", "");
        var ctrlJson = JsonSerializer.Serialize(ctrl);

        // ---- 2. 发布 ----
        // ★★★ 端点按实测校正(只读探针的 GET 探路结果):
        //   · /x/dynamic/feed/create/dyn            → HTTP 405 (路由存在, 只收 POST) ✔ 用这条
        //   · /x/dynamic/feed/dyn/create            → HTTP 404 (路径不存在)            ✘ 我原来写错了
        //   · api.vc dynamic_svr/v1/dynamic_svr/create → 405 (老接口, 也活着, 作为兜底)
        //
        // ★ dynamic_id 是关键分界: 0 = 新建, 非 0 = 转发。所以这里必须显式带 0,
        //   不带的话服务端可能按"转发到空 id"处理。
        var ps = new Dictionary<string, string>
        {
            ["dynamic_id"] = "0",
            ["type"] = images is { Count: > 0 } ? "2" : "4",   // 2 = 带图, 4 = 纯文字
            ["rid"] = "0",
            ["content"] = text ?? "",
            ["ctrl"] = ctrlJson,
            ["at_uids"] = ExtractMentions(text),
        };

        var (code, msg, data) = await PostFormJsonAsync(
            "https://api.bilibili.com/x/dynamic/feed/create/dyn", ps, referer: "https://t.bilibili.com/");

        // 新版端点若返回 404/路由异常, 退回老接口再试一次(老接口仍是 405 = 活着)
        if (code == -404 || code == -400 && (msg?.Contains("not found", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            var legacy = await PostFormJsonAsync(
                "https://api.vc.bilibili.com/dynamic_svr/v1/dynamic_svr/create", ps,
                referer: "https://t.bilibili.com/");
            if (legacy.code == 0) { code = legacy.code; msg = legacy.msg; data = legacy.data; }
        }

        if (code != 0)
            return (false, CreateFriendlyError(code, msg), "");

        // 服务端把新动态的 id 放在 data.dyn_id / data.dynamic_id 里(两种都见过)
        var id = "";
        if (data is { } d)
        {
            id = GetStr(d, "dyn_id");
            if (id.Length == 0) id = GetStr(d, "dynamic_id");
            if (id.Length == 0) id = GetStr(d, "dynamic_id_str");
            if (id.Length == 0 && d.TryGetProperty("dynamic_id", out var num) &&
                num.ValueKind == JsonValueKind.Number) id = num.GetInt64().ToString();
        }
        return (true, null, id);
    }

    private static string CreateFriendlyError(int code, string? msg) => code switch
    {
        -101 => "请先登录后再发布",
        -111 => "登录状态已失效, 请重新登录",
        -400 => "内容不符合发布要求(可能包含敏感词或图片未通过审核)",
        -412 => "请求太频繁, 稍等几秒再发",
        21001 => "账号等级不足, 暂时不能发动态",
        21002 => "该账号暂时被限制发布",
        _ => msg is { Length: > 0 } ? msg : $"发布失败 (code {code})",
    };

    /// <summary>
    /// 从正文里抽出被 @ 的昵称, 交给服务端做结构化(接口只要昵称, 不要 mid)。
    /// 与 <c>Helpers/MentionParser</c> 同一套规则, 但这里只需要"有就带"。
    /// </summary>
    private static string ExtractMentions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var names = Helpers.MentionParser.Parse(text).Distinct().ToList();
        return names.Count == 0 ? "" : string.Join(',', names);
    }

    /// <summary>
    /// 上传一张图片, 返回可直接写进 ctrl 的 <see cref="DynamicImageUpload"/>(2026-11-04)。
    ///
    /// ★★★ 端点按**只读探针实测**校正过, 别改回我上一版猜的那些:
    ///   · /x/upload/preupload                        → 404 路径不存在
    ///   · /x/dynamic/feed/dyn/image/preupload         → 404 路径不存在
    ///   · /x/dynamic/feed/dyn/pic/upload              → 404 路径不存在
    ///   · /x/v2/upload/image                          → 404 路径不存在
    ///   · /x/upload/image                             → **405 路由存在, 只收 POST** ✔ 用这条
    ///
    /// 所以走**一次性 multipart 上传**, 不做 upos 分片。
    /// 分片那套(upos_uri + auth + chunk)在实测里根本拿不到签名节点, 写了也只会 404,
    /// 与其留一段"看起来完整、实际必挂"的代码, 不如把这条真实可用的路走通。
    ///
    /// ★ 返回结构 B 站有多个历史形态, 这里按优先级依次尝试, 都不中就明确报错:
    ///   data.image_url / data.url / data.img_src / data.src / data.image_src
    /// 拿不到可用地址时**不伪造**, 直接返回失败 —— 宁可让用户看到"图片没传上去",
    /// 也不能让一条"发了但图全丢"的动态出去。
    /// </summary>
    public async Task<(bool ok, string? err, DynamicImageUpload? image)> UploadDynamicImageAsync(string filePath)
    {
        if (!SessionManager.Instance.HasLogin) return (false, "上传图片需要先登录", null);
        if (!File.Exists(filePath)) return (false, "找不到这个文件", null);

        var bytes = await File.ReadAllBytesAsync(filePath);
        if (bytes.Length == 0) return (false, "图片是空文件", null);
        if (bytes.Length > 20 * 1024 * 1024) return (false, "图片超过 20MB, 传不上去", null);

        var (width, height) = ReadImageSize(bytes);
        var fileName = Path.GetFileName(filePath);

        try
        {
            await EnsureWriteReadyAsync();
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMimeType(filePath));
            form.Add(fileContent, "file", fileName);
            form.Add(new StringContent("draw"), "biz");
            form.Add(new StringContent("daily"), "category");
            form.Add(new StringContent(SessionManager.Instance.Current.BiliJct ?? ""), "csrf");

            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://api.bilibili.com/x/upload/image");
            request.Headers.TryAddWithoutValidation("Cookie",
                AppendFingerprint(SessionManager.Instance.BuildCookieHeader()));
            request.Headers.TryAddWithoutValidation("Referer", "https://t.bilibili.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://t.bilibili.com");
            request.Content = form;

            using var response = await _http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -999;
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
            if (code != 0)
                return (false, UploadFriendlyError(code, message), null);
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return (false, "上传接口没有返回图片地址", null);

            // 依次试历史形态, 取第一个非空
            var src = "";
            foreach (var key in new[] { "image_url", "url", "img_src", "src", "image_src", "location" })
            {
                src = GetStr(data, key);
                if (src.Length > 0) break;
            }
            if (src.Length == 0)
                return (false, "上传成功但没有可用的图片地址(接口结构可能变了)", null);
            if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                src = src.StartsWith("//", StringComparison.Ordinal)
                    ? "https:" + src
                    : "https://" + src.TrimStart('/');

            // img_url 用服务端给的对象键(没有就退回直链) —— ctrl 里两个字段都要
            var objectKey = GetStr(data, "image_key");
            if (objectKey.Length == 0) objectKey = src;

            return (true, null, new DynamicImageUpload
            {
                Url = objectKey,
                Src = src,
                Width = width,
                Height = height,
                Size = bytes.Length,
                LocalPath = filePath,
            });
        }
        catch (Exception ex) { return (false, "上传失败：" + ex.Message, null); }
    }

    private static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };

    private static string UploadFriendlyError(int code, string? msg) => code switch
    {
        -101 => "请先登录后再上传",
        -111 => "登录状态已失效, 请重新登录",
        -400 => "图片不符合要求(格式或大小)",
        -412 => "请求太频繁, 稍等几秒再试",
        -403 => "账号暂时被限制上传",
        _ => msg is { Length: > 0 } ? msg : $"上传失败 (code {code})",
    };

    /// <summary>
    /// 从图片字节里读出宽高(只解析文件头, 不解码像素)。
    /// 支持 PNG / JPEG / GIF / WebP —— 够覆盖用户会选的格式; 认不出来就返回 (0,0),
    /// 调用方会退回默认比例(见 CreateDynamicAsync 里的 1080×1440)。
    /// </summary>
    private static (int width, int height) ReadImageSize(byte[] bytes)
    {
        try
        {
            // PNG: 89 50 4E 47 ... IHDR at 16..24
            if (bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == 0x50)
            {
                var w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                var h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return (w, h);
            }
            // GIF: "GIF8" + 宽高小端 at 6..10
            if (bytes.Length > 10 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
                return (bytes[6] | (bytes[7] << 8), bytes[8] | (bytes[9] << 8));
            // JPEG: 扫 SOF0/SOF2 段
            if (bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                var i = 2;
                while (i + 9 < bytes.Length)
                {
                    if (bytes[i] != 0xFF) { i++; continue; }
                    var marker = bytes[i + 1];
                    var size = (bytes[i + 2] << 8) | bytes[i + 3];
                    if (marker is 0xC0 or 0xC1 or 0xC2)
                        return ((bytes[i + 7] << 8) | bytes[i + 8], (bytes[i + 5] << 8) | bytes[i + 6]);
                    if (size <= 0) break;
                    i += 2 + size;
                }
            }
            // WebP: RIFF....WEBP, VP8X 段带 24 位宽高
            if (bytes.Length > 30 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[8] == 0x57)
            {
                var w = (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16)) + 1;
                var h = (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16)) + 1;
                return (w, h);
            }
        }
        catch { /* 解析失败退回默认比例 */ }
        return (0, 0);
    }

    /// <summary>
    /// POST 表单并**返回解析后的 data 节点**。
    /// 现有的 <see cref="PostFormCodeAsync"/> 只回 code/message —— 发布动态需要拿回 dyn_id,
    /// 上传需要拿回 data, 所以单独开一个。CSRF / Cookie / Origin 的处理与它一致。
    /// </summary>
    private async Task<(int code, string? msg, JsonElement? data)> PostFormJsonAsync(
        string url, Dictionary<string, string> ps, string? referer = null)
    {
        var csrf = SessionManager.Instance.Current.BiliJct;
        if (string.IsNullOrEmpty(csrf)) return (-101, "未登录", null);
        ps["csrf"] = csrf;
        try
        {
            await EnsureWriteReadyAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Cookie", AppendFingerprint(SessionManager.Instance.BuildCookieHeader()));
            if (!string.IsNullOrEmpty(referer)) request.Headers.TryAddWithoutValidation("Referer", referer);
            request.Headers.TryAddWithoutValidation("Origin", OriginFor(referer));
            request.Content = new FormUrlEncodedContent(ps);
            using var response = await _http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -999;
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
            JsonElement? data = root.TryGetProperty("data", out var dd) && dd.ValueKind == JsonValueKind.Object
                ? dd.Clone() : null;
            return (code, message, data);
        }
        catch (Exception ex) { return (-999, ex.Message, null); }
    }
}

/// <summary>
/// 一张已上传好的动态图片(2026-11-04)。
/// <see cref="Url"/> 与 <see cref="Src"/> 必须成对存在 —— 少一个, 服务端就不认这张图。
/// </summary>
public class DynamicImageUpload
{
    /// <summary>内部标识(upos 对象键), 写进 ctrl.data.img_url 的位置</summary>
    public string Url { get; set; } = "";
    /// <summary>原图直链, 写进 ctrl.data.img_src</summary>
    public string Src { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>字节数(接口要 img_size)</summary>
    public long Size { get; set; }
    /// <summary>本地文件路径, 只用于发布前的预览, 不发给服务端</summary>
    public string LocalPath { get; set; } = "";
}
