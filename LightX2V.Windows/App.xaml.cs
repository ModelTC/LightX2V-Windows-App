using System.Threading;
using System.Windows;
namespace LightX2V.Windows;

public partial class App : Application
{
    Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, "Local\\LightX2V.Windows.App", out var first);
        if (!first) { MessageBox.Show("LightX2V APP is already running.", "LightX2V APP"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            System.Diagnostics.Trace.WriteLine(args.Exception);
            MessageBox.Show(L10n.Current[args.Exception.Message], "LightX2V APP");
            args.Handled = true;
            Shutdown(1);
        };
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        MainViewModel? model = null;
        try
        {
            model = new MainViewModel();
            await new StartupCoordinator(model, new WpfStartupHost(this)).StartAsync();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
            try { model?.Dispose(); }
            finally { MessageBox.Show(L10n.Current[error.Message], "LightX2V APP"); Shutdown(1); }
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
