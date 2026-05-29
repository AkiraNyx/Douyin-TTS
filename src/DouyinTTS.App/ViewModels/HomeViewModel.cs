using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DouyinTTS.Core.Live;
using DouyinTTS.Core.Live.Models;
using DouyinTTS.Core.TTS;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace DouyinTTS.App.ViewModels;

public partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly DouyinWebViewClient _webViewClient;
    private readonly EdgeTTSService _ttsService;
    private readonly TTSQueue _ttsQueue;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _connectCts;
    private bool _debugMode;

    /// <summary>
    /// 由 HomePage 注入，用于获取已初始化的 CoreWebView2
    /// </summary>
    public Func<Task<CoreWebView2>>? GetCoreWebView2 { get; set; }

    [ObservableProperty]
    private string _roomInput = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatus = "未连接";

    [ObservableProperty]
    private string _roomTitle = string.Empty;

    [ObservableProperty]
    private int _viewerCount;

    [ObservableProperty]
    private int _danmakuCount;

    [ObservableProperty]
    private int _giftCount;

    [ObservableProperty]
    private int _memberCount;

    [ObservableProperty]
    private int _queueCount;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private double _volume = 100;

    public ObservableCollection<LiveEventItem> Messages { get; } = [];
    public ObservableCollection<LiveEventItem> FilteredMessages { get; } = [];

    partial void OnFilterTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnVolumeChanged(double value)
    {
        _mediaPlayer.Volume = value / 100.0;
        ApplicationData.Current.LocalSettings.Values["VolumePercent"] = value;
    }

    private void ApplyFilter()
    {
        FilteredMessages.Clear();
        var filter = FilterText;
        if (string.IsNullOrEmpty(filter))
        {
            foreach (var m in Messages)
                FilteredMessages.Add(m);
        }
        else
        {
            foreach (var m in Messages)
            {
                if (m.Content.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    m.UserName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    m.Timestamp.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    FilteredMessages.Add(m);
            }
        }
    }

    public HomeViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _webViewClient = new DouyinWebViewClient(new DebugLogger());
        _ttsService = new EdgeTTSService();
        _ttsQueue = new TTSQueue(_ttsService);
        _mediaPlayer = new MediaPlayer { AutoPlay = false };
        _mediaPlayer.MediaEnded += (_, _) => _dispatcher.TryEnqueue(PlayNextFromQueue);
        _mediaPlayer.MediaFailed += (_, _) => _dispatcher.TryEnqueue(PlayNextFromQueue);

        LoadSettings();

        _webViewClient.OnEvent += OnLiveEvent;
        _webViewClient.OnStateChanged += OnStateChanged;
        _webViewClient.OnError += OnError;
        _ttsQueue.OnAudioData += OnAudioData;
        _ttsQueue.OnError += OnTtsError;
        _ttsQueue.OnDebug += OnTtsDebug;
    }

    private void LoadSettings()
    {
        var settings = ApplicationData.Current.LocalSettings;
        _debugMode = settings.Values["DebugMode"] as bool? ?? false;
        Volume = settings.Values["VolumePercent"] as double? ?? 100;
        _mediaPlayer.Volume = Volume / 100.0;

        var filterText = settings.Values["FilterKeywords"] as string ?? "";
        var config = new TTSConfig
        {
            VoiceName = settings.Values["VoiceName"] as string ?? "zh-CN-XiaoxiaoNeural",
            Rate = settings.Values["Rate"] as string ?? "+0%",
            Volume = settings.Values["Volume"] as string ?? "+0%",
            EnableDanmaku = settings.Values["EnableDanmaku"] as bool? ?? true,
            EnableGift = settings.Values["EnableGift"] as bool? ?? true,
            EnableMember = settings.Values["EnableMember"] as bool? ?? false,
            EnableLike = settings.Values["EnableLike"] as bool? ?? false,
            FilterKeywords = filterText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            MinMessageLength = settings.Values["MinMessageLength"] as int? ?? 1,
            MaxMessageLength = settings.Values["MaxMessageLength"] as int? ?? 100,
            DedupeWindowSeconds = settings.Values["DedupeWindowSeconds"] as int? ?? 3,
        };
        _ttsQueue.UpdateConfig(config);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected || string.IsNullOrWhiteSpace(RoomInput))
            return;

        if (GetCoreWebView2 == null)
        {
            ConnectionStatus = "错误: WebView2 未初始化";
            return;
        }

        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        try
        {
            ConnectionStatus = "连接中...";
            _ttsService.ResetConnection();

            var coreWebView2 = await GetCoreWebView2();
            var roomId = ExtractRoomId(RoomInput);
            await _webViewClient.ConnectAsync(coreWebView2, roomId, _connectCts.Token);
            _ttsQueue.Start();
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "连接已取消";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"连接失败: {ex.Message}";
        }
    }

    private static string ExtractRoomId(string input)
    {
        input = input.Trim();
        if (long.TryParse(input, out _) && input.Length > 0)
            return input;

        var match = Regex.Match(input, @"live\.douyin\.com/(\d+)");
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(input, @"douyin\.com/(\d+)");
        if (match.Success) return match.Groups[1].Value;

        return input;
    }

    [RelayCommand]
    private void CancelConnect()
    {
        _connectCts?.Cancel();
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            await _ttsQueue.StopAsync();
            _ttsService.ResetConnection();

            if (IsConnected)
                await _webViewClient.DisconnectAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"断开失败: {ex.Message}";
        }
        finally
        {
            IsConnected = false;
            ConnectionStatus = "未连接";
        }
    }

    // 调试事件中需要过滤的高频前缀
    private static readonly string[] NoisyDebugPrefixes = ["Frame#", "Msg#", "EmptyMethod#"];

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    private void OnLiveEvent(LiveEvent evt)
    {
        // 非调试模式下过滤 DebugEvent
        if (evt is DebugEvent && !_debugMode) return;

        // 调试模式下也过滤高频噪音事件
        if (evt is DebugEvent dbg2 && NoisyDebugPrefixes.Any(p => dbg2.Method.StartsWith(p)))
            return;

        _dispatcher.TryEnqueue(() =>
        {
            var item = new LiveEventItem
            {
                Type = evt.Type,
                UserName = evt.UserName,
                Content = evt switch
                {
                    DanmakuEvent d => d.Content,
                    GiftEvent g => $"送出 {g.GiftName} x{g.GiftCount}",
                    MemberEvent => "进入直播间",
                    LikeEvent l => $"点了 {l.Count} 个赞",
                    SystemEvent s => s.Message,
                    DebugEvent dbg => $"[{dbg.Method}] {dbg.PayloadSize}B{(dbg.Error != null ? $" {dbg.Error}" : "")}",
                    _ => string.Empty
                },
                Timestamp = evt.Timestamp.ToString("HH:mm:ss")
            };

            // RoomStatsEvent 只更新在线人数，不显示在列表中
            if (evt is not RoomStatsEvent)
            {
                Messages.Insert(0, item);

                // 限制列表大小
                while (Messages.Count > 500)
                    Messages.RemoveAt(Messages.Count - 1);

                // 更新过滤列表
                var filter = FilterText;
                if (string.IsNullOrEmpty(filter) ||
                    item.Content.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    item.UserName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    FilteredMessages.Insert(0, item);
                    while (FilteredMessages.Count > 500)
                        FilteredMessages.RemoveAt(FilteredMessages.Count - 1);
                }
            }

            // 更新统计
            switch (evt)
            {
                case DanmakuEvent:
                    DanmakuCount++;
                    _ttsQueue.EnqueueDanmaku(evt.UserName, ((DanmakuEvent)evt).Content);
                    break;
                case GiftEvent g:
                    GiftCount++;
                    _ttsQueue.EnqueueGift(evt.UserName, g.GiftName, g.GiftCount);
                    break;
                case MemberEvent:
                    MemberCount++;
                    _ttsQueue.EnqueueMember(evt.UserName);
                    break;
                case LikeEvent l:
                    _ttsQueue.EnqueueLike(evt.UserName, l.Count);
                    break;
                case RoomStatsEvent rs when rs.ViewerCount > 0:
                    ViewerCount = rs.ViewerCount;
                    break;
            }

            QueueCount = _ttsQueue.Count;
        });
    }

    private void OnStateChanged(ConnectionState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsConnected = state == ConnectionState.Connected;

            // 断开连接时保留错误信息，不覆盖
            if (state == ConnectionState.Disconnected && ConnectionStatus.Contains("失败"))
                return;

            ConnectionStatus = state switch
            {
                ConnectionState.Disconnected => "未连接",
                ConnectionState.Connecting => "连接中...",
                ConnectionState.Connected => $"已连接 (room_id: {_webViewClient.DebugRoomId})",
                ConnectionState.Reconnecting => "重连中...",
                _ => "未知状态"
            };
        });
    }

    private void OnError(string error)
    {
        _dispatcher.TryEnqueue(() =>
        {
            Messages.Insert(0, new LiveEventItem
            {
                Type = LiveEventType.System,
                UserName = "系统",
                Content = error,
                Timestamp = DateTime.Now.ToString("HH:mm:ss")
            });
        });
    }

    private void OnTtsError(string error)
    {
        System.Diagnostics.Debug.WriteLine($"[TTS] 错误: {error}");
        _dispatcher.TryEnqueue(() =>
        {
            Messages.Insert(0, new LiveEventItem
            {
                Type = LiveEventType.System,
                UserName = "TTS",
                Content = error,
                Timestamp = DateTime.Now.ToString("HH:mm:ss")
            });
        });
    }

    private void OnTtsDebug(string message)
    {
        if (!_debugMode) return;
        _dispatcher.TryEnqueue(() =>
        {
            Messages.Insert(0, new LiveEventItem
            {
                Type = LiveEventType.Debug,
                UserName = "TTS",
                Content = message,
                Timestamp = DateTime.Now.ToString("HH:mm:ss")
            });
            while (Messages.Count > 500)
                Messages.RemoveAt(Messages.Count - 1);
        });
    }

    private readonly Queue<byte[]> _audioQueue = new();
    private bool _isPlaying;
    private string? _currentAudioFile;

    private void OnAudioData(byte[] audioData)
    {
        System.Diagnostics.Debug.WriteLine($"[TTS] 收到音频数据: {audioData.Length} 字节");

        _dispatcher.TryEnqueue(() =>
        {
            _audioQueue.Enqueue(audioData);
            if (!_isPlaying)
                PlayNextFromQueue();
        });
    }

    private void PlayNextFromQueue()
    {
        while (_audioQueue.Count > 0)
        {
            try
            {
                _isPlaying = true;
                var audioData = _audioQueue.Dequeue();

                // 先释放旧 Source 的文件句柄，再删除临时文件
                var oldFile = _currentAudioFile;
                _mediaPlayer.Source = null;
                if (oldFile != null)
                {
                    try { File.Delete(oldFile); } catch { }
                }

                // 写入临时文件
                var tempFile = Path.Combine(Path.GetTempPath(), $"douyin_tts_{Guid.NewGuid():N}.mp3");
                File.WriteAllBytes(tempFile, audioData);
                _currentAudioFile = tempFile;

                // 播放
                _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri($"file:///{tempFile}"));
                _mediaPlayer.Play();
                return; // 播放成功，等 MediaEnded/MediaFailed 回调
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS] 播放失败: {ex.Message}");
                // 继续尝试队列中的下一个
            }
        }

        _isPlaying = false;
    }

    public void RefreshSettings()
    {
        LoadSettings();
    }

    public bool DebugMode
    {
        get => _debugMode;
        set
        {
            if (SetProperty(ref _debugMode, value))
                ApplicationData.Current.LocalSettings.Values["DebugMode"] = value;
        }
    }

    public void Dispose()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _webViewClient.Dispose();
        _ttsService.Dispose();
        _ttsQueue.Dispose();
        _mediaPlayer.Dispose();
        if (_currentAudioFile != null)
        {
            try { File.Delete(_currentAudioFile); } catch { }
        }
        CleanupTempFiles();
        GC.SuppressFinalize(this);
    }

    private static void CleanupTempFiles()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            var files = Directory.GetFiles(tempDir, "douyin_tts_*.mp3");
            foreach (var file in files)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}

internal class DebugLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);
        System.Diagnostics.Debug.WriteLine($"[{logLevel}] {msg}");
    }
}

public class LiveEventItem
{
    public LiveEventType Type { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;

    public string TypeIcon => Type switch
    {
        LiveEventType.Danmaku => "",
        LiveEventType.Gift => "",
        LiveEventType.Member => "",
        LiveEventType.Like => "",
        _ => ""
    };

    public string TypeColor => Type switch
    {
        LiveEventType.Danmaku => "#FF2196F3",
        LiveEventType.Gift => "#FFFF9800",
        LiveEventType.Member => "#FF4CAF50",
        LiveEventType.Like => "#FFE91E63",
        _ => UserName == "TTS" ? "#FFE53935" : "#FF9E9E9E"
    };

    public Microsoft.UI.Xaml.Media.SolidColorBrush TypeBrush
    {
        get
        {
            var c = TypeColor;
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(
                byte.Parse(c[1..3], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(c[3..5], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(c[5..7], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(c[7..9], System.Globalization.NumberStyles.HexNumber)));
        }
    }
}
