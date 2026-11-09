using System.Collections.ObjectModel;
namespace LightX2V.Windows;

public sealed class SetupController : Observable,IDisposable
{
    readonly MainViewModel owner;readonly IEnvironmentCatalog catalog;bool computerScanned,environmentsSearched;CancellationTokenSource? cancel;Task? operation;bool busy,environmentReady;int page;string workspace,stage="",detail="",logs="",error="";double percent;EnvironmentCandidate? selected;bool mirror,useExisting;string backend;
    CancellationTokenSource? searchCancellation;bool searchingEnvironments,environmentChoiceMade,disposed;readonly TimeSpan searchTimeout;
    public MainViewModel Owner=>owner;
    public L10n T=>owner.T;
    public DriverSetupModel Driver {get;}
    public HardwareInfo Hardware=>Driver.Hardware;
    public string Backend {get=>backend;set{if(Busy||Driver?.Busy==true||Driver?.Pending==true||Backend==value)return;StopEnvironmentSearch();backend=value;Driver!.Backend=value;Selected=null;UseExisting=false;EnvironmentReady=false;Environments.Clear();environmentsSearched=false;Changed();Changed(nameof(EnvironmentSummary));Changed(nameof(BackendRecommendation));Changed(nameof(Requirements));Changed(nameof(HardwareLabel));Changed(nameof(ManagedSupported));}}
    public bool ComputerScanned {get=>computerScanned;private set=>Set(ref computerScanned,value);}
    public bool HasDetectedBackend=>Hardware.All.Any(d=>d.Backend==Backend);
    public string BackendRecommendation=>!ComputerScanned?T["Scanning your computer…"]:!HasDetectedBackend&&Hardware.DetectionFailed?T["Graphics detection could not finish. Your driver may already be installed. Scan again or copy the detection details for help."]:!HasDetectedBackend?T["No supported graphics card was identified. Check the graphics software below, then scan again."]:string.Format(T[Driver.Pending?"Finish the pending {0} installation before changing the backend.":"Selected {0} for your graphics card. You can keep this choice."],Backends.Label(Backend));
    public string EnvironmentSummary=>SearchingEnvironments?string.Format(T["Searching · {0} compatible environments available. You can choose one now."],Environments.Count(e=>e.Ready&&e.Backend==Backend)):!environmentsSearched?T["Compatible components will appear here after the computer check."]:string.Format(T["Found {0} compatible environments"],Environments.Count(e=>e.Ready&&e.Backend==Backend));
    public string Requirements=>"Python "+SetupManifest.Read(Backend).Python.Version+" · "+string.Join(" · ",new[]{"torch","transformers","diffusers","safetensors"}.Select(name=>SetupManifest.Read(Backend).Packages.Single(p=>p.Name==name)).Select(p=>p.Name+" "+p.Version));
    public bool ManagedSupported=>Hardware.Supports(Backend);
    public string HardwareLabel=>string.Join("\n",Hardware.All.Where(d=>d.Backend.Length>0).Select(d=>d.Name+" · "+d.Driver));
    public string ResourceLabel {get{try{var r=ResourceSnapshot.Read(Workspace);return $"{T["System memory"]} {r.TotalRam/1073741824.0:F1} GiB · {T["Free"]} {r.AvailableRam/1073741824.0:F1} GiB · {T["Disk"]} {r.DiskFree/1073741824.0:F1} GiB {T["Free"]}";}catch(Exception e){return e.Message;}}}
    public int Page {get=>page;set{if(!Set(ref page,value))return;if(value!=1)StopEnvironmentSearch();if(value==3)Error="";Changed(nameof(PageTitle));}}
    public string PageTitle=>T[Page==0?"Choose workspace":Page==1?"Prepare environment":Page==2?"Prepare models":"Setup complete"];
    public string Workspace {get=>workspace;set{if(Driver?.Pending==true&&value!=workspace)return;if(value!=workspace)StopEnvironmentSearch();Set(ref workspace,value);Changed(nameof(ResourceLabel));}}
    public bool Mirror {get=>mirror;set=>Set(ref mirror,value);}
    public bool UseExisting {get=>useExisting;set{if(!value&&SearchingEnvironments)environmentChoiceMade=true;Set(ref useExisting,value);}}
    public bool SearchingEnvironments {get=>searchingEnvironments;private set{Set(ref searchingEnvironments,value);Changed(nameof(EnvironmentSummary));}}
    public bool Busy {get=>busy;private set{Set(ref busy,value);owner.SetupStateChanged();Changed(nameof(CanContinue));}}
    public bool EnvironmentReady {get=>environmentReady;private set{Set(ref environmentReady,value);Changed(nameof(CanContinue));}}
    public bool CanContinue=>!Busy&&(Page==0||Page==1&&EnvironmentReady||Page==2&&owner.TurboCard.Ready);
    public string Stage {get=>T[stage];private set{stage=value;Changed();}}
    public string Detail {get=>detail;private set=>Set(ref detail,value);}
    public string Logs {get=>logs;private set=>Set(ref logs,value);}
    public string Error {get=>error;private set=>Set(ref error,value);}
    public double Percent {get=>percent;private set=>Set(ref percent,value);}
    public ObservableCollection<EnvironmentCandidate> Environments {get;}=[];
    public EnvironmentCandidate? Selected {get=>selected;set{Set(ref selected,value);if(value?.Ready==true){UseExisting=true;Driver?.ConfirmComputeReady();}}}
    public SetupController(MainViewModel owner,IEnvironmentCatalog? catalog=null,DriverSetupModel? driver=null,IDriverPlatform? driverPlatform=null,TimeSpan? searchTimeout=null){this.catalog=catalog??new NativeEnvironmentCatalog();this.owner=owner;this.searchTimeout=searchTimeout??SetupDiscovery.EnvironmentSearchTimeout;workspace=owner.Settings.Workspace;backend=Backends.Resolve(owner.Settings.Backend,HardwareInfo.Read());Driver=driver??new(backend,()=>Workspace,T,driverPlatform);Driver.PropertyChanged+=DriverChanged;backend=Driver.Backend;if(Driver.Pending)page=1;Mirror=owner.T.Language=="zh";owner.T.PropertyChanged+=LanguageChanged;}
    void DriverChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e){Changed(nameof(HardwareLabel));Changed(nameof(ManagedSupported));Changed(nameof(BackendRecommendation));owner.SetupStateChanged();}
    void LanguageChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e){Changed(nameof(PageTitle));Changed(nameof(Stage));Changed(nameof(ResourceLabel));Changed(nameof(EnvironmentSummary));Changed(nameof(BackendRecommendation));}
    public void Restart(){Backend=Backends.Resolve(owner.Settings.Backend,Hardware);Workspace=owner.Settings.Workspace;Page=0;Stage="";Detail="";Error="";Percent=0;EnvironmentReady=owner.Ready;ComputerScanned=false;environmentsSearched=false;Changed(nameof(EnvironmentSummary));}
    void Progress(SetupProgress value){if(value.Stage.Length>0)Stage=value.Stage;Detail=value.Detail;Percent=value.Percent;if(value.Log!=null){Logs+=value.Log+System.Environment.NewLine;if(Logs.Length>40000)Logs=Logs[^30000..];}}
    async Task Execute(Func<CancellationToken,IProgress<SetupProgress>,Task> action)
    {
        if(Busy||Driver.Busy||owner.IsChecking||owner.IsPreflighting)return;Error="";Busy=true;cancel=new();var progress=new Progress<SetupProgress>(Progress);
        try{operation=action(cancel.Token,progress);await operation;}
        catch(OperationCanceledException) when(cancel.IsCancellationRequested){Error=T["Setup cancelled; progress is saved"] ;Stage="Setup cancelled";}
        catch(Exception e){Logs+=e+System.Environment.NewLine;Error=FriendlySetupError(e.Message);Stage="Setup failed";}
        finally{Busy=false;cancel.Dispose();cancel=null;operation=null;Changed(nameof(CanContinue));}
    }
    string FriendlySetupError(string message)
    {
        if(message.Contains("BF16 profile requires",StringComparison.OrdinalIgnoreCase))return T["This graphics card does not support the current image-generation mode. Use a compatible GPU; reinstalling the driver alone will not change this."];
        if(message.Contains("free space",StringComparison.OrdinalIgnoreCase)||message.Contains("disk space",StringComparison.OrdinalIgnoreCase))return T["There is not enough disk space. Free up space or choose another save location, then try again."];
        if(message.Contains("CUDA 12.8",StringComparison.OrdinalIgnoreCase))return T["The graphics software needs an update. Finish the graphics software step above, then check again."];
        if(message==T["Complete the graphics software step first, then continue."])return message;
        return T["We could not finish preparing this computer. Check your internet connection and available disk space, then try again."];
    }
    public AppSettings EnvironmentSettings(){var settings=owner.Settings.Clone();settings.Workspace=Workspace;settings.Backend=Backend;return settings;}
    public void ResumeModels(){Workspace=owner.Settings.Workspace;EnvironmentReady=true;Page=2;}
    public async Task ResumeStartupAsync()
    {
        await Execute(async(token,progress)=>
        {
            if(!owner.Ready)await owner.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            if(owner.Ready)
            {
                if(owner.VerifiedEnvironment is {} candidate){Upsert(candidate);Selected=candidate;UseExisting=true;}
                ResumeModels();
            }
            else{Page=1;await ScanComputerCore(token,progress);}
        });
        if(Page==1&&ComputerScanned&&!Busy)await SearchEnvironmentsCore();
    }
    public void AcceptCandidate(EnvironmentCandidate candidate)
    {
        if(Busy||Driver.Busy||!candidate.Ready||candidate.Backend!=Backend||!Environments.Contains(candidate))return;
        environmentChoiceMade=true;
        Driver.ConfirmComputeReady();Selected=candidate;UseExisting=true;
    }
    public async Task SelectPythonAsync(string path)
    {
        StopEnvironmentSearch();
        await Execute(async(token,progress)=>
        {
            var settings=EnvironmentSettings();settings.ValidateWorkspace();
            if(!ComputerScanned)await ScanComputerCore(token,progress);
            var candidate=await catalog.Probe(path,Workspace,token,Backend);
            token.ThrowIfCancellationRequested();Upsert(candidate);
            if(candidate.Ready&&candidate.Backend==Backend)Selected=candidate;
        });
    }
    void Upsert(EnvironmentCandidate candidate)
    {
        var old=Environments.FirstOrDefault(e=>e.Path.Equals(candidate.Path,StringComparison.OrdinalIgnoreCase));if(old!=null)Environments.Remove(old);
        Environments.Add(candidate);environmentsSearched=true;Changed(nameof(EnvironmentSummary));
    }
    async Task SearchEnvironmentsCore()
    {
        StopEnvironmentSearch();
        if(Busy||Driver.Busy||!ComputerScanned||disposed)return;
        var settings=EnvironmentSettings();
        if(Selected!=null)settings.Python=Selected.Path;
        var source=new CancellationTokenSource(searchTimeout);
        searchCancellation=source;
        Environments.Clear();Selected=null;UseExisting=false;EnvironmentReady=false;
        environmentChoiceMade=false;environmentsSearched=true;SearchingEnvironments=true;
        Stage="Searching local environments";Detail="";Percent=0;Error="";
        bool Current()=>!disposed&&ReferenceEquals(searchCancellation,source)&&Backend==settings.Backend&&Workspace==settings.Workspace;
        void Publish(EnvironmentCandidate candidate)
        {
            if(!Current())return;
            Upsert(candidate);
            if(!environmentChoiceMade&&candidate.Ready&&candidate.Backend==Backend&&
                (Selected==null||candidate.Path.Equals(settings.Python,StringComparison.OrdinalIgnoreCase)))Selected=candidate;
        }
        var progress=new Progress<SetupProgress>(value=>
        {
            if(!Current())return;
            if(value.Candidate!=null)Publish(value.Candidate);
            if(value.Stage.Length>0)Stage=value.Stage;
            Detail=value.Detail;
        });
        try
        {
            var found=await catalog.Search(settings,progress,source.Token).WaitAsync(source.Token);
            if(!Current())return;
            foreach(var candidate in found)Publish(candidate);
            Stage="Choose an environment or install automatically";
        }
        catch(OperationCanceledException) when(source.IsCancellationRequested)
        {
            if(Current())Stage="Environment search time limit reached";
        }
        catch(Exception error){if(Current())Error=error.Message;}
        finally
        {
            if(Current()){searchCancellation=null;SearchingEnvironments=false;Changed(nameof(EnvironmentSummary));}
            source.Dispose();
        }
    }
    public void StopEnvironmentSearch()
    {
        var source=searchCancellation;searchCancellation=null;
        source?.Cancel();SearchingEnvironments=false;
    }
    async Task ScanComputerCore(CancellationToken token,IProgress<SetupProgress> progress)
    {
        ComputerScanned=false;Stage="Scanning your computer…";Detail="";Percent=0;
        await Driver.CheckAsync();token.ThrowIfCancellationRequested();
        if(!Driver.Checked||Driver.Error.Length>0)throw new IOException(Driver.Detail);
        var recommended=Hardware.Backend;
        if(!Driver.Pending&&recommended.Length>0&&recommended!=Backend)
        {
            backend=recommended;Driver.Backend=backend;Selected=null;UseExisting=false;Environments.Clear();Changed(nameof(Backend));Changed(nameof(Requirements));
            await Driver.CheckAsync();token.ThrowIfCancellationRequested();
            if(!Driver.Checked||Driver.Error.Length>0)throw new IOException(Driver.Detail);
        }
        ComputerScanned=true;Changed(nameof(HardwareLabel));Changed(nameof(ResourceLabel));Changed(nameof(BackendRecommendation));Changed(nameof(ManagedSupported));
    }
    public async Task ScanComputerAsync()
    {
        StopEnvironmentSearch();
        await Execute(async(token,progress)=>{EnvironmentSettings().ValidateWorkspace();await ScanComputerCore(token,progress);});
        if(ComputerScanned&&!Busy)await SearchEnvironmentsCore();
    }
    public async Task SearchEnvironmentsAsync()
    {
        if(SearchingEnvironments){StopEnvironmentSearch();return;}
        if(!ComputerScanned)await Execute(async(token,progress)=>{EnvironmentSettings().ValidateWorkspace();await ScanComputerCore(token,progress);});
        if(ComputerScanned&&!Busy)await SearchEnvironmentsCore();
    }
    public async Task CheckBackendAsync()
    {
        StopEnvironmentSearch();
        await Execute(async(token,_)=>{await Driver.CheckAsync();token.ThrowIfCancellationRequested();Changed(nameof(BackendRecommendation));});
        if(ComputerScanned&&!Busy)await SearchEnvironmentsCore();
    }
    public async Task BeginWorkspaceAsync()
    {
        await Execute(async(token,progress)=>{
            var before=owner.Settings.Workspace;var settings=owner.Settings.Clone();settings.Workspace=Workspace.Trim();settings.Backend=Backend;settings.ValidateWorkspace();Workspace=settings.Workspace;
            var fresh=AppSettings.Fresh(settings.Workspace);
            if(string.IsNullOrWhiteSpace(before)||!Path.GetFullPath(before).Equals(Path.GetFullPath(settings.Workspace),StringComparison.OrdinalIgnoreCase))
            {
                if(settings.Python.StartsWith(Path.Combine(before,"environments"),StringComparison.OrdinalIgnoreCase))settings.Python="";
                if(settings.Repository.StartsWith(Path.Combine(before,"runtimes"),StringComparison.OrdinalIgnoreCase))settings.Repository="";
                if(settings.Model.Length==0||settings.Model.StartsWith(Path.Combine(before,"models"),StringComparison.OrdinalIgnoreCase))settings.Model=fresh.Model;
                if(settings.T2IDirectory.Length==0||settings.T2IDirectory.StartsWith(Path.Combine(before,"models"),StringComparison.OrdinalIgnoreCase))settings.T2IDirectory=fresh.T2IDirectory;
                if(settings.I2IDirectory.Length==0||settings.I2IDirectory.StartsWith(Path.Combine(before,"models"),StringComparison.OrdinalIgnoreCase))settings.I2IDirectory=fresh.I2IDirectory;
            }
            owner.CommitSetup(settings);Page=1;await ScanComputerCore(token,progress);
        });
        if(ComputerScanned&&!Busy)await SearchEnvironmentsCore();
    }
    public async Task PrepareEnvironmentAsync(bool install)
    {
        StopEnvironmentSearch();
        await Execute(async(token,progress)=>{
            var settings=owner.Settings.Clone();settings.Workspace=Workspace.Trim();settings.Backend=Backend;settings.ValidateWorkspace();
            EnvironmentReady=false;
            if(!await Driver.EnsureReadyAsync(token))throw new InvalidOperationException(T["Complete the graphics software step first, then continue."]);
            if(install)
            {
                settings.Python=await catalog.Install(settings.Workspace,Mirror,progress,token,Backend);
            }
            else if(Selected?.Ready==true&&Selected.Backend==Backend)settings.Python=Selected.Path;
            else throw new InvalidOperationException(T["Choose a compatible environment"]);
            progress.Report(new("Checking environment"));
            var probe=install?await catalog.ProbeInstalled(settings.Python,settings.Workspace,token,Backend):
                Selected is {} candidate&&SetupDiscovery.CandidateIsCurrent(candidate,Backend)?candidate:
                await catalog.Probe(settings.Python,settings.Workspace,token,Backend);
            token.ThrowIfCancellationRequested();Upsert(probe);
            if(!probe.Ready||probe.Backend!=Backend)throw new InvalidOperationException(probe.Detail);
            Selected=probe;UseExisting=true;
            progress.Report(new("Preparing LightX2V source"));settings.Repository=await SetupInstaller.PrepareSource(settings.Workspace,token);token.ThrowIfCancellationRequested();
            settings.Backend=probe.Backend;owner.CommitVerifiedEnvironment(settings,probe);
            EnvironmentReady=true;Stage="Environment ready";Percent=100;await DiscoverModelsCore(token,progress);Page=2;
        });
    }
    async Task DiscoverModelsCore(CancellationToken token,IProgress<SetupProgress> progress)
    {
        progress.Report(new("Searching local models"));var paths=await Task.Run(()=>SetupDiscovery.Models(owner.Settings,token),token);
        foreach(var card in owner.ModelCards)if(paths.TryGetValue(card.Key,out var path))card.Path=path;
        if(!owner.Ready)await owner.CheckAsync(token);Stage="Model discovery complete";Changed(nameof(CanContinue));
    }
    public async Task DiscoverModelsAsync()=>await Execute(DiscoverModelsCore);
    public async Task DownloadModelsAsync()
    {
        if(Busy||owner.IsDownloading||!owner.CanConfigureModels)return;Error="";Stage="Downloading required models";
        try{var done=await owner.DownloadRequiredModelsAsync();Stage=done?"All required files are ready":"Download stopped; downloaded files are kept";Changed(nameof(CanContinue));if(!done)Error=owner.Toast;}
        catch(Exception e){Error=e.Message;}
    }
    public async Task CancelAsync(){StopEnvironmentSearch();cancel?.Cancel();await Driver.CancelAsync();if(operation!=null)try{await operation;}catch{}if(owner.IsDownloading)await owner.StopModelDownloadAsync();if(owner.IsGenerationBusy)await owner.StopAsync();}
    public void Dispose(){disposed=true;StopEnvironmentSearch();cancel?.Cancel();Driver.PropertyChanged-=DriverChanged;Driver.Dispose();owner.T.PropertyChanged-=LanguageChanged;}
}

public sealed partial class MainViewModel
{
    public SetupController Setup {get;private set;}=null!;
    public bool IsPreparingSetup=>Setup?.Busy==true||Setup?.Driver.Busy==true;
    public Action? RequestSetup;
    void InitializeSetup(IEnvironmentCatalog? catalog=null,IDriverPlatform? driverPlatform=null)=>Setup=new(this,catalog,driverPlatform:driverPlatform);
    internal void SetupStateChanged(){RefreshStatus();Changed(nameof(IsPreparingSetup));}
    internal void CommitSetup(AppSettings value)
    {
        var changed=Settings.Workspace!=value.Workspace;
        var reload=changed||!WorkspaceAvailable;
        var history=reload?StateStore.LoadHistory(value.Workspace):null;
        if(changed)Persist();InvalidateEnvironment();Settings=value.Clone();SavePreferences();
        if(reload){all.Clear();LoadWorkspace(history);NewCreation();}
        ReloadModelPaths();Changed(nameof(Settings));Changed(nameof(NeedsInitialSetup));
    }
    public Task<bool> DownloadRequiredModelsAsync()=>StartRequiredModelDownloads();
    void DisposeSetup()=>Setup?.Dispose();
}
