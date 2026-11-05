using BiliTouch.Models;
using BiliTouch.Helpers;

namespace BiliTouch.Services;

public partial class ApiClient
{
    public async Task<(bool ok, string? error, LiveRoomDetails? room)> GetLiveRoomDetailsAsync(long roomId)
    {
        if (roomId <= 0) return (false, "直播间号无效", null);
        await EnsureBuvidAsync();
        var query = new Dictionary<string, string> { ["room_id"] = roomId.ToString() };
        var (code, error, data) = await GetJsonAsync("https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom", query,
            sign: true, allowRetry: false, referer: $"https://live.bilibili.com/{roomId}", riskRetries: 0);
        if (code == 0 && data.HasValue)
        {
            var root = data.Value; var room = LiveJson.Object(root, "room_info");
            var anchor = LiveJson.Object(LiveJson.Object(root, "anchor_info"), "base_info");
            var sc = new List<LiveChatEntry>();
            var scInfo = LiveJson.Object(root, "super_chat_info");
            if (scInfo.ValueKind == System.Text.Json.JsonValueKind.Object && scInfo.TryGetProperty("message_list", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var item in list.EnumerateArray()) { try { sc.Add(LiveChatEntry.ParseSuperChat(item)); } catch { } }
            return (true, null, new LiveRoomDetails
            {
                RoomId = LiveJson.Number(room, "room_id") is > 0 and var real ? real : roomId,
                UserId = LiveJson.Number(room, "uid"), Title = LiveJson.Text(room, "title"), Anchor = LiveJson.Text(anchor, "uname"),
                Face = LiveJson.Text(anchor, "face"), Area = LiveJson.Text(room, "area_name"), LiveStatus = (int)LiveJson.Number(room, "live_status"),
                Announcement = LivePlainText(LiveJson.Text(LiveJson.Object(root, "room_news"), "content")),
                Watched = LiveJson.Text(LiveJson.Object(root, "watched_show"), "text_large"),
                Followed = LiveJson.Number(LiveJson.Object(root, "relation_info"), "attention") == 1, SuperChats = sc
            });
        }
        // The old public room endpoint often remains readable when the richer web endpoint is blocked.
        var fallback = await GetJsonAsync("https://api.live.bilibili.com/room/v1/Room/get_info", query, allowRetry: false,
            referer: $"https://live.bilibili.com/{roomId}", riskRetries: 0);
        if (fallback.code != 0 || !fallback.data.HasValue) return (false, error ?? fallback.msg ?? "直播间信息暂时无法获取", null);
        var value = fallback.data.Value; var uid = LiveJson.Number(value, "uid");
        var user = await GetJsonAsync("https://api.live.bilibili.com/live_user/v1/Master/info", new() { ["uid"] = uid.ToString() },
            allowRetry: false, referer: $"https://live.bilibili.com/{roomId}", riskRetries: 0);
        var userInfo = user.data.HasValue ? LiveJson.Object(user.data.Value, "info") : default;
        return (true, null, new LiveRoomDetails
        {
            RoomId = LiveJson.Number(value, "room_id") is > 0 and var fallbackRoom ? fallbackRoom : roomId, UserId = uid,
            Title = LiveJson.Text(value, "title"), Anchor = LiveJson.Text(userInfo, "uname"), Face = LiveJson.Text(userInfo, "face"),
            Area = LiveJson.Text(value, "area_name"), LiveStatus = (int)LiveJson.Number(value, "live_status"), Announcement = LivePlainText(LiveJson.Text(value, "description"))
        });
    }

    private static string LivePlainText(string value) => System.Net.WebUtility.HtmlDecode(
        System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100))).Trim();

    public async Task<List<LiveChatEntry>> GetRecentLiveMessagesAsync(long roomId)
    {
        var result = new List<LiveChatEntry>();
        var response = await GetJsonAsync("https://api.live.bilibili.com/xlive/web-room/v1/dM/gethistory", new() { ["roomid"] = roomId.ToString() },
            allowRetry: false, referer: $"https://live.bilibili.com/{roomId}", riskRetries: 0);
        if (response.code != 0 || !response.data.HasValue || !response.data.Value.TryGetProperty("room", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
        foreach (var item in list.EnumerateArray()) result.Add(new LiveChatEntry
        { Id = LiveJson.Text(item, "id"), User = LiveJson.Text(item, "nickname"), UserId = LiveJson.Number(item, "uid"), Text = LiveJson.Text(item, "text") });
        return result;
    }

    public async Task<(bool ok, string? err)> SendLiveMessageAsync(long roomId, string text)
    {
        text = text.Trim();
        if (!SessionManager.Instance.HasLogin) return (false, "请先登录后再发言");
        if (roomId <= 0 || text.Length == 0 || text.Length > 100) return (false, "请输入 1–100 字的弹幕");
        var result = await PostFormAsync("https://api.live.bilibili.com/msg/send", new()
        {
            ["roomid"] = roomId.ToString(), ["msg"] = text, ["color"] = "16777215", ["fontsize"] = "25", ["mode"] = "1",
            ["rnd"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ["bubble"] = "0", ["csrf_token"] = SessionManager.Instance.Current.BiliJct ?? ""
        }, $"https://live.bilibili.com/{roomId}");
        return result.ok ? result : (false, FriendlyRiskMessage(result.err));
    }
}
