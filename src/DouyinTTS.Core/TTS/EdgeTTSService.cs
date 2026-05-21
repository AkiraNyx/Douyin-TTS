using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DouyinTTS.Core.TTS;

public class EdgeTTSService : IDisposable
{
    private readonly ILogger? _logger;
    private ClientWebSocket? _webSocket;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _isInitialized;

    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string BaseUrl = "speech.platform.bing.com/consumer/speech/synthesize/readaloud";
    private const string ChromiumFullVersion = "143.0.3650.75";
    private const string ChromiumMajorVersion = "143";
    private const string SecMsGecVersion = "1-" + ChromiumFullVersion;
    private const string VoiceListUrl = $"https://{BaseUrl}/voices/list?trustedclienttoken={TrustedClientToken}";

    // Windows epoch offset (1601-01-01 to 1970-01-01 in seconds)
    private const long WinEpoch = 11644473600;
    private const long S_TO_NS = 1_000_000_000; // Python: S_TO_NS = 1e9

    public event Action<string>? OnError;

    public EdgeTTSService(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 生成 Sec-MS-GEC 认证令牌
    /// </summary>
    private static string GenerateSecMsGec()
    {
        var ticks = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ticks += WinEpoch;
        ticks -= ticks % 300; // 向下取整到 5 分钟
        ticks *= S_TO_NS / 100;
        var strToHash = $"{ticks}{TrustedClientToken}";
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(strToHash));
        return Convert.ToHexString(hash).ToUpperInvariant();
    }

    /// <summary>
    /// 生成随机 MUID cookie
    /// </summary>
    private static string GenerateMuid()
    {
        var bytes = new byte[16];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToUpperInvariant();
    }

