using System.Threading;
using System.Windows;
namespace LightX2V.Windows;

public partial class App : Application
{
    Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Length==3&&e.Args[0] is "--maintenance" or "--update")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            if(e.Args[0]=="--maintenance")
            {
                await UninstallWindow.RunHelperAsync(e.Args[1],e.Args[2]);
                return;
            }
            var exitCode=0;
            try
            {
                await AppUpdater.RunHelperAsync(e.Args[1],e.Args[2]);
            }
            catch(Exception error)
            {
                try
                {
                    MaintenanceProcess.ValidatePlanFile(e.Args[1]);
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(e.Args[1])!,"failure.txt"),error.ToString());
                }
                catch(Exception reportError){System.Diagnostics.Trace.WriteLine(reportError);}
                MessageBox.Show(L10n.Current["The app update could not finish."]+"\n"+L10n.Current[error.Message],"LightX2V");
                exitCode=1;
            }
            try{MaintenanceProcess.ScheduleCleanup(e.Args[1]);}catch(Exception cleanupError){System.Diagnostics.Trace.WriteLine(cleanupError);}
            Shutdown(exitCode);
            return;
        }
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
