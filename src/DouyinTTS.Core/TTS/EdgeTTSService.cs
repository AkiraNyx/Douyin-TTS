using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DouyinTTS.Core.TTS;

public class EdgeTTSService : IDisposable
{
    private readonly ILogger? _logger;
    private ClientWebSocket? _webSocket;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _isInitialized;

    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string VoiceListUrl = "https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/voices/list?trustedclienttoken=6A5AA1D4EAFF4E9FB37E23D68491D6F4";

    public event Action<byte[]>? OnAudioData;
    public event Action? OnSynthesisComplete;
    public event Action<string>? OnError;

    public EdgeTTSService(ILogger? logger = null)
    {
        _logger = logger;
    }

    private async Task EnsureConnectedAsync()
    {
        if (_isInitialized && _webSocket?.State == WebSocketState.Open)
            return;

        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        _webSocket.Options.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

        await _webSocket.ConnectAsync(new Uri(WssUrl), CancellationToken.None);
        _isInitialized = true;
    }

    /// <summary>
    /// 使用 Edge TTS 合成语音
    /// </summary>
    public async Task<byte[]> SynthesizeAsync(string text, string voiceName = "zh-CN-XiaoxiaoNeural",
        string rate = "+0%", string volume = "+0%", CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync();
            return await SynthesizeInternalAsync(text, voiceName, rate, volume, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TTS 合成失败");
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
                // 前 2 字节是头长度
                if (data.Length > 2)
                {
                    var headerLen = (data[0] << 8) | data[1];
                    if (data.Length > headerLen + 2)
                    {
                        var audioChunk = data[(headerLen + 2)..];
                        audioData.AddRange(audioChunk);
                        OnAudioData?.Invoke(audioChunk);
                    }
                }
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                var textMsg = Encoding.UTF8.GetString(data);
                if (textMsg.Contains("Path:turn.end"))
                {
                    OnSynthesisComplete?.Invoke();
                    break;
                }
                if (textMsg.Contains("Path:turn.start"))
                {
                    // 开始接收
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
        config.AppendLine($"X-Timestamp:{timestamp}");
        config.AppendLine($"Content-Type:application/json; charset=utf-8");
        config.AppendLine($"Path:speech.config");
        config.AppendLine();
        config.AppendLine(JsonSerializer.Serialize(new
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
        // 转义 XML 特殊字符
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

    public void Dispose()
    {
        _webSocket?.Dispose();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
