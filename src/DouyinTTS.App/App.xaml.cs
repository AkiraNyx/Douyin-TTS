using DouyinTTS.App.ViewModels;
using Microsoft.UI.Xaml;

namespace DouyinTTS.App;

public partial class App : Application
{
    public static HomeViewModel? ViewModel { get; set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        m_window = new MainWindow();
        m_window.Closed += (_, _) => ViewModel?.Dispose();
        m_window.Activate();
    }

    internal static Window? m_window;
}
