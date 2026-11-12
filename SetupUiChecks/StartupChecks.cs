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
        var fixture=StartupFixture("startup-install-cuda");var downloads=new FakeModelDownloader{Hold="turbo",EtaSeconds=120};
        using(var vm=new MainViewModel(fixture.Preferences,downloads,fixture.Catalog,fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            Check(vm.UseAutomaticDownloadSource&&!vm.UseHuggingFace&&!vm.UseModelScope&&vm.Settings.DownloadSource=="auto","A fresh workspace defaults to automatic model-download source selection");
            vm.Prompt="保留我的创作草稿";var host=new StartupHost();var startup=new StartupCoordinator(vm,host).StartAsync();var wizard=host.Wizard!;
            Check(host.SetupShows==1&&host.WorkspaceShows==0&&!startup.IsCompleted,"A fresh install opens only setup; the workspace is not constructed before setup finishes");
            Check(wizard.Owner==null&&wizard.ShowInTaskbar&&wizard.WindowStartupLocation==WindowStartupLocation.CenterScreen&&wizard.ResizeMode==ResizeMode.CanResize,"Initial setup is an independent taskbar window with normal minimize/maximize controls and no modal workspace owner");
            await vm.Setup.BeginWorkspaceAsync();await WaitForSearch(vm.Setup);Check(fixture.Catalog.SearchCalls==1,"Continuing from save location automatically searches compatible environments");
            await vm.Setup.PrepareEnvironmentAsync(true);
            Check(vm.Setup.Error==""&&vm.Ready&&vm.Setup.EnvironmentReady&&vm.Setup.Page==2,"Automatic CUDA preparation verifies and advances directly to model preparation without a manual rescan");
            Check(vm.Setup.Environments.Single().Ready&&vm.Setup.Selected==vm.Setup.Environments[0]&&vm.Setup.UseExisting&&vm.Setup.EnvironmentSummary.Contains("1"),"A newly installed environment immediately updates the list, count and selected environment");
            Check(fixture.Catalog.InstallCalls==1&&fixture.Catalog.InstalledProbeCalls==1&&fixture.Catalog.ProbeCalls==0&&fixture.Catalog.SearchCalls==1,"Installation uses one post-install GPU probe, with no duplicate connection probe or second environment search");
            Check(vm.Settings.Python==fixture.Catalog.InstallResult&&Files.Read<Preferences>(Path.Combine(fixture.Preferences,"preferences.json"))!.Settings.Python==fixture.Catalog.InstallResult,"The verified Python path is saved before continuing");
            vm.T2ICard.Path=fixture.Settings.T2IDirectory;vm.I2ICard.Path=fixture.Settings.I2IDirectory;
            vm.EnhancementEnabled=true;
            vm.TurboCard.Path=Path.Combine(fixture.Settings.Workspace,"missing-download");var downloading=vm.Setup.DownloadModelsAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.IsDownloading&&host.WorkspaceShows==0&&wizard.IsEnabled&&wizard.Owner==null,"During model download only the independent, enabled setup window exists");
            Check(downloads.Requests.Single().Resource=="turbo"&&downloads.Requests.Single().Source=="auto","First-time download requests only the base model with automatic source selection even when enhancement was previously enabled");
            await Render(wizard,760,660,"startup-download-eta-zh");
            var etaLine=DownloadEtaLine(wizard,vm.TurboCard);
            var downloadSummary=Children<TextBlock>((DependencyObject)wizard.Content).Single(t=>AutomationProperties.GetAutomationId(t)=="SetupDownloadSummary");
            Check(vm.TurboCard.DownloadEta=="预计还需约 2 分钟"&&etaLine.Text==vm.TurboCard.DownloadEta&&downloadSummary.Text.Contains(vm.TurboCard.DownloadEta),"The wizard shows the Chinese remaining-time estimate in both the model card and overall download summary");
            vm.SetLanguage("en");await Render(wizard,620,470,"startup-download-eta-en-small");
            Check(vm.TurboCard.DownloadEta=="About 2 min remaining"&&etaLine.Text==vm.TurboCard.DownloadEta&&downloadSummary.Text.Contains(vm.TurboCard.DownloadEta),"An active wizard download updates its card and summary estimates when switching to English");
            vm.SetLanguage("zh");downloads.ReportProgress("downloading",null,0);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.TurboCard.DownloadEta=="正在估算剩余时间…"&&etaLine.Text==vm.TurboCard.DownloadEta&&downloadSummary.Text.Contains(vm.TurboCard.DownloadEta),"A wizard download with no usable speed shows that remaining time is still being estimated");
            downloads.ReportProgress("checking",120);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.TurboCard.DownloadEta==""&&etaLine.Text==""&&!downloadSummary.Text.Contains("预计"),"Checksum verification clears the wizard's transfer-time estimate from the card and summary");
            wizard.WindowState=WindowState.Minimized;Check(wizard.WindowState==WindowState.Minimized&&vm.IsDownloading,"Minimizing setup does not cancel or pause the active download");wizard.WindowState=WindowState.Normal;
            await vm.Setup.CancelAsync();await downloading;
            Check(!vm.IsDownloading&&!startup.IsCompleted&&host.WorkspaceShows==0,"Stopping a download keeps the user in setup without revealing a workspace");
            downloads.ReportProgress("downloading",120);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.TurboCard.DownloadEta==""&&etaLine.Text=="","Cancellation clears the remaining-time estimate and ignores late progress from the cancelled download");
            downloads.Hold=null;downloads.Phase="downloading";await vm.Setup.DownloadModelsAsync();
            Check(vm.RequiredModelsReady&&vm.Setup.CanContinue&&downloads.Requests.Count==2&&downloads.Requests.All(r=>r.Resource=="turbo")&&!vm.T2ICard.Ready&&!vm.I2ICard.Ready,"Base-model completion unlocks setup without requesting either optional enhancement model");
            await Render(wizard,760,660,"startup-models-only");
            var cards=Children<ContentControl>((DependencyObject)wizard.Content).Where(c=>c.Content is ModelCard).ToArray();
            Check(cards.Length==1&&ReferenceEquals(cards[0].Content,vm.TurboCard),"The first-time model page presents only the base-model card");
            var routes=Children<RadioButton>((DependencyObject)wizard.Content).Where(r=>r.GroupName=="ModelSource").ToArray();
            Check(routes.Length==3&&routes.Single(r=>r.IsChecked==true).Content?.ToString()==vm.T["Automatic (recommended)"],"The wizard presents automatic selection as the selected recommended route alongside manual sources");
            vm.UseHuggingFace=true;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(routes.Single(r=>r.IsChecked==true).Content?.ToString()=="Hugging Face","The wizard source controls follow source changes made through the shared view model");vm.UseAutomaticDownloadSource=true;
            vm.SetLanguage("en");await Render(wizard,620,470,"startup-models-en-small");
            vm.SetLanguage("zh");await Render(wizard,620,470,"startup-models-zh-small");
            ClickOffscreen(CompletionAction(wizard));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.Setup.Page==3&&!startup.IsCompleted&&host.WorkspaceShows==0,"The completion page still precedes creation of the workspace");
            Check(vm.Setup.Error=="","A completed configuration does not retain an earlier Download stopped warning");
            await Render(wizard,760,660,"startup-ready-only");ClickOffscreen(CompletionAction(wizard));await startup;
            Check(wizard.Completed&&host.WorkspaceShows==1&&host.Exits==0&&vm.Prompt=="保留我的创作草稿"&&!vm.IsRunning,"Start creating opens the workspace exactly once and preserves the draft without starting inference");
            vm.EnhancementEnabled=false;
            Check(vm.CanGenerate&&!vm.T2ICard.Ready&&!vm.I2ICard.Ready,"The completed base-model setup permits ordinary generation with both optional models absent");
        }
        fixture=StartupFixture("startup-install-amd","amd_rocm");
        using(var vm=new MainViewModel(fixture.Preferences,environmentCatalog:fixture.Catalog,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            await vm.Setup.BeginWorkspaceAsync();await vm.Setup.PrepareEnvironmentAsync(true);
            Check(vm.Ready&&vm.Setup.Page==2&&vm.Setup.Selected?.Backend=="amd_rocm"&&fixture.Catalog.SearchCalls==1&&fixture.Catalog.InstalledProbeCalls==1,"AMD preparation also publishes and selects its verified environment without another search");
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
            var host=new StartupHost();var startup=new StartupCoordinator(vm,host).StartAsync();Check(!vm.NeedsInitialSetup&&!vm.RequiredModelsReady&&host.SetupShows==1&&host.WorkspaceShows==0,"Restarting after environment installation still shows only setup while model files are missing");host.Wizard!.Close();await startup;
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
