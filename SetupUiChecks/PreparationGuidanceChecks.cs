using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using LightX2V.Windows;

internal static partial class Program
{
    sealed class IncrementalCatalog(EnvironmentCandidate first) : IEnvironmentCatalog
    {
        public readonly TaskCompletionSource<bool> Searching=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProbeCalls;
        public async Task<EnvironmentSearchResult> Search(AppSettings settings,IProgress<SetupProgress> progress,CancellationToken token)
        {
            progress.Report(new("Searching local environments",Candidate:first));Searching.TrySetResult(true);
            await Release.Task;
            var late=first with{Path=Path.Combine(settings.Workspace,"late-environment","python.exe")};
            progress.Report(new("Searching local environments",Candidate:late));
            return new([first,late]);
        }
        public Task<EnvironmentCandidate> Probe(string path,string workspace,CancellationToken token,string backend="auto")
        {
            ProbeCalls++;
            return Task.FromResult(StampedCandidate(path,backend));
        }
        public Task<string> Install(string workspace,bool mirror,IProgress<SetupProgress> progress,CancellationToken token,string backend="amd_rocm")=>throw new NotSupportedException();
    }
    static EnvironmentCandidate StampedCandidate(string path,string backend)
    {
        var file=new FileInfo(path);
        return Valid(path) with{Backend=backend,PythonLength=file.Length,PythonModifiedUtcTicks=file.LastWriteTimeUtc.Ticks};
    }
    static async Task PreparationGuidanceChecks()
    {
        await WizardEnvironmentDownloadChecks();
        foreach(var changeExecutable in new[]{false,true})
        {
            var fixture=StartupFixture(changeExecutable?"incremental-environment-changed":"incremental-environment-selected");
            var first=StampedCandidate(fixture.Catalog.InstallResult,"cuda");var catalog=new IncrementalCatalog(first);
            using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:catalog,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));
            await vm.Setup.BeginWorkspaceAsync();
            await catalog.Searching.Task.WaitAsync(TimeSpan.FromSeconds(5));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.Setup.SearchingEnvironments&&!vm.Setup.Busy&&vm.Setup.Environments.Contains(first),"A compatible environment is published while other discovery checks are still running");
            vm.Setup.AcceptCandidate(first);
            Check(vm.Setup.Selected==first&&vm.Setup.CanReuseEnvironment&&!vm.Setup.CanDownloadEnvironment,"An incremental compatible result is immediately selectable during discovery without enabling a download");
            if(changeExecutable)File.AppendAllText(first.Path,"changed executable fixture");
            await vm.Setup.PrepareEnvironmentAsync(false);
            Check(vm.Ready&&vm.Setup.Page==2&&!vm.Setup.SearchingEnvironments,"Preparing the selected result cancels outstanding discovery and advances without waiting for other environments");
            Check(catalog.ProbeCalls==(changeExecutable?1:0),changeExecutable?
                "A changed Python executable is rechecked before its environment can be reused":
                "An unchanged Python executable reuses this session's verified result without a duplicate GPU check");
            catalog.Release.TrySetResult(true);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.Setup.Environments.All(candidate=>!candidate.Path.Contains("late-environment")),"Late discovery callbacks cannot replace the chosen environment after preparation starts");
        }
        var bounded=StartupFixture("incremental-environment-budget");
        var timedCatalog=new IncrementalCatalog(StampedCandidate(bounded.Catalog.InstallResult,"cuda"));
        using(var vm=new MainViewModel(bounded.Preferences,driverPlatform:bounded.Platform,modelDiscovery:new ModelDiscovery([],[])))
        using(var setup=new SetupController(vm,timedCatalog,driverPlatform:bounded.Platform,searchTimeout:TimeSpan.FromMilliseconds(150)))
        {
            var watch=Stopwatch.StartNew();await setup.SearchEnvironmentsAsync();
            Check(watch.Elapsed<TimeSpan.FromSeconds(5)&&!setup.Busy&&!setup.SearchingEnvironments&&setup.Environments.Count==1,
                "The total search budget returns usable results even if a discovery service ignores cancellation");
            Check(setup.CanRetryEnvironmentSearch&&setup.CanReuseEnvironment&&!setup.CanDownloadEnvironment,"A timed-out search leaves a verified environment reusable and offers only search retry");
            timedCatalog.Release.TrySetResult(true);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(setup.Environments.Count==1,"Results arriving after the total search budget cannot modify the completed candidate list");
        }
        var guidance=ReadinessFixture("generation-guidance");var downloads=new FakeModelDownloader{Hold="i2i"};
        using(var vm=new MainViewModel(guidance.Preferences,downloads,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:guidance.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            Check(vm.CanGenerateReason==vm.T["Describe the image you want to create."],"An empty draft explains why image generation is unavailable");
            vm.Prompt="A blue teapot";vm.EnhancementEnabled=true;
            Check(vm.CanGenerateReason==vm.T["Download text-to-image prompt enhancement, or use the base model to continue."]&&vm.NeedsEnhancementDownload,
                "Missing text-to-image enhancement gives a specific download or base-model action");
            vm.InputImages.Add(new(){Path=Path.Combine(guidance.Settings.Workspace,"reference.png"),Name="Reference",Width=512,Height=512});
            Check(vm.CanGenerateReason==vm.T["Download reference-image prompt enhancement, or use the base model to continue."],
                "Adding a reference image changes the missing enhancement guidance to the correct model");
            var window=new MainWindow(vm);await Render(window,1280,820,"generation-guidance-zh");
            var hint=(Button)window.FindName("GenerateButton");
            Check(hint.ToolTip?.ToString()==vm.CanGenerateReason&&!Children<TextBlock>((DependencyObject)window.Content).Any(t=>AutomationProperties.GetAutomationId(t)=="CanGenerateReason"),"The composer exposes missing-model guidance through its send tooltip without extra hint rows");
            vm.DownloadEnhancementCommand.Execute(null);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(downloads.Requests.Single().Resource=="i2i"&&vm.DisableEnhancementCommand.CanExecute(null),"The guidance downloads only the enhancement model for the current reference-image workflow");
            var downloadStatus=vm.I2ICard.Status;
            await vm.CheckAsync();
            Check(vm.I2ICard.IsDownloading&&vm.I2ICard.Status==downloadStatus&&!vm.I2ICard.Ready,"A base-model environment check preserves the active optional model's download status");
            vm.DisableEnhancementCommand.Execute(null);
            Check(vm.CanGenerate&&!vm.ShowEnhancementActions&&vm.CanGenerateReason=="","The base-model action clears the blocker while the optional download continues");
            await vm.StopModelDownloadAsync();
            vm.Prompt=new string('x',20001);
            Check(vm.CanGenerateReason==vm.T["Shorten the prompt to 20,000 characters or fewer."],"An overlong prompt has an actionable character-limit explanation");
            vm.Prompt="A blue teapot";vm.EnhancementEnabled=true;vm.SetLanguage("en");await Render(window,1280,820,"generation-guidance-en");
            Check(hint.ToolTip?.ToString()==vm.CanGenerateReason&&hint.ToolTip.ToString()!.Contains("reference-image"),"Generation blocker text follows language changes in the actual composer");
            vm.SetLanguage("zh");window.Close();
        }
    }

    static async Task WizardEnvironmentDownloadChecks()
    {
        var fixture=StartupFixture("wizard-environment-download");
        var catalog=fixture.Catalog;
        catalog.Delayed=true;
        using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:catalog,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));
        var wizard=new FirstRunWindow(vm);
        await vm.Setup.BeginWorkspaceAsync();
        await Render(wizard,760,660,"wizard-environment-search-pending");
        var next=CompletionAction(wizard);
        var search=Children<Button>((DependencyObject)wizard.Content).Single(button=>AutomationProperties.GetAutomationId(button)=="SetupEnvironmentSearchAction");
        string download=vm.T["Download required components automatically"];

        Check(vm.Setup.SearchingEnvironments&&!next.IsEnabled&&!Equals(next.Content,download)&&search.Visibility==Visibility.Visible,
            "While discovery is pending the wizard exposes Stop searching and no automatic download");
        ClickOffscreen(next);
        await vm.Setup.PrepareEnvironmentAsync(true);
        Check(catalog.InstallCalls==0&&vm.Setup.SearchingEnvironments,"Both the wizard action and the controller refuse to download during discovery");
        ClickOffscreen(search);
        Check(!vm.Setup.SearchingEnvironments&&vm.Setup.CanRetryEnvironmentSearch&&!vm.Setup.CanDownloadEnvironment&&Equals(search.Content,vm.T["Search local environments"]),
            "Stopping search preserves the retry action and does not make automatic download available");

        catalog.Delayed=false;
        catalog.SearchCompletion=EnvironmentSearchCompletion.TimedOut;
        ClickOffscreen(search);
        await WaitForSearch(vm.Setup);
        Check(vm.Setup.CanRetryEnvironmentSearch&&!next.IsEnabled&&!Equals(next.Content,download),
            "A search budget expiry with zero verified results does not permit a download");

        catalog.SearchCompletion=EnvironmentSearchCompletion.Completed;
        catalog.SearchFailure=new IOException("Fixture search error");
        ClickOffscreen(search);
        await WaitForSearch(vm.Setup);
        Check(vm.Setup.CanRetryEnvironmentSearch&&!next.IsEnabled&&!Equals(next.Content,download),
            "A failed search permits another search without offering automatic download");

        catalog.SearchFailure=null;
        catalog.Found=[catalog.ProbeResult!];
        ClickOffscreen(search);
        await WaitForSearch(vm.Setup);
        Check(vm.Setup.CanReuseEnvironment&&next.IsEnabled&&!Equals(next.Content,download)&&search.Visibility==Visibility.Collapsed,
            "A completed search with a verified environment offers Continue to reuse it");
        await vm.Setup.PrepareEnvironmentAsync(true);
        Check(catalog.InstallCalls==0,"The wizard controller refuses a redundant installation when it found a compatible environment");

        catalog.Found=[catalog.ProbeResult! with{Backend="amd_rocm"}];
        await vm.Setup.ScanComputerAsync();
        await WaitForSearch(vm.Setup);
        Check(vm.Setup.CanDownloadEnvironment&&next.IsEnabled&&Equals(next.Content,download),
            "Only a completed search with zero compatible environments displays the explicit automatic download action");
        ClickOffscreen(next);
        for(var i=0;i<1000&&vm.Setup.Busy;i++)await Task.Delay(10);
        Check(catalog.InstallCalls==1&&vm.Ready&&vm.Setup.Page==2,
            "Clicking the explicit download action installs checks and selects the environment");
        wizard.Close();
    }
}
