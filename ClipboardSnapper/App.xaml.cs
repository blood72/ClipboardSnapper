using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;

namespace ClipboardSnapper;

public partial class App : Application
{
    private MainWindow? _window;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window ??= new MainWindow();
        _window.Activate();
    }

    internal void RequestActivation() => _dispatcher.TryEnqueue(() => _window?.ActivateExisting());
}
