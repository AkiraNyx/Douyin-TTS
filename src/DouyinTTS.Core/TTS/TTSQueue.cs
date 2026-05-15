using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

namespace DouyinTTS.Core.TTS;

public class TTSConfig
{
    public string VoiceName { get; set; } = "zh-CN-XiaoxiaoNeural";
    public string Rate { get; set; } = "+0%";
    public string Volume { get; set; } = "+0%";
    public int MaxQueueSize { get; set; } = 20;
    public bool EnableDanmaku { get; set; } = true;
    public bool EnableGift { get; set; } = true;
    public bool EnableMember { get; set; } = false;
    public bool EnableLike { get; set; } = false;
    public List<string> FilterKeywords { get; set; } = [];
    public int MinMessageLength { get; set; } = 1;
    public int MaxMessageLength { get; set; } = 100;
    public int DedupeWindowSeconds { get; set; } = 3;
}

public class TTSQueue : IDisposable
{
    private readonly EdgeTTSService _ttsService;
    private readonly ILogger? _logger;
    private readonly BufferBlock<(string text, string type)> _queue;
    private readonly CancellationTokenSource _cts = new();
    private Task? _processTask;
    private readonly ConcurrentDictionary<string, DateTime> _recentMessages = new();
    private TTSConfig _config;

    public event Action<byte[]>? OnAudioData;
    public event Action<string>? OnError;
    public bool IsPlaying { get; private set; }

    public TTSQueue(EdgeTTSService ttsService, TTSConfig? config = null, ILogger? logger = null)
    {
        _ttsService = ttsService;
        _logger = logger;
        _config = config ?? new TTSConfig();
        _queue = new BufferBlock<(string text, string type)>(new DataflowBlockOptions
        {
            BoundedCapacity = _config.MaxQueueSize
        });
    }

    public void UpdateConfig(TTSConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// 添加弹幕消息到队列
    /// </summary>
    public bool EnqueueDanmaku(string userName, string content)
    {
        if (!_config.EnableDanmaku) return false;
        if (!PassFilter(content)) return false;

        var text = $"{userName}说：{content}";
        return Enqueue(text, "danmaku");
    }

    /// <summary>
    /// 添加礼物消息到队列
    /// </summary>
    public bool EnqueueGift(string userName, string giftName, int count)
    {
        if (!_config.EnableGift) return false;

        var text = count > 1
            ? $"{userName}送出了{giftName}乘以{count}"
            : $"{userName}送出了{giftName}";
        return Enqueue(text, "gift");
    }

    /// <summary>
    /// 添加进场消息到队列
    /// </summary>
    public bool EnqueueMember(string userName)
    {
        if (!_config.EnableMember) return false;

        var text = $"{userName}进入了直播间";
        return Enqueue(text, "member");
    }

    /// <summary>
    /// 添加点赞消息到队列
    /// </summary>
    public bool EnqueueLike(string userName, int count)
    {
        if (!_config.EnableLike) return false;

        var text = count > 1 ? $"{userName}点了{count}个赞" : $"{userName}点了赞";
        return Enqueue(text, "like");
    }

    private bool Enqueue(string text, string type)
    {
        // 去重检查
        var key = $"{type}:{text}";
        var now = DateTime.UtcNow;

        if (_recentMessages.TryGetValue(key, out var lastTime))
        {
            if ((now - lastTime).TotalSeconds < _config.DedupeWindowSeconds)
                return false;
        }

        _recentMessages[key] = now;

        // 清理过期记录
        CleanupRecentMessages(now);

        // 尝试入队，队满时丢弃
        return _queue.Post((text, type));
    }

    private bool PassFilter(string content)
    {
        if (content.Length < _config.MinMessageLength) return false;
        if (content.Length > _config.MaxMessageLength) return false;

        if (_config.FilterKeywords.Count > 0)
        {
            foreach (var keyword in _config.FilterKeywords)
            {
                if (content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        return true;
    }

    private void CleanupRecentMessages(DateTime now)
    {
        foreach (var kvp in _recentMessages)
        {
            if ((now - kvp.Value).TotalSeconds > _config.DedupeWindowSeconds * 2)
                _recentMessages.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    /// 启动处理循环
    /// </summary>
    public void Start()
    {
        if (_processTask != null) return;
        _processTask = Task.Run(ProcessLoopAsync);
    }

    /// <summary>
    /// 停止处理
    /// </summary>
    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_processTask != null)
            try { await _processTask; } catch { }
        _processTask = null;
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var (text, type) = await _queue.ReceiveAsync(_cts.Token);

                try
                {
                    IsPlaying = true;
                    var audioData = await _ttsService.SynthesizeAsync(
                        text,
                        _config.VoiceName,
                        _config.Rate,
                        _config.Volume,
                        _cts.Token);

                    if (audioData.Length > 0)
                        OnAudioData?.Invoke(audioData);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "TTS 播放失败: {Text}", text);
                    OnError?.Invoke($"播放失败: {ex.Message}");
                }
                finally
                {
                    IsPlaying = false;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Clear()
    {
        while (_queue.TryReceive(out _)) { }
    }

    public int Count => _queue.Count;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
