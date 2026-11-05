using LightX2V.Windows;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
internal static partial class Program
{
    sealed class FakeModelDownloader : IModelDownloader
    {
        public List<ModelDownloadRequest> Requests=[];public string? Hold,Fail;public bool OmitFiles;
        public async Task<ModelDownloadResult> DownloadAsync(ModelDownloadRequest request,IProgress<ModelDownloadProgress> progress,CancellationToken token)
        {
            Requests.Add(request);progress.Report(new("downloading","model-file",42,12*1048576));
            if(Hold==request.Resource)await Task.Delay(Timeout.Infinite,token);
            await Task.Yield();if(Fail==request.Resource)return new(false,Message:"Simulated network interruption");
            if(!OmitFiles){if(request.Resource=="turbo")TinyTurbo(request.Destination);else SeedPe(request.Destination,request.Resource,true);}
            return new(true);
        }
    }
    static void TinyTurbo(string folder)
    {
        Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"model_index.json"),"{\"sample_sigmas\":[1,2,3,4,5,6,7,8]}");
        foreach(var name in new[]{"transformer","text_encoder","vae","scheduler","processor"})Directory.CreateDirectory(Path.Combine(folder,name));
        foreach(var name in new[]{"transformer","text_encoder","vae"})
        {
            using var file=File.Create(Path.Combine(folder,name,"qa-fixture.safetensors"));using var writer=new BinaryWriter(file);
            var header=Encoding.UTF8.GetBytes("{\"__metadata__\":{}}");writer.Write((ulong)header.Length);writer.Write(header);
        }
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateHardLink(string file,string existing,IntPtr reserved);
    static void SeedPe(string destination,string key,bool system)
    {
        var actual=AppSettings.Detect();var source=key=="t2i"?actual.T2IDirectory:actual.I2IDirectory;Directory.CreateDirectory(destination);
        foreach(var file in Directory.EnumerateFiles(source,"*.gguf"))
        {
            var path=Path.Combine(destination,Path.GetFileName(file));if(!File.Exists(path)&&!CreateHardLink(path,file,IntPtr.Zero))throw new System.ComponentModel.Win32Exception();
        }
        if(system)File.Copy(Path.Combine(source,"system_prompt.txt"),Path.Combine(destination,"system_prompt.txt"),true);
    }
    static async Task UntilDownloadIdle(MainViewModel vm,int seconds=120)
    {
        var until=DateTime.UtcNow.AddSeconds(seconds);while(vm.IsDownloading||vm.IsChecking){if(DateTime.UtcNow>until)throw new TimeoutException("Model download QA did not finish");await Task.Delay(30);}
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    static async Task ModelDownloadChecks()
    {
        var settings=AppSettings.Fresh(Path.Combine(root,"automatic-models-isolated"));var prefs=Path.Combine(root,"automatic-models-prefs");Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});
        var fake=new FakeModelDownloader{Hold="turbo"};using var vm=new MainViewModel(prefs,fake);vm.Setup.Page=2;var wizard=new FirstRunWindow(vm);
        await Render(wizard,760,700,"models-missing-zh");
        var automatic=Children<Button>((DependencyObject)wizard.Content).Single(b=>AutomationProperties.GetAutomationId(b)=="SetupDownloadModels");
        Check(automatic.IsEnabled&&automatic.Content.ToString()==vm.T["Download required files automatically"]&&fake.Requests.Count==0,"Step three exposes an explicit one-click automatic download without downloading on page render");
        automatic.Command.Execute(null);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(vm.IsDownloading&&vm.DownloadingKey=="turbo"&&!vm.CanConfigureModels&&vm.TurboCard.Percent==42,"The wizard button invokes the download pipeline and exposes numeric progress while configuration is locked");
        Check(fake.Requests.Single().Source=="modelScope"&&fake.Requests.Single().Destination==vm.TurboCard.Path,"One-click download passes the selected source and destination directly, without a CLI prompt");await Render(wizard,760,700,"models-downloading");
        vm.RequiredModelsDownloadCommand.Execute(null);await UntilDownloadIdle(vm);
        Check(!vm.TurboCard.Ready&&fake.Requests.Count==1&&vm.TurboCard.Status==vm.T["Download cancelled"],"Stopping the wizard queue prevents later resources from starting and preserves the cancelled state");
        fake.Hold=null;await vm.Setup.DownloadModelsAsync();Check(vm.RequiredModelsReady&&vm.TurboCard.Ready&&fake.Requests.Count==2&&!vm.T2ICard.Ready,"Without enhancement, automatic download only prepares Qwen and permits continuing after file validation");
        Check(!vm.TurboCard.DownloadCommand.CanExecute(null)&&!vm.RequiredModelsDownloadCommand.CanExecute(null),"Ready model files disable redundant download actions");await Render(wizard,760,700,"models-ready-basic");
        vm.EnhancementEnabled=true;fake.Fail="t2i";await vm.Setup.DownloadModelsAsync();
        Check(fake.Requests[^1].Resource=="t2i"&&fake.Requests.Count==3&&vm.T2ICard.Failed&&!vm.I2ICard.Ready&&!vm.Setup.CanContinue,"A failed enhancement download stops the queue, shows retry and prevents continuing with missing files");await Render(wizard,760,700,"models-download-failed");
        fake.Fail=null;fake.Hold="i2i";var retry=vm.Setup.DownloadModelsAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);var limit=DateTime.UtcNow.AddSeconds(20);while(vm.DownloadingKey!="i2i"){if(DateTime.UtcNow>limit)throw new TimeoutException("I2I queue did not start");await Task.Delay(10);}
        Check(vm.T2ICard.Ready&&vm.IsDownloading&&fake.Requests[^1].Resource=="i2i"&&fake.Requests.Count==5,"Retry skips ready Qwen, completes T2I, then starts I2I in a single owned queue");await vm.StopModelDownloadAsync();await retry;
        fake.Hold=null;await vm.Setup.DownloadModelsAsync();Check(vm.RequiredModelsReady&&vm.Setup.CanContinue&&fake.Requests.Count==6&&fake.Requests[^1].Resource=="i2i","After cancellation, retry downloads only the remaining model and unlocks the next step");await Render(wizard,760,700,"models-ready-all");
        vm.T2ICard.Path=Path.Combine(settings.Workspace,"other-t2i");vm.UseHuggingFace=true;fake.OmitFiles=true;await vm.Setup.DownloadModelsAsync();
        Check(!vm.T2ICard.Ready&&vm.T2ICard.Failed&&!vm.Setup.CanContinue&&fake.Requests[^1].Source=="huggingFace","A success event with incomplete local files is rejected, and the changed source reaches the retry");
        var guideWindow=vm.CreateModelGuide(vm.T2ICard);await Render(guideWindow,600,490,"model-guide-automatic");
        Check(Children<Button>((DependencyObject)guideWindow.Content).Any(b=>AutomationProperties.GetAutomationId(b)=="GuideAutomaticDownload"&&b.IsEnabled),"Download guides provide the same automatic action as their model cards");
        Check(Children<Expander>((DependencyObject)guideWindow.Content).Single(e=>AutomationProperties.GetAutomationId(e)=="ManualModelDownload").IsExpanded==false,"Manual commands are optional and collapsed in the download guide");guideWindow.Close();
        vm.SetLanguage("en");await Render(wizard,760,700,"models-download-en");vm.SetLanguage("zh");await Render(wizard,620,470,"models-download-small");wizard.Close();
        await NativeAutomaticModelDownload();
    }
    static async Task NativeAutomaticModelDownload()
    {
        var native=AppSettings.Detect();
        var cancelWorkspace=Path.Combine(root,"native-download-cancel");var cancelDestination=Path.Combine(cancelWorkspace,"PE-T2I");SeedPe(cancelDestination,"t2i",false);
        using(var cancellation=new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();
            var cancelled=await new NativeModelDownloader().DownloadAsync(new("t2i","modelScope",cancelDestination,native.Python,cancelWorkspace),new Progress<ModelDownloadProgress>(),cancellation.Token);
            Check(cancelled.Cancelled&&watch.Elapsed<TimeSpan.FromSeconds(12),"The native downloader cancels its actual Python child within the bounded stop interval");
        }
        native.Workspace=Path.Combine(root,"native-automatic-download");native.EnhancementEnabled=true;native.DownloadSource="modelScope";
        native.T2IDirectory=Path.Combine(native.Workspace,"models","PE-T2I");SeedPe(native.T2IDirectory,"t2i",false);
        Check(!File.Exists(Path.Combine(native.T2IDirectory,"system_prompt.txt")),"Native automatic-download acceptance starts with an actually missing required file and reuses existing GGUF bytes");
        var prefs=Path.Combine(root,"native-download-prefs");Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=native});using var vm=new MainViewModel(prefs);vm.Setup.Page=2;
        var wizard=new FirstRunWindow(vm);await Render(wizard,760,700,"models-native-before");vm.RequiredModelsDownloadCommand.Execute(null);await UntilDownloadIdle(vm,240);
        Check(vm.T2ICard.Ready&&vm.RequiredModelsReady&&vm.Setup.CanContinue,"The actual wizard download command fetches missing PE-T2I files from ModelScope, verifies them and enables continuing");
        var manifest=Path.Combine(native.T2IDirectory,".lightx2v-model.json");Check(File.Exists(manifest)&&File.Exists(Path.Combine(native.T2IDirectory,"system_prompt.txt")),"The real native download writes the verified repository manifest and previously missing system prompt");
        await Render(wizard,760,700,"models-native-ready");Files.Write(Path.Combine(root,"native-model-download.json"),new{source=native.DownloadSource,downloadedMissingFile="system_prompt.txt",nativeCancellationVerified=true,reusedExistingGguf=true,downloadedWholeModel=false,ready=vm.RequiredModelsReady,modelManifest=manifest,logs=Directory.GetDirectories(Path.Combine(native.Workspace,"downloads"))});wizard.Close();
    }
}
