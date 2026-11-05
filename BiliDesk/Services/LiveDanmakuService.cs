using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace BiliDesk.Services;

/// <summary>
/// 直播间弹幕(2026-11-04)。
///
/// 与点播弹幕**完全是两套东西**: 点播是 HTTP 拉一个 XML 按时间轴播; 直播是 WebSocket 长连接,
/// 服务端推一条解一条。这里实现官方 web 端那套协议:
///
///   1. `getDanmuInfo`(需 WBI 签名)拿 token + 服务器列表;
///   2. 连 wss://host:port/sub, 发认证包(op=7);
///   3. 服务端回 op=8 表示认证成功, 之后推 op=5 的广播包(弹幕/礼物/上舰/进场…);
///   4. 每 30s 发一个心跳包(op=2), 服务端回 op=3;
///   5. 断线要重连(指数退避), token 过期要重新取。
///
/// ★ 包格式: 16 字节头 + body
///     4B 总长度(含头) | 2B 头长度(固定 16) | 2B 协议版本 | 4B 操作码 | 4B 序号
///   协议版本: 0=明文JSON, 1=整数(人气值), 2=zlib 压缩(里面是多个完整包), 3=brotli
/// ★ zlib 那层最容易漏: 压缩包里是**多个**完整的包, 要循环解, 不是解一个。
/// </summary>
public sealed class LiveDanmakuService : IDisposable
{
    /// <summary>一条直播弹幕(只保留界面要显示的字段)</summary>
    public sealed class Message
    {
        public string User { get; init; } = "";
        public string Text { get; init; } = "";
        public int MedalLevel { get; init; }
        public string MedalName { get; init; } = "";
        /// <summary>房管/舰长等身份标记(空 = 普通观众)</summary>
        public string Badge { get; init; } = "";
        /// <summary>true = 这是"XXX 进入直播间"这类系统消息</summary>
        public bool IsSystem { get; init; }
        /// <summary>礼物/上舰等高亮消息</summary>
        public bool IsHighlight { get; init; }
    }

    private const int OpHeartbeat = 2;
    private const int OpHeartbeatReply = 3;
    private const int OpMessage = 5;
    private const int OpAuth = 7;
    private const int OpAuthReply = 8;
    private const int HeaderLen = 16;
    private const int HeartbeatMs = 30_000;

    private readonly ApiClient _api;
    private readonly Dispatcher _dispatcher;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private long _roomId;
    private volatile bool _running;

    /// <summary>收到一条可显示的弹幕/系统消息(在 UI 线程上触发)</summary>
    public event Action<Message>? Received;
    /// <summary>连接状态变化(在 UI 线程上触发)</summary>
    public event Action<bool>? ConnectionChanged;

    public LiveDanmakuService(ApiClient api, Dispatcher dispatcher)
    {
        _api = api;
        _dispatcher = dispatcher;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>开始接收某个直播间的弹幕。重复调用会先断开旧的。</summary>
    public void Start(long roomId)
    {
        if (roomId <= 0) return;
        Stop();
        _roomId = roomId;
        _running = true;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(roomId, _cts.Token));
    }

