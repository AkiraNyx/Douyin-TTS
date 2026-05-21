using DouyinTTS.Core.TTS;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage;

namespace DouyinTTS.App.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading;
    private readonly DispatcherQueue? _dispatcher;

    public SettingsPage()
    {
        try
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            InitializeComponent();
            LoadSettings();
            _ = LoadVoicesAsync();
            AttachHandlers();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsPage] 初始化失败: {ex}");
        }
    }

    private void AttachHandlers()
    {
        VoiceComboBox.SelectionChanged += (_, _) => { if (!_loading) DeferredSave(); };
        VolumeSlider.ValueChanged += (_, e) => { if (!_loading) { VolumeText.Text = $"{(int)e.NewValue}%"; DeferredSave(); } };
        DanmakuToggle.Toggled += (_, _) => { if (!_loading) DeferredSave(); };
        GiftToggle.Toggled += (_, _) => { if (!_loading) DeferredSave(); };
        MemberToggle.Toggled += (_, _) => { if (!_loading) DeferredSave(); };
        LikeToggle.Toggled += (_, _) => { if (!_loading) DeferredSave(); };
        FilterKeywordsBox.TextChanged += (_, _) => { if (!_loading) DeferredSave(); };
        DedupeWindowBox.ValueChanged += (_, _) => { if (!_loading) DeferredSave(); };
        DebugModeToggle.Toggled += (_, _) => { if (!_loading) DeferredSave(); };
    }

    private void DeferredSave()
    {
        _dispatcher?.TryEnqueue(SaveSettings);
    }

    private void LoadSettings()
    {
        _loading = true;
        var settings = ApplicationData.Current.LocalSettings;
        VolumeSlider.Value = settings.Values["VolumePercent"] as double? ?? 100;
        VolumeText.Text = $"{(int)VolumeSlider.Value}%";
        DanmakuToggle.IsOn = settings.Values["EnableDanmaku"] as bool? ?? true;
        GiftToggle.IsOn = settings.Values["EnableGift"] as bool? ?? true;
        MemberToggle.IsOn = settings.Values["EnableMember"] as bool? ?? false;
        LikeToggle.IsOn = settings.Values["EnableLike"] as bool? ?? false;
        FilterKeywordsBox.Text = settings.Values["FilterKeywords"] as string ?? string.Empty;
        DedupeWindowBox.Value = settings.Values["DedupeWindowSeconds"] as int? ?? 3;
        DebugModeToggle.IsOn = settings.Values["DebugMode"] as bool? ?? false;
        _loading = false;
    }

    private async Task LoadVoicesAsync()
    {
        try
        {
            var voices = await EdgeTTSService.GetVoiceListAsync();
            _loading = true;
            VoiceComboBox.ItemsSource = voices;
            var savedVoice = ApplicationData.Current.LocalSettings.Values["VoiceName"] as string ?? "zh-CN-XiaoxiaoNeural";
            var index = voices.FindIndex(v => v.Name == savedVoice);
            if (index >= 0) VoiceComboBox.SelectedIndex = index;
            _loading = false;
        }
        catch
        {
            _loading = true;
            VoiceComboBox.ItemsSource = VoiceInfo.GetChineseVoices();
            _loading = false;
        }
    }

    private void SaveSettings()
    {
        var settings = ApplicationData.Current.LocalSettings;
        if (VoiceComboBox.SelectedItem is VoiceInfo voice)
            settings.Values["VoiceName"] = voice.Name;
        settings.Values["Volume"] = $"{(VolumeSlider.Value - 100):+0;-0;+0}%";
        settings.Values["VolumePercent"] = VolumeSlider.Value;
        settings.Values["EnableDanmaku"] = DanmakuToggle.IsOn;
        settings.Values["EnableGift"] = GiftToggle.IsOn;
        settings.Values["EnableMember"] = MemberToggle.IsOn;
        settings.Values["EnableLike"] = LikeToggle.IsOn;
        settings.Values["FilterKeywords"] = FilterKeywordsBox.Text;
        settings.Values["DedupeWindowSeconds"] = (int)DedupeWindowBox.Value;
        settings.Values["DebugMode"] = DebugModeToggle.IsOn;
        App.ViewModel?.RefreshSettings();
    }
}
