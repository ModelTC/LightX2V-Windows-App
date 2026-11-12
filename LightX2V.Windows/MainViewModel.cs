using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LightX2V.Windows;

public sealed partial class MainViewModel : Observable, IDisposable
{
    readonly string preferencesPath; readonly Dispatcher dispatcher; readonly DispatcherTimer timer; BridgeClient? active; Generation? activeJob; string creation = Guid.NewGuid().ToString("N");
    readonly IEnvironmentCatalog environmentCatalog;
    CancellationTokenSource? checkCancellation,preflightCancellation; Task? checkTask; string? activeCheckKey,verifiedEnvironmentKey;
    long environmentRevision; bool preflighting,viewModelDisposed;
    readonly List<Generation> all = []; bool loading; bool isChecking; bool ready; bool stopping; string prompt = ""; string logs = ""; string device = "GPU"; string toast = ""; bool showLog; bool showInspector = true; int tier = 1; string ratio = "Adaptive"; bool randomSeed = true; string seed = "42";
    public L10n T => L10n.Current; public AppSettings Settings { get; private set; }
    public ObservableCollection<Generation> Recent { get; } = []; public ObservableCollection<Generation> Turns { get; } = [];
    Generation? selected; public Generation? Selected { get => selected; set { if (Set(ref selected, value) && value != null) { creation = value.CreationId; RestoreCreation(value);RefreshTurns();logTarget=value;Changed(nameof(LogTarget));RefreshLogPanel();Persist(); } } }
    public string Prompt { get => prompt; set { Set(ref prompt, value); Changed(nameof(CanGenerate)); CommandManager.InvalidateRequerySuggested(); } }
    public int Tier { get => tier; set { if(Set(ref tier, Math.Clamp(value,0,2)))RememberCreation(); Changed(nameof(SizeText));Changed(nameof(CanGenerate));CommandManager.InvalidateRequerySuggested(); } }
    public string Ratio { get => ratio; set { if(Set(ref ratio, value))RememberCreation(); Changed(nameof(SizeText));Changed(nameof(CanGenerate));CommandManager.InvalidateRequerySuggested(); } }
    public bool RandomSeed { get => randomSeed; set { Set(ref randomSeed, value); Changed(nameof(FixedSeed)); } }
    public bool FixedSeed => !RandomSeed; public string Seed { get => seed; set => Set(ref seed, value); }
    public string SizeText { get { var d = CurrentDimensions(); return Ratio=="Adaptive"?T["Automatic"]:ValidDimensions?$"{d.Width} × {d.Height} px":T["Choose an aspect ratio"]; } }
    public bool IsPreflighting {get=>preflighting;private set{Set(ref preflighting,value);RefreshStatus();}}
    public bool IsGenerationBusy=>IsRunning||IsPreflighting;
    public bool IsRunning => active is not null; public bool IsStopping { get => stopping; private set { Set(ref stopping, value); Changed(nameof(GenerateLabel)); } }
    public bool IsChecking { get => isChecking; private set { Set(ref isChecking, value); RefreshStatus(); } }
    public bool Ready { get => ready; private set { Set(ref ready, value); RefreshStatus(); } }
    public string Device { get => device; private set => Set(ref device, value); }
    public string EnvironmentStatus => IsChecking ? T["Checking environment"] : Ready ? T["Ready"] : File.Exists(Settings.Python)&&TurboCard.Ready ? T["Ready to create"] : T["Not connected"];
    public string Logs { get => logs; private set => Set(ref logs, value); }
    public string Toast { get => toast; private set {Set(ref toast,value);Changed(nameof(VisibleToast));} }
    public string VisibleToast => Toast==T["Environment connected"]?"":Toast;
    public Command ModelFolderCommand { get; }
    bool modelExpanded=false,parametersExpanded=false;
    public bool ModelExpanded {get=>modelExpanded;set{if(Set(ref modelExpanded,value))SavePreferences();}}
    public bool ParametersExpanded {get=>parametersExpanded;set{if(Set(ref parametersExpanded,value))SavePreferences();}}
    public bool ShowLog { get => showLog; set => Set(ref showLog, value); }
    public bool ShowInspector { get => showInspector; set => Set(ref showInspector, value); }
    public bool Welcome => Turns.Count == 0; public bool NeedsInitialSetup=>!File.Exists(Settings.Python)||Setup?.Driver.Pending==true; public bool CanGenerate => HasSelectedModel && WorkspaceAvailable && !NeedsInitialSetup && TurboCard.Ready && !IsPreparingSetup && Setup?.Driver.Pending!=true && !IsGenerationBusy && !IsImporting && !IsBaseModelDownloading && EnhancementReady && ValidDimensions && Prompt.Trim().Length is > 0 and <= 20000;
    public string GenerateLabel => IsGenerationBusy ? T[IsStopping ? "Stopping" : "Stop generation"] : T["Generate image"];
    public Command NewCommand { get; }
    public Command GenerateCommand { get; }
    public Command SubmitCommand {get;}
    public Command StopCommand {get;}
    public Command SettingsCommand { get; }
    public Command CheckCommand { get; }
    public Command LogCommand { get; }
    public Command InspectorCommand { get; }
    public Command ReturnCommand { get; }
    public ParameterCommand ExampleCommand { get; }
    public ParameterCommand ReuseCommand { get; }
    public ParameterCommand CopyCommand { get; }
    public ParameterCommand RevealCommand { get; }
    public ParameterCommand ExportCommand { get; }
    public ParameterCommand OpenLogCommand { get; }
    public ParameterCommand RemoveCommand { get; }
    public Action? RequestSettings; public event Action? ContentUpdated;
    public MainViewModel(string? preferencesRoot = null,IModelDownloader? modelDownloader=null,IEnvironmentCatalog? environmentCatalog=null,IDriverPlatform? driverPlatform=null)
    {
        this.modelDownloader=modelDownloader??new NativeModelDownloader();
        this.environmentCatalog=environmentCatalog??new NativeEnvironmentCatalog();
        dispatcher = Dispatcher.CurrentDispatcher; var useDefaultPreferences=preferencesRoot==null; preferencesRoot ??= AppSettings.PreferencesRoot(); preferencesPath = Path.Combine(preferencesRoot, "preferences.json");
        var saved = StateStore.LoadPreferences(preferencesPath);
        var prefs = saved.Preferences;
        if (prefs is null && saved.Notice is null && useDefaultPreferences && AppSettings.LegacyPreferences() is { } old)
        {
            saved = StateStore.LoadPreferences(old);
            prefs = saved.Preferences;
            if (prefs is not null) StateStore.SavePreferences(preferencesPath, prefs);
        }
        T.Use(prefs?.Language ?? "zh");
        string? startupNotice = saved.Notice;
        Settings = prefs?.Settings ?? AppSettings.Detect(message => startupNotice = message);
        modelExpanded=prefs?.ModelExpanded??false;parametersExpanded=prefs?.ParametersExpanded??false;
        InitializeImages();InitializeModels();InitializeSetup(this.environmentCatalog,driverPlatform);InitializeGenerationGuidance();
        LoadWorkspace();
        if (startupNotice is not null) Toast = T[startupNotice] + (Toast.Length > 0 ? Environment.NewLine + Toast : "");
        SubmitCommand=new(()=>_=GenerateAsync(),()=>CanGenerate);StopCommand=new(()=>_=StopAsync(),()=>IsGenerationBusy&&!IsStopping);
        NewCommand = new(NewCreation,()=>!IsImporting); GenerateCommand = new(() => { if (IsGenerationBusy) _ = StopAsync(); else _ = GenerateAsync(); }, () => IsGenerationBusy && !IsStopping || CanGenerate);
        SettingsCommand = new(() => RequestSettings?.Invoke(), () => !IsGenerationBusy && !IsImporting && !IsDownloading && !IsPreparingSetup); CheckCommand = new(() => _ = CheckForUiAsync(), () => !IsGenerationBusy && !IsChecking && !IsDownloading);
        ModelFolderCommand = new(async()=>{try{await WindowsShell.RevealAsync(Settings.Model);Toast="";}catch(Exception e){Toast=ShellFailure(e,"Unable to open Explorer");}});
        LogCommand = new(ToggleLog); InspectorCommand = new(() => ShowInspector = !ShowInspector); ReturnCommand = new(() => { if (activeJob != null) Selected = activeJob; });
        ExampleCommand = new(p => Prompt = ExamplePrompt(p?.ToString())); ReuseCommand = new(p => Reuse(p as Generation)); CopyCommand = new(p => Copy(p as Generation)); RevealCommand = new(p => _ = RevealAsync(p as Generation)); ExportCommand = new(p => Export(p as Generation)); OpenLogCommand = new(p => OpenLog(p as Generation)); RemoveCommand = new(p => Remove(p as Generation));
        timer = new(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { activeJob?.Refresh();RefreshLiveLog(); }, dispatcher); timer.Start();
    }
    static string ExamplePrompt(string? key) => key switch { "light" => "A quiet Japanese cafe on a rainy afternoon, soft window light, cinematic photography, warm tones", "nature" => "A tiny glass house in a misty green forest, soft morning sunlight, architectural photography, peaceful atmosphere", _ => "A capybara wearing a wizard hat sits at a desk, reading a book by candlelight. Oil painting style, delicate brushwork, warm tones." };
    public bool CanSelectModel=>!IsGenerationBusy&&!IsPreparingSetup;
    public bool HasSelectedModel=>Settings.ModelSelected;
    public string ModelSelectorLabel=>HasSelectedModel?"Qwen-Image-2.1-Turbo · "+T["Standard"]:T["Choose model"];
    public void SelectStandardModel()
    {
        if(IsGenerationBusy||IsPreparingSetup)return;
        Settings.ModelSelected=true;ModelExpanded=true;ParametersExpanded=true;SavePreferences();RefreshStatus();
    }
    public void SetLanguage(string language) { T.Use(language); Toast=T.Relocalize(Toast);SavePreferences(); foreach (var g in all) g.Refresh(); RefreshLogPanel(); RefreshStatus(); ContentUpdated?.Invoke(); }
    public void ApplySettings(AppSettings settings)
    {
        if(IsGenerationBusy||IsDownloading)throw new InvalidOperationException("Wait for the active generation or model download to finish.");
        settings.ValidateWorkspace();var keepVerified=Ready&&verifiedEnvironmentKey==EnvironmentKey(settings);var different=settings.Workspace!=Settings.Workspace;
        var reload = different || !WorkspaceAvailable;
        var history = reload ? StateStore.LoadHistory(settings.Workspace) : null;
        InvalidateEnvironment(keepVerified);Settings=settings.Clone();SavePreferences();
        if(reload){all.Clear();InputImages.Clear();Prompt="";LoadWorkspace(history);}
        Changed(nameof(Settings));Changed(nameof(NeedsInitialSetup));ReloadModelPaths();RefreshStatus();
    }
    void RefreshRecent() { var id = selected?.CreationId; loading = true; Recent.Clear(); foreach (var g in all.GroupBy(g => g.CreationId).Select(g => g.OrderByDescending(x => x.CreatedAt).First()).OrderByDescending(g => g.CreatedAt)) Recent.Add(g); selected = Recent.FirstOrDefault(g => g.CreationId == id); loading = false; Changed(nameof(Selected)); }
    void RefreshTurns() { Turns.Clear(); foreach (var g in all.Where(g => g.CreationId == creation).OrderBy(g => g.CreatedAt)) Turns.Add(g); Changed(nameof(Welcome)); ContentUpdated?.Invoke(); }
    public void NewCreation() { if(IsImporting)return;if(IsPreflighting){preflightCancellation?.Cancel();CancelEnvironmentCheck();}InputImages.Clear(); creation = Guid.NewGuid().ToString("N"); selected = null; Changed(nameof(Selected)); Prompt = "";tier=1;ratio="Adaptive";Settings.EnhancementEnabled=true;RandomSeed=true;Changed(nameof(Tier));Changed(nameof(Ratio));Changed(nameof(EnhancementEnabled));Changed(nameof(SizeText));RememberCreation(); Turns.Clear(); Changed(nameof(Welcome)); ResetLogViewer(); Toast = ""; ContentUpdated?.Invoke(); }
    void RefreshStatus() { foreach (var n in new[] { nameof(EnvironmentStatus), nameof(CanGenerate), nameof(IsRunning),nameof(IsPreflighting),nameof(IsGenerationBusy), nameof(GenerateLabel),nameof(CanAddImages),nameof(HasSelectedModel),nameof(CanSelectModel),nameof(ModelSelectorLabel) }) Changed(n); ModelStateChanged();CommandManager.InvalidateRequerySuggested(); }

