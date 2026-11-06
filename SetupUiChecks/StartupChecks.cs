using LightX2V.Windows;
using System.IO;
using System.Windows;
using System.Windows.Threading;

internal static partial class Program
{
    sealed class StartupHost : IStartupHost
    {
        public FirstRunWindow? Wizard;public int WorkspaceShows,Exits,SetupShows;
        public Task<bool> ShowSetupAsync(MainViewModel model)
        {
            SetupShows++;Wizard=new FirstRunWindow(model,standalone:true);
            var result=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Wizard.Closed+=(_,_)=>result.TrySetResult(Wizard.Completed);return result.Task;
        }
        public void ShowWorkspace(MainViewModel model)=>WorkspaceShows++;
        public void Exit()=>Exits++;
    }
    static (AppSettings Settings,string Preferences,FakeCatalog Catalog,FakeDriverPlatform Platform) StartupFixture(string name,string backend="cuda")
    {
        var settings=AppSettings.Fresh(Path.Combine(root,name));settings.Backend=backend;
        var preferences=Path.Combine(root,name+"-preferences");Files.Write(Path.Combine(preferences,"preferences.json"),new Preferences{Settings=settings});
        var python=Path.Combine(settings.Workspace,"environments",backend,"python.exe");Directory.CreateDirectory(Path.GetDirectoryName(python)!);File.WriteAllText(python,"Non-executable fixture; the injected catalog performs all install/probe operations.");
        var probe=Valid(python) with{Backend=backend,Device=backend=="cuda"?"NVIDIA GeForce RTX 4070 Laptop GPU":"AMD Radeon 8060S",TorchVersion=backend=="cuda"?"2.11.0+cu128":"2.11.0+rocm7.13.0",CudaVersion=backend=="cuda"?"12.8":"",HipVersion=backend=="cuda"?"":"7.13.0"};
        var catalog=new FakeCatalog{InstallResult=python,ProbeResult=probe};
        var platform=new FakeDriverPlatform{Computer=HardwareInfo.FromAdapters([new(backend=="cuda"?"NVIDIA GeForce RTX 4070 Laptop GPU":"AMD Radeon 8060S","32.0.16.2101")])};
        return(settings,preferences,catalog,platform);
    }
    static async Task StartupChecks()
    {
        var fixture=StartupFixture("startup-install-cuda");var downloads=new FakeModelDownloader{Hold="turbo"};
        using(var vm=new MainViewModel(fixture.Preferences,downloads,fixture.Catalog,fixture.Platform))
        {
            vm.Prompt="保留我的创作草稿";var host=new StartupHost();var startup=new StartupCoordinator(vm,host).StartAsync();var wizard=host.Wizard!;
            Check(host.SetupShows==1&&host.WorkspaceShows==0&&!startup.IsCompleted,"A fresh install opens only setup; the workspace is not constructed before setup finishes");
            Check(wizard.Owner==null&&wizard.ShowInTaskbar&&wizard.WindowStartupLocation==WindowStartupLocation.CenterScreen&&wizard.ResizeMode==ResizeMode.CanResize,"Initial setup is an independent taskbar window with normal minimize/maximize controls and no modal workspace owner");
            await vm.Setup.BeginWorkspaceAsync();Check(vm.Setup.Environments.Count==0&&fixture.Catalog.SearchCalls==1,"The RTX fixture starts with no compatible local environment");
            await vm.Setup.PrepareEnvironmentAsync(true);
            Check(vm.Setup.Error==""&&vm.Ready&&vm.Setup.EnvironmentReady&&vm.Setup.Page==2,"Automatic CUDA preparation verifies and advances directly to model preparation without a manual rescan");
            Check(vm.Setup.Environments.Single().Ready&&vm.Setup.Selected==vm.Setup.Environments[0]&&vm.Setup.UseExisting&&vm.Setup.EnvironmentSummary.Contains("1"),"A newly installed environment immediately updates the list, count and selected environment");
            Check(fixture.Catalog.InstallCalls==1&&fixture.Catalog.InstalledProbeCalls==1&&fixture.Catalog.ProbeCalls==0&&fixture.Catalog.SearchCalls==1,"Installation uses one post-install GPU probe, with no duplicate connection probe or second environment search");
            Check(vm.Settings.Python==fixture.Catalog.InstallResult&&Files.Read<Preferences>(Path.Combine(fixture.Preferences,"preferences.json"))!.Settings.Python==fixture.Catalog.InstallResult,"The verified Python path is saved before continuing");
            vm.TurboCard.Path=Path.Combine(fixture.Settings.Workspace,"missing-download");var downloading=vm.Setup.DownloadModelsAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.IsDownloading&&host.WorkspaceShows==0&&wizard.IsEnabled&&wizard.Owner==null,"During model download only the independent, enabled setup window exists");
            wizard.WindowState=WindowState.Minimized;Check(wizard.WindowState==WindowState.Minimized&&vm.IsDownloading,"Minimizing setup does not cancel or pause the active download");wizard.WindowState=WindowState.Normal;
            await vm.Setup.CancelAsync();await downloading;
            Check(!vm.IsDownloading&&!startup.IsCompleted&&host.WorkspaceShows==0,"Stopping a download keeps the user in setup without revealing a workspace");
            var model=Path.Combine(fixture.Settings.Workspace,"fixture-model");TinyTurbo(model);vm.TurboCard.Path=model;
            await Render(wizard,760,660,"startup-models-only");ClickOffscreen(CompletionAction(wizard));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.Setup.Page==3&&!startup.IsCompleted&&host.WorkspaceShows==0,"The completion page still precedes creation of the workspace");
            Check(vm.Setup.Error=="","A completed configuration does not retain an earlier Download stopped warning");
            await Render(wizard,760,660,"startup-ready-only");ClickOffscreen(CompletionAction(wizard));await startup;
            Check(wizard.Completed&&host.WorkspaceShows==1&&host.Exits==0&&vm.Prompt=="保留我的创作草稿"&&!vm.IsRunning,"Start creating opens the workspace exactly once and preserves the draft without starting inference");
        }
        fixture=StartupFixture("startup-install-amd","amd_rocm");
        using(var vm=new MainViewModel(fixture.Preferences,environmentCatalog:fixture.Catalog,driverPlatform:fixture.Platform))
        {
            await vm.Setup.BeginWorkspaceAsync();await vm.Setup.PrepareEnvironmentAsync(true);
            Check(vm.Ready&&vm.Setup.Page==2&&vm.Setup.Selected?.Backend=="amd_rocm"&&fixture.Catalog.SearchCalls==1&&fixture.Catalog.InstalledProbeCalls==1,"AMD preparation also publishes and selects its verified environment without another search");
        }
        fixture=StartupFixture("startup-cancel-probe");fixture.Catalog.DelayProbe=true;
        using(var vm=new MainViewModel(fixture.Preferences,environmentCatalog:fixture.Catalog,driverPlatform:fixture.Platform))
        {
            await vm.Setup.BeginWorkspaceAsync();var preparing=vm.Setup.PrepareEnvironmentAsync(true);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.Setup.Busy&&fixture.Catalog.InstalledProbeCalls==1,"Post-install checking remains cancellable while the new GPU environment initializes");await vm.Setup.CancelAsync();await preparing;
            Check(!vm.Ready&&!vm.Setup.EnvironmentReady&&vm.Setup.Page==1&&vm.Settings.Python==""&&vm.Setup.Environments.Count==0,"Cancelling the post-install check cannot save or select an unverified environment");
        }
        foreach(var wrongBackend in new[]{false,true})
        {
            fixture=StartupFixture(wrongBackend?"startup-wrong-backend":"startup-failed-probe");fixture.Catalog.ProbeResult=fixture.Catalog.ProbeResult! with{Ready=wrongBackend,Backend=wrongBackend?"amd_rocm":"cuda",Detail="Probe fixture did not pass"};
            using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:fixture.Catalog,driverPlatform:fixture.Platform);await vm.Setup.BeginWorkspaceAsync();await vm.Setup.PrepareEnvironmentAsync(true);
            Check(!vm.Ready&&!vm.Setup.EnvironmentReady&&vm.Setup.Page==1&&vm.Setup.Error.Length>0&&vm.Settings.Python=="",wrongBackend?"A probe for another backend cannot mark CUDA setup complete":"A failed GPU check is shown inline and cannot mark installation as usable");
        }
        fixture=StartupFixture("startup-close-setup");
        using(var vm=new MainViewModel(fixture.Preferences))
        {
            var host=new StartupHost();var startup=new StartupCoordinator(vm,host).StartAsync();host.Wizard!.Close();await startup;
            Check(host.WorkspaceShows==0&&host.Exits==1&&!host.Wizard.Completed,"Closing first-time setup exits the app instead of revealing an unconfigured workspace");
        }
        fixture=StartupFixture("startup-picker-install");
        using(var pickerModel=new EnvironmentSelectionModel(fixture.Settings,L10n.Current,true,fixture.Catalog,new DriverSetupModel("cuda",()=>fixture.Settings.Workspace,L10n.Current,fixture.Platform)))
        {
            await pickerModel.SearchAsync();await pickerModel.InstallAsync();
            Check(pickerModel.Error==""&&pickerModel.Candidates.Single().Ready&&pickerModel.Selected==pickerModel.Candidates[0]&&pickerModel.Summary.Contains("1")&&fixture.Catalog.SearchCalls==1,"The settings environment picker also shows and selects a newly installed CUDA environment without another search");
        }
        fixture=StartupFixture("startup-resume-models");fixture.Settings.Python=fixture.Catalog.InstallResult;Files.Write(Path.Combine(fixture.Preferences,"preferences.json"),new Preferences{Settings=fixture.Settings});
        using(var vm=new MainViewModel(fixture.Preferences))
        {
            var host=new StartupHost();var startup=new StartupCoordinator(vm,host).StartAsync();Check(!vm.NeedsInitialSetup&&!vm.RequiredModelsReady&&host.SetupShows==1&&host.WorkspaceShows==0,"Restarting after environment installation still shows only setup while model files are missing");host.Wizard!.Close();await startup;
        }
        TinyTurbo(fixture.Settings.Model);
        using(var vm=new MainViewModel(fixture.Preferences))
        {
            var host=new StartupHost();await new StartupCoordinator(vm,host).StartAsync();Check(host.SetupShows==0&&host.WorkspaceShows==1,"An existing environment and complete model files retain direct workspace startup");
        }
    }
}