    public void Stop()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _socket?.Abort(); } catch { }
        try { _socket?.Dispose(); } catch { }
        _socket = null;
        _cts?.Dispose();
        _cts = null;
        RaiseConnection(false);
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------ 连接主循环

    private async Task RunAsync(long roomId, CancellationToken ct)
    {
        var backoff = 1000;
        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                var info = await _api.GetLiveDanmakuInfoAsync(roomId);
                if (info.url == null)
                {
                    await Task.Delay(backoff, ct);
                    backoff = Math.Min(backoff * 2, 15_000);
                    continue;
                }

                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0");
                ws.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
                _socket = ws;
                await ws.ConnectAsync(new Uri(info.url), ct);

                // 认证包
                var authBody = BuildAuthBody(roomId, info.uid, info.token);
                await SendAsync(ws, OpAuth, authBody, ct);
                RaiseConnection(true);
                backoff = 1000;

                // 心跳 + 收包并行
                using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var hb = HeartbeatLoopAsync(ws, hbCts.Token);
                await ReceiveLoopAsync(ws, ct);
                hbCts.Cancel();
                try { await hb; } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // 断线: 退避后重连
            }
            finally
            {
                RaiseConnection(false);
                try { _socket?.Dispose(); } catch { }
                _socket = null;
            }

            if (ct.IsCancellationRequested || !_running) break;
            try { await Task.Delay(backoff, ct); } catch { break; }
            backoff = Math.Min(backoff * 2, 15_000);
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendAsync(ws, OpHeartbeat, Array.Empty<byte>(), ct);
            }
            catch { break; }
            try { await Task.Delay(HeartbeatMs, ct); } catch { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            if (ms.Length >= HeaderLen) ParsePackets(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
    }

    // ------------------------------------------------------------ 包解析

    /// <summary>一个 WebSocket 帧里可能是**多个**包(压缩包里更是), 循环解完。</summary>
    internal void ParsePackets(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset + HeaderLen <= data.Length)
        {
            var packetLen = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
            var headLen = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset + 4, 2));
            var version = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset + 6, 2));
            var op = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 8, 4));

            if (packetLen <= 0 || headLen < HeaderLen || offset + packetLen > data.Length) break;
            var body = data.Slice(offset + headLen, packetLen - headLen);

            switch (version)
            {
                case 0:
                case 1:
                    HandlePacket(op, body);
                    break;
                case 2:
                    // zlib: 解压后是多个完整包, 递归解
                    try
                    {
                        using var input = new MemoryStream(body.ToArray());
                        using var z = new ZLibStream(input, CompressionMode.Decompress);
                        using var output = new MemoryStream();
                        z.CopyTo(output);
                        ParsePackets(output.GetBuffer().AsSpan(0, (int)output.Length));
                    }
                    catch { }
                    break;
                case 3:
                    // brotli: 本项目不引第三方库, 遇到就跳过(官方 web 端默认给 zlib)
                    break;
            }

            offset += packetLen;
        }
    }

    internal void HandlePacket(int op, ReadOnlySpan<byte> body)
    {
        if (op == OpHeartbeatReply)
        {
            // 人气值(版本 1 时 body 是 4 字节整数) —— 我们不显示, 忽略
            return;
        }
        if (op != OpMessage || body.Length == 0) return;

        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";

            // DANMU_MSG: 普通弹幕
            if (cmd.StartsWith("DANMU_MSG", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array) return;
                // info[1] = 弹幕文本, info[2][1] = 用户名, info[3] = 粉丝牌
                var text = info.GetArrayLength() > 1 && info[1].ValueKind == JsonValueKind.String
                    ? info[1].GetString() ?? "" : "";
                if (text.Length == 0) return;

                var user = "";
                if (info.GetArrayLength() > 2 && info[2].ValueKind == JsonValueKind.Array &&
                    info[2].GetArrayLength() > 1)
                    user = info[2][1].GetString() ?? "";

                var medalLevel = 0;
                var medalName = "";
                if (info.GetArrayLength() > 3 && info[3].ValueKind == JsonValueKind.Array &&
                    info[3].GetArrayLength() > 1)
                {
                    medalLevel = info[3][0].ValueKind == JsonValueKind.Number ? info[3][0].GetInt32() : 0;
                    medalName = info[3][1].ValueKind == JsonValueKind.String ? info[3][1].GetString() ?? "" : "";
                }

                Raise(new Message
                {
                    User = user,
                    Text = text,
                    MedalLevel = medalLevel,
                    MedalName = medalName,
                });
                return;
            }

            // 进场 / 关注 / 礼物 / 上舰: 统一按系统消息显示(不刷屏, 只留重要的)
            if (cmd is "INTERACT_WORD" or "INTERACT_WORD_V2")
            {
                var user = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                    ? Str(d, "uname") : "";
                if (user.Length == 0) return;
                Raise(new Message { User = user, Text = "进入直播间", IsSystem = true });
                return;
            }
            if (cmd == "SEND_GIFT")
            {
                if (!root.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return;
                var user = Str(d, "uname");
                var gift = Str(d, "giftName");
                var num = d.TryGetProperty("num", out var n) && n.ValueKind == JsonValueKind.Number
                    ? n.GetInt32() : 1;
                Raise(new Message
                {
                    User = user,
                    Text = $"送出 {gift} ×{num}",
                    IsSystem = true,
                    IsHighlight = true,
                });
                return;
            }
            if (cmd is "SUPER_CHAT_MESSAGE")
            {
                if (!root.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return;
                var user = Str(d, "uname");
                var text = Str(d, "message");
                Raise(new Message { User = user, Text = "醒目留言: " + text, IsHighlight = true });
            }
        }
        catch { }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ------------------------------------------------------------ 发包

    private static byte[] BuildAuthBody(long roomId, long uid, string token)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("uid", uid);
            w.WriteNumber("roomid", roomId);
            w.WriteNumber("protover", 2);          // 2 = 收 zlib 压缩包
            w.WriteString("platform", "web");
            w.WriteNumber("type", 2);
            w.WriteString("key", token);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static async Task SendAsync(ClientWebSocket ws, int op, byte[] body, CancellationToken ct)
    {
        var packet = new byte[HeaderLen + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), HeaderLen);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6, 2), 1);   // 发出去的用版本 1(不压缩)
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), op);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), 1);  // sequence
        body.CopyTo(packet, HeaderLen);
        await ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, ct);
    }

    // ------------------------------------------------------------ 事件派发(回 UI 线程)

    private void Raise(Message m) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { Received?.Invoke(m); } catch { }
        }));

    private void RaiseConnection(bool ok) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { ConnectionChanged?.Invoke(ok); } catch { }
        }));
}
