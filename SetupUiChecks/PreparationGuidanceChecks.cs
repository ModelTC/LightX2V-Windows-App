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
        public async Task<List<EnvironmentCandidate>> Search(AppSettings settings,IProgress<SetupProgress> progress,CancellationToken token)
        {
            progress.Report(new("Searching local environments",Candidate:first));Searching.TrySetResult(true);
            await Release.Task;
            var late=first with{Path=Path.Combine(settings.Workspace,"late-environment","python.exe")};
            progress.Report(new("Searching local environments",Candidate:late));
            return [first,late];
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
        foreach(var changeExecutable in new[]{false,true})
        {
            var fixture=StartupFixture(changeExecutable?"incremental-environment-changed":"incremental-environment-selected");
            var first=StampedCandidate(fixture.Catalog.InstallResult,"cuda");var catalog=new IncrementalCatalog(first);
            using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:catalog,driverPlatform:fixture.Platform);
            await vm.Setup.BeginWorkspaceAsync();var scanning=vm.Setup.SearchEnvironmentsAsync();
            await catalog.Searching.Task.WaitAsync(TimeSpan.FromSeconds(5));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.Setup.SearchingEnvironments&&!vm.Setup.Busy&&vm.Setup.Environments.Contains(first),"A compatible environment is published while other discovery checks are still running");
            vm.Setup.AcceptCandidate(first);
            Check(vm.Setup.Selected==first&&vm.Setup.UseExisting,"An incremental compatible result is immediately selectable during discovery");
            if(changeExecutable)File.AppendAllText(first.Path,"changed executable fixture");
            await vm.Setup.PrepareEnvironmentAsync(false);await scanning;
            Check(vm.Ready&&vm.Setup.Page==2&&!vm.Setup.SearchingEnvironments,"Preparing the selected result cancels outstanding discovery and advances without waiting for other environments");
            Check(catalog.ProbeCalls==(changeExecutable?1:0),changeExecutable?
                "A changed Python executable is rechecked before its environment can be reused":
                "An unchanged Python executable reuses this session's verified result without a duplicate GPU check");
            catalog.Release.TrySetResult(true);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.Setup.Environments.All(candidate=>!candidate.Path.Contains("late-environment")),"Late discovery callbacks cannot replace the chosen environment after preparation starts");
        }
        var bounded=StartupFixture("incremental-environment-budget");
        var timedCatalog=new IncrementalCatalog(StampedCandidate(bounded.Catalog.InstallResult,"cuda"));
        using(var vm=new MainViewModel(bounded.Preferences,driverPlatform:bounded.Platform))
        using(var setup=new SetupController(vm,timedCatalog,driverPlatform:bounded.Platform,searchTimeout:TimeSpan.FromMilliseconds(150)))
        {
            var watch=Stopwatch.StartNew();await setup.SearchEnvironmentsAsync();
            Check(watch.Elapsed<TimeSpan.FromSeconds(5)&&!setup.Busy&&!setup.SearchingEnvironments&&setup.Environments.Count==1,
                "The total search budget returns usable results even if a discovery service ignores cancellation");
            timedCatalog.Release.TrySetResult(true);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(setup.Environments.Count==1,"Results arriving after the total search budget cannot modify the completed candidate list");
        }
        var guidance=ReadinessFixture("generation-guidance");var downloads=new FakeModelDownloader{Hold="i2i"};
        using(var vm=new MainViewModel(guidance.Preferences,downloads,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:guidance.Platform))
        {
            Check(vm.CanGenerateReason==vm.T["Describe the image you want to create."],"An empty draft explains why image generation is unavailable");
            vm.Prompt="A blue teapot";vm.EnhancementEnabled=true;
            Check(vm.CanGenerateReason==vm.T["Download text-to-image prompt enhancement, or use the base model to continue."]&&vm.NeedsEnhancementDownload,
                "Missing text-to-image enhancement gives a specific download or base-model action");
            vm.InputImages.Add(new(){Path=Path.Combine(guidance.Settings.Workspace,"reference.png"),Name="Reference",Width=512,Height=512});
            Check(vm.CanGenerateReason==vm.T["Download reference-image prompt enhancement, or use the base model to continue."],
                "Adding a reference image changes the missing enhancement guidance to the correct model");
            var window=new MainWindow(vm);await Render(window,1280,820,"generation-guidance-zh");
            var hint=Children<TextBlock>((DependencyObject)window.Content).Single(text=>AutomationProperties.GetAutomationId(text)=="CanGenerateReason");
            Check(hint.Text==vm.CanGenerateReason&&vm.DownloadEnhancementCommand.CanExecute(null),"The composer displays its generation blocker and offers the relevant model download");
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
            Check(hint.Text==vm.CanGenerateReason&&hint.Text.Contains("reference-image"),"Generation blocker text follows language changes in the actual composer");
            vm.SetLanguage("zh");window.Close();
        }
    }
}
