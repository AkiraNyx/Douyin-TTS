using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DouyinTTS.Core.TTS;
using Windows.Storage;

namespace DouyinTTS.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private string _selectedVoice = "zh-CN-XiaoxiaoNeural";

    [ObservableProperty]
    private double _rate;

    [ObservableProperty]
    private double _volume = 100;

    [ObservableProperty]
    private bool _enableDanmaku = true;

    [ObservableProperty]
    private bool _enableGift = true;

    [ObservableProperty]
    private bool _enableMember;

    [ObservableProperty]
    private bool _enableLike;

    [ObservableProperty]
    private string _filterKeywordsText = string.Empty;

    [ObservableProperty]
    private int _minMessageLength = 1;

    [ObservableProperty]
    private int _maxMessageLength = 100;

    [ObservableProperty]
    private int _dedupeWindowSeconds = 3;

    public ObservableCollection<VoiceInfo> Voices { get; } = [];

    public List<RateOption> RateOptions { get; } =
    [
        new("-50%", "慢速"),
        new("-25%", "较慢"),
        new("+0%", "正常"),
        new("+25%", "较快"),
        new("+50%", "快速"),
        new("+100%", "极快"),
    ];

    [ObservableProperty]
    private RateOption _selectedRate;

    public SettingsViewModel()
    {
        _selectedRate = RateOptions[2]; // 正常
        LoadSettings();
        _ = LoadVoicesAsync();
    }

    private void LoadSettings()
    {
        var settings = ApplicationData.Current.LocalSettings;

        SelectedVoice = settings.Values["VoiceName"] as string ?? "zh-CN-XiaoxiaoNeural";

        var rateStr = settings.Values["Rate"] as string ?? "+0%";
        SelectedRate = RateOptions.FirstOrDefault(r => r.Value == rateStr) ?? RateOptions[2];

        Volume = settings.Values["VolumePercent"] as double? ?? 100;
        EnableDanmaku = settings.Values["EnableDanmaku"] as bool? ?? true;
        EnableGift = settings.Values["EnableGift"] as bool? ?? true;
        EnableMember = settings.Values["EnableMember"] as bool? ?? false;
        EnableLike = settings.Values["EnableLike"] as bool? ?? false;
        FilterKeywordsText = settings.Values["FilterKeywords"] as string ?? string.Empty;
        MinMessageLength = settings.Values["MinMessageLength"] as int? ?? 1;
        MaxMessageLength = settings.Values["MaxMessageLength"] as int? ?? 100;
        DedupeWindowSeconds = settings.Values["DedupeWindowSeconds"] as int? ?? 3;
    }

    [RelayCommand]
    private async Task LoadVoicesAsync()
    {
        try
        {
            var voices = await EdgeTTSService.GetVoiceListAsync();
            Voices.Clear();
            foreach (var v in voices)
                Voices.Add(v);
        }
        catch
        {
            // 加载失败时使用默认列表
            foreach (var v in VoiceInfo.GetChineseVoices())
                Voices.Add(v);
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var settings = ApplicationData.Current.LocalSettings;
        settings.Values["VoiceName"] = SelectedVoice;
        settings.Values["Rate"] = SelectedRate.Value;
        settings.Values["Volume"] = $"+{(Volume - 100):F0}%";
        settings.Values["VolumePercent"] = Volume;
        settings.Values["EnableDanmaku"] = EnableDanmaku;
        settings.Values["EnableGift"] = EnableGift;
        settings.Values["EnableMember"] = EnableMember;
        settings.Values["EnableLike"] = EnableLike;
        settings.Values["FilterKeywords"] = FilterKeywordsText;
        settings.Values["MinMessageLength"] = MinMessageLength;
        settings.Values["MaxMessageLength"] = MaxMessageLength;
        settings.Values["DedupeWindowSeconds"] = DedupeWindowSeconds;
    }

    public TTSConfig GetTTSConfig()
    {
        return new TTSConfig
        {
            VoiceName = SelectedVoice,
            Rate = SelectedRate.Value,
            Volume = $"+{(Volume - 100):F0}%",
            EnableDanmaku = EnableDanmaku,
            EnableGift = EnableGift,
            EnableMember = EnableMember,
            EnableLike = EnableLike,
            FilterKeywords = FilterKeywordsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            MinMessageLength = MinMessageLength,
            MaxMessageLength = MaxMessageLength,
            DedupeWindowSeconds = DedupeWindowSeconds,
        };
    }
}

public class RateOption
{
    public string Value { get; }
    public string Label { get; }

    public RateOption(string value, string label)
    {
        Value = value;
        Label = label;
    }

    public override string ToString() => Label;
}
