using DouyinTTS.App.Pages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace DouyinTTS.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "DouyinTTS";
        AppTitleBar.Subtitle = $"v{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 设置默认窗口大小
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 900, Height = 640 });

        // 设置背景材质
        ApplyBackdrop();

        MainFrame.Navigate(typeof(HomePage));
        MainFrame.Navigated += (_, _) => UpdateTitleBarBackButton();
        NavView.SelectedItem = NavHome;
    }

    public void ApplyBackdrop()
    {
        var saved = ApplicationData.Current.LocalSettings.Values["BackdropType"] as string ?? "Mica";
        SetBackdrop(saved);
    }

    public void SetBackdrop(string type)
    {
        switch (type)
        {
            case "Mica":
                var mica1 = new MicaBackdrop();
                mica1.Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base;
                this.SystemBackdrop = mica1;
                break;
            case "MicaAlt":
                var mica2 = new MicaBackdrop();
                mica2.Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt;
                this.SystemBackdrop = mica2;
                break;
            case "Acrylic":
                this.SystemBackdrop = new DesktopAcrylicBackdrop();
                break;
            case "None":
            default:
                this.SystemBackdrop = null;
                break;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag?.ToString();
        Type? pageType = tag switch
        {
            "Home" => typeof(HomePage),
            "Settings" => typeof(SettingsPage),
            _ => null
        };
        if (pageType != null && MainFrame.CurrentSourcePageType != pageType)
            MainFrame.Navigate(pageType);
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args)
    {
        if (MainFrame.CanGoBack)
        {
            MainFrame.GoBack();
            UpdateNavigationSelection();
        }
        UpdateTitleBarBackButton();
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void UpdateTitleBarBackButton()
    {
        AppTitleBar.IsBackButtonEnabled = MainFrame.CanGoBack;
    }

    private void UpdateNavigationSelection()
    {
        NavView.SelectedItem = MainFrame.CurrentSourcePageType switch
        {
            Type page when page == typeof(HomePage) => NavHome,
            Type page when page == typeof(SettingsPage) => NavSettings,
            _ => NavView.SelectedItem
        };
    }
}
