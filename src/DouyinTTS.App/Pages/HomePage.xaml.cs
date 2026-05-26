using System.Diagnostics;
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

        if (App.ViewModel == null)
            App.ViewModel = new HomeViewModel(DispatcherQueue);

        ViewModel = App.ViewModel;
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.GetCoreWebView2 = InitializeWebView2Async;

        // 同步当前状态到 UI
        UpdateConnectionUI();
        DanmakuCountText.Text = $"弹幕: {ViewModel.DanmakuCount}";
        GiftCountText.Text = $"礼物: {ViewModel.GiftCount}";
        MemberCountText.Text = $"进场: {ViewModel.MemberCount}";
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
            case nameof(HomeViewModel.ConnectionStatus):
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

    private bool _isConnecting;

    private void UpdateConnectionUI()
    {
        var isConnected = ViewModel.IsConnected;
        ConnectButton.Visibility = isConnected || _isConnecting ? Visibility.Collapsed : Visibility.Visible;
        ConnectButton.IsEnabled = !isConnected && !_isConnecting;
        DisconnectButton.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = _isConnecting && !isConnected ? Visibility.Visible : Visibility.Collapsed;
        RoomInputBox.IsEnabled = !isConnected && !_isConnecting;

        StatusIndicator.Fill = isConnected
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 175, 80))
            : _isConnecting
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 152, 0))
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
        StatusText.Text = ViewModel.ConnectionStatus;

        // 有错误信息时显示复制按钮
        var status = ViewModel.ConnectionStatus;
        var hasError = status.Contains("失败") || status.Contains("错误") || status.Contains("超时");
        CopyStatusButton.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;

        // 连接成功后显示"显示 WebView"按钮
        ShowWebViewButton.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;

        RoomTitleText.Text = ViewModel.RoomTitle;
        RoomTitleText.Visibility = isConnected && !string.IsNullOrEmpty(ViewModel.RoomTitle) ? Visibility.Visible : Visibility.Collapsed;
        ViewerPanel.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        ViewerCountText.Text = ViewModel.ViewerCount.ToString();

        // 连接成功后更新 WebView 提示
        if (isConnected && WebViewOverlay.Visibility == Visibility.Visible)
        {
            WebViewInfoBar.Message = "已连接成功！如果需要登录，请完成验证后点击下方按钮隐藏窗口";
        }
    }

    private CancellationTokenSource? _timerCts;

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var input = RoomInputBox.Text?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            StatusText.Text = "请输入房间号";
            return;
        }

        ViewModel.RoomInput = input;
        _isConnecting = true;
        ConnectButton.IsEnabled = false;
        RoomInputBox.IsEnabled = false;
        UpdateConnectionUI();

        // 启动计时器
        _timerCts?.Dispose();
        _timerCts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var timerTask = Task.Run(async () =>
        {
            try
            {
                while (!_timerCts.Token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatusText.Text = $"连接中... {sw.Elapsed.TotalSeconds:F0}秒";
                    });
                    await Task.Delay(1000, _timerCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        try
        {
            // 连接时自动显示 WebView，提示用户登录
            WebViewOverlay.Visibility = Visibility.Visible;
            MessagePanel.Visibility = Visibility.Collapsed;
            WebViewInfoBar.Message = "正在连接直播间，请完成登录或验证码验证...";

            await ViewModel.ConnectCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            StatusText.Text = $"错误: {inner.GetType().Name}: {inner.Message}";
        }
        finally
        {
            _timerCts.Cancel();
            sw.Stop();
            _isConnecting = false;

            // UpdateConnectionUI 会通过 OnStateChanged 自动更新按钮状态
            // 这里只处理连接失败的 UI 更新
            if (!ViewModel.IsConnected)
            {
                ConnectButton.IsEnabled = true;
                RoomInputBox.IsEnabled = true;
                var status = ViewModel.ConnectionStatus;
                if (status.Contains("失败") || status.Contains("错误") || status.Contains("超时"))
                    StatusText.Text = status;
                else if (StatusText.Text.StartsWith("连接中"))
                    StatusText.Text = $"连接失败 ({sw.Elapsed.TotalSeconds:F0}秒)";
                CopyStatusButton.Visibility = Visibility.Visible;
            }
            UpdateConnectionUI();
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DisconnectCommand.CanExecute(null))
            await ViewModel.DisconnectCommand.ExecuteAsync(null);
    }

    private async void CopyStatusButton_Click(object sender, RoutedEventArgs e)
    {
        var text = StatusText.Text;
        if (!string.IsNullOrEmpty(text))
        {
            Windows.ApplicationModel.DataTransfer.DataPackage package = new();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            CopyStatusButton.IsEnabled = false;
            await Task.Delay(1500);
            CopyStatusButton.IsEnabled = true;
        }
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is LiveEventItem item)
        {
            var text = string.IsNullOrEmpty(item.UserName)
                ? item.Content
                : $"{item.UserName}: {item.Content}";
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
    }

    private void FilterPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
            ViewModel.FilterText = tag;
    }

    private void FilterClear_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.FilterText = string.Empty;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelConnectCommand.Execute(null);
    }

    private bool _webViewInitialized;

    private async Task<Microsoft.Web.WebView2.Core.CoreWebView2> InitializeWebView2Async()
    {
        if (!_webViewInitialized)
        {
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync();
            await WebView.EnsureCoreWebView2Async(env);
            _webViewInitialized = true;
        }
        return WebView.CoreWebView2;
    }

    private void ShowWebViewButton_Click(object sender, RoutedEventArgs e)
    {
        WebViewOverlay.Visibility = Visibility.Visible;
        MessagePanel.Visibility = Visibility.Collapsed;
    }

    private void HideWebViewButton_Click(object sender, RoutedEventArgs e)
    {
        WebViewOverlay.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void RefreshWebViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2 != null)
        {
            WebView.CoreWebView2.Reload();
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }
}
