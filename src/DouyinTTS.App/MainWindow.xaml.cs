using DouyinTTS.App.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DouyinTTS.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "DouyinTTS - 抖音弹幕播报";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        MainFrame.Navigate(typeof(HomePage));
        NavView.SelectedItem = NavHome;
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
}
