using System.Text.Json;

namespace BiliTouch.Services;

public partial class ApiClient
{
    public async Task<MessageUnreadSnapshot> GetMessageUnreadAsync()
    {
        if (!Svc.Session.HasLogin) return new(0, 0, 0, 0);
        var query = new Dictionary<string, string> { ["build"] = "0", ["mobi_app"] = "web" };
        var notices = GetJsonAsync("https://api.bilibili.com/x/msgfeed/unread", query, referer: "https://message.bilibili.com/");
        var messages = GetJsonAsync("https://api.vc.bilibili.com/session_svr/v1/session_svr/single_unread", query, referer: MsgReferer);
        await Task.WhenAll(notices, messages);
        var n = await notices; var m = await messages;
        return ParseMessageUnread(n.code == 0 ? n.data : null, m.code == 0 ? m.data : null);
    }

    internal static MessageUnreadSnapshot ParseMessageUnread(JsonElement? notices, JsonElement? messages)
    {
        static int? Count(JsonElement? element, string key)
        {
            if (element is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value) ||
                !long.TryParse(value.ToString(), out var count)) return null;
            return (int)Math.Clamp(count, 0, int.MaxValue);
        }
        var follow = Count(messages, "follow_unread"); var unfollow = Count(messages, "unfollow_unread");
        int? privateCount = follow.HasValue || unfollow.HasValue ? (int)Math.Min(int.MaxValue, (long)(follow ?? 0) + (unfollow ?? 0)) : null;
        return new(privateCount, Count(notices, "reply"), Count(notices, "at"), Count(notices, "like"));
    }
}
