using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DouyinTTS.Core.Live.Models;
using DouyinTTS.Core.Live.Protocol;
using Microsoft.Extensions.Logging;

namespace DouyinTTS.Core.Live;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

public class DouyinLiveClient : IDisposable
{
    private readonly ILogger? _logger;
    private readonly DouyinProtoParser _parser;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private long _roomId;
    private string _token = string.Empty;
    private string _cookie = string.Empty;
    private string _wssUrl = string.Empty;
    private long _heartbeatInterval = 10000;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public RoomInfo? Room { get; private set; }
    public event Action<LiveEvent>? OnEvent;
    public event Action<ConnectionState>? OnStateChanged;
    public event Action<string>? OnError;

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip
            | System.Net.DecompressionMethods.Deflate
            | System.Net.DecompressionMethods.Brotli
    });

    static DouyinLiveClient()
    {
        Http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        Http.DefaultRequestHeaders.Add("Referer", "https://live.douyin.com/");
        Http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
    }

    public DouyinLiveClient(ILogger? logger = null)
    {
        _logger = logger;
        _parser = new DouyinProtoParser(logger);
    }

    /// <summary>
    /// 从直播间链接或房间号解析出真实房间 ID
    /// </summary>
    public static async Task<long> ResolveRoomIdAsync(string input)
    {
        input = input.Trim();

        // 如果是纯数字，直接使用
        if (long.TryParse(input, out var roomId) && roomId > 0)
            return roomId;

        // 如果是链接，提取房间号
        var match = Regex.Match(input, @"douyin\.com/(\d+)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out roomId))
            return roomId;

        // 如果是分享链接，尝试解析
        match = Regex.Match(input, @"live\.douyin\.com/(\d+)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out roomId))
            return roomId;

        // 短链接重定向解析
        if (input.Contains("v.douyin.com") || input.Contains("iesdouyin.com"))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, input);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
            match = Regex.Match(finalUrl, @"live\.douyin\.com/(\d+)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out roomId))
                return roomId;
        }

        throw new ArgumentException($"无法解析直播间地址: {input}");
    }

    /// <summary>
    /// 连接到抖音直播间
    /// </summary>
    public async Task ConnectAsync(string roomInput)
    {
        if (State == ConnectionState.Connected || State == ConnectionState.Connecting)
            return;

        SetState(ConnectionState.Connecting);
        _cts = new CancellationTokenSource();

        try
        {
            _roomId = await ResolveRoomIdAsync(roomInput);
            _logger?.LogInformation("解析房间号: {RoomId}", _roomId);

            // 获取直播间信息
            await FetchRoomInfoAsync(_roomId);

            // 生成 cookie（如果需要）
            if (string.IsNullOrEmpty(_cookie))
            {
                _cookie = $"ttwid={GenerateTtwid()}";
            }

            // 建立 WebSocket 连接
            await ConnectWebSocketAsync();

            SetState(ConnectionState.Connected);

            // 启动接收和心跳任务
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "连接失败");
            OnError?.Invoke($"连接失败: {ex.Message}");
            SetState(ConnectionState.Disconnected);
            throw;
        }
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "用户断开", CancellationToken.None);
            }
            catch { }
        }

        _webSocket?.Dispose();
        _webSocket = null;

        if (_receiveTask != null)
            try { await _receiveTask; } catch { }
        if (_heartbeatTask != null)
            try { await _heartbeatTask; } catch { }

        _cts?.Dispose();
        _cts = null;

        SetState(ConnectionState.Disconnected);
    }

    private async Task FetchRoomInfoAsync(long roomId)
    {
        var url = $"https://live.douyin.com/webcast/room/web/enter/?aid=6383&app_name=douyin_web&live_id=1&device_platform=web&language=zh-CN&browser_language=zh-CN&browser_platform=Win32&browser_name=Chrome&browser_version=125.0.0.0&web_rid={roomId}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"ttwid={GenerateTtwid()}; __ac_nonce={GenerateNonce()}");

        var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            Room = new RoomInfo
            {
                RoomId = roomId,
                Title = data.TryGetProperty("user", out var user) && user.TryGetProperty("nickname", out var nick)
                    ? nick.GetString() ?? string.Empty : string.Empty,
                OwnerName = data.TryGetProperty("owner", out var owner) && owner.TryGetProperty("nickname", out var ownerNick)
                    ? ownerNick.GetString() ?? string.Empty : string.Empty,
                ViewerCount = data.TryGetProperty("room", out var room) && room.TryGetProperty("user_count", out var uc)
                    ? uc.GetInt32() : 0,
                IsLive = data.TryGetProperty("room", out var roomInfo) && roomInfo.TryGetProperty("status", out var status)
                    && status.GetInt32() == 2,
            };

            _logger?.LogInformation("房间信息: {Title}, 在线: {Viewers}, 直播中: {IsLive}",
                Room.Title, Room.ViewerCount, Room.IsLive);
        }
    }

    private async Task ConnectWebSocketAsync()
    {
        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        _webSocket.Options.SetRequestHeader("Origin", "https://live.douyin.com");
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

        var wsUrl = $"wss://webcast5-ws-web-lf.douyin.com/webcast/im/push/v2/?aid=6383&app_name=douyin_web&live_id=1&device_platform=web&language=zh-CN&browser_language=zh-CN&browser_platform=Win32&browser_name=Chrome&browser_version=125.0.0.0&web_rid={_roomId}&cursor=&internal_ext=&compress=gzip";

        await _webSocket.ConnectAsync(new Uri(wsUrl), _cts!.Token);

        // 发送加入房间请求
        var joinPayload = DouyinProtoParser.BuildJoinRoomPayload(_roomId, _cookie, _token);
        await _webSocket.SendAsync(joinPayload, WebSocketMessageType.Binary, true, _cts.Token);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];

        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger?.LogInformation("WebSocket 关闭");
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var data = ms.ToArray();
                if (data.Length == 0) continue;

                ProcessMessage(data);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger?.LogWarning(ex, "WebSocket 异常");
            OnError?.Invoke($"连接断开: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                SetState(ConnectionState.Disconnected);
        }
    }

    private void ProcessMessage(byte[] data)
    {
        try
        {
            var frame = DouyinProtoParser.DecodeFrame(data);
            if (frame == null) return;

            // 处理 ACK
            if (frame.Method == 3)
            {
                var ack = DouyinProtoParser.BuildAckPayload(frame.LogId);
                _webSocket?.SendAsync(ack, WebSocketMessageType.Binary, true, _cts!.Token);
                return;
            }

            // 解析 Response
            var response = _parser.ParseResponse(frame);
            if (response == null) return;

            // 更新心跳间隔
            if (response.HeartbeatDuration > 0)
                _heartbeatInterval = response.HeartbeatDuration;

            // 提取事件
            var events = _parser.ExtractEvents(response);
            foreach (var evt in events)
            {
                OnEvent?.Invoke(evt);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "处理消息失败");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                await Task.Delay((int)_heartbeatInterval, ct);

                var heartbeat = DouyinProtoParser.BuildHeartbeatPayload();
                await _webSocket.SendAsync(heartbeat, WebSocketMessageType.Binary, true, ct);
                _logger?.LogDebug("发送心跳包");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "心跳异常");
        }
    }

    private void SetState(ConnectionState state)
    {
        State = state;
        OnStateChanged?.Invoke(state);
    }

    private static string GenerateTtwid()
    {
        var bytes = new byte[24];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLower();
    }

    private static string GenerateNonce()
    {
        var chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        return string.Create(21, chars, (span, c) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = c[Random.Shared.Next(c.Length)];
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _webSocket?.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
