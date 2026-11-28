using LightX2V.Windows;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

internal static partial class Program
{
    sealed class StartupHost : IStartupHost
    {
        public SettingsWindow? Wizard;public int WorkspaceShows,Exits,SetupShows;
        public Task<bool> ShowSetupAsync(MainViewModel model)
        {
            SetupShows++;Wizard=new SettingsWindow(model,firstRun:true);
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
        var probe=Valid(python) with{Backend=backend,Device=backend=="cuda"?"NVIDIA GeForce RTX 4070 Laptop GPU":"AMD Radeon 8060S",TorchVersion=backend=="cuda"?"2.11.0+cu128":"2.11.0+rocm7.14.1",CudaVersion=backend=="cuda"?"12.8":"",HipVersion=backend=="cuda"?"":"7.14.1"};
        var catalog=new FakeCatalog{InstallResult=python,ProbeResult=probe};
        var platform=new FakeDriverPlatform{Computer=HardwareInfo.FromAdapters([new(backend=="cuda"?"NVIDIA GeForce RTX 4070 Laptop GPU":"AMD Radeon 8060S","32.0.16.2101")])};
        return(settings,preferences,catalog,platform);
    }
    static async Task StartupChecks()
    {
        var fixture=StartupFixture("startup-install-cuda");var downloads=new FakeModelDownloader{Hold="turbo",EtaSeconds=120};
        Files.Write(Path.Combine(fixture.Preferences,"preferences.json"),new Preferences{WorkspaceConfirmed=false,Settings=fixture.Settings});
        using(var vm=new MainViewModel(fixture.Preferences,downloads,fixture.Catalog,fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            Check(vm.UseAutomaticDownloadSource&&!vm.UseHuggingFace&&!vm.UseModelScope&&vm.Settings.DownloadSource=="auto","A fresh workspace defaults to automatic model-download source selection");
            var host=new StartupHost();var startup=new StartupCoordinator(vm,host).StartAsync();var wizard=host.Wizard!;
            Check(host.SetupShows==1&&host.WorkspaceShows==0&&!startup.IsCompleted,"A fresh install opens only setup; the workspace is not constructed before setup finishes");
            Check(wizard.Owner==null&&wizard.ShowInTaskbar&&wizard.WindowStartupLocation==WindowStartupLocation.CenterScreen&&wizard.ResizeMode==ResizeMode.CanResize,"Initial setup is an independent taskbar window with normal minimize/maximize controls and no modal workspace owner");
            await Render(wizard,640,460,"first-run-settings");
            var controls=Children<FrameworkElement>((DependencyObject)wizard.Content).ToLookup(c=>AutomationProperties.GetAutomationId(c));
            var save=(Button)controls["SettingsSave"].Single();
            Check(!save.IsEnabled&&!controls["SearchEnvironment"].Single().IsEnabled&&controls.Contains("SettingsLanguage")&&controls.Contains("SearchEnvironment")&&!controls.Contains("SetupDownloadModels"),"Welcome shows language, workspace and environment, and requires a Python path before Save");
            ((TextBox)controls["WorkspacePath"].Single()).Text=fixture.Settings.Workspace;
            ((TextBox)controls["PythonPath"].Single()).Text=fixture.Catalog.InstallResult;
            Check(save.IsEnabled,"Save becomes available after choosing the workspace and Python");
            ClickOffscreen(save);
            await startup.WaitAsync(TimeSpan.FromSeconds(60));
            Check(wizard.Completed&&host.WorkspaceShows==1&&vm.Ready&&!vm.NeedsInitialSetup&&!vm.RequiredModelsReady&&downloads.Requests.Count==0,"Save verifies the environment and opens the workspace without downloading any model");
        }
        fixture=StartupFixture("startup-cancel-probe");fixture.Catalog.DelayProbe=true;
        using(var vm=new MainViewModel(fixture.Preferences,environmentCatalog:fixture.Catalog,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            await vm.Setup.BeginWorkspaceAsync();var preparing=vm.Setup.PrepareEnvironmentAsync(true);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.Setup.Busy&&fixture.Catalog.InstalledProbeCalls==1,"Post-install checking remains cancellable while the new GPU environment initializes");await vm.Setup.CancelAsync();await preparing;
            Check(!vm.Ready&&!vm.Setup.EnvironmentReady&&vm.Setup.Page==1&&vm.Settings.Python==""&&vm.Setup.Environments.Count==0,"Cancelling the post-install check cannot save or select an unverified environment");
        }
        foreach(var wrongBackend in new[]{false,true})
        {
            fixture=StartupFixture(wrongBackend?"startup-wrong-backend":"startup-failed-probe");fixture.Catalog.ProbeResult=fixture.Catalog.ProbeResult! with{Ready=wrongBackend,Backend=wrongBackend?"amd_rocm":"cuda",Detail="Probe fixture did not pass"};
            using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:fixture.Catalog,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));await vm.Setup.BeginWorkspaceAsync();await vm.Setup.PrepareEnvironmentAsync(true);
            Check(!vm.Ready&&!vm.Setup.EnvironmentReady&&vm.Setup.Page==1&&vm.Setup.Error.Length>0&&vm.Settings.Python=="",wrongBackend?"A probe for another backend cannot mark CUDA setup complete":"A failed GPU check is shown inline and cannot mark installation as usable");
        }
        fixture=StartupFixture("startup-close-setup");
        using(var vm=new MainViewModel(fixture.Preferences,modelDiscovery:new ModelDiscovery([],[])))
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
        using(var vm=new MainViewModel(fixture.Preferences,modelDiscovery:new ModelDiscovery([],[])))
        {
            var host=new StartupHost();await new StartupCoordinator(vm,host).StartAsync();Check(!vm.NeedsInitialSetup&&!vm.RequiredModelsReady&&host.SetupShows==0&&host.WorkspaceShows==1,"A configured workspace reopens without requiring a model download");
        }
        TinyTurbo(fixture.Settings.Model);fixture.Settings.EnhancementEnabled=true;Files.Write(Path.Combine(fixture.Preferences,"preferences.json"),new Preferences{Settings=fixture.Settings});
        using(var vm=new MainViewModel(fixture.Preferences,modelDiscovery:new ModelDiscovery([],[])))
        {
            var host=new StartupHost();await new StartupCoordinator(vm,host).StartAsync();Check(host.SetupShows==0&&host.WorkspaceShows==1&&vm.EnhancementEnabled&&!vm.T2ICard.Ready&&!vm.I2ICard.Ready,"A saved workspace with the base model opens directly even when its enabled optional enhancement models are missing");
        }
        await DownloadSourcePreferenceChecks();
        await OptionalModelDownloadChecks();
        await PreparationGuidanceChecks();
        await ModelValidationChecks();
    }

