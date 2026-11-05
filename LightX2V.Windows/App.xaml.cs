using System.Threading;
using System.Windows;
namespace LightX2V.Windows;

public partial class App : Application
{
    Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, "Local\\LightX2V.Windows.App", out var first);
        if (!first) { MessageBox.Show("LightX2V APP is already running.", "LightX2V APP"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, "LightX2V APP"); args.Handled = true; };
        var window = new MainWindow(new MainViewModel()); MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