    /// <summary>
    /// 构建 WebSocket URL（含认证参数）
    /// </summary>
    private static string BuildWssUrl()
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var gec = GenerateSecMsGec();
        return $"wss://{BaseUrl}/edge/v1?TrustedClientToken={TrustedClientToken}" +
               $"&ConnectionId={connectionId}" +
               $"&Sec-MS-GEC={gec}" +
               $"&Sec-MS-GEC-Version={SecMsGecVersion}";
    }

    private async Task EnsureConnectedAsync()
    {
        if (_isInitialized && _webSocket?.State == WebSocketState.Open)
            return;

        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        // 设置 headers（匹配 edge-tts Python 库的 WSS_HEADERS）
        _webSocket.Options.SetRequestHeader("Pragma", "no-cache");
        _webSocket.Options.SetRequestHeader("Cache-Control", "no-cache");
        _webSocket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        _webSocket.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumMajorVersion}.0.0.0 Safari/537.36 Edg/{ChromiumMajorVersion}.0.0.0");

        // Cookie 需要通过 CookieContainer 设置
        _webSocket.Options.Cookies = new System.Net.CookieContainer();
        _webSocket.Options.Cookies.Add(new System.Net.Cookie("muid", GenerateMuid(), "/", "speech.platform.bing.com"));

        // 启用 permessage-deflate 压缩（匹配 aiohttp compress=15）
        _webSocket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions
        {
            ClientMaxWindowBits = 15
        };

        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

        var url = BuildWssUrl();
        _logger?.LogInformation("Edge TTS 连接: {Url}", url[..Math.Min(80, url.Length)]);

        try
        {
            await _webSocket.ConnectAsync(new Uri(url), CancellationToken.None);
        }
        catch (WebSocketException ex)
        {
            _logger?.LogError(ex, "Edge TTS WebSocket 连接失败");
            OnError?.Invoke($"TTS 连接失败: {ex.Message}");
            throw;
        }

        _isInitialized = true;
        _logger?.LogInformation("Edge TTS 连接成功, 状态: {State}", _webSocket.State);
    }

    /// <summary>
    /// 使用 Edge TTS 合成语音（失败时自动重连重试一次）
    /// </summary>
    public async Task<byte[]> SynthesizeAsync(string text, string voiceName = "zh-CN-XiaoxiaoNeural",
        string rate = "+0%", string volume = "+0%", CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            Exception? lastError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        _logger?.LogWarning("TTS 第 {Attempt} 次重试", attempt + 1);
                        OnError?.Invoke("TTS 连接异常，正在重连...");
                    }
                    await EnsureConnectedAsync();
                    return await SynthesizeInternalAsync(text, voiceName, rate, volume, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger?.LogWarning(ex, "TTS 合成失败 (attempt {Attempt}, type {Type})", attempt + 1, ex.GetType().Name);
                    ResetConnection();
                }
            }
            throw lastError ?? new InvalidOperationException("TTS 合成失败");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TTS 合成最终失败");
            OnError?.Invoke($"合成失败: {ex.Message}");
            _isInitialized = false;
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<byte[]> SynthesizeInternalAsync(string text, string voiceName,
        string rate, string volume, CancellationToken ct)
    {
        // 发送配置请求
        var configMsg = BuildConfigMessage();
        await _webSocket!.SendAsync(configMsg, WebSocketMessageType.Text, true, ct);

        // 构建 SSML
        var ssml = BuildSsml(text, voiceName, rate, volume);
        var requestId = Guid.NewGuid().ToString("N")[..32];

        // 发送 SSML 请求
        var ssmlMsg = $"X-RequestId:{requestId}\r\nContent-Type:application/ssml+xml\r\nPath:ssml\r\n\r\n{ssml}";
        var ssmlBytes = Encoding.UTF8.GetBytes(ssmlMsg);
        await _webSocket.SendAsync(ssmlBytes, WebSocketMessageType.Text, true, ct);

        // 接收音频数据
        var audioData = new List<byte>();
        var buffer = new byte[65536];

        while (!ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await _webSocket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("连接已关闭");
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var data = ms.ToArray();

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // 二进制消息包含音频数据
                if (data.Length > 2)
                {
                    var headerLen = (data[0] << 8) | data[1];
                    if (data.Length > headerLen + 2)
                    {
                        var audioChunk = data[(headerLen + 2)..];
                        audioData.AddRange(audioChunk);
                        _logger?.LogDebug("TTS 音频块: {Len} 字节, 累计 {Total}", audioChunk.Length, audioData.Count);
                    }
                }
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                var textMsg = Encoding.UTF8.GetString(data);
                _logger?.LogDebug("TTS 文本消息: {Path}", textMsg.Contains("Path:") ? textMsg[textMsg.IndexOf("Path:")..Math.Min(textMsg.Length, textMsg.IndexOf("Path:") + 30)] : textMsg[..Math.Min(50, textMsg.Length)]);
                if (textMsg.Contains("Path:turn.end"))
                {
                    break;
                }
                if (textMsg.Contains("Path:turn.start"))
                {
                    continue;
                }
            }
        }

        return audioData.ToArray();
    }

    private static byte[] BuildConfigMessage()
    {
        var requestId = Guid.NewGuid().ToString("N")[..32];
        var timestamp = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000' (Coordinated Universal Time)");

        var config = new StringBuilder();
        config.Append($"X-Timestamp:{timestamp}\r\n");
        config.Append("Content-Type:application/json; charset=utf-8\r\n");
        config.Append("Path:speech.config\r\n\r\n");
        config.Append(JsonSerializer.Serialize(new
        {
            context = new
            {
                synthesis = new
                {
                    audio = new
                    {
                        metadataoptions = new { sentenceBoundaryEnabled = "false", wordBoundaryEnabled = "false" },
                        outputFormat = "audio-24khz-48kbitrate-mono-mp3"
                    }
                }
            }
        }));

        return Encoding.UTF8.GetBytes(config.ToString());
    }

    private static string BuildSsml(string text, string voiceName, string rate, string volume)
    {
        text = System.Security.SecurityElement.Escape(text) ?? text;

        return $"""
            <speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='zh-CN'>
                <voice name='{voiceName}'>
                    <prosody rate='{rate}' volume='{volume}'>
                        {text}
                    </prosody>
                </voice>
            </speak>
            """;
    }

    /// <summary>
    /// 获取可用语音列表
    /// </summary>
    public static async Task<List<VoiceInfo>> GetVoiceListAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient();
        var response = await http.GetStringAsync(VoiceListUrl, ct);
        var voices = JsonSerializer.Deserialize<List<JsonElement>>(response);

        var result = new List<VoiceInfo>();
        if (voices == null) return result;

        foreach (var v in voices)
        {
            var locale = v.GetProperty("Locale").GetString() ?? "";
            if (!locale.StartsWith("zh-")) continue;

            result.Add(new VoiceInfo
            {
                Name = v.GetProperty("ShortName").GetString() ?? "",
                DisplayName = v.GetProperty("FriendlyName").GetString() ?? "",
                Locale = locale,
                Gender = v.GetProperty("Gender").GetString() ?? ""
            });
        }

        return result;
    }

    public void ResetConnection()
    {
        _isInitialized = false;
        _webSocket?.Dispose();
        _webSocket = null;
    }

    public void Dispose()
    {
        _webSocket?.Dispose();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
