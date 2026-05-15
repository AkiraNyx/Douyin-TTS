using DouyinTTS.App.ViewModels;
using DouyinTTS.Core.Live.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace DouyinTTS.App.Pages;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; }

    public HomePage()
    {
        InitializeComponent();
        ViewModel = new HomeViewModel(DispatcherQueue);
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.RefreshSettings();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HomeViewModel.IsConnected):
                UpdateConnectionUI();
                break;
            case nameof(HomeViewModel.DanmakuCount):
                DanmakuCountText.Text = $"弹幕: {ViewModel.DanmakuCount}";
                break;
            case nameof(HomeViewModel.GiftCount):
                GiftCountText.Text = $"礼物: {ViewModel.GiftCount}";
                break;
            case nameof(HomeViewModel.MemberCount):
                MemberCountText.Text = $"进场: {ViewModel.MemberCount}";
                break;
        }
    }

    private void UpdateConnectionUI()
    {
        var isConnected = ViewModel.IsConnected;
        ConnectButton.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        RoomInputBox.IsEnabled = !isConnected;
        StatusIndicator.Fill = isConnected
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 175, 80))
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
        StatusText.Text = ViewModel.ConnectionStatus;
        RoomTitleText.Text = ViewModel.RoomTitle;
        RoomTitleText.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        ViewerPanel.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        ViewerCountText.Text = ViewModel.ViewerCount.ToString();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RoomInput = RoomInputBox.Text;
        if (ViewModel.ConnectCommand.CanExecute(null))
            await ViewModel.ConnectCommand.ExecuteAsync(null);
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DisconnectCommand.CanExecute(null))
            await ViewModel.DisconnectCommand.ExecuteAsync(null);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Dispose();
    }
}
