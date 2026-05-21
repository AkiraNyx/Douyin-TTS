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

    private string _webRid = string.Empty;
    private string _roomId = string.Empty;
    private string _pushId = string.Empty;
    private string _ttwid = string.Empty;
    private readonly Dictionary<string, string> _allCookies = new();
    private long _heartbeatInterval = 10000;
    private DateTime _connectedAt = DateTime.MinValue;
    private const int IgnoreHistorySeconds = 5;
    private readonly HashSet<string> _seenMethods = new();

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public RoomInfo? Room { get; private set; }
    public string DebugRoomId => _roomId;
    public int RawMessageCount { get; private set; }
    public event Action<LiveEvent>? OnEvent;
    public event Action<ConnectionState>? OnStateChanged;
    public event Action<string>? OnError;

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip
            | System.Net.DecompressionMethods.Deflate
            | System.Net.DecompressionMethods.Brotli
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static DouyinLiveClient()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        Http.DefaultRequestHeaders.Add("Referer", "https://live.douyin.com/");
        Http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
    }

    public DouyinLiveClient(ILogger? logger = null)
    {
        _logger = logger;
        _parser = new DouyinProtoParser(logger);
    }

    public async Task ConnectAsync(string roomInput)
    {
        if (State == ConnectionState.Connected || State == ConnectionState.Connecting)
            return;

        SetState(ConnectionState.Connecting);
        _seenMethods.Clear();

        // 连接阶段用 30 秒超时，连接成功后切换为无超时 CTS
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            _webRid = await ResolveWebRidAsync(roomInput);
            _logger?.LogInformation("web_rid: {WebRid}", _webRid);

            await FetchTtwidAsync();
            await FetchRoomPageInfoAsync();

            if (string.IsNullOrEmpty(_roomId))
                throw new InvalidOperationException("无法获取直播间内部 ID，可能房间号无效或直播已结束");

            await ConnectWebSocketAsync();

            SetState(ConnectionState.Connected);

            // 连接成功，切换为无超时的 session CTS（旧的 30s CTS 会被 Dispose）
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            // 发送连接成功系统消息
            var roomInfo = Room != null
                ? $"已连接: {Room.Title} (在线: {Room.ViewerCount}) room_id={_roomId}"
                : $"已连接 room_id={_roomId}";
            OnEvent?.Invoke(new Models.SystemEvent { Message = roomInfo });

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        }
        catch (OperationCanceledException)
        {
            OnError?.Invoke("连接超时，请检查网络或房间号");
            SetState(ConnectionState.Disconnected);
            throw new TimeoutException("连接超时");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "连接失败");
            OnError?.Invoke($"连接失败: {ex.Message}");
            SetState(ConnectionState.Disconnected);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try { await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "用户断开", CancellationToken.None); }
            catch { }
        }

        _webSocket?.Dispose();
        _webSocket = null;

        if (_receiveTask != null) try { await _receiveTask; } catch { }
        if (_heartbeatTask != null) try { await _heartbeatTask; } catch { }

        _cts?.Dispose();
        _cts = null;

        SetState(ConnectionState.Disconnected);
    }

    private static async Task<string> ResolveWebRidAsync(string input)
    {
        input = input.Trim();

        if (long.TryParse(input, out var rid) && rid > 0)
            return input;

        var match = Regex.Match(input, @"live\.douyin\.com/(\d+)");
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(input, @"douyin\.com/(\d+)");
        if (match.Success) return match.Groups[1].Value;

        if (input.Contains("v.douyin.com") || input.Contains("iesdouyin.com"))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, input);
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
            match = Regex.Match(finalUrl, @"live\.douyin\.com/(\d+)");
            if (match.Success) return match.Groups[1].Value;
        }

        throw new ArgumentException($"无法解析直播间地址: {input}");
    }

    private async Task FetchTtwidAsync()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

        var response = await client.GetAsync("https://live.douyin.com/");

        // 收集所有 Set-Cookie
        CollectCookies(response);

        if (_allCookies.TryGetValue("ttwid", out var ttwid))
        {
            _ttwid = ttwid;
            _logger?.LogInformation("获取 ttwid 成功, 共 {Count} 个 cookie", _allCookies.Count);
            return;
        }

        _logger?.LogWarning("无法获取 ttwid，使用备用值");
        _ttwid = GenerateTtwid();
        _allCookies["ttwid"] = _ttwid;
    }

    private void CollectCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return;
        foreach (var cookie in cookies)
        {
            // 解析 "name=value; ..." 格式，只取 name=value 部分
            var parts = cookie.Split(';')[0].Trim();
            var eqIdx = parts.IndexOf('=');
            if (eqIdx > 0)
            {
                var name = parts[..eqIdx].Trim();
                var value = parts[(eqIdx + 1)..].Trim();
                if (!string.IsNullOrEmpty(value) && value != "deleted")
                    _allCookies[name] = value;
            }
        }
    }

    private async Task FetchRoomPageInfoAsync()
    {
        // 从 HTML 页面提取 room_id（API 可能返回空数据）
        var msToken = GenerateMsToken();
        var acNonce = GenerateAcNonce();
        var pageUrl = $"https://live.douyin.com/{_webRid}";
        var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        request.Headers.Add("Cookie", $"ttwid={_ttwid}; msToken={msToken}; __ac_nonce={acNonce}");

        var response = await Http.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        // 收集页面响应中的 cookie
        CollectCookies(response);

        _logger?.LogInformation("页面 HTTP {Status}, HTML {Len} 字节, cookie {CookieCount} 个", response.StatusCode, html.Length, _allCookies.Count);

        // 匹配与 web_rid 关联的 roomId（JSON 中格式：roomId\":\"数字\",\"web_rid\":\"输入值\"）
        var roomPattern = @"roomId\\"":\\""(\d+)\\"",\\""web_rid\\"":\\""(" + Regex.Escape(_webRid) + @")\\""";
        var roomMatch = Regex.Match(html, roomPattern);
        if (roomMatch.Success)
            _roomId = roomMatch.Groups[1].Value;

        // 匹配 user_unique_id（JSON 转义格式）
        var pushMatch = Regex.Match(html, @"user_unique_id.{1,10}:""(\d{10,})""");
        if (!pushMatch.Success)
            pushMatch = Regex.Match(html, @"user_unique_id[^\d]*(\d{15,})");
        if (pushMatch.Success)
            _pushId = pushMatch.Groups[1].Value;

        if (string.IsNullOrEmpty(_pushId))
            _pushId = GeneratePushId();

        // 调试：输出 pushId 提取结果和附近上下文
        var uidIdx = html.IndexOf("user_unique_id", StringComparison.OrdinalIgnoreCase);
        var uidContext = uidIdx >= 0 ? html.Substring(uidIdx, Math.Min(100, html.Length - uidIdx)) : "未找到";
        _logger?.LogInformation("room_id={RoomId}, push_id={PushId}, push_id_len={Len}", _roomId, _pushId, _pushId.Length);
        _logger?.LogInformation("user_unique_id 上下文: {Context}", uidContext);

        if (string.IsNullOrEmpty(_roomId))
        {
            var debugIdx = html.IndexOf("roomId", StringComparison.OrdinalIgnoreCase);
            var debugSnippet = debugIdx >= 0 ? html.Substring(debugIdx, Math.Min(80, html.Length - debugIdx)) : "未找到";
            var webRidIdx = html.IndexOf(_webRid, StringComparison.OrdinalIgnoreCase);
            var webRidSnippet = webRidIdx >= 0 ? html.Substring(webRidIdx, Math.Min(80, html.Length - webRidIdx)) : "未找到";
            throw new InvalidOperationException(
                $"房间号 {_webRid} 不存在或未开播\n" +
                $"roomId: {debugSnippet}\n" +
                $"webRid: {webRidSnippet}");
        }

        // 验证提取到的 roomId 是否与 web_rid 在同一 JSON 对象中
        var verifyPattern = $"roomId\\\\\":\\\\\"{_roomId}\\\\\",\\\\\"web_rid\\\\\":\\\\\"{Regex.Escape(_webRid)}\\\\\"";
        if (!Regex.IsMatch(html, verifyPattern))
        {
            throw new InvalidOperationException(
                $"提取到的 roomId {_roomId} 不属于房间号 {_webRid}");
        }

        // 提取房间标题和观看人数
        ExtractRoomInfo(html);
    }

    private void ExtractRoomInfo(string html)
    {
        // 先找到 roomId 所在位置，在其附近搜索标题
        var roomIdIdx = html.IndexOf(_roomId, StringComparison.Ordinal);
        if (roomIdIdx < 0)
        {
            Room = new RoomInfo { RoomId = 0, Title = $"房间 {_webRid}", IsLive = true };
            return;
        }

        // 在 roomId 后方 3000 字符内搜索 title（JSON 中 title 通常在 roomId 之后）
        var searchEnd = Math.Min(html.Length, roomIdIdx + 3000);
        var afterRoom = html.Substring(roomIdIdx, searchEnd - roomIdIdx);

        // 匹配 title 后面的值，跳过转义引号
        // 模式: title":"  或  title":"
        var titleMatch = Regex.Match(afterRoom, @"title.{1,5}:\s*[\""]([^\""]{2,200})");
        var title = titleMatch.Success ? titleMatch.Groups[1].Value : $"房间 {_webRid}";

        // 匹配在线人数（支持多种字段名和格式）
        var viewerCount = TryParseViewerCount(afterRoom) ?? TryParseViewerCount(html) ?? 0;

        Room = new RoomInfo
        {
            RoomId = long.TryParse(_roomId, out var rid) ? rid : 0,
            Title = title,
            ViewerCount = viewerCount,
            IsLive = true
        };

        _logger?.LogInformation("房间标题: '{Title}', 在线: {Viewers}", title, viewerCount);
    }

    private static int? TryParseViewerCount(string text)
    {
        // 尝试 user_count_str（可能是 "1.2万" 或 "12345" 或 "12,345"）
        var strMatch = Regex.Match(text, @"user_count_str.{1,10}:\s*""([^""]+)""");
        if (strMatch.Success)
        {
            var raw = strMatch.Groups[1].Value;
            var wanMatch = Regex.Match(raw, @"([\d.]+)\s*万");
            if (wanMatch.Success && double.TryParse(wanMatch.Groups[1].Value, out var wan))
                return (int)(wan * 10000);
            if (int.TryParse(raw.Replace(",", ""), out var count))
                return count;
        }

        // 尝试 user_count / viewer_count / online_count（纯数字）
        foreach (var field in new[] { "user_count", "viewer_count", "online_count" })
        {
            var match = Regex.Match(text, $"{field}.{{1,5}}:\\s*(\\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var val) && val > 0)
                return val;
        }

        return null;
    }

    private async Task ConnectWebSocketAsync()
    {
        if (string.IsNullOrEmpty(_pushId))
            _pushId = GeneratePushId();

        // 初始化签名引擎
        await Task.Run(() =>
        {
            try
            {
                DouyinSignature.Initialize(UserAgent);
                _logger?.LogInformation("签名引擎初始化成功");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "签名引擎初始化失败");
            }
        });

        var fetchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var wsUrl = DouyinSignature.BuildWebSocketUrl(_roomId, _pushId, UserAgent, fetchTime);
        _logger?.LogInformation("WebSocket URL 长度: {Len}, room_id={RoomId}, push_id={PushId}", wsUrl.Length, _roomId, _pushId);
        _logger?.LogInformation("WebSocket URL: {Url}", wsUrl);

        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("User-Agent", UserAgent);
        _webSocket.Options.SetRequestHeader("Cookie", $"ttwid={_ttwid}");
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

        await _webSocket.ConnectAsync(new Uri(wsUrl), _cts!.Token);
        _connectedAt = DateTime.UtcNow;
        _logger?.LogInformation("WebSocket 连接成功, 状态: {State}", _webSocket.State);

        // 通知连接状态
        OnEvent?.Invoke(new Models.DebugEvent
        {
            Method = "WebSocket",
            PayloadSize = (int)_webSocket.State,
            Error = $"WS={_webSocket.State}, URL长度={wsUrl.Length}"
        });
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        var lastMessageTime = DateTime.UtcNow;

        // 启动独立的看门狗定时器（不依赖消息循环）
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5000, ct);
                    var silent = (DateTime.UtcNow - lastMessageTime).TotalSeconds;
                    OnEvent?.Invoke(new Models.DebugEvent
                    {
                        Method = "诊断",
                        PayloadSize = RawMessageCount,
                        Error = $"已收 {RawMessageCount} 条, 静默 {silent:F0}秒, WS={_webSocket?.State}, HB间隔={_heartbeatInterval}ms"
                    });
                }
            }
            catch (OperationCanceledException) { }
        });

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
                        _logger?.LogInformation("WebSocket 关闭: {Status} {Desc}", result.CloseStatus, result.CloseStatusDescription);
                        OnEvent?.Invoke(new Models.DebugEvent
                        {
                            Method = "WS关闭",
                            Error = $"Status={result.CloseStatus}, Desc={result.CloseStatusDescription}"
                        });
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var data = ms.ToArray();
                if (data.Length == 0) continue;

                lastMessageTime = DateTime.UtcNow;
                await ProcessMessageAsync(data);
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

    private async Task ProcessMessageAsync(byte[] data)
    {
        try
        {
            RawMessageCount++;

            var frame = DouyinProtoParser.DecodeFrame(data);
            if (frame == null)
            {
                OnEvent?.Invoke(new Models.DebugEvent
                {
                    Method = $"DecodeFail#{RawMessageCount}",
                    PayloadSize = data.Length,
                    Error = $"前8字节={Convert.ToHexString(data[..Math.Min(8, data.Length)])}"
                });
                return;
            }

            var response = _parser.ParseResponse(frame);
            if (response == null)
            {
                OnEvent?.Invoke(new Models.DebugEvent
                {
                    Method = $"ParseFail#{RawMessageCount}",
                    PayloadSize = frame.Payload?.Length ?? 0,
                    Error = $"PayloadLen={frame.Payload?.Length}, Enc={frame.PayloadEncoding}, PType={frame.PayloadType}"
                });
                return;
            }

            var msgCount = response.MessagesList?.Count ?? 0;
            if (response.HeartbeatDuration > 0)
            {
                // 服务器发的是秒，转为毫秒，最小 5 秒
                var hbMs = response.HeartbeatDuration * 1000;
                _heartbeatInterval = Math.Max(hbMs, 5000);
            }

            // 每条消息报告解析状态
            var methodNames = response.MessagesList?.Select(m => m.Method).ToArray() ?? [];
            foreach (var m in methodNames) _seenMethods.Add(m);

            // 每 50 帧：深度扫描所有消息的 payload 寻找 gift 数据
            if (RawMessageCount % 50 == 1 && response.MessagesList != null)
            {
                foreach (var msg in response.MessagesList)
                {
                    if (msg.Payload == null || msg.Payload.Length == 0) continue;

                    // 搜索 payload 中所有字符串字段，找 gift 相关
                    var allFields = DouyinProtoParser.ReadFields(msg.Payload);
                    var giftStrings = new List<string>();
                    ScanForGifts(allFields, giftStrings, 0);

                    if (giftStrings.Count > 0)
                    {
                        OnEvent?.Invoke(new Models.DebugEvent
                        {
                            Method = $"GIFT_FOUND",
                            PayloadSize = msg.Payload.Length,
                            Error = $"method={msg.Method}: {string.Join("; ", giftStrings)}"
                        });
                    }
                }
            }

            // 检查 PushFrame headers 中是否有 gift 相关信息
            if (frame.HeadersList?.Headers != null)
            {
                foreach (var h in frame.HeadersList.Headers)
                {
                    if (h.Key.Contains("gift", StringComparison.OrdinalIgnoreCase) ||
                        h.Key.Contains("compress", StringComparison.OrdinalIgnoreCase))
                    {
                        OnEvent?.Invoke(new Models.DebugEvent
                        {
                            Method = "Header",
                            PayloadSize = 0,
                            Error = $"{h.Key}={h.Value[..Math.Min(50, h.Value.Length)]}"
                        });
                    }
                }
            }

            OnEvent?.Invoke(new Models.DebugEvent
            {
                Method = $"Frame#{RawMessageCount}",
                PayloadSize = data.Length,
                Error = $"msgs={msgCount}, ack={response.NeedAck}, hb={response.HeartbeatDuration}, enc={frame.PayloadEncoding}, ptype={frame.PayloadType}, types=[{string.Join(",", methodNames)}]"
            });

            // 每 20 帧报告一次所有已见过的消息类型
            if (RawMessageCount % 20 == 0)
            {
                OnEvent?.Invoke(new Models.DebugEvent
                {
                    Method = "AllTypes",
                    PayloadSize = _seenMethods.Count,
                    Error = $"已见 {_seenMethods.Count} 种消息类型: [{string.Join(",", _seenMethods.OrderBy(x => x))}]"
                });
            }

            // 需要 ACK 时发送确认包（payloadType="ack" + internalExt 作为 payload）
            if (response.NeedAck != 0)
            {
                var ack = DouyinProtoParser.BuildAckPayload(frame.LogId, response.InternalExt);
                try
                {
                    await _webSocket!.SendAsync(ack, WebSocketMessageType.Binary, true, _cts!.Token);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "发送 ACK 失败");
                }
            }

            var events = _parser.ExtractEvents(response);
            var elapsed = (DateTime.UtcNow - _connectedAt).TotalSeconds;
            foreach (var evt in events)
            {
                // 连接后前几秒内过滤历史弹幕/礼物/进场/点赞消息
                if (elapsed < IgnoreHistorySeconds
                    && evt.Type is not (LiveEventType.System or LiveEventType.Debug))
                    continue;
                OnEvent?.Invoke(evt);
            }

            // 如果没有提取到事件但有消息，报告
            if (events.Count == 0 && msgCount > 0)
                OnEvent?.Invoke(new Models.DebugEvent { Method = "NoEvents", PayloadSize = msgCount, Error = $"有{msgCount}条消息但0个事件" });
        }
        catch (Exception ex)
        {
            OnEvent?.Invoke(new Models.DebugEvent { Method = "ProcessError", Error = ex.Message, PayloadSize = data.Length });
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

    private static string GeneratePushId()
    {
        // 19 位数字，范围 [7300..., 7400...)，避免溢出 long.MaxValue
        var num = 7300000000000000000L + Random.Shared.NextInt64(100000000000000000L);
        return num.ToString();
    }

    private static string GenerateMsToken(int length = 116)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789=_";
        return new string(Enumerable.Range(0, length).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }

    private static string GenerateAcNonce(int length = 21)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Range(0, length).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }

    private static void ScanForGifts(List<ProtoField> fields, List<string> results, int depth)
    {
        if (depth > 3) return;
        foreach (var f in fields)
        {
            if (f.WireType == 2 && f.Bytes != null)
            {
                var str = f.AsString;
                if (str.Contains("gift", StringComparison.OrdinalIgnoreCase) && str.Length < 100)
                    results.Add($"f{f.FieldNumber}=\"{str}\"");
                // 递归扫描子消息
                try
                {
                    var inner = DouyinProtoParser.ReadFields(f.Bytes);
                    if (inner.Count > 0)
                        ScanForGifts(inner, results, depth + 1);
                }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _webSocket?.Dispose();
        try { _receiveTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _heartbeatTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _cts?.Dispose();
        DouyinSignature.Dispose();
        GC.SuppressFinalize(this);
    }
}
