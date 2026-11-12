using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace LightX2V.Windows;

public sealed class ModelCard : Observable
{
    readonly MainViewModel owner;
    readonly string title;
    string path = "", status = "", downloadPhase = "";
    string? statusKey;
    bool ready, failed;
    double percent;
    double? etaSeconds;
    public string Key { get; }
    public string Title => owner.T[title];
    public string DisplayTitle=>Key=="turbo"?Title:Key=="t2i"?"PE-T2I":"PE-I2I";
    public string Repository { get; }
    public string Path
    {
        get => path;
        set
        {
            var normalized = value?.Trim() ?? "";
            if (Set(ref path, normalized)) owner.SetModelPath(this, normalized);
        }
    }
    internal void AssignPath(string value)
    {
        path = value;
        Changed(nameof(Path));
    }
    public bool Ready
    {
        get => ready;
        internal set { Set(ref ready, value); Changed(nameof(Indicator)); }
    }
    public string Status
    {
        get => statusKey == null ? status : owner.T[statusKey]+(status.Length>0?": "+status:"");
        internal set { statusKey = null; status = value; Changed(); }
    }
    internal (string? Key,string Value) CaptureStatus()=>(statusKey,status);
    internal void RestoreStatus((string? Key,string Value) value){statusKey=value.Key;status=value.Value;Changed(nameof(Status));}
    internal void SetStatus(string key,string detail="")
    {
        status=detail;statusKey = key;
        Changed(nameof(Status));
    }
    public string Indicator => Ready ? "✓" : "○";
    public bool IsDownloading => owner.DownloadingKey == Key;
    public bool Failed { get => failed; internal set => Set(ref failed, value); }
    internal string ActiveDownloadSource { get; set; } = "";
    public double Percent { get => percent; internal set => Set(ref percent, value); }
    public bool ShowProgress => IsDownloading || statusKey == null && status.Length > 0;
    public string DownloadEta
    {
        get
        {
            if (!IsDownloading || downloadPhase != "downloading") return "";
            if (etaSeconds is not double seconds || !double.IsFinite(seconds) || seconds < 0)
                return owner.T["Estimating remaining time…"];
            if (seconds < 60) return owner.T["Less than a minute remaining"];
            var minutes = Math.Ceiling(seconds / 60);
            if (minutes < 60) return string.Format(owner.T["About {0} min remaining"], minutes);
            return string.Format(owner.T["About {0} h {1} min remaining"], Math.Floor(minutes / 60), minutes % 60);
        }
    }
    internal void UpdateDownloadEstimate(string phase, double? seconds = null)
    {
        downloadPhase = phase;
        etaSeconds = seconds;
        Changed(nameof(DownloadEta));
    }
    public string DownloadLabel => owner.T[IsDownloading ? "Stop" : Failed ? "Retry download" : "Download"];
    public bool CanChoose => owner.CanConfigureModels;
    public bool CanDownload => IsDownloading || !Ready && owner.CanConfigureModels && !owner.IsChecking;
    public bool CanRepair => owner.CanConfigureModels && !owner.IsChecking;
    public Command ChooseCommand { get; }
    public Command DownloadCommand { get; }
    public Command GuideCommand { get; }
    public Command StatusCommand { get; }
    public Command RepairCommand { get; }
    public ModelCard(MainViewModel owner, string key, string title, string repository)
    {
        this.owner = owner;
        this.title = title;
        Key = key;
        Repository = repository;
        ChooseCommand = new(() =>
        {
            var dialog = new OpenFolderDialog { Title = owner.T["Choose"] + " " + Title };
            if (dialog.ShowDialog() == true) Path = dialog.FolderName;
        }, () => CanChoose);
        DownloadCommand = new(() =>
        {
            if (IsDownloading) _ = owner.StopModelDownloadAsync();
            else _ = owner.DownloadModelAsync(Key);
        }, () => CanDownload);
        GuideCommand = new(() => owner.ShowModelGuide(this));
        StatusCommand = new(() => owner.RecheckModel(this), () => !owner.IsDownloading && !owner.IsGenerationBusy);
        RepairCommand = new(() => _ = owner.RepairModelAsync(Key), () => CanRepair);
    }
    internal void Refresh()
    {
        foreach (var name in new[] { nameof(Title),nameof(DisplayTitle), nameof(DownloadLabel), nameof(DownloadEta), nameof(IsDownloading), nameof(CanChoose), nameof(CanDownload), nameof(CanRepair), nameof(Indicator), nameof(Status), nameof(ShowProgress) })
            Changed(name);
    }
}

