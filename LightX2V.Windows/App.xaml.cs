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
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, "LightX2V APP"); args.Handled = true; };
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var model=new MainViewModel();
        try{await new StartupCoordinator(model,new WpfStartupHost(this)).StartAsync();}
        catch(Exception error){model.Dispose();MessageBox.Show(error.Message,"LightX2V APP");Shutdown();}
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
