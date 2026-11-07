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
    readonly List<Generation> all = []; bool loading; bool isChecking; bool ready; bool stopping; string prompt = ""; string logs = ""; string device = "GPU"; string toast = ""; bool showLog; bool showInspector = true; int tier = 1; string ratio = "Adaptive"; bool randomSeed = true; string seed = "42";
    public L10n T => L10n.Current; public AppSettings Settings { get; private set; }
    public ObservableCollection<Generation> Recent { get; } = []; public ObservableCollection<Generation> Turns { get; } = [];
    Generation? selected; public Generation? Selected { get => selected; set { if (Set(ref selected, value) && value != null) { creation = value.CreationId; RefreshTurns(); } } }
    public string Prompt { get => prompt; set { Set(ref prompt, value); Changed(nameof(CanGenerate)); CommandManager.InvalidateRequerySuggested(); } }
    public int Tier { get => tier; set { Set(ref tier, Math.Clamp(value,0,2)); Changed(nameof(SizeText));Changed(nameof(CanGenerate));CommandManager.InvalidateRequerySuggested(); } }
    public string Ratio { get => ratio; set { Set(ref ratio, value); Changed(nameof(SizeText));Changed(nameof(CanGenerate));CommandManager.InvalidateRequerySuggested(); } }
    public bool RandomSeed { get => randomSeed; set { Set(ref randomSeed, value); Changed(nameof(FixedSeed)); } }
    public bool FixedSeed => !RandomSeed; public string Seed { get => seed; set => Set(ref seed, value); }
    public string SizeText { get { var d = CurrentDimensions(); return ValidDimensions?$"{d.Width} × {d.Height} px":T["Choose an aspect ratio"]; } }
    public bool IsRunning => active is not null; public bool IsStopping { get => stopping; private set { Set(ref stopping, value); Changed(nameof(GenerateLabel)); } }
    public bool IsChecking { get => isChecking; private set { Set(ref isChecking, value); RefreshStatus(); } }
    public bool Ready { get => ready; private set { Set(ref ready, value); RefreshStatus(); } }
    public string Device { get => device; private set => Set(ref device, value); }
    public string EnvironmentStatus => IsChecking ? T["Checking environment"] : Ready ? T["Ready"] : T["Not connected"];
    public string Logs { get => logs; private set => Set(ref logs, value); }
    public string Toast { get => toast; private set {Set(ref toast,value);Changed(nameof(VisibleToast));} }
    public string VisibleToast => Toast==T["Environment connected"]?"":Toast;
    public Command ModelFolderCommand { get; }
    bool modelExpanded=true,parametersExpanded=true;
    public bool ModelExpanded {get=>modelExpanded;set{if(Set(ref modelExpanded,value))SavePreferences();}}
    public bool ParametersExpanded {get=>parametersExpanded;set{if(Set(ref parametersExpanded,value))SavePreferences();}}
    public bool ShowLog { get => showLog; set => Set(ref showLog, value); }
    public bool ShowInspector { get => showInspector; set => Set(ref showInspector, value); }
    public bool Welcome => Turns.Count == 0; public bool NeedsInitialSetup=>!File.Exists(Settings.Python)||Setup?.Driver.Pending==true; public bool CanGenerate => Ready && TurboCard.Ready && !IsPreparingSetup && Setup?.Driver.Pending!=true && !IsRunning && !IsChecking && !IsImporting && !IsDownloading && EnhancementReady && ValidDimensions && Prompt.Trim().Length is > 0 and <= 20000;
    public string GenerateLabel => IsRunning ? T[IsStopping ? "Stopping" : "Stop generation"] : T["Generate image"];
    public Command NewCommand { get; }
    public Command GenerateCommand { get; }
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
        dispatcher = Dispatcher.CurrentDispatcher; var useDefaultPreferences=preferencesRoot==null; preferencesRoot ??= AppSettings.PreferencesRoot(); preferencesPath = Path.Combine(preferencesRoot, "preferences.json");
        Preferences? prefs = null; try { if (File.Exists(preferencesPath)) prefs = Files.Read<Preferences>(preferencesPath); else if(useDefaultPreferences&&AppSettings.LegacyPreferences() is {} old) {prefs=Files.Read<Preferences>(old);Files.Write(preferencesPath,prefs);} } catch (Exception e) { Debug.WriteLine(e); }
        T.Use(prefs?.Language ?? "zh"); Settings = prefs?.Settings ?? AppSettings.Detect();
        modelExpanded=prefs?.ModelExpanded??true;parametersExpanded=prefs?.ParametersExpanded??true;
        InitializeImages();InitializeModels();InitializeSetup(environmentCatalog,driverPlatform);
        LoadWorkspace();
        NewCommand = new(NewCreation,()=>!IsImporting); GenerateCommand = new(() => { if (IsRunning) _ = StopAsync(); else _ = GenerateAsync(); }, () => IsRunning && !IsStopping || CanGenerate);
        SettingsCommand = new(() => RequestSettings?.Invoke(), () => !IsRunning && !IsChecking && !IsImporting && !IsDownloading && !IsPreparingSetup); CheckCommand = new(() => _ = CheckAsync(), () => !IsRunning && !IsChecking && !IsDownloading);
        ModelFolderCommand = new(async()=>{try{await WindowsShell.RevealAsync(Settings.Model);Toast="";}catch(Exception e){Toast=ShellFailure(e,"Unable to open Explorer");}});
        LogCommand = new(ToggleLog); InspectorCommand = new(() => ShowInspector = !ShowInspector); ReturnCommand = new(() => { if (activeJob != null) Selected = activeJob; });
        ExampleCommand = new(p => Prompt = ExamplePrompt(p?.ToString())); ReuseCommand = new(p => Reuse(p as Generation)); CopyCommand = new(p => Copy(p as Generation)); RevealCommand = new(p => _ = RevealAsync(p as Generation)); ExportCommand = new(p => Export(p as Generation)); OpenLogCommand = new(p => OpenLog(p as Generation)); RemoveCommand = new(p => Remove(p as Generation));
        timer = new(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { activeJob?.Refresh();RefreshLiveLog(); }, dispatcher); timer.Start();
    }
    static string ExamplePrompt(string? key) => key switch { "light" => "A quiet Japanese cafe on a rainy afternoon, soft window light, cinematic photography, warm tones", "nature" => "A tiny glass house in a misty green forest, soft morning sunlight, architectural photography, peaceful atmosphere", _ => "A capybara wearing a wizard hat sits at a desk, reading a book by candlelight. Oil painting style, delicate brushwork, warm tones." };
    public void SetLanguage(string language) { T.Use(language); SavePreferences(); foreach (var g in all) g.Refresh(); RefreshLogPanel(); RefreshStatus(); ContentUpdated?.Invoke(); }
    public void ApplySettings(AppSettings settings) { if (IsRunning||IsDownloading) throw new InvalidOperationException("Wait for the active generation or model download to finish."); settings.ValidateWorkspace(); var different = settings.Workspace != Settings.Workspace; Settings = settings.Clone(); SavePreferences(); Ready = false; if (different) { all.Clear(); LoadWorkspace(); NewCreation(); } Changed(nameof(Settings)); ReloadModelPaths(); _ = CheckAsync(); }
    void SavePreferences() => Files.Write(preferencesPath, new Preferences { Language = T.Language, Settings = Settings.Clone(),ModelExpanded=ModelExpanded,ParametersExpanded=ParametersExpanded });
    void LoadWorkspace()
    {
        if (string.IsNullOrWhiteSpace(Settings.Workspace)) return; try
        {
            var p = Path.Combine(Settings.Workspace, "workspace.json"); if (File.Exists(p)) all.AddRange(Files.Read<WorkspaceState>(p)?.Generations ?? []); else if (Directory.Exists(Path.Combine(Settings.Workspace, "outputs"))) foreach (var j in Directory.EnumerateFiles(Path.Combine(Settings.Workspace, "outputs"), "generation.json", SearchOption.AllDirectories)) { try { if (Files.Read<Generation>(j) is { } g) all.Add(g); } catch (JsonException) { } }
            foreach (var g in all.Where(g => g.Running)) { g.Status = "interrupted";if(g.HasEnhancement&&!g.EnhancementCompleted)g.EnhancementStatus="interrupted"; g.Error = T["Interrupted message"]; g.Seconds = Math.Max(0, (DateTimeOffset.Now - g.CreatedAt).TotalSeconds); }
            RefreshRecent(); Persist();
        }
        catch (Exception e) { Toast = e.Message; }
    }
    void Persist() { if (loading || string.IsNullOrWhiteSpace(Settings.Workspace)) return; try { Files.Write(Path.Combine(Settings.Workspace, "workspace.json"), new WorkspaceState { Generations = all }); foreach (var g in all.Where(g => g.Id == activeJob?.Id)) Files.Write(Path.Combine(g.Directory, "generation.json"), g); } catch (Exception e) { Toast = e.Message; } }
    void RefreshRecent() { var id = selected?.CreationId; loading = true; Recent.Clear(); foreach (var g in all.GroupBy(g => g.CreationId).Select(g => g.OrderByDescending(x => x.CreatedAt).First()).OrderByDescending(g => g.CreatedAt)) Recent.Add(g); selected = Recent.FirstOrDefault(g => g.CreationId == id); loading = false; Changed(nameof(Selected)); }
    void RefreshTurns() { Turns.Clear(); foreach (var g in all.Where(g => g.CreationId == creation).OrderBy(g => g.CreatedAt)) Turns.Add(g); Changed(nameof(Welcome)); ContentUpdated?.Invoke(); }
    public void NewCreation() { if(IsImporting)return;InputImages.Clear(); creation = Guid.NewGuid().ToString("N"); selected = null; Changed(nameof(Selected)); Prompt = ""; Turns.Clear(); Changed(nameof(Welcome)); ResetLogViewer(); Toast = ""; ContentUpdated?.Invoke(); }
    void RefreshStatus() { foreach (var n in new[] { nameof(EnvironmentStatus), nameof(CanGenerate), nameof(IsRunning), nameof(GenerateLabel),nameof(CanAddImages) }) Changed(n); ModelStateChanged();CommandManager.InvalidateRequerySuggested(); }

    public async Task CheckAsync(CancellationToken token=default)
    {
        if(IsRunning||IsChecking)return;IsChecking=true;Ready=false;Toast="";
        try
        {
            Settings.ValidateEnvironment();
            if(Settings.Repository.Equals(SetupInstaller.SourceDirectory(Settings.Workspace),StringComparison.OrdinalIgnoreCase)||!File.Exists(Path.Combine(Settings.Repository,"lightx2v","infer.py")))
            {Settings.Repository=await SetupInstaller.PrepareSource(Settings.Workspace,token);Changed(nameof(Settings));}
            var probe=await SetupDiscovery.Probe(Settings.Python,Settings.Workspace,token,Settings.Backend);ApplyEnvironmentProbe(probe);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception e){Toast=T["Environment check failed"]+": "+e.Message;}
        finally{IsChecking=false;}
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
        foreach(var card in ModelCards)RefreshModel(card);ModelStateChanged();
        Toast=Ready?T["Environment connected"]:T["Environment check failed"]+": "+probe.Detail;SavePreferences();
    }
    public async Task GenerateAsync()
    {
        if (!CanGenerate) return; long seedValue; if (RandomSeed) seedValue = Random.Shared.NextInt64(4294967296L); else if (!long.TryParse(Seed, out seedValue) || seedValue < 0 || seedValue > uint.MaxValue) { Toast = T["Enter a valid seed"]; return; }
        var settings = Settings.Clone(); var d = CurrentDimensions(); var g = new Generation { CreationId = creation, Python = settings.Python }; g.Directory = Path.Combine(settings.Workspace, "outputs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + g.Id[..8]); try { settings.Validate(); Directory.CreateDirectory(g.Directory); } catch (Exception e) { Toast = e.Message; return; }
        g.Request = new() { Backend=settings.Backend, Repository = settings.Repository, Model = settings.Model, Config = Path.Combine(AppContext.BaseDirectory, "Resources", "qwen_image_21_turbo.json"), Prompt = Prompt.Trim(), Width = d.Width, Height = d.Height, Seed = seedValue, Output = Path.Combine(g.Directory, "image.png") };
        try{g.Request.PromptEnhancement=EnhancementEnabled?SnapshotEnhancement():null;g.Request.Resolution=EnhancementEnabled&&Ratio=="Adaptive"?512*(1<<Tier):null;if(EnhancementEnabled)g.EnhancementStatus="thinking";g.Request.InputImages=SnapshotImages(g.Directory);g.Request.Task=g.Request.InputImages.Count>0?"i2i":"t2i";}catch(Exception e){Toast=e.Message;return;}
        InputImages.Clear();activeJob = g; active = new(); all.Add(g); Prompt = ""; Toast = ""; BeginGenerationLogs(g); RefreshRecent(); Selected = g; RefreshTurns(); RefreshStatus(); bool terminal = false;
        try
        {
            Files.Write(Path.Combine(g.Directory, "request.json"), g.Request); Persist(); var code = await active.RunAsync(settings.Python, "run", Path.Combine(g.Directory, "request.json"), Path.Combine(settings.Workspace, "tmp"), e => dispatcher.Invoke(() =>
            {
                var type = e.GetProperty("type").GetString(); switch (type)
                {
                    case "enhancement_child_started": g.EnhancementPid=e.GetProperty("pid").GetInt32();break;
                    case "enhancement_phase": if(g.EnhancementStatus=="thinking"&&e.GetProperty("phase").GetString()!="thinking")g.ShowThinking=false;g.EnhancementStatus=e.GetProperty("phase").GetString()!;g.Phase="enhancement";break;
                    case "enhancement_thinking": g.EnhancementThinking+=e.GetProperty("delta").GetString();break;
                    case "enhancement_prompt": g.EnhancedPrompt+=e.GetProperty("delta").GetString();break;
                    case "enhancement_result": g.EnhancementStatus="completed";g.EnhancedPrompt=e.GetProperty("prompt").GetString()!;g.EnhancementRatio=e.GetProperty("whRatio").GetString()!;g.EnhancementRatioFollow=e.GetProperty("ratioFollow").GetString()!;g.EnhancementSeconds=e.GetProperty("duration").GetDouble();break;
                    case "effective_request": g.Request.Width=e.GetProperty("width").GetInt32();g.Request.Height=e.GetProperty("height").GetInt32();g.Changed(nameof(Generation.Request));break;
                    case "child_started": g.BackendPid = e.GetProperty("pid").GetInt32(); break;
                    case "log": Log(e.GetProperty("message").GetString() ?? ""); break;
                    case "step": g.Step = e.GetProperty("step").GetInt32(); g.Phase = "sampling"; break;
                    case "status": g.Phase = e.GetProperty("phase").GetString() ?? "loading"; break;
                    case "error": g.Error = e.GetProperty("message").GetString(); Log(g.Error ?? ""); break;
                    case "done": terminal = true; g.Seconds = e.TryGetProperty("seconds", out var sec) ? sec.GetDouble() : (DateTimeOffset.Now - g.CreatedAt).TotalSeconds; g.Status = e.GetProperty("status").GetString() ?? "failed"; if (e.TryGetProperty("message", out var msg)) g.Error = msg.GetString(); if(g.HasEnhancement&&g.EnhancementStatus!="completed"&&g.Status!="completed")g.EnhancementStatus=g.Status;if (g.Status == "cancelled") g.Error = T["Generation cancelled"]; Persist(); break;
                }
                g.Refresh(); ContentUpdated?.Invoke();
            })); if (!terminal) { g.Status = IsStopping ? "cancelled" : "failed"; g.Error = $"Backend exited without a completion event (code {code})."; }
        }
        catch (Exception e) { g.Status = IsStopping ? "cancelled" : "failed"; g.Error = e.Message; Log(e.Message); }
        finally { g.Seconds = Math.Max(g.Seconds, (DateTimeOffset.Now - g.CreatedAt).TotalSeconds); if (g.Running) { g.Status = "interrupted";if(g.HasEnhancement&&!g.EnhancementCompleted)g.EnhancementStatus="interrupted"; g.Error = T["Interrupted message"]; } if(g.HasEnhancement&&!g.EnhancementCompleted&&!g.Running)g.EnhancementStatus=g.Status;g.Refresh(); try { Files.Write(Path.Combine(g.Directory, "generation.json"), g); } catch (Exception e) { Toast = e.Message; } RefreshLogPanel(); active.Dispose(); active = null; activeJob = null; IsStopping = false; RefreshRecent(); if (creation == g.CreationId) { selected = Recent.FirstOrDefault(x => x.CreationId == creation); Changed(nameof(Selected)); RefreshTurns(); } Persist(); RefreshStatus(); }
    }
    public async Task StopAsync() { if (active == null || activeJob == null || IsStopping) return; IsStopping = true; await active.StopAsync(Path.Combine(activeJob.Directory, "cancel.signal")); }
    void Reuse(Generation? g) { if (g == null||IsImporting) return;InputImages.Clear();foreach(var image in g.Request.InputImages)InputImages.Add(image); Prompt = g.Request.Prompt;EnhancementEnabled=g.Request.PromptEnhancement?.Enabled==true; RandomSeed = false; Seed = g.Request.Seed.ToString();if(g.Request.Resolution is int resolution){Tier=resolution==2048?2:resolution==512?0:1;Ratio="Adaptive";return;} for (int t = 0; t < 3; t++) foreach (var r in Presets.Ratios.Where(r=>r!="Adaptive")) { if (Presets.Dimensions(t, r) == (g.Request.Width, g.Request.Height)) { Tier = t; Ratio = r; return; } } if(g.Request.InputImages.Count>0){var image=g.Request.InputImages[^1];for(int t=0;t<3;t++)if(Presets.AdaptiveDimensions(t,image.Width,image.Height)==(g.Request.Width,g.Request.Height)){Tier=t;Ratio="Adaptive";return;}} }
    void Copy(Generation? g) { if (g?.Completed != true) return; try { Clipboard.SetImage(ImageLoader.Load(g.Request.Output, 0)); Toast = T["Copied"]; } catch (Exception e) { Toast = e.Message; } }
    void Export(Generation? g) { if (g?.Completed != true) return; var dlg = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "LightX2V-" + g.Id[..8] + ".png" }; if (dlg.ShowDialog() == true) { try { File.Copy(g.Request.Output, dlg.FileName, true); Toast = T["Exported"]; } catch (Exception e) { Toast = e.Message; } } }
    void Remove(Generation? g) { if (g == null || all.Any(x => x.CreationId == g.CreationId && x.Running)) return; all.RemoveAll(x => x.CreationId == g.CreationId); RefreshRecent(); if (creation == g.CreationId) NewCreation(); Persist(); }
    public void Dispose() { DisposeImages();DisposeModels();DisposeSetup();timer.Stop(); active?.Dispose(); }
}
