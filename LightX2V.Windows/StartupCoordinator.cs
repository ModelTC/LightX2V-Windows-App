using System.Windows;

namespace LightX2V.Windows;

public interface IStartupHost
{
    Task<bool> ShowSetupAsync(MainViewModel model);
    void ShowWorkspace(MainViewModel model);
    void Exit();
}

public sealed class StartupCoordinator(MainViewModel model,IStartupHost host)
{
    public async Task StartAsync()
    {
        if((model.NeedsInitialSetup||!model.RequiredModelsReady)&&!await host.ShowSetupAsync(model))
        {
            model.Dispose();host.Exit();return;
        }
        host.ShowWorkspace(model);
    }
}

internal sealed class WpfStartupHost(Application application) : IStartupHost
{
    public Task<bool> ShowSetupAsync(MainViewModel model)
    {
        application.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wizard=new FirstRunWindow(model,standalone:true);
        application.MainWindow=wizard;
        wizard.Closed+=(_,_)=>completion.TrySetResult(wizard.Completed);
        wizard.Show();
        return completion.Task;
    }
    public void ShowWorkspace(MainViewModel model)
    {
        MainWindow? window=null;
        window=new MainWindow(model,()=>_ = ResumeSetupAsync(window!,model));
        application.MainWindow=window;application.ShutdownMode=ShutdownMode.OnMainWindowClose;
        window.Show();
    }
    async Task ResumeSetupAsync(MainWindow window,MainViewModel model)
    {
        if(!window.IsVisible)return;
        application.ShutdownMode=ShutdownMode.OnExplicitShutdown;window.Hide();model.Setup.Restart();
        if(await ShowSetupAsync(model))
        {
            application.MainWindow=window;application.ShutdownMode=ShutdownMode.OnMainWindowClose;window.Show();
        }
        else{window.Close();application.Shutdown();}
    }
    public void Exit()=>application.Shutdown();
}
