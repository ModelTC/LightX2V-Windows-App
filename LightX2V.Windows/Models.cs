using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace LightX2V.Windows;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
}
public sealed class Command(Action action, Func<bool>? enabled = null) : ICommand
{
    public bool CanExecute(object? p) => enabled?.Invoke() ?? true;
    public void Execute(object? p) => action();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
public sealed class ParameterCommand(Action<object?> action) : ICommand
{
    public bool CanExecute(object? p) => true; public void Execute(object? p) => action(p);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
public static class Files
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static T? Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
    public static void Write<T>(string path, T value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, Json), new System.Text.UTF8Encoding(false)); File.Move(temp, path, true); }
}
public sealed class AppSettings
{
    public string Workspace { get; set; } = ""; public string Python { get; set; } = ""; public string Repository { get; set; } = ""; public string Model { get; set; } = "";
    public string Backend {get;set;}="auto";
    public bool EnhancementEnabled {get;set;}
    public string T2IDirectory {get;set;}="";
    public string I2IDirectory {get;set;}="";
    public string DownloadSource {get;set;}="auto";
    public static string FindBackend()
    {
        var candidates = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChatGPT", "windows跑通LightX2V") };
        for (DirectoryInfo? p = new(AppContext.BaseDirectory); p != null; p = p.Parent) candidates.Add(p.FullName);
        return candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "baseline-root.json")) && File.Exists(Path.Combine(p, "python312", "python.exe"))) ?? "";
    }
    public static AppSettings Detect(Action<string>? report = null) => LegacyWorkspaceImporter.TryRead(FindBackend(), report) ?? Fresh();
    public static string PreferencesRoot()
    {
        var preferred=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LightX2V","configuration");
        try{Directory.CreateDirectory(preferred);var test=Path.Combine(preferred,".write-probe-"+Guid.NewGuid().ToString("N"));File.WriteAllText(test,"");File.Delete(test);return preferred;}
        catch(IOException){}catch(UnauthorizedAccessException){}
        var legacy=FindBackend();return legacy.Length>0?Path.Combine(legacy,"output","app-preferences"):Path.Combine(AppContext.BaseDirectory,"UserData");
    }
    public static AppSettings Fresh(string? workspace = null)
    {
        workspace ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LightX2V");
        string ModelPath(string key) => Path.Combine(workspace, "models", ModelCatalog.Get(key).Repository.Replace('/', Path.DirectorySeparatorChar));
        return new() { Workspace = workspace, Model = ModelPath("turbo"), T2IDirectory = ModelPath("t2i"), I2IDirectory = ModelPath("i2i") };
    }
    public static string? LegacyPreferences() {var root=FindBackend();var p=root.Length>0?Path.Combine(root,"output","app-preferences","preferences.json"):Path.Combine(AppContext.BaseDirectory,"UserData","preferences.json");return File.Exists(p)?p:null;}
    public void ValidateWorkspace(){if(!Path.IsPathFullyQualified(Workspace))throw new InvalidOperationException("Choose an absolute workspace path.");Directory.CreateDirectory(Workspace);var p=Path.Combine(Workspace,".write-probe-"+Guid.NewGuid().ToString("N"));File.WriteAllText(p,"");File.Delete(p);}
    public void ValidateEnvironment(){ValidateWorkspace();if(!Path.IsPathFullyQualified(Python)||!File.Exists(Python)||!Python.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Choose a native Windows Python executable.");}
    public AppSettings Clone() => new() { Backend=Backend, Workspace = Workspace, Python = Python, Repository = Repository, Model = Model,EnhancementEnabled=EnhancementEnabled,T2IDirectory=T2IDirectory,I2IDirectory=I2IDirectory,DownloadSource=DownloadSource };
    public void Validate()
    {
        foreach (var p in new[] { Workspace, Python, Repository, Model }) if (!Path.IsPathFullyQualified(p)) throw new InvalidOperationException("Choose absolute paths in Settings.");
        if (!File.Exists(Python) || !Python.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a native Windows Python executable.");
        if (!File.Exists(Path.Combine(Repository, "lightx2v", "infer.py"))) throw new InvalidOperationException("LightX2V source directory is invalid.");
        if (!File.Exists(Path.Combine(Model, "model_index.json"))) throw new InvalidOperationException("Model directory is invalid.");
        Directory.CreateDirectory(Workspace);
    }
}
public sealed class Preferences { public int SchemaVersion {get;set;}=1; public bool ModelExpanded {get;set;}=true;public bool ParametersExpanded {get;set;}=true; public string Language { get; set; } = "zh"; public AppSettings Settings { get; set; } = new(); }
public sealed class WorkspaceState { public int SchemaVersion { get; set; } = 1; public List<Generation> Generations { get; set; } = []; }
public sealed class InputImage {public string Id {get;set;}=Guid.NewGuid().ToString("N");public string Path {get;set;}="";public string Name {get;set;}="";public int Width {get;set;}public int Height {get;set;}}
public sealed class InferenceRequest
{
    public string Backend {get;set;}="auto";
    public PromptEnhancementSettings? PromptEnhancement {get;set;}
    public int? Resolution {get;set;}
    public string Task {get;set;}="t2i";public List<InputImage> InputImages {get;set;}=[];public int ReferenceResolution {get;set;}=512;
    public int Protocol { get; set; } = 1; public string Repository { get; set; } = ""; public string Model { get; set; } = ""; public string Config { get; set; } = ""; public string Prompt { get; set; } = "";
    public int Width { get; set; } = 1024; public int Height { get; set; } = 1024; public long Seed { get; set; } = 42; public string Output { get; set; } = "";
}
public sealed class Generation : Observable
{
    public string EnhancementStatus {get;set;}="";
    public string EnhancementThinking {get;set;}="";
    public string EnhancedPrompt {get;set;}="";
    public string EnhancementRatio {get;set;}="";
    public string EnhancementRatioFollow {get;set;}="";
    public double EnhancementSeconds {get;set;}
    public int EnhancementPid {get;set;}
    bool showThinking=true;
    public bool ShowThinking {get=>showThinking;set=>Set(ref showThinking,value);}
    [JsonIgnore]public bool HasEnhancement=>Request.PromptEnhancement?.Enabled==true;
    [JsonIgnore]public bool EnhancementCompleted=>EnhancementStatus=="completed";
    [JsonIgnore]public string EnhancementLabel=>L10n.Current[EnhancementStatus=="completed"?"Enhanced":EnhancementStatus=="rewriting"?"Writing enhanced prompt":EnhancementStatus=="thinking"?"Thinking":"Enhancement incomplete"];
    [JsonIgnore]public string EnhancementDuration {get{var seconds=EnhancementStatus is "thinking" or "rewriting"?(DateTimeOffset.Now-CreatedAt).TotalSeconds:EnhancementSeconds;return $"{(int)seconds/60}:{(int)seconds%60:00}";}}
    [JsonIgnore]public bool ShowImageStage=>!HasEnhancement||EnhancementCompleted;
    [JsonIgnore]public bool EnhancementFailed=>HasEnhancement&&!EnhancementCompleted&&Failed;
    public string Id { get; set; } = Guid.NewGuid().ToString("N"); public string CreationId { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public InferenceRequest Request { get; set; } = new(); public string Python { get; set; } = ""; public string Directory { get; set; } = "";
    private string status = "running"; public string Status { get => status; set { Set(ref status, value); Refresh(); } }
    private string phase = "loading"; public string Phase { get => phase; set { Set(ref phase, value); Refresh(); } }
    private int step; public int Step { get => step; set { Set(ref step, value); Refresh(); } }
    public int StepTotal { get; set; } = 8;
    public double Seconds { get; set; }
    public string? Error { get; set; }
    public int BackendPid { get; set; }
    [JsonIgnore] public string? PreviewPath => Completed ? Request.Output : null;
    [JsonIgnore] public string Title => Request.Prompt.Replace('\n', ' ').Trim();
    [JsonIgnore] public string Subtitle => CreatedAt.ToString(L10n.Current.Language=="zh"?"yyyy年M月d日":"MMM d, yyyy",System.Globalization.CultureInfo.InvariantCulture) + " · " + L10n.Current[Status];
    [JsonIgnore] public string PhaseText => !Running ? ResultStatus : L10n.Current[Phase] + (Step > 0 ? $" · {Step}/{StepTotal}" : "");
    [JsonIgnore] public string Elapsed => TimeSpan.FromSeconds(Status == "running" ? (DateTimeOffset.Now - CreatedAt).TotalSeconds : Seconds).ToString(@"mm\:ss");
    [JsonIgnore] public bool Completed => Status == "completed"; [JsonIgnore] public bool Running => Status == "running"; [JsonIgnore] public bool Failed => !Completed && !Running;
    [JsonIgnore] public string ResultStatus => L10n.Current[Completed?"Generated":Status];
    [JsonIgnore] public string Duration => L10n.Current.Language=="zh"?$"{(int)Seconds/60} 分 {(int)Seconds%60} 秒":$"{(int)Seconds/60}m {(int)Seconds%60}s";
    [JsonIgnore] public string CreatedTime => CreatedAt.ToString("HH:mm");
    [JsonIgnore] public string Meta => L10n.Current[Status] + " · " + Elapsed + $" · {Request.Width} × {Request.Height} · seed {Request.Seed}";
    public void Refresh() { foreach (var n in new[] { nameof(Title), nameof(Subtitle), nameof(PhaseText), nameof(Elapsed), nameof(Completed), nameof(Running), nameof(Failed), nameof(Meta), nameof(Error), nameof(PreviewPath),nameof(ResultStatus),nameof(Duration),nameof(EnhancementStatus),nameof(EnhancementThinking),nameof(EnhancedPrompt),nameof(EnhancementRatio),nameof(EnhancementCompleted),nameof(EnhancementLabel),nameof(EnhancementDuration),nameof(HasEnhancement),nameof(ShowImageStage),nameof(EnhancementFailed) }) Changed(n); }
}
public sealed class PromptEnhancementSettings
{
    public bool Enabled {get;set;}=true;
    public string RuntimeDirectory {get;set;}="";
    [JsonPropertyName("t2iModel")] public string T2IModel {get;set;}="";
    [JsonPropertyName("t2iSystemPrompt")] public string T2ISystemPrompt {get;set;}="";
    [JsonPropertyName("i2iModel")] public string I2IModel {get;set;}="";
    [JsonPropertyName("i2iSystemPrompt")] public string I2ISystemPrompt {get;set;}="";
    public string VisionProjector {get;set;}="";
}
public static class Presets
{
    public static (int Width,int Height) AdaptiveDimensions(int tier,int width,int height)
    {
        double ratio=(double)width/height,size=512*(1<<tier);
        return((int)(Math.Round(Math.Sqrt(size*size*ratio)/32)*32),(int)(Math.Round(Math.Sqrt(size*size/ratio)/32)*32));
    }
    public static readonly string[] Ratios = ["Adaptive", "1:1", "4:3", "3:4", "3:2", "2:3", "16:9", "9:16"];
    private static readonly (int, int)[][] Sizes = [[(512, 512), (576, 448), (448, 576), (640, 416), (416, 640), (672, 384), (384, 672)], [(1024, 1024), (1184, 896), (896, 1184), (1248, 832), (832, 1248), (1376, 768), (768, 1376)], [(2048, 2048), (2400, 1792), (1792, 2400), (2528, 1696), (1696, 2528), (2752, 1536), (1536, 2752)]];
    public static (int Width, int Height) Dimensions(int tier, string ratio) { int i = Array.IndexOf(Ratios, ratio); var s = Sizes[tier][Math.Max(0, i - 1)]; return (s.Item1, s.Item2); }
}