    static async Task DownloadSourcePreferenceChecks()
    {
        var fixture=StartupFixture("download-source-preferences");
        using var vm=new MainViewModel(fixture.Preferences,modelDiscovery:new ModelDiscovery([],[]));
        foreach(var source in new[]{"huggingFace","modelScope","auto"})
        {
            if(source=="huggingFace")vm.UseHuggingFace=true;
            else if(source=="modelScope")vm.UseModelScope=true;
            else vm.UseAutomaticDownloadSource=true;
            using var restored=new MainViewModel(fixture.Preferences,modelDiscovery:new ModelDiscovery([],[]));
            Check(restored.Settings.DownloadSource==source&&restored.UseHuggingFace==(source=="huggingFace")&&restored.UseModelScope==(source=="modelScope")&&restored.UseAutomaticDownloadSource==(source=="auto"),"The selected download source survives reopening preferences without changing an explicit choice: "+source);
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    static async Task OptionalModelDownloadChecks()
    {
        var fixture=ReadinessFixture("optional-model-download");var catalog=new ReadinessCatalog();var downloads=new FakeModelDownloader{Hold="t2i",EtaSeconds=120};
        using var vm=new MainViewModel(fixture.Preferences,downloads,catalog,fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));
        vm.Prompt="An ordinary image while optional files download";vm.Tier=0;vm.Ratio="1:1";
        Check(!vm.EnhancementEnabled&&vm.T2ICard.DownloadCommand.CanExecute(null)&&vm.I2ICard.DownloadCommand.CanExecute(null),"Both enhancement model cards offer independent downloads before enhancement is enabled");
        var window=new MainWindow(vm);await Render(window,1280,820,"optional-models-sidebar");
        vm.SetLanguage("en");await Render(window,1280,820,"optional-models-sidebar-en");vm.SetLanguage("zh");
        var modelButtons=Children<Button>((DependencyObject)window.Content).ToArray();
        var t2iDownload=modelButtons.Single(b=>ReferenceEquals(b.Command,vm.T2ICard.DownloadCommand));
        var i2iDownload=modelButtons.Single(b=>ReferenceEquals(b.Command,vm.I2ICard.DownloadCommand));
        var enhancementToggle=(CheckBox)window.FindName("EnhancementToggle");
        Check(!t2iDownload.IsEnabled&&!i2iDownload.IsEnabled&&enhancementToggle.IsChecked==false,"Idle enhancement cards are muted and disabled when enhancement is off, matching the Mac reference");
        vm.EnhancementEnabled=true;Check(t2iDownload.IsEnabled&&i2iDownload.IsEnabled,"Enabling enhancement makes both independent model downloads available");
        var downloading=vm.DownloadModelAsync("t2i");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);vm.EnhancementEnabled=false;
        Check(vm.IsDownloading&&vm.DownloadingKey=="t2i"&&downloads.Requests.Single().Resource=="t2i"&&vm.CanGenerate,"Downloading a selected optional model leaves the ready base model available for ordinary generation");
        var etaLine=DownloadEtaLine(window,vm.T2ICard);
        Check(vm.T2ICard.DownloadEta=="预计还需约 2 分钟"&&etaLine.Text==vm.T2ICard.DownloadEta,"The right sidebar displays the optional model's remaining-time estimate in Chinese");
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var guide=vm.CreateModelGuide(vm.T2ICard,t2iDownload);
        Check(!Children<TextBlock>((DependencyObject)guide.Child).Any(t=>AutomationProperties.GetAutomationId(t)=="ModelGuideDownloadEta")&&downloads.Requests.Count==1,"Opening a model guide neither repeats download status nor starts a download");
        vm.SetLanguage("en");await Render(window,1280,820,"optional-model-download-eta-en");
        Check(vm.T2ICard.DownloadEta=="About 2 min remaining"&&etaLine.Text==vm.T2ICard.DownloadEta,"The right sidebar updates an active remaining-time estimate to English");
        foreach(var (seconds,text) in new (double Seconds,string Text)[]
        {
            (59,"Less than a minute remaining"),
            (60,"About 1 min remaining"),
            (61,"About 2 min remaining"),
            (3599,"About 1 h 0 min remaining"),
            (3661,"About 1 h 2 min remaining")
        })
        {
            downloads.ReportProgress("downloading",seconds);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.T2ICard.DownloadEta==text&&etaLine.Text==text,"Remaining-time formatting rounds up without understating the estimate for "+seconds+" seconds");
        }
        foreach(var seconds in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1d})
        {
            downloads.ReportProgress("downloading",seconds);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.T2ICard.DownloadEta=="Estimating remaining time…"&&etaLine.Text==vm.T2ICard.DownloadEta,"An invalid remaining-time estimate displays the estimating state for "+seconds);
        }
        downloads.ReportProgress("downloading",null,0);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(vm.T2ICard.DownloadEta=="Estimating remaining time…"&&etaLine.Text==vm.T2ICard.DownloadEta,"The sidebar shows an estimating state while download speed is unavailable");
        downloads.ReportProgress("checking",120);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(vm.T2ICard.DownloadEta==""&&etaLine.Text=="","The sidebar clears the transfer-time estimate during model checksum verification");
        vm.SetLanguage("zh");downloads.ReportProgress("downloading",120);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(vm.CanToggleEnhancement&&vm.T2ICard.DownloadCommand.CanExecute(null)&&!vm.I2ICard.DownloadCommand.CanExecute(null),"Optional downloads keep enhancement selection and the active stop action available while preventing a second download");
        Check(enhancementToggle.IsEnabled&&t2iDownload.IsEnabled&&!i2iDownload.IsEnabled,"The sidebar toggle and selected model stop button stay enabled during its download");
        vm.EnhancementEnabled=true;
        Check(!vm.CanGenerate&&vm.RequiredModelsReady,"An enabled incomplete enhancer gates enhanced generation while the base model remains ready");
        vm.EnhancementEnabled=false;
        Check(vm.CanGenerate&&vm.IsDownloading,"Turning enhancement off during its download immediately restores basic generation");
        var sending=vm.GenerateAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(vm.IsPreflighting&&vm.IsDownloading&&vm.T2ICard.DownloadCommand.CanExecute(null)&&vm.DownloadAllCommand.CanExecute(null),"A basic generation can start preflight during optional download and both download stop controls remain usable");
        vm.T2ICard.DownloadCommand.Execute(null);await downloading;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!vm.IsDownloading&&vm.IsPreflighting&&vm.RequiredModelsReady,"Stopping an optional download does not cancel an independent generation preflight");
        Check(vm.T2ICard.DownloadEta==""&&etaLine.Text=="","Stopping an optional model download clears its sidebar remaining-time estimate");
        await vm.StopAsync();await sending;
        downloads.Hold="i2i";downloads.Fail="i2i";downloads.ReleaseHold=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing=vm.DownloadModelAsync("i2i");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var failedEtaLine=DownloadEtaLine(window,vm.I2ICard);
        Check(vm.I2ICard.DownloadEta=="预计还需约 2 分钟"&&failedEtaLine.Text==vm.I2ICard.DownloadEta,"A second optional model owns its own remaining-time estimate");
        downloads.ReleaseHold.TrySetResult(true);await failing;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(vm.I2ICard.Failed&&vm.CanGenerate&&vm.RequiredModelsReady&&downloads.Requests.Count==2,"An optional model failure leaves basic generation available and does not start another resource");
        Check(vm.I2ICard.DownloadEta==""&&failedEtaLine.Text=="","A failed optional download clears the previously displayed remaining-time estimate");
        window.Close();
    }
    static TextBlock DownloadEtaLine(Window window,ModelCard card)=>Children<TextBlock>((DependencyObject)window.Content).Single(t=>AutomationProperties.GetAutomationId(t)=="ModelDownloadEta"&&ReferenceEquals(t.DataContext,card));
}
