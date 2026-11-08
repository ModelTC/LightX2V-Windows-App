using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightX2V.Windows;

internal static partial class Program
{
    static bool smoke,signatures,completionOnly,hardwareOnly,startupOnly;static string root="";static readonly List<string> passed=[];
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);passed.Add(message);}
    static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T typed)yield return typed;foreach(var nested in Children<T>(child))yield return nested;}
    }
    static async Task Render(Window window,int width,int height,string name)
    {
        var content=(FrameworkElement)window.Content;content.DataContext=window.DataContext;content.Width=width;content.Height=height;
        content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);content.UpdateLayout();
        foreach(var id in new[]{"SetupContinue","SetupBack","SetupClose","EnvironmentSearch","EnvironmentClose"})
        {
            var button=Children<Button>(content).FirstOrDefault(b=>AutomationProperties.GetAutomationId(b)==id);if(button==null)continue;
            var bounds=button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));Check(bounds.Left>=-1&&bounds.Top>=-1&&bounds.Right<=width+1&&bounds.Bottom<=height+1,"Footer action stays inside "+name+": "+id);
        }
        foreach(var scroll in Children<ScrollViewer>(content).Where(s=>s.VerticalScrollBarVisibility==ScrollBarVisibility.Auto))Check(scroll.ExtentWidth<=scroll.ViewportWidth+2,"Content fits the horizontal viewport in "+name);
        var drawing=new DrawingVisual();using(var context=drawing.RenderOpen()){context.DrawRectangle(window.Background,null,new Rect(0,0,width,height));context.DrawRectangle(new VisualBrush(content){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,width,height),ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,width,height)},null,new Rect(0,0,width,height));}
        var bitmap=new RenderTargetBitmap(width*2,height*2,192,192,PixelFormats.Pbgra32);bitmap.Render(drawing);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(root,name+".png"));png.Save(file);
    }
    static EnvironmentCandidate Valid(string path)=>new(path,true,"AMD Radeon 8060S · 2.11.0+rocm7.13.0"){PythonVersion="3.12.10",TorchVersion="2.11.0+rocm7.13.0",HipVersion="7.13.0",Device="AMD Radeon 8060S",Packages=new Dictionary<string,string>{{"transformers","5.20.2"},{"diffusers","0.42.0"},{"safetensors","0.8.0"},{"accelerate","1.15.0"}}};
    [STAThread]static int Main(string[] args)
    {
        startupOnly=args.Contains("startup");hardwareOnly=args.Contains("hardware");completionOnly=args.Contains("completion");smoke=args.Contains("smoke");signatures=args.Contains("signatures");root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/LightX2V.Windows;component/Resources/Theme.xaml")});int result=1;
        PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(Path.Combine(root,"bindings.log")));
        app.Dispatcher.BeginInvoke(async()=>{try{await Run();File.WriteAllText(Path.Combine(root,"checks.json"),JsonSerializer.Serialize(new{passed,completedAt=DateTimeOffset.Now,offscreenRendering=true,realUiAutomation=false},new JsonSerializerOptions{WriteIndented=true}));result=0;}catch(Exception e){File.WriteAllText(Path.Combine(root,"failure.txt"),e.ToString());}finally{Trace.Flush();app.Shutdown();}});app.Run();return result;
    }
    static async Task Run()
    {
        await StartupChecks();await ReadinessChecks();
        if(startupOnly)return;
        if(hardwareOnly){await HardwareDetectionChecks();await DriverChecks();await WizardPreparationChecks();await CompletionChecks();return;}
        await HardwareDetectionChecks();if(completionOnly){await CompletionChecks();return;}
        await CompletionChecks();await DriverChecks();await WizardPreparationChecks();await ModelDownloadChecks();
        var hybrid=HardwareInfo.FromAdapters([new("AMD Radeon 8060S","AMD driver"),new("NVIDIA GeForce RTX 4090","NVIDIA driver")]);
        Check(hybrid.Backend=="cuda"&&hybrid.Supports("cuda")&&hybrid.Supports("amd_rocm"),"Hybrid graphics retain both adapters and prefer the discrete NVIDIA runtime");
        Check(SetupInstaller.EnvironmentDirectory(root,"cuda")!=SetupInstaller.EnvironmentDirectory(root,"amd_rocm"),"CUDA and ROCm installations use separate managed directories");
        Check(!SetupManifest.Read("cuda").Packages.Any(p=>p.Name.StartsWith("rocm"))&&SetupManifest.Read("cuda").Packages.Single(p=>p.Name=="torch").Version=="2.11.0+cu128","The pinned CUDA installation contains CUDA PyTorch and no ROCm wheels");
        CudaDriver.ValidateVersion(12080);CudaDriver.ValidateVersion(13000);
        try{CudaDriver.ValidateVersion(12060);throw new Exception("Missing driver guard");}catch(InvalidOperationException){Check(true,"An older CUDA driver is rejected before the large environment download");}
        var guard=Path.Combine(root,"installation-guard");var guardEnv=SetupInstaller.EnvironmentDirectory(guard);Directory.CreateDirectory(guardEnv);var sentinel=Path.Combine(guardEnv,"user-file.txt");File.WriteAllText(sentinel,"preserve");
        try{await SetupInstaller.Install(guard,false,[],new Progress<SetupProgress>(),CancellationToken.None);throw new Exception("Missing installation ownership guard");}catch(IOException){Check(File.ReadAllText(sentinel)=="preserve","Native AMD automatic installation preserves an unowned environment directory");}
        var settings=AppSettings.Fresh(Path.Combine(root,"隔离工作区"));var fake=new FakeCatalog();var valid=Valid(@"C:\Users\amd\Documents\ChatGPT\工作目录 中文\environments\native-amd-gfx1151-v1\python.exe");var invalid=new EnvironmentCandidate(@"C:\Python312\python.exe",false,"Missing or incompatible dependencies: torch");fake.Found=[invalid,valid];
        using var model=new EnvironmentSelectionModel(settings,L10n.Current,true,fake,new DriverSetupModel("amd_rocm",()=>settings.Workspace,L10n.Current,new FakeDriverPlatform()));await model.SearchAsync();
        Check(model.Candidates.Count==2&&model.Candidates[0].Ready&&model.Summary.Contains("1"),"Search shows every result with compatible candidates first and the correct count");
        Check(model.Candidates[0].VersionRows.Count==4&&model.Requirements.Contains("2.11.0+rocm7.13.0")&&!model.Requirements.Contains("MPS"),"Dependency cards show the actual native AMD profile and package versions");
        model.SelectCommand.Execute(invalid);Check(model.Selected==null,"An incompatible result cannot be selected");
        model.SelectCommand.Execute(valid);Check(model.Selected==valid&&settings.Python=="","Selection does not silently save or mutate the caller's settings");
        fake.ProbeResult=valid;await model.CheckPythonAsync(valid.Path);Check(model.Candidates.Count==2,"Manually checked paths update the existing result instead of duplicating it");
        fake.InstallResult=valid.Path;model.Mirror=false;await model.InstallAsync();Check(fake.InstallCalls==1&&!fake.LastMirror&&model.Candidates.Count==2,"Explicit automatic installation uses the chosen official source and rechecks the installed environment");
        model.Mirror=true;await model.InstallAsync();Check(fake.LastMirror,"The selected Tsinghua source reaches the installer");
        fake.Delayed=true;var pending=model.SearchAsync();Check(model.Busy,"Search exposes an active state immediately");var before=model.Selected;model.SelectCommand.Execute(valid);Check(model.Selected==before,"Selection is blocked while a check is active");await model.CancelAsync();await pending;Check(!model.Busy&&model.Candidates.Count==2&&model.Error.Length>0,"Cancellation finishes cleanly and preserves previous search results");fake.Delayed=false;
        using(var unsupported=new EnvironmentSelectionModel(settings,L10n.Current,false,fake)){await unsupported.InstallAsync();Check(unsupported.Error.Length>0&&fake.InstallCalls==2,"Automatic installation stays disabled for unsupported hardware");}
        await model.SearchAsync();var picker=new EnvironmentPickerWindow(settings,L10n.Current,model);await Render(picker,800,700,"environment-results-zh");
        Check(Children<ComboBox>((DependencyObject)picker.Content).Single().SelectionBoxItem?.ToString()=="AMD ROCm","Backend picker shows the user-facing label instead of an internal object");
        Check(Children<TextBlock>((DependencyObject)picker.Content).Any(t=>t.Text==L10n.Current["Prepare pinned Python and GPU PyTorch dependencies."]),"Localized explanatory text with punctuation renders in full");
        var buttons=Children<Button>((DependencyObject)picker.Content).Where(b=>AutomationProperties.GetAutomationId(b)=="SelectEnvironment").ToArray();Check(buttons.Length==2&&buttons.Count(b=>b.IsEnabled)==1,"Only the compatible environment card offers an enabled selection action");
        await Render(picker,620,470,"environment-results-small");L10n.Current.Use("en");await Render(picker,800,700,"environment-results-en");L10n.Current.Use("zh");
        fake.DelayInstall=true;var installing=model.InstallAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);await Render(picker,800,700,"environment-installing");Check(model.Installing&&!buttons.Any(b=>b.IsEnabled),"Installing locks environment selection and exposes progress");await model.CancelAsync();await installing;Check(!model.Busy,"Automatic installation cancellation returns the picker to idle");fake.DelayInstall=false;
        model.Backend="cuda";var cuda=valid with{Path=@"C:\Users\amd\Documents\LightX2V\environments\native-nvidia-cu128-v1\python.exe",Device="NVIDIA GeForce RTX 4090",Backend="cuda",HipVersion="",CudaVersion="12.8",TorchVersion="2.11.0+cu128"};fake.Found=[cuda];fake.ProbeResult=cuda;await model.SearchAsync();await model.InstallAsync();Check(fake.LastBackend=="cuda"&&model.Candidates.Single().Backend=="cuda","The CUDA choice reaches search and automatic installation without reusing ROCm");await Render(picker,800,700,"environment-cuda-preview");model.Backend="amd_rocm";
        fake.Found=[];await model.SearchAsync();await Render(picker,800,620,"environment-empty");Check(model.Summary.Contains("0"),"An empty search has a zero-result state and keeps installation choices available");picker.Close();
        var prefs=Path.Combine(root,"preferences");Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});
        using(var vm=new MainViewModel(prefs))
        {
            var wizard=new FirstRunWindow(vm);await Render(wizard,760,660,"wizard-workspace");vm.Setup.Backend="cuda";Check(vm.Setup.EnvironmentSettings().Backend=="cuda"&&vm.Settings.Backend!="cuda","Wizard backend changes remain unsaved drafts");vm.Setup.Backend="amd_rocm";await vm.Setup.ScanComputerAsync();Check(vm.Setup.ComputerScanned&&vm.Setup.Backend=="amd_rocm"&&vm.Setup.HardwareLabel.Contains("8060S"),"The real wizard scans AMD hardware, selects ROCm and displays its computer information");vm.Setup.Environments.Add(valid);vm.Setup.AcceptCandidate(valid);vm.Setup.Page=1;await Render(wizard,760,660,"wizard-environment");await Render(wizard,620,470,"wizard-environment-small");
            vm.Setup.UseExisting=false;Check(!vm.Setup.UseExisting,"Installation mode changes without losing the selected local environment");
            var bodyScroll=Children<ScrollViewer>((DependencyObject)wizard.Content).First(s=>s.VerticalScrollBarVisibility==ScrollBarVisibility.Auto);bodyScroll.ScrollToBottom();await Render(wizard,760,660,"wizard-inline-components");bodyScroll.ScrollToTop();
            Check(!Children<Expander>((DependencyObject)wizard.Content).Any(e=>AutomationProperties.GetAutomationId(e)=="SetupLogExpander"),"The preparation log is removed from every wizard step");
            Check(!Children<Button>((DependencyObject)wizard.Content).Any(b=>AutomationProperties.GetAutomationId(b)=="SetupSearchEnvironment"),"The wizard has no duplicate modal environment chooser entry");
            var radios=Children<RadioButton>((DependencyObject)wizard.Content).Where(r=>AutomationProperties.GetAutomationId(r)=="SetupEnvironmentCandidate").ToArray();Check(radios.Length>=1&&radios.Any(r=>r.IsEnabled),"Verified local components are selectable inline in step two");
            var scan=Children<Border>((DependencyObject)wizard.Content).Single(b=>AutomationProperties.GetAutomationId(b)=="SetupComputerScan");var driverCard=Children<DriverSetupCard>((DependencyObject)wizard.Content).Single();Check(scan.TransformToAncestor((Visual)wizard.Content).Transform(new Point()).Y<driverCard.TransformToAncestor((Visual)wizard.Content).Transform(new Point()).Y,"Machine scan and backend recommendation appear before the driver and components");
            vm.Setup.Page=2;await Render(wizard,760,660,"wizard-models");vm.Setup.Page=3;await Render(wizard,760,660,"wizard-complete");vm.SetLanguage("en");vm.Setup.Page=1;await Render(wizard,760,660,"wizard-environment-en");vm.SetLanguage("zh");wizard.Close();
            var general=new SettingsWindow(vm);await Render(general,680,500,"settings");Check(Children<Button>((DependencyObject)general.Content).Any(b=>AutomationProperties.GetAutomationId(b)=="SearchEnvironment"),"General settings retains the entry to the dedicated environment picker");general.Close();
        }
        var native=AppSettings.Detect();var actual=await SetupDiscovery.Probe(native.Python,settings.Workspace,CancellationToken.None);
        Check(actual.Ready&&actual.PythonVersion.Length>0&&actual.TorchVersion.Contains("rocm")&&actual.VersionRows.Count==4,"A real native AMD probe populates the card's Python, PyTorch, ROCm and package versions");Files.Write(Path.Combine(root,"native-environment.json"),actual);
        if(signatures)await CheckOfficialAssistants();
        if(smoke)
        {
            var normal=AppSettings.Detect();normal.Workspace=Path.Combine(root,"native-smoke");normal.Backend="amd_rocm";normal.EnhancementEnabled=false;
            var smokePrefs=Path.Combine(root,"smoke-preferences");Files.Write(Path.Combine(smokePrefs,"preferences.json"),new Preferences{Settings=normal});
            using var vm=new MainViewModel(smokePrefs);await vm.CheckAsync();Check(vm.Ready,"The app connects to the real AMD backend after the dual-backend migration");
            vm.Tier=0;vm.Ratio="1:1";vm.Prompt="A blue ceramic teapot on a wooden table beside a sunny window, warm natural light, realistic photography.";
            await vm.GenerateAsync();var generated=vm.Turns.Last();Files.Write(Path.Combine(root,"amd-inference.json"),generated);
            Check(generated.Completed&&generated.Request.Backend=="amd_rocm"&&File.Exists(generated.Request.Output),"The migrated app completes a real 512 image through the AMD backend");
            Files.Write(Path.Combine(root,"backend-sha256.json"),Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"Backend"),"*.py").ToDictionary(p=>Path.GetFileName(p),p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()));
        }
    }
    sealed class FakeCatalog : IEnvironmentCatalog
    {
        public List<EnvironmentCandidate> Found=[];public EnvironmentCandidate? ProbeResult;public string InstallResult="";public int InstallCalls,SearchCalls,ProbeCalls,InstalledProbeCalls;public bool LastMirror,Delayed,DelayInstall,DelayProbe;public string LastBackend="",SearchBackend="";public List<string>? Events;
        public async Task<List<EnvironmentCandidate>> Search(AppSettings settings,IProgress<SetupProgress> progress,CancellationToken token){SearchCalls++;SearchBackend=settings.Backend;Events?.Add("search:"+SearchBackend);if(Delayed)await Task.Delay(Timeout.Infinite,token);return Found;}
        public async Task<EnvironmentCandidate> Probe(string path,string workspace,CancellationToken token,string backend="auto"){ProbeCalls++;if(DelayProbe)await Task.Delay(Timeout.Infinite,token);return ProbeResult??new EnvironmentCandidate(path,false,"Unavailable");}
        public async Task<EnvironmentCandidate> ProbeInstalled(string path,string workspace,CancellationToken token,string backend="auto"){InstalledProbeCalls++;if(DelayProbe)await Task.Delay(Timeout.Infinite,token);return ProbeResult??new EnvironmentCandidate(path,false,"Unavailable");}
        public async Task<string> Install(string workspace,bool mirror,IProgress<SetupProgress> progress,CancellationToken token,string backend="amd_rocm"){InstallCalls++;LastMirror=mirror;LastBackend=backend;progress.Report(new("Preparing your environment","Python / AMD PyTorch",45));if(DelayInstall)await Task.Delay(Timeout.Infinite,token);return InstallResult;}
    }
}
