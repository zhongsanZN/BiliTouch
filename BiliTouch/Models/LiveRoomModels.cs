using System.ComponentModel;
using System.Text.Json;

namespace BiliTouch.Models;

public enum LiveChatKind { Danmaku, System, SuperChat }

public sealed class LiveChatEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; init; } = "";
    public long UserId { get; init; }
    public string User { get; init; } = "";
    public string Text { get; init; } = "";
    public string Medal { get; init; } = "";
    public string Badge { get; init; } = "";
    public uint Color { get; init; } = 0xFFFFFF;
    public LiveChatKind Kind { get; init; }
    public int Price { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset ExpiresAt { get; init; }
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string Identity => string.Join(" · ", new[] { Badge, Medal }.Where(s => s.Length > 0));
    public string PriceText => Price > 0 ? $"¥{Price}" : "SC";
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow;
    public string RemainingText => IsExpired ? "已结束" : TimeSpan.FromSeconds(Math.Ceiling((ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)).ToString(ExpiresAt - DateTimeOffset.UtcNow >= TimeSpan.FromHours(1) ? @"h\:mm\:ss" : @"mm\:ss");
    public void RefreshClock() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingText)));

    public static LiveChatEntry ParseSuperChat(JsonElement data)
    {
        var info = data.TryGetProperty("user_info", out var user) ? user : data;
        long end = LiveJson.Number(data, "end_time");
        if (end <= 0) end = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Clamp(LiveJson.Number(data, "time"), 1, 7200);
        return new LiveChatEntry
        {
            Kind = LiveChatKind.SuperChat, Id = LiveJson.Text(data, "id"), UserId = LiveJson.Number(data, "uid"),
            User = LiveJson.Text(info, "uname"), Text = LiveJson.Text(data, "message"),
            Price = (int)Math.Clamp(LiveJson.Number(data, "price"), 0, 1000000),
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(end, 0, 253402300799))
        };
    }
}

public sealed class LiveRoomDetails
{
    public long RoomId { get; init; }
    public long UserId { get; init; }
    public string Title { get; init; } = "";
    public string Anchor { get; init; } = "";
    public string Face { get; init; } = "";
    public string Area { get; init; } = "";
    public string Announcement { get; init; } = "";
    public string Watched { get; init; } = "";
    public int LiveStatus { get; init; }
    public bool Followed { get; init; }
    public List<LiveChatEntry> SuperChats { get; init; } = new();
}

internal static class LiveJson
{
    public static string Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
        ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "" : "";
    public static long Number(JsonElement e, string key) => long.TryParse(Text(e, key), out var n) ? n : 0;
    public static JsonElement Object(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
}
