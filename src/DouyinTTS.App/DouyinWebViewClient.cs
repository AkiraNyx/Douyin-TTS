using System.Text.Json;
using DouyinTTS.Core.Live;
using DouyinTTS.Core.Live.Models;
using DouyinTTS.Core.Live.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace DouyinTTS.App;

public class DouyinWebViewClient : IDisposable
{
    private readonly ILogger? _logger;
    private readonly DouyinProtoParser _parser;
    private CoreWebView2? _core;
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _seenMethods = new();
    private DateTime _connectedAt = DateTime.MinValue;
    private TaskCompletionSource<bool>? _connectTcs;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(120);
    private bool _connected;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string RoomId { get; private set; } = string.Empty;
    public string DebugRoomId => RoomId;
    public int RawMessageCount { get; private set; }
    public event Action<LiveEvent>? OnEvent;
    public event Action<ConnectionState>? OnStateChanged;
    public event Action<string>? OnError;

    public DouyinWebViewClient(ILogger? logger = null)
    {
        _logger = logger;
        _parser = new DouyinProtoParser(logger);
    }

    public async Task ConnectAsync(CoreWebView2 coreWebView2, string roomId, CancellationToken externalCt = default)
    {
        if (State == ConnectionState.Connected || State == ConnectionState.Connecting)
            return;

        // 清理旧状态
        _connectTcs?.TrySetCanceled();
        _connectTcs = null;
        _connected = false;

        SetState(ConnectionState.Connecting);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _seenMethods.Clear();
        RawMessageCount = 0;
        _core = coreWebView2;
        RoomId = roomId;

        try
        {
            _connectTcs = new TaskCompletionSource<bool>();

            // 注册外部取消回调
            externalCt.Register(() => _connectTcs?.TrySetCanceled(externalCt));

            // 注入 hook — 数据存入队列
            await InjectHook();

            // 注册事件（先移除旧监听器再添加，防止重复）
            _core.NavigationCompleted -= OnNavigationCompleted;
            _core.NavigationCompleted += OnNavigationCompleted;
            _core.NewWindowRequested += (_, e) => e.Handled = true;

            // 导航
            var url = $"https://live.douyin.com/{roomId}";
            _logger?.LogInformation("导航: {Url}", url);
            _core.Navigate(url);

            // 启动轮询（在 UI 线程上，因为 ExecuteScriptAsync 需要 UI 线程）
            var pollCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _ = PollLoop(pollCts.Token);

            // 等待连接或超时
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeoutCts.CancelAfter(ConnectTimeout);
            var completed = await Task.WhenAny(
                _connectTcs.Task,
                Task.Delay(ConnectTimeout, timeoutCts.Token));

            if (completed != _connectTcs.Task || !_connectTcs.Task.Result)
                throw new TimeoutException($"WebSocket 连接超时 ({ConnectTimeout.TotalSeconds}秒)");
        }
        catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
        {
            OnError?.Invoke("连接已取消");
            SetState(ConnectionState.Disconnected);
            throw;
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
        _connectTcs?.TrySetCanceled();
        _connectTcs = null;
        _connected = false;

        if (_core != null)
        {
            try
            {
                _core.NavigationCompleted -= OnNavigationCompleted;
                _core.Stop();
                _core.Navigate("about:blank");
            }
            catch { }
        }
        _cts?.Dispose();
        _cts = null;
        SetState(ConnectionState.Disconnected);
        await Task.CompletedTask;
    }

    private async Task InjectHook()
    {
        // 改进版 hook：同时 hook 主窗口和 iframe 的 WebSocket
        // 使用 Object.defineProperty 防止被页面覆盖
        const string script = """
            (function(){
                if(window._wsHook) return 'skip';
                window._wsHook=true;
                window._wsQueue=[];
                window._wsState=-1;
                window._wsErr=null;
                var O=window.WebSocket;
                window._wsOrig=O;

                function HookWS(OrigWS){
                    return function(u,p){
                        var ws=p?new OrigWS(u,p):new OrigWS(u);
                        window._ws=ws;
                        window._wsQueue.push({t:'c',u:(u||'').toString().substring(0,200)});
                        ws.addEventListener('open',function(){
                            window._wsState=1;
                            window._wsQueue.push({t:'o'});
                        });
                        ws.addEventListener('message',function(e){
                            try{
                                if(e.data instanceof ArrayBuffer){
                                    var b=new Uint8Array(e.data),s='';
                                    for(var i=0;i<b.length;i++) s+=String.fromCharCode(b[i]);
                                    window._wsQueue.push({t:'b',d:btoa(s)});
                                }else if(typeof e.data==='string'){
                                    window._wsQueue.push({t:'s',d:e.data.substring(0,500)});
                                }
                            }catch(x){window._wsErr=x.message;}
                        });
                        ws.addEventListener('close',function(e){
                            window._wsState=3;
                            window._wsQueue.push({t:'x',c:e.code});
                        });
                        ws.addEventListener('error',function(){
                            window._wsQueue.push({t:'e'});
                        });
                        return ws;
                    };
                }

                var Hooked=HookWS(O);
                Hooked.prototype=O.prototype;
                Hooked.CONNECTING=O.CONNECTING;
                Hooked.OPEN=O.OPEN;
                Hooked.CLOSING=O.CLOSING;
                Hooked.CLOSED=O.CLOSED;
                window.WebSocket=Hooked;

                // 同时 hook iframe 的 WebSocket（页面可能在 iframe 中创建）
                function hookIframes(){
                    try{
                        var iframes=document.querySelectorAll('iframe');
                        for(var i=0;i<iframes.length;i++){
                            try{
                                var cw=iframes[i].contentWindow;
                                if(cw && cw.WebSocket && !cw.WebSocket._wsHooked){
                                    var orig=cw.WebSocket;
                                    var hooked=HookWS(orig);
                                    hooked.prototype=orig.prototype;
                                    hooked.CONNECTING=orig.CONNECTING;
                                    hooked.OPEN=orig.OPEN;
                                    hooked.CLOSING=orig.CLOSING;
                                    hooked.CLOSED=orig.CLOSED;
                                    hooked._wsHooked=true;
                                    cw.WebSocket=hooked;
                                    window._wsQueue.push({t:'diag',m:'iframe_hooked',u:iframes[i].src||''});
                                }
                            }catch(x){/* cross-origin iframe, expected */}
                        }
                    }catch(x){}
                }
                // 定期尝试 hook iframe
                hookIframes();
                setTimeout(hookIframes,1000);
                setTimeout(hookIframes,3000);
                setTimeout(hookIframes,5000);

                return 'ok';
            })();
            """;
        var result = await _core!.ExecuteScriptAsync(script);
        _logger?.LogInformation("Hook 注入结果: {Result}", result);
        OnEvent?.Invoke(new DebugEvent { Method = "Hook", PayloadSize = 0, Error = $"注入结果={result}" });

        // 同时用 AddScriptToExecuteOnDocumentCreatedAsync 注入（覆盖后续导航和 iframe）
        const string addScriptHook = """
            (function(){
                if(window._wsHook) return;
                window._wsHook=true;
                window._wsQueue=window._wsQueue||[];
                window._wsState=-1;
                var O=window.WebSocket;
                function HookWS(OrigWS){
                    return function(u,p){
                        var ws=p?new OrigWS(u,p):new OrigWS(u);
                        window._ws=ws;
                        window._wsQueue.push({t:'c',u:(u||'').toString().substring(0,200)});
                        ws.addEventListener('open',function(){
                            window._wsState=1;
                            window._wsQueue.push({t:'o'});
                        });
                        ws.addEventListener('message',function(e){
                            try{
                                if(e.data instanceof ArrayBuffer){
                                    var b=new Uint8Array(e.data),s='';
                                    for(var i=0;i<b.length;i++) s+=String.fromCharCode(b[i]);
                                    window._wsQueue.push({t:'b',d:btoa(s)});
                                }else if(typeof e.data==='string'){
                                    window._wsQueue.push({t:'s',d:e.data.substring(0,500)});
                                }
                            }catch(x){}
                        });
                        ws.addEventListener('close',function(e){
                            window._wsState=3;
                            window._wsQueue.push({t:'x',c:e.code});
                        });
                        ws.addEventListener('error',function(){
                            window._wsQueue.push({t:'e'});
                        });
                        return ws;
                    };
                }
                var Hooked=HookWS(O);
                Hooked.prototype=O.prototype;
                Hooked.CONNECTING=O.CONNECTING;
                Hooked.OPEN=O.OPEN;
                Hooked.CLOSING=O.CLOSING;
                Hooked.CLOSED=O.CLOSED;
                window.WebSocket=Hooked;
            })();
            """;
        await _core.AddScriptToExecuteOnDocumentCreatedAsync(addScriptHook);
        _logger?.LogInformation("AddScript hook 已注入");
    }

    private async Task PollLoop(CancellationToken ct)
    {
        // 等页面加载
        try
        {
            await Task.Delay(3000, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        OnEvent?.Invoke(new DebugEvent { Method = "PollStart", PayloadSize = 0, Error = "轮询启动" });

        int pollCount = 0;
        while (!ct.IsCancellationRequested && _core != null && State != ConnectionState.Disconnected)
        {
            try
            {
                pollCount++;

                // 1) 读队列
                var json = await _core.ExecuteScriptAsync(
                    "(function(){var q=window._wsQueue||[];window._wsQueue=[];return JSON.stringify(q)})()");

                if (!string.IsNullOrEmpty(json) && json != "null" && json != "\"\"" && json.Length > 2)
                {
                    var items = JsonSerializer.Deserialize<JsonElement>(json);
                    // ExecuteScriptAsync 可能双重编码：外层是 String，内层才是实际 JSON
                    if (items.ValueKind == JsonValueKind.String)
                        items = JsonSerializer.Deserialize<JsonElement>(items.GetString()!);
                    if (items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                            HandleItem(item);
                    }
                }

                // 2) 诊断 + 备用检测（合并为一次脚本调用，减少开销）
                if (!_connected)
                {
                    var diag = await _core.ExecuteScriptAsync("""
                        (function(){
                            var r={hook:!!window._wsHook,orig:!!window._wsOrig};
                            r.ws=window._ws?{s:window._ws.readyState,u:(window._ws.url||'').substring(0,100)}:null;
                            r.q=window._wsQueue?window._wsQueue.length:0;
                            r.err=window._wsErr||null;
                            // 检查所有 WebSocket 实例（尝试通过 Performance API）
                            try{
                                var entries=performance.getEntriesByType('resource');
                                var wsEntries=[];
                                for(var i=0;i<entries.length;i++){
                                    if(entries[i].initiatorType==='websocket'||entries[i].name.indexOf('wss:')===0)
                                        wsEntries.push(entries[i].name.substring(0,100));
                                }
                                r.perf=wsEntries;
                            }catch(x){r.perf=[];}
                            return JSON.stringify(r);
                        })()
                        """);

                    if (!string.IsNullOrEmpty(diag) && diag != "null")
                    {
                        var diagJson = JsonSerializer.Deserialize<JsonElement>(diag);
                        if (diagJson.ValueKind == JsonValueKind.String)
                            diagJson = JsonSerializer.Deserialize<JsonElement>(diagJson.GetString()!);
                        var hasHook = diagJson.TryGetProperty("hook", out var h) && h.GetBoolean();
                        var hasWs = diagJson.TryGetProperty("ws", out var w) && w.ValueKind == JsonValueKind.Object;
                        var wsState = hasWs && diagJson.GetProperty("ws").TryGetProperty("s", out var s) ? s.GetInt32() : -1;
                        var wsUrl = hasWs && diagJson.GetProperty("ws").TryGetProperty("u", out var u) ? u.GetString() : null;
                        var err = diagJson.TryGetProperty("err", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                        var perf = diagJson.TryGetProperty("perf", out var p) && p.ValueKind == JsonValueKind.Array
                            ? p.EnumerateArray().Select(x => x.GetString()).ToArray() : [];

                        // 每 10 次轮询或首次发现 WebSocket 时报告
                        if (pollCount <= 3 || pollCount % 10 == 0 || hasWs || perf.Length > 0)
                        {
                            OnEvent?.Invoke(new DebugEvent
                            {
                                Method = "Diag",
                                PayloadSize = pollCount,
                                Error = $"hook={hasHook} ws={hasWs}({wsState}) url={wsUrl} perf=[{string.Join(",", perf)}] err={err}"
                            });
                        }

                        // 备用连接检测：hook 拦截到了 WebSocket 且已连接
                        if (hasWs && wsState == 1)
                        {
                            if (!_connected)
                            {
                                _connected = true;
                                _connectedAt = DateTime.UtcNow;
                                SetState(ConnectionState.Connected);
                                _connectTcs?.TrySetResult(true);
                                OnEvent?.Invoke(new DebugEvent { Method = "WS_Ready", PayloadSize = 0, Error = $"WebSocket 已连接(readyState检测) url={wsUrl}" });
                                _ = MuteAsync();
                                // 为已存在的 WebSocket 补充事件监听
                                await AttachExistingWsListeners();
                            }
                        }
                    }
                }

                // 3) 连接后，持续读数据（队列已在上面读取）
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                OnEvent?.Invoke(new DebugEvent { Method = "PollErr", PayloadSize = 0, Error = ex.Message });
            }

            try { await Task.Delay(_connected ? 100 : 300, ct); } catch { break; }
        }
        OnEvent?.Invoke(new DebugEvent { Method = "PollEnd", PayloadSize = 0, Error = "轮询结束" });
    }

    /// <summary>
    /// 为已存在但 hook 未拦截到的 WebSocket 补充事件监听
    /// </summary>
    private async Task AttachExistingWsListeners()
    {
        if (_core == null) return;
        try
        {
            await _core.ExecuteScriptAsync("""
                (function(){
                    var ws=window._ws;
                    if(!ws||ws._hasListeners) return;
                    ws._hasListeners=true;
                    ws.addEventListener('message',function(e){
                        try{
                            if(e.data instanceof ArrayBuffer){
                                var b=new Uint8Array(e.data),s='';
                                for(var i=0;i<b.length;i++) s+=String.fromCharCode(b[i]);
                                (window._wsQueue=window._wsQueue||[]).push({t:'b',d:btoa(s)});
                            }else if(typeof e.data==='string'){
                                (window._wsQueue=window._wsQueue||[]).push({t:'s',d:e.data.substring(0,500)});
                            }
                        }catch(x){}
                    });
                    ws.addEventListener('close',function(e){
                        (window._wsQueue=window._wsQueue||[]).push({t:'x',c:e.code});
                    });
                    ws.addEventListener('error',function(){
                        (window._wsQueue=window._wsQueue||[]).push({t:'e'});
                    });
                })();
                """);
        }
        catch { }
    }

    private void HandleItem(JsonElement item)
    {
        var t = item.GetProperty("t").GetString();
        switch (t)
        {
            case "c":
                var u = item.TryGetProperty("u", out var up) ? up.GetString() : "";
                OnEvent?.Invoke(new DebugEvent { Method = "WS_Created", PayloadSize = 0, Error = u });
                break;
            case "o":
                _connected = true;
                _connectedAt = DateTime.UtcNow;
                SetState(ConnectionState.Connected);
                _connectTcs?.TrySetResult(true);
                OnEvent?.Invoke(new DebugEvent { Method = "WS_Open", PayloadSize = 0, Error = "已连接" });
                _ = MuteAsync();
                break;
            case "b":
                var b64 = item.TryGetProperty("d", out var d) ? d.GetString() : "";
                if (!string.IsNullOrEmpty(b64))
                {
                    var data = Convert.FromBase64String(b64);
                    ProcessMessage(data);
                }
                break;
            case "s":
                var txt = item.TryGetProperty("d", out var sd) ? sd.GetString() : "";
                OnEvent?.Invoke(new DebugEvent { Method = "WS_Text", PayloadSize = txt?.Length ?? 0, Error = txt?.Length > 100 ? txt[..100] + "..." : txt });
                break;
            case "x":
                SetState(ConnectionState.Disconnected);
                OnEvent?.Invoke(new DebugEvent { Method = "WS_Close", PayloadSize = 0, Error = "WebSocket 关闭" });
                break;
            case "e":
                OnError?.Invoke("WebSocket 错误");
                break;
            case "diag":
                var msg = item.TryGetProperty("m", out var m) ? m.GetString() : "";
                var du = item.TryGetProperty("u", out var du2) ? du2.GetString() : "";
                OnEvent?.Invoke(new DebugEvent { Method = $"JS_{msg}", PayloadSize = 0, Error = du });
                break;
        }
    }

    private void ProcessMessage(byte[] data)
    {
        try
        {
            RawMessageCount++;
            var frame = DouyinProtoParser.DecodeFrame(data);
            if (frame == null)
            {
                OnEvent?.Invoke(new DebugEvent
                {
                    Method = $"DecodeFail#{RawMessageCount}",
                    PayloadSize = data.Length,
                    Error = $"head={Convert.ToHexString(data[..Math.Min(8, data.Length)])}"
                });
                return;
            }

            var response = _parser.ParseResponse(frame);
            if (response == null) return;

            var methodNames = response.MessagesList?.Select(m => m.Method).ToArray() ?? [];
            foreach (var m in methodNames) _seenMethods.Add(m);

            if (RawMessageCount % 50 == 0)
            {
                OnEvent?.Invoke(new DebugEvent
                {
                    Method = "AllTypes",
                    PayloadSize = _seenMethods.Count,
                    Error = $"已见 {_seenMethods.Count} 种: [{string.Join(",", _seenMethods.OrderBy(x => x))}]"
                });
            }

            if (response.NeedAck != 0)
                _ = SendAckAsync(frame.LogId, response.InternalExt);

            var events = _parser.ExtractEvents(response);
            var elapsed = (DateTime.UtcNow - _connectedAt).TotalSeconds;
            foreach (var evt in events)
            {
                if (elapsed < 5 && evt.Type is not (LiveEventType.System or LiveEventType.Debug))
                    continue;
                OnEvent?.Invoke(evt);
            }
        }
        catch (Exception ex)
        {
            OnEvent?.Invoke(new DebugEvent { Method = "ProcessErr", Error = ex.Message, PayloadSize = data.Length });
        }
    }

    private async Task SendAckAsync(long logId, string internalExt)
    {
        if (_core == null) return;
        try
        {
            var ack = DouyinProtoParser.BuildAckPayload(logId, internalExt);
            var hex = Convert.ToHexString(ack).ToLower();
            await _core.ExecuteScriptAsync(
                $"(function(){{var h='{hex}',b=new Uint8Array(h.length/2);for(var i=0;i<h.length;i+=2)b[i/2]=parseInt(h.substr(i,2),16);if(window._ws)window._ws.send(b)}})()");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ACK 失败");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _logger?.LogInformation("页面加载成功");
            // 页面加载后，尝试重新 hook iframe
            _ = RehookIframes();
        }
        else
        {
            OnError?.Invoke($"页面加载失败: {e.WebErrorStatus}");
            _connectTcs?.TrySetResult(false);
            SetState(ConnectionState.Disconnected);
        }
    }

    private async Task RehookIframes()
    {
        if (_core == null) return;
        try
        {
            await _core.ExecuteScriptAsync("""
                (function(){
                    try{
                        var iframes=document.querySelectorAll('iframe');
                        for(var i=0;i<iframes.length;i++){
                            try{
                                var cw=iframes[i].contentWindow;
                                if(cw && cw.WebSocket && !cw.WebSocket._wsHooked){
                                    var O=cw.WebSocket;
                                    var H=function(u,p){var ws=p?new O(u,p):new O(u);window._ws=ws;(window._wsQueue=window._wsQueue||[]).push({t:'c',u:(u||'').toString().substring(0,200)});ws.addEventListener('open',function(){window._wsState=1;(window._wsQueue=window._wsQueue||[]).push({t:'o'})});ws.addEventListener('message',function(e){try{if(e.data instanceof ArrayBuffer){var b=new Uint8Array(e.data),s='';for(var j=0;j<b.length;j++)s+=String.fromCharCode(b[j]);(window._wsQueue=window._wsQueue||[]).push({t:'b',d:btoa(s)})}else if(typeof e.data==='string'){(window._wsQueue=window._wsQueue||[]).push({t:'s',d:e.data.substring(0,500)})}}catch(x){}});ws.addEventListener('close',function(e){window._wsState=3;(window._wsQueue=window._wsQueue||[]).push({t:'x',c:e.code})});ws.addEventListener('error',function(){(window._wsQueue=window._wsQueue||[]).push({t:'e'})});return ws};
                                    H.prototype=O.prototype;
                                    H._wsHooked=true;
                                    cw.WebSocket=H;
                                    (window._wsQueue=window._wsQueue||[]).push({t:'diag',m:'iframe_hooked_late',u:iframes[i].src||''});
                                }
                            }catch(x){}
                        }
                    }catch(x){}
                })();
                """);
        }
        catch { }
    }

    private async Task MuteAsync()
    {
        if (_core == null) return;
        try
        {
            await _core.ExecuteScriptAsync("""
                (function(){
                    document.querySelectorAll('video,audio').forEach(function(e){e.muted=true;e.volume=0;e.pause()});
                    new MutationObserver(function(m){
                        m.forEach(function(x){
                            x.addedNodes.forEach(function(n){
                                if(n.tagName==='VIDEO'||n.tagName==='AUDIO'){n.muted=true;n.volume=0;n.pause()}
                                if(n.querySelectorAll) n.querySelectorAll('video,audio').forEach(function(e){e.muted=true;e.volume=0;e.pause()})
                            })
                        })
                    }).observe(document.body,{childList:true,subtree:true})
                })()
                """);
        }
        catch { }
    }

    private void SetState(ConnectionState state)
    {
        State = state;
        OnStateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _connectTcs?.TrySetCanceled();
        _connectTcs = null;
        _connected = false;

        if (_core != null)
        {
            try
            {
                _core.NavigationCompleted -= OnNavigationCompleted;
                _core.Stop();
            }
            catch { }
        }
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