public sealed partial class MainViewModel
{
    public ModelCard TurboCard {get;private set;}=null!;
    public ModelCard T2ICard {get;private set;}=null!;
    public ModelCard I2ICard {get;private set;}=null!;
    public IReadOnlyList<ModelCard> ModelCards=>[TurboCard,T2ICard,I2ICard];
    public bool EnhancementEnabled
    {
        get => Settings.EnhancementEnabled;
        set
        {
            if (Settings.EnhancementEnabled == value) return;
            Settings.EnhancementEnabled = value;RememberCreation();
            SavePreferences();
            Changed();
            ModelStateChanged();
        }
    }
    public bool UseAutomaticDownloadSource {get=>Settings.DownloadSource is not ("huggingFace" or "modelScope");set{if(value)SetDownloadSource("auto");}}
    public bool UseHuggingFace {get=>Settings.DownloadSource=="huggingFace";set{if(value)SetDownloadSource("huggingFace");}}
    public bool UseModelScope {get=>Settings.DownloadSource=="modelScope";set{if(value)SetDownloadSource("modelScope");}}
    public bool IsDownloading=>downloadCancellation!=null;
    public bool IsDownloadingAll=>downloadingAll;
    public bool CanConfigureModels=>!IsPreparingSetup&&!IsGenerationBusy&&!IsImporting&&!IsDownloading;
    public bool CanToggleEnhancement=>!IsPreparingSetup&&!IsGenerationBusy&&!IsImporting;
    public bool IsBaseModelDownloading=>IsDownloading&&DownloadingKey is not ("t2i" or "i2i");
    public string DownloadAllLabel=>T[IsDownloading?"Stop downloads":"Download all models"];
    public string? DownloadingKey {get;private set;}
    public Command DownloadAllCommand {get;private set;}=null!;
    public ParameterCommand CopyEnhancedCommand {get;private set;}=null!;
    readonly IModelDownloader modelDownloader;
    CancellationTokenSource? downloadCancellation;
    Task<bool>? modelDownloadOperation;
    bool downloadingAll, modelsDisposed;
    public bool RequiredModelsReady=>TurboCard.Ready;
    public string RequiredDownloadLabel=>T[IsDownloading?"Stop downloads":RequiredModelsReady?"All required files are ready":TurboCard.Failed?"Retry automatic download":"Download required files automatically"];
    public string ModelDownloadSummary
    {
        get
        {
            if (DownloadingKey is not {} key)
                return RequiredModelsReady ? T["All required files are ready"] : TurboCard.Failed ? TurboCard.Status
                    : T["Click automatic download. LightX2V will save and check the files for you; no commands are needed."];
            var card = ModelCards.Single(card => card.Key == key);
            var summary = card.Title + " · " + card.Status;
            return card.DownloadEta.Length > 0 ? summary + " · " + card.DownloadEta : summary;
        }
    }
    public Command RequiredModelsDownloadCommand {get;private set;}=null!;
    static string RuntimeDirectory=>Path.Combine(AppContext.BaseDirectory,"Runtime","llama-b11564-vulkan");
    string DefaultModelPath(string repository)
    {
        return Path.Combine(Path.Combine(Settings.Workspace,"models"),repository.Replace('/',Path.DirectorySeparatorChar));
    }
    void InitializeModels()
    {
        ModelCard Card(string key)
        {
            var definition = ModelCatalog.Get(key);
            return new ModelCard(this, key, definition.Title, definition.Repository);
        }
        TurboCard = Card("turbo");
        T2ICard = Card("t2i");
        I2ICard = Card("i2i");
        RequiredModelsDownloadCommand = new(() =>
        {
            if (IsDownloading) _ = StopModelDownloadAsync();
            else _ = Setup.DownloadModelsAsync();
        }, () => IsDownloading || CanConfigureModels && !RequiredModelsReady);
        DownloadAllCommand = new(() =>
        {
            if (IsDownloading) _ = StopModelDownloadAsync();
            else _ = DownloadAllModelsAsync();
        }, () => IsDownloading || CanConfigureModels && !IsChecking);
        CopyEnhancedCommand = new(value =>
        {
            if (value is not Generation generation || generation.EnhancedPrompt.Length == 0) return;
            Clipboard.SetText(generation.EnhancedPrompt);
            Toast = T["Copied"];
        });
        ReloadModelPaths();
    }
    void ReloadModelPaths()
    {
        if (Settings.T2IDirectory.Length == 0) Settings.T2IDirectory = DefaultModelPath(T2ICard.Repository);
        if (Settings.I2IDirectory.Length == 0) Settings.I2IDirectory = DefaultModelPath(I2ICard.Repository);
        TurboCard.AssignPath(Settings.Model);
        T2ICard.AssignPath(Settings.T2IDirectory);
        I2ICard.AssignPath(Settings.I2IDirectory);
        foreach (var card in ModelCards) RefreshModel(card);
        Changed(nameof(EnhancementEnabled));
        Changed(nameof(UseAutomaticDownloadSource));
        Changed(nameof(UseHuggingFace));
        Changed(nameof(UseModelScope));
        ModelStateChanged();
    }
    internal void SetModelPath(ModelCard card,string value)
    {
        value = value.Trim();
        if (card.Key == "turbo")
        {
            Settings.Model = value;
            Changed(nameof(Settings));
        }
        else if (card.Key == "t2i") Settings.T2IDirectory = value;
        else Settings.I2IDirectory = value;
        SavePreferences();
        RefreshModel(card);
        ModelStateChanged();
    }
    void SetDownloadSource(string source)
    {
        if (!CanConfigureModels || Settings.DownloadSource == source) return;
        Settings.DownloadSource = source;
        SavePreferences();
        Changed(nameof(UseAutomaticDownloadSource));
        Changed(nameof(UseHuggingFace));
        Changed(nameof(UseModelScope));
    }
    static (string Model,string System,string Projector) EnhancementFiles(ModelCard card)
    {
        if (!ModelValidation.Ready(card.Path, card.Key))
            throw new InvalidOperationException("Choose a complete " + card.Title + " model directory.");
        var definition = ModelCatalog.Get(card.Key);
        var projector = definition.ProjectorFiles.Select(file => Path.Combine(card.Path, file))
            .FirstOrDefault(ModelValidation.Gguf) ?? "";
        return (Path.Combine(card.Path, definition.ModelFile), Path.Combine(card.Path, definition.SystemPromptFile), projector);
    }
    void RefreshModel(ModelCard card)
    {
        card.Ready = ModelValidation.Ready(card.Path, card.Key);
        card.SetStatus(card.Ready ? "Model files ready" : "Missing model files");
    }
    internal void RecheckModel(ModelCard card){RefreshModel(card);ModelStateChanged();}
    bool EnhancementReady=>!EnhancementEnabled||((HasInputImages?I2ICard:T2ICard).Ready&&File.Exists(Path.Combine(RuntimeDirectory,"llama-completion.exe"))&&File.Exists(Path.Combine(RuntimeDirectory,"llama-mtmd-cli.exe")));
    PromptEnhancementSettings SnapshotEnhancement()
    {
        var card = HasInputImages ? I2ICard : T2ICard;
        var files = EnhancementFiles(card);
        return new()
        {
            RuntimeDirectory = RuntimeDirectory,
            T2IModel = card.Key == "t2i" ? files.Model : "",
            T2ISystemPrompt = card.Key == "t2i" ? files.System : "",
            I2IModel = card.Key == "i2i" ? files.Model : "",
            I2ISystemPrompt = card.Key == "i2i" ? files.System : "",
            VisionProjector = files.Projector
        };
    }
    void ModelStateChanged()
    {
        if (TurboCard == null) return;
        foreach (var card in ModelCards) card.Refresh();
        foreach (var name in new[] { nameof(IsDownloading), nameof(IsBaseModelDownloading), nameof(CanConfigureModels), nameof(CanToggleEnhancement), nameof(DownloadAllLabel), nameof(IsDownloadingAll), nameof(RequiredModelsReady), nameof(RequiredDownloadLabel), nameof(ModelDownloadSummary), nameof(CanGenerate), nameof(CanAddImages) })
            Changed(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public Task DownloadModelAsync(string key)
    {
        var card = ModelCards.Single(card => card.Key == key);
        return StartModelDownloads([card], false);
    }
    public Task RepairModelAsync(string key)
    {
        var card = ModelCards.Single(card => card.Key == key);
        return StartModelDownloads([card], false, repair: true);
    }
    Task DownloadAllModelsAsync() => StartModelDownloads(ModelCards.ToArray(), true);
    internal Task<bool> StartRequiredModelDownloads() => StartModelDownloads([TurboCard], false);
    Task<bool> StartModelDownloads(ModelCard[] cards, bool all, bool repair = false)
    {
        if (!CanConfigureModels || IsChecking) return Task.FromResult(false);
        cards = cards.Where(card => repair || !card.Ready).ToArray();
        if (cards.Length == 0)
        {
            Toast = T["All models ready"];
            return Task.FromResult(true);
        }
        downloadingAll = all;
        var cancellation = new CancellationTokenSource();
        downloadCancellation = cancellation;
        ModelStateChanged();
        return modelDownloadOperation = RunModelDownloads(cards, cancellation);
    }
    async Task<bool> RunModelDownloads(ModelCard[] cards, CancellationTokenSource cancellation)
    {
        try
        {
            foreach (var card in cards)
            {
                if (cancellation.IsCancellationRequested || !await DownloadOneAsync(card, cancellation.Token)) return false;
            }
            return !cancellation.IsCancellationRequested;
        }
        finally
        {
            downloadCancellation = null;
            DownloadingKey = null;
            downloadingAll = false;
            cancellation.Dispose();
            ModelStateChanged();
            // GPU readiness is checked on send; finishing a download must not start a background torch import.
        }
    }
    async Task<bool> DownloadOneAsync(ModelCard card, CancellationToken token)
    {
        var completed = false;
        try
        {
            if (card.Path.Length == 0) card.Path = DefaultModelPath(card.Repository);
            DownloadingKey = card.Key;
            card.Ready = false;
            card.Failed = false;
            card.Percent = 0;
            card.ActiveDownloadSource = "";
            card.UpdateDownloadEstimate("preparing");
            card.SetStatus("Preparing download");
            ModelStateChanged();
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                if (modelsDisposed || token.IsCancellationRequested || DownloadingKey != card.Key) return;
                card.UpdateDownloadEstimate(value.Phase, value.EtaSeconds);
                if (value.Source.Length > 0) card.ActiveDownloadSource = value.Source;
                var sourceName = card.ActiveDownloadSource == "modelScope" ? "ModelScope" : "Hugging Face";
                if (value.Phase == "selecting_source") card.SetStatus("Selecting a download source…");
                else if (value.Phase is "source_selected" or "switching_source")
                    card.Status = string.Format(T[value.Phase == "switching_source" ? "Switching download source to {0}…" : "Downloading from {0}"], sourceName);
                else
                {
                    card.Percent = value.Percent;
                    var prefix = card.ActiveDownloadSource is "huggingFace" or "modelScope" ? sourceName + " · " : "";
                    card.Status = prefix + (value.Phase == "checking" ? T["Checking model files"] + " · " + value.File
                        : $"{value.Percent:F1}% · {value.BytesPerSecond / 1048576:F1} MB/s");
                }
                ModelStateChanged();
            });
            var request = new ModelDownloadRequest(card.Key, UseAutomaticDownloadSource ? "auto" : Settings.DownloadSource, card.Path, Settings.Python, Settings.Workspace);
            var result = await modelDownloader.DownloadAsync(request, progress, token);
            if (token.IsCancellationRequested || result.Cancelled)
            {
                card.SetStatus("Download cancelled");
                return false;
            }
            if (!result.Completed)
                throw new IOException(result.Message.Length > 0 ? result.Message : T["The download did not finish. Please try again."]);
            RefreshModel(card);
            completed = card.Ready;
            if (!completed) throw new IOException(T["The downloaded files are incomplete. Retry the automatic download."]);
            card.Percent = 100;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            card.SetStatus("Download cancelled");
            return false;
        }
        catch (Exception error)
        {
            card.Failed = true;
            card.SetStatus("Download failed",error.Message);
            return false;
        }
        finally
        {
            if (!completed)
            {
                var status = card.CaptureStatus();
                RefreshModel(card);
                card.RestoreStatus(status);
                Toast = card.Status;
            }
            card.UpdateDownloadEstimate("");
            DownloadingKey = null;
            ModelStateChanged();
        }
    }
    public async Task StopModelDownloadAsync()
    {
        var cancellation = downloadCancellation;
        if (cancellation == null) return;
        cancellation.Cancel();
        if (modelDownloadOperation is {} pending) await pending;
    }
    void DisposeModels()
    {
        modelsDisposed = true;
        downloadCancellation?.Cancel();
    }
}
