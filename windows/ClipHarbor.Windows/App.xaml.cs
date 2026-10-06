using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ClipHarbor.Windows;

public partial class App : Application
{
    private MainWindow? _window;
    private AppInstance? _instance;
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instance = AppInstance.FindOrRegisterForKey("ClipHarbor.Windows");
        if (!_instance.IsCurrent)
        {
            await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }
        _window = new MainWindow();
        _instance.Activated += (_, _) => _window.DispatcherQueue.TryEnqueue(() => _window.ShowHistory(false));
        if (!Environment.GetCommandLineArgs().Contains("--background")) _window.ShowHistory(false);
    }
}