    static string EnvironmentKey(AppSettings settings)
    {
        // A session result is tied to the executable and environment configuration.
        // Nothing is persisted as trusted GPU readiness across app launches.
        FileInfo? python=null;try{if(!string.IsNullOrWhiteSpace(settings.Python))python=new FileInfo(settings.Python);}catch(ArgumentException){}
        return string.Join("\n",settings.Python.ToUpperInvariant(),settings.Workspace.ToUpperInvariant(),settings.Repository.ToUpperInvariant(),settings.Backend,
            python?.Exists==true?python.Length:0,python?.Exists==true?python.LastWriteTimeUtc.Ticks:0);
    }
    void CancelEnvironmentCheck()
    {
        var pending=checkCancellation;checkCancellation=null;checkTask=null;activeCheckKey=null;
        pending?.Cancel();IsChecking=false;
    }
    void InvalidateEnvironment(bool keepVerified=false)
    {
        environmentRevision++;CancelEnvironmentCheck();
        if(!keepVerified){verifiedEnvironmentKey=null;VerifiedEnvironment=null;Ready=false;}
    }
    async Task CheckForUiAsync(){try{await CheckAsync();}catch(OperationCanceledException){}}
    public async Task CheckAsync(CancellationToken token=default)
    {
        if(IsRunning||viewModelDisposed)return;token.ThrowIfCancellationRequested();
        var key=EnvironmentKey(Settings);
        if(checkTask is {IsCompleted:false} pending&&activeCheckKey==key){await pending.WaitAsync(token);return;}
        CancelEnvironmentCheck();var settings=Settings.Clone();var revision=environmentRevision;
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);checkCancellation=cancellation;activeCheckKey=key;
        IsChecking=true;Ready=false;verifiedEnvironmentKey=null;VerifiedEnvironment=null;Toast=T["Checking environment"];
        var task=RunEnvironmentCheckAsync(settings,key,revision,cancellation);checkTask=task;
        await task.WaitAsync(token);
    }
    async Task RunEnvironmentCheckAsync(AppSettings settings,string key,long revision,CancellationTokenSource cancellation)
    {
        var token=cancellation.Token;
        bool Current()=>!viewModelDisposed&&revision==environmentRevision&&ReferenceEquals(checkCancellation,cancellation)&&EnvironmentKey(Settings)==key;
        try
        {
            settings.ValidateEnvironment();
            if(settings.Repository.Equals(SetupInstaller.SourceDirectory(settings.Workspace),StringComparison.OrdinalIgnoreCase)||!File.Exists(Path.Combine(settings.Repository,"lightx2v","infer.py")))
                settings.Repository=await SetupInstaller.PrepareSource(settings.Workspace,token);
            var probe=await environmentCatalog.Probe(settings.Python,settings.Workspace,token,settings.Backend);
            token.ThrowIfCancellationRequested();if(!Current())return;
            if(probe.Ready&&(!probe.Path.Equals(settings.Python,StringComparison.OrdinalIgnoreCase)||(settings.Backend!="auto"&&probe.Backend!=settings.Backend)))throw new InvalidOperationException("The environment probe does not match the selected Python and backend.");
            Settings.Repository=settings.Repository;Changed(nameof(Settings));ApplyEnvironmentProbe(probe);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception e){if(Current())Toast=T["Environment check failed"]+": "+e.Message;}
        finally
        {
            if(ReferenceEquals(checkCancellation,cancellation)){checkCancellation=null;activeCheckKey=null;IsChecking=false;}
            cancellation.Dispose();
        }
    }
    internal async Task<bool> EnsureGenerationEnvironmentAsync(CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(Ready&&verifiedEnvironmentKey==EnvironmentKey(Settings))return true;
        await CheckAsync(token);token.ThrowIfCancellationRequested();
        return Ready&&verifiedEnvironmentKey==EnvironmentKey(Settings);
    }
    internal void CommitVerifiedEnvironment(AppSettings settings,EnvironmentCandidate probe)
    {
        settings.ValidateEnvironment();
        if(!probe.Ready||probe.Backend!=settings.Backend||!probe.Path.Equals(settings.Python,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The environment probe does not match the chosen Python and backend.");
        CommitSetup(settings);ApplyEnvironmentProbe(probe);
    }
    internal EnvironmentCandidate? VerifiedEnvironment {get;private set;}
    void ApplyEnvironmentProbe(EnvironmentCandidate probe)
    {
        VerifiedEnvironment=probe.Ready?probe:null;
        Ready=probe.Ready;
        if(Ready){Settings.Backend=probe.Backend;ResourceWarning=ResourceWarnings(probe);Changed(nameof(ResourceWarning));Device=probe.Detail.Split(" · ")[0].Replace("(TM)","").Replace(" Graphics","");}
        verifiedEnvironmentKey=Ready?EnvironmentKey(Settings):null;
        foreach(var card in ModelCards.Where(card => !card.IsDownloading)) RefreshModel(card);
        ModelStateChanged();
        Toast=Ready?T["Environment connected"]:T["Environment check failed"]+": "+probe.Detail;SavePreferences();
    }
    public async Task GenerateAsync()
    {
        if (!CanGenerate) return; long seedValue; if (RandomSeed) seedValue = Random.Shared.NextInt64(4294967296L); else if (!long.TryParse(Seed, out seedValue) || seedValue < 0 || seedValue > uint.MaxValue) { Toast = T["Enter a valid seed"]; return; }
        // Capture the submitted draft before awaiting the first-session GPU check.
        // Editing another draft or selecting history while it runs cannot change this request.
        var settings=Settings.Clone();var submittedRevision=environmentRevision;var d=CurrentDimensions();var submittedPrompt=Prompt;var submittedCreation=creation;
        var submittedTier=Tier;var submittedRatio=Ratio;var submittedRandomSeed=RandomSeed;var submittedSeed=Seed;var submittedEnhancement=EnhancementEnabled;
        var images=InputImages.Select(x=>new InputImage{Id=x.Id,Path=x.Path,Name=x.Name,Width=x.Width,Height=x.Height}).ToList();
        PromptEnhancementSettings? enhancement;
        try{enhancement=submittedEnhancement?SnapshotEnhancement():null;}catch(Exception e){Toast=e.Message;return;}
        int? resolution=submittedRatio=="Adaptive"?512*(1<<submittedTier):null;
        using(var cancellation=new CancellationTokenSource())
        {
            preflightCancellation=cancellation;IsPreflighting=true;
            try
            {
                if(!await EnsureGenerationEnvironmentAsync(cancellation.Token))return;
                if(viewModelDisposed||environmentRevision!=submittedRevision)cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                settings.Backend=Settings.Backend;settings.Repository=Settings.Repository;
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested){Toast="";return;}
            catch(Exception e){Toast=T["Environment check failed"]+": "+e.Message;return;}
            finally{if(ReferenceEquals(preflightCancellation,cancellation))preflightCancellation=null;IsPreflighting=false;IsStopping=false;}
        }
        var g=new Generation{CreationId=submittedCreation,Python=settings.Python};
        g.Directory=Path.Combine(settings.Workspace,"outputs",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+g.Id[..8]);
        try{settings.Validate();Directory.CreateDirectory(g.Directory);}catch(Exception e){Toast=e.Message;return;}
        g.Request=new(){Backend=settings.Backend,Repository=settings.Repository,Model=settings.Model,Config=Path.Combine(AppContext.BaseDirectory,"Resources","qwen_image_21_turbo.json"),
            Prompt=submittedPrompt.Trim(),Width=d.Width,Height=d.Height,Seed=seedValue,Output=Path.Combine(g.Directory,"image.png"),PromptEnhancement=enhancement,Resolution=resolution};
        try
        {
            foreach(var image in images)
            {
                var folder=Path.Combine(g.Directory,"inputs");Directory.CreateDirectory(folder);var path=Path.Combine(folder,$"{g.Request.InputImages.Count+1:D2}.png");File.Copy(image.Path,path,false);
                g.Request.InputImages.Add(new(){Id=image.Id,Name=image.Name,Path=path,Width=image.Width,Height=image.Height});
            }
            g.Request.Task=images.Count>0?"i2i":"t2i";if(submittedEnhancement)g.EnhancementStatus="thinking";
        }
        catch(Exception e){Toast=e.Message;return;}
        var unchanged=creation==submittedCreation&&Prompt==submittedPrompt&&Tier==submittedTier&&Ratio==submittedRatio&&RandomSeed==submittedRandomSeed&&Seed==submittedSeed&&EnhancementEnabled==submittedEnhancement&&
            InputImages.Select(x=>(x.Id,x.Path)).SequenceEqual(images.Select(x=>(x.Id,x.Path)));
        if(unchanged){InputImages.Clear();Prompt="";}
        var client = new BridgeClient();
        activeJob=g;active=client;all.Add(g);Toast="";BeginGenerationLogs(g);RefreshRecent();if(creation==submittedCreation)Selected=g;RefreshTurns();RefreshStatus();bool terminal=false;
        try
        {
            Files.Write(Path.Combine(g.Directory, "request.json"), g.Request);
            Persist();
            var code = await client.RunAsync(settings.Python, "run", Path.Combine(g.Directory, "request.json"), Path.Combine(settings.Workspace, "tmp"),
                message => dispatcher.Invoke(() => terminal |= HandleGenerationEvent(g, message)));
            if (!terminal)
            {
                g.Status = IsStopping ? "cancelled" : "failed";
                g.Error = $"Backend exited without a completion event (code {code}).";
            }
        }
        catch (Exception error)
        {
            g.Status = IsStopping ? "cancelled" : "failed";
            g.Error = error.Message;
            Log(error.Message);
        }
        finally { FinishGeneration(g, client); }
    }
    void Reuse(Generation? g) { if (g == null||IsImporting) return;Selected=g;InputImages.Clear();foreach(var image in g.Request.InputImages)InputImages.Add(image); Prompt = g.Request.Prompt;EnhancementEnabled=g.Request.PromptEnhancement?.Enabled==true; RandomSeed = true;if(g.Request.Resolution is int resolution){Tier=resolution==2048?2:resolution==512?0:1;Ratio="Adaptive";return;} for (int t = 0; t < 3; t++) foreach (var r in Presets.Ratios.Where(r=>r!="Adaptive")) { if (Presets.Dimensions(t, r) == (g.Request.Width, g.Request.Height)) { Tier = t; Ratio = r; return; } } if(g.Request.InputImages.Count>0){var image=g.Request.InputImages[^1];for(int t=0;t<3;t++)if(Presets.AdaptiveDimensions(t,image.Width,image.Height)==(g.Request.Width,g.Request.Height)){Tier=t;Ratio="Adaptive";return;}} }
    void Copy(Generation? g) { if (g?.Completed != true) return; try { Clipboard.SetImage(ImageLoader.Load(g.Request.Output, 0)); Toast = T["Copied"]; } catch (Exception e) { Toast = e.Message; } }
    void Export(Generation? g) { if (g?.Completed != true) return; var dlg = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "LightX2V-" + g.Id[..8] + ".png" }; if (dlg.ShowDialog() == true) { try { File.Copy(g.Request.Output, dlg.FileName, true); Toast = T["Exported"]; } catch (Exception e) { Toast = e.Message; } } }
    void Remove(Generation? g) { if (g == null || all.Any(x => x.CreationId == g.CreationId && x.Running)) return; all.RemoveAll(x => x.CreationId == g.CreationId);creationSettings.Remove(g.CreationId); RefreshRecent(); if (creation == g.CreationId) NewCreation(); Persist(); }
    public void Dispose() { viewModelDisposed=true;preflightCancellation?.Cancel();InvalidateEnvironment();DisposeImages();DisposeModels();DisposeSetup();timer.Stop(); active?.Dispose(); }
}
