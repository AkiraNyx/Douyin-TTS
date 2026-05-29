using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
    private CancellationTokenSource _cts = new();
    private Task? _processTask;
    private readonly ConcurrentDictionary<string, DateTime> _recentMessages = new();
    private TTSConfig _config;
    private static readonly Regex EmojiRegex = new(@"\[.{1,10}\]", RegexOptions.Compiled);

    public event Action<byte[]>? OnAudioData;
    public event Action<string>? OnError;
    public event Action<string>? OnDebug;
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

        // 过滤抖音表情 [捂脸] [大笑] 等
        content = EmojiRegex.Replace(content, "").Trim();
        if (string.IsNullOrWhiteSpace(content)) return false;
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
            ? $"{userName}送出了{count}个{giftName}"
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
            {
                OnDebug?.Invoke($"[TTS] 去重: {text[..Math.Min(20, text.Length)]}");
                return false;
            }
        }

        _recentMessages[key] = now;

        // 清理过期记录
        CleanupRecentMessages(now);

        // 尝试入队，队满时丢弃
        var posted = _queue.Post((text, type));
        OnDebug?.Invoke($"[TTS] 入队{(posted ? "" : "失败(队满)")}: {text[..Math.Min(20, text.Length)]} 队列={_queue.Count}");
        return posted;
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
        _cts = new CancellationTokenSource();
        _processTask = Task.Run(ProcessLoopAsync);
    }

    /// <summary>
    /// 停止处理
    /// </summary>
    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_processTask != null)
        {
            try { await _processTask; }
            catch (OperationCanceledException) { }
            _processTask = null;
        }
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
                    var preview = text[..Math.Min(30, text.Length)];
                    OnDebug?.Invoke($"[TTS] 开始合成: {preview} 队列剩余={_queue.Count}");
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                    var audioData = await _ttsService.SynthesizeAsync(
                        text,
                        _config.VoiceName,
                        _config.Rate,
                        _config.Volume,
                        timeoutCts.Token);

                    sw.Stop();
                    OnDebug?.Invoke($"[TTS] 合成完成: {audioData.Length}B {sw.ElapsedMilliseconds}ms");

                    if (audioData.Length > 0)
                        OnAudioData?.Invoke(audioData);
                }
                catch (OperationCanceledException)
                {
                    OnError?.Invoke("[TTS] 合成超时 (30秒)");
                    _ttsService.ResetConnection();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "TTS 播放失败: {Text}", text);
                    OnError?.Invoke($"播放失败: [{ex.GetType().Name}] {ex.Message}");
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
        if (_processTask is { IsCompleted: false })
        {
            try { _processTask.Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException) { } // 仅忽略取消导致的异常
        }
        _processTask = null;
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
