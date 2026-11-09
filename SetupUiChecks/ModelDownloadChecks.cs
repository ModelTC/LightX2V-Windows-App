using LightX2V.Windows;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
internal static partial class Program
{
    sealed class FakeModelDownloader : IModelDownloader
    {
        public List<ModelDownloadRequest> Requests=[];public string? Hold,Fail;public bool OmitFiles;
        public string Phase="downloading";
        public double BytesPerSecond=12*1048576;
        public double? EtaSeconds;
        public TaskCompletionSource<bool>? ReleaseHold;
        IProgress<ModelDownloadProgress>? currentProgress;
        public void ReportProgress(string phase,double? etaSeconds,double bytesPerSecond=12*1048576)
        {
            Phase=phase;EtaSeconds=etaSeconds;BytesPerSecond=bytesPerSecond;
            currentProgress?.Report(new(phase,"model-file",42,bytesPerSecond,EtaSeconds:etaSeconds));
        }
        public async Task<ModelDownloadResult> DownloadAsync(ModelDownloadRequest request,IProgress<ModelDownloadProgress> progress,CancellationToken token)
        {
            Requests.Add(request);currentProgress=progress;ReportProgress(Phase,EtaSeconds,BytesPerSecond);
            if(Hold==request.Resource)
            {
                if(ReleaseHold==null)await Task.Delay(Timeout.Infinite,token);
                else await ReleaseHold.Task.WaitAsync(token);
            }
            await Task.Yield();if(Fail==request.Resource)return new(false,Message:"Simulated network interruption");
            if(!OmitFiles&&request.Resource=="turbo")TinyTurbo(request.Destination);
            return new(true);
        }
    }
    static void TinyTurbo(string folder)
    {
        var definition = ModelCatalog.Get("turbo");
        foreach (var relative in definition.RequiredFiles)
        {
            var path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!relative.EndsWith(".json"))
            {
                File.WriteAllText(path, "Non-executable model validation fixture.");
                continue;
            }
            var properties = new Dictionary<string, object>();
            if (definition.JsonProperties.TryGetValue(relative, out var expected))
                foreach (var (name, value) in expected) properties[name] = value;
            if (relative == "model_index.json") properties["sample_sigmas"] = definition.SampleSigmas;
            Files.Write(path, properties);
        }
        foreach (var (component, layout) in definition.WeightComponents)
        {
            var path = Path.Combine(folder, component, layout.File);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            using var writer = new BinaryWriter(file);
            var headerText = "{\"fixture\":{\"dtype\":\"BF16\",\"shape\":[1],\"data_offsets\":[0,2]}}";
            var header = Encoding.UTF8.GetBytes(headerText.PadRight((headerText.Length + 7) / 8 * 8));
            writer.Write((ulong)header.Length);
            writer.Write(header);
            writer.Write((ushort)0);
        }
    }
    static async Task UntilDownloadIdle(MainViewModel vm,int seconds=120)
    {
        var until=DateTime.UtcNow.AddSeconds(seconds);while(vm.IsDownloading||vm.IsChecking){if(DateTime.UtcNow>until)throw new TimeoutException("Model download QA did not finish");await Task.Delay(30);}
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    static async Task ModelDownloadChecks()
    {
        var settings=AppSettings.Fresh(Path.Combine(root,"automatic-models-isolated"));var prefs=Path.Combine(root,"automatic-models-prefs");Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});
        var fake=new FakeModelDownloader{Hold="turbo"};using var vm=new MainViewModel(prefs,fake,new FakeCatalog(),new FakeDriverPlatform());vm.Setup.Page=2;var wizard=new FirstRunWindow(vm);
        await Render(wizard,760,700,"models-missing-zh");
        var automatic=Children<Button>((DependencyObject)wizard.Content).Single(b=>AutomationProperties.GetAutomationId(b)=="SetupDownloadModels");
        Check(automatic.IsEnabled&&automatic.Content.ToString()==vm.T["Download required files automatically"]&&fake.Requests.Count==0,"Step three exposes an explicit one-click automatic download without downloading on page render");
        automatic.Command.Execute(null);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(vm.IsDownloading&&vm.DownloadingKey=="turbo"&&!vm.CanConfigureModels&&vm.TurboCard.Percent==42,"The wizard button invokes the download pipeline and exposes numeric progress while configuration is locked");
        Check(fake.Requests.Single().Source=="auto"&&fake.Requests.Single().Destination==vm.TurboCard.Path,"One-click download passes automatic source selection and the destination directly, without a CLI prompt");await Render(wizard,760,700,"models-downloading");
        vm.RequiredModelsDownloadCommand.Execute(null);await UntilDownloadIdle(vm);
        Check(!vm.TurboCard.Ready&&fake.Requests.Count==1&&vm.TurboCard.Status==vm.T["Download cancelled"],"Stopping the wizard queue prevents later resources from starting and preserves the cancelled state");
        fake.Hold=null;await vm.Setup.DownloadModelsAsync();Check(vm.RequiredModelsReady&&vm.TurboCard.Ready&&fake.Requests.Count==2&&!vm.T2ICard.Ready,"Without enhancement, automatic download only prepares Qwen and permits continuing after file validation");
        Check(!vm.TurboCard.DownloadCommand.CanExecute(null)&&!vm.RequiredModelsDownloadCommand.CanExecute(null),"Ready model files disable redundant download actions");await Render(wizard,760,700,"models-ready-basic");
        vm.EnhancementEnabled=true;await vm.Setup.DownloadModelsAsync();
        Check(fake.Requests.Count==2&&vm.RequiredModelsReady&&vm.Setup.CanContinue&&!vm.T2ICard.Ready&&!vm.I2ICard.Ready,"An enabled enhancement preference never adds optional models to first-time setup");
        fake.Fail="t2i";await vm.DownloadModelAsync("t2i");
        Check(fake.Requests[^1].Resource=="t2i"&&fake.Requests.Count==3&&vm.T2ICard.Failed&&!vm.I2ICard.Ready&&vm.Setup.CanContinue,"An explicit enhancement download failure leaves basic setup complete and the other optional model untouched");await Render(wizard,760,700,"models-download-failed");
        fake.Fail=null;fake.Hold="t2i";var retry=vm.DownloadModelAsync("t2i");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(vm.IsDownloading&&vm.DownloadingKey=="t2i"&&fake.Requests.Count==4&&vm.T2ICard.DownloadCommand.CanExecute(null),"Retrying one optional model starts only that resource and keeps its stop action enabled");await vm.StopModelDownloadAsync();await retry;
        Check(!vm.IsDownloading&&vm.RequiredModelsReady&&vm.Setup.CanContinue&&!vm.I2ICard.Ready,"Cancelling an optional download preserves basic setup readiness");await Render(wizard,760,700,"models-ready-basic-after-cancel");
        vm.T2ICard.Path=Path.Combine(settings.Workspace,"other-t2i");vm.UseHuggingFace=true;fake.Hold=null;fake.OmitFiles=true;await vm.DownloadModelAsync("t2i");
        Check(!vm.T2ICard.Ready&&vm.T2ICard.Failed&&vm.Setup.CanContinue&&fake.Requests[^1].Source=="huggingFace","A success event with incomplete optional files is rejected without blocking basic setup, and the changed source reaches the retry");
        var guideWindow=vm.CreateModelGuide(vm.T2ICard);await Render(guideWindow,600,490,"model-guide-automatic");
        Check(Children<Button>((DependencyObject)guideWindow.Content).Any(b=>AutomationProperties.GetAutomationId(b)=="GuideAutomaticDownload"&&b.IsEnabled),"Download guides provide the same automatic action as their model cards");
        Check(Children<Expander>((DependencyObject)guideWindow.Content).Single(e=>AutomationProperties.GetAutomationId(e)=="ManualModelDownload").IsExpanded==false,"Manual commands are optional and collapsed in the download guide");guideWindow.Close();
        vm.SetLanguage("en");await Render(wizard,760,700,"models-download-en");vm.SetLanguage("zh");await Render(wizard,620,470,"models-download-small");wizard.Close();
    }
}
