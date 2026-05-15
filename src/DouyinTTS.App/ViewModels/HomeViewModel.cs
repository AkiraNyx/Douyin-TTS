using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DouyinTTS.Core.Live;
using DouyinTTS.Core.Live.Models;
using DouyinTTS.Core.TTS;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace DouyinTTS.App.ViewModels;

public partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly DouyinLiveClient _liveClient;
    private readonly EdgeTTSService _ttsService;
    private readonly TTSQueue _ttsQueue;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherQueue _dispatcher;

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

    public ObservableCollection<LiveEventItem> Messages { get; } = [];

    public HomeViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _liveClient = new DouyinLiveClient();
        _ttsService = new EdgeTTSService();
        _ttsQueue = new TTSQueue(_ttsService);
        _mediaPlayer = new MediaPlayer { AutoPlay = false };

        LoadSettings();

        _liveClient.OnEvent += OnLiveEvent;
        _liveClient.OnStateChanged += OnStateChanged;
        _liveClient.OnError += OnError;
        _ttsQueue.OnAudioData += OnAudioData;
        _ttsQueue.OnError += OnTtsError;
    }

    private void LoadSettings()
    {
        var settings = ApplicationData.Current.LocalSettings;
        var config = new TTSConfig
        {
            VoiceName = settings.Values["VoiceName"] as string ?? "zh-CN-XiaoxiaoNeural",
            Rate = settings.Values["Rate"] as string ?? "+0%",
            Volume = settings.Values["Volume"] as string ?? "+0%",
            EnableDanmaku = settings.Values["EnableDanmaku"] as bool? ?? true,
            EnableGift = settings.Values["EnableGift"] as bool? ?? true,
            EnableMember = settings.Values["EnableMember"] as bool? ?? false,
            EnableLike = settings.Values["EnableLike"] as bool? ?? false,
        };
        _ttsQueue.UpdateConfig(config);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected || string.IsNullOrWhiteSpace(RoomInput))
            return;

        try
        {
            ConnectionStatus = "连接中...";
            await _liveClient.ConnectAsync(RoomInput);
            _ttsQueue.Start();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"连接失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (!IsConnected) return;

        await _ttsQueue.StopAsync();
        await _liveClient.DisconnectAsync();
    }

    private void OnLiveEvent(LiveEvent evt)
    {
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
                    _ => string.Empty
                },
                Timestamp = evt.Timestamp.ToString("HH:mm:ss")
            };

            Messages.Insert(0, item);

            // 限制列表大小
            while (Messages.Count > 200)
                Messages.RemoveAt(Messages.Count - 1);

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
            }

            QueueCount = _ttsQueue.Count;
        });
    }

    private void OnStateChanged(ConnectionState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsConnected = state == ConnectionState.Connected;
            ConnectionStatus = state switch
            {
                ConnectionState.Disconnected => "未连接",
                ConnectionState.Connecting => "连接中...",
                ConnectionState.Connected => "已连接",
                ConnectionState.Reconnecting => "重连中...",
                _ => "未知状态"
            };

            if (state == ConnectionState.Connected && _liveClient.Room != null)
            {
                RoomTitle = _liveClient.Room.Title;
                ViewerCount = _liveClient.Room.ViewerCount;
            }
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
        System.Diagnostics.Debug.WriteLine($"TTS 错误: {error}");
    }

    private void OnAudioData(byte[] audioData)
    {
        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                // 将 MP3 数据写入临时文件播放
                var tempFile = Path.Combine(Path.GetTempPath(), $"douyin_tts_{Guid.NewGuid():N}.mp3");
                File.WriteAllBytes(tempFile, audioData);

                var source = MediaSource.CreateFromUri(new Uri($"file:///{tempFile}"));
                _mediaPlayer.Source = source;
                _mediaPlayer.Play();

                // 播放完成后清理
                _mediaPlayer.MediaEnded += (_, _) =>
                {
                    try { File.Delete(tempFile); } catch { }
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"音频播放失败: {ex.Message}");
            }
        });
    }

    public void RefreshSettings()
    {
        LoadSettings();
    }

    public void Dispose()
    {
        _liveClient.Dispose();
        _ttsService.Dispose();
        _ttsQueue.Dispose();
        _mediaPlayer.Dispose();
        GC.SuppressFinalize(this);
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
        LiveEventType.Like => "",
        _ => ""
    };

    public string TypeColor => Type switch
    {
        LiveEventType.Danmaku => "#FF2196F3",
        LiveEventType.Gift => "#FFFF9800",
        LiveEventType.Member => "#FF4CAF50",
        LiveEventType.Like => "#FFE91E63",
        _ => "#FF9E9E9E"
    };
}
