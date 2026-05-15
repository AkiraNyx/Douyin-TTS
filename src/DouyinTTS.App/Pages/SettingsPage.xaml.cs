using DouyinTTS.Core.TTS;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace DouyinTTS.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
        _ = LoadVoicesAsync();
    }

    private void LoadSettings()
    {
        var settings = ApplicationData.Current.LocalSettings;
        VolumeSlider.Value = settings.Values["VolumePercent"] as double? ?? 100;
        DanmakuToggle.IsOn = settings.Values["EnableDanmaku"] as bool? ?? true;
        GiftToggle.IsOn = settings.Values["EnableGift"] as bool? ?? true;
        MemberToggle.IsOn = settings.Values["EnableMember"] as bool? ?? false;
        LikeToggle.IsOn = settings.Values["EnableLike"] as bool? ?? false;
        FilterKeywordsBox.Text = settings.Values["FilterKeywords"] as string ?? string.Empty;
        DedupeWindowBox.Value = settings.Values["DedupeWindowSeconds"] as int? ?? 3;
    }

    private async Task LoadVoicesAsync()
    {
        try
        {
            var voices = await EdgeTTSService.GetVoiceListAsync();
            VoiceComboBox.ItemsSource = voices;
            var savedVoice = ApplicationData.Current.LocalSettings.Values["VoiceName"] as string ?? "zh-CN-XiaoxiaoNeural";
            var index = voices.FindIndex(v => v.Name == savedVoice);
            if (index >= 0) VoiceComboBox.SelectedIndex = index;
        }
        catch
        {
            var voices = VoiceInfo.GetChineseVoices();
            VoiceComboBox.ItemsSource = voices;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = ApplicationData.Current.LocalSettings;
        if (VoiceComboBox.SelectedItem is VoiceInfo voice)
            settings.Values["VoiceName"] = voice.Name;
        settings.Values["Volume"] = $"+{(VolumeSlider.Value - 100):F0}%";
        settings.Values["VolumePercent"] = VolumeSlider.Value;
        settings.Values["EnableDanmaku"] = DanmakuToggle.IsOn;
        settings.Values["EnableGift"] = GiftToggle.IsOn;
        settings.Values["EnableMember"] = MemberToggle.IsOn;
        settings.Values["EnableLike"] = LikeToggle.IsOn;
        settings.Values["FilterKeywords"] = FilterKeywordsBox.Text;
        settings.Values["DedupeWindowSeconds"] = (int)DedupeWindowBox.Value;

        var dialog = new ContentDialog
        {
            Title = "保存成功",
            Content = "设置已保存，下次连接时生效。",
            CloseButtonText = "确定",
            XamlRoot = Content.XamlRoot
        };
        _ = dialog.ShowAsync();
    }
}
