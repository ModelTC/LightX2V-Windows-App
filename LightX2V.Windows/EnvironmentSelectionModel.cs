using System.Collections.ObjectModel;

namespace LightX2V.Windows;

public interface IEnvironmentCatalog
{
    Task<List<EnvironmentCandidate>> Search(AppSettings settings,IProgress<SetupProgress> progress,CancellationToken token);
    Task<EnvironmentCandidate> Probe(string path,string workspace,CancellationToken token,string backend="auto");
    Task<string> Install(string workspace,bool mirror,IProgress<SetupProgress> progress,CancellationToken token,string backend="amd_rocm");
}
public sealed class NativeEnvironmentCatalog : IEnvironmentCatalog
{
    public Task<List<EnvironmentCandidate>> Search(AppSettings settings,IProgress<SetupProgress> progress,CancellationToken token)=>SetupDiscovery.Search(settings,progress,token);
    public Task<EnvironmentCandidate> Probe(string path,string workspace,CancellationToken token,string backend="auto")=>SetupDiscovery.Probe(path,workspace,token,backend);
    public Task<string> Install(string workspace,bool mirror,IProgress<SetupProgress> progress,CancellationToken token,string backend="amd_rocm")
    {
        var legacy=AppSettings.FindBackend();return SetupInstaller.Install(workspace,mirror,legacy.Length>0?[Path.Combine(legacy,"downloads")]:[],progress,token,backend);
    }
}
public sealed class EnvironmentSelectionModel : Observable,IDisposable
{
    readonly IEnvironmentCatalog catalog;readonly AppSettings settings;CancellationTokenSource? cancel;Task? operation;
    bool busy,installing,mirror,disposed,searched;string stage="",detail="",error="";double percent;
    public L10n T {get;}
    readonly bool? supportOverride;
    public DriverSetupModel Driver {get;}
    HardwareInfo hardware=>Driver.Hardware;
    public bool ManagedSupported=>supportOverride??hardware.Supports(Backend);
    public string Backend {get=>Backends.Resolve(settings.Backend,hardware);set{if(Busy||Driver.Pending||Backend==value)return;settings.Backend=value;Driver.Backend=value;Candidates.Clear();Selected=null;searched=false;Changed();foreach(var name in new[]{nameof(ManagedSupported),nameof(CanInstall),nameof(Requirements),nameof(ManagedDescription),nameof(Summary),nameof(BackendHint)})Changed(name);}}
    public string BackendHint=>T[Backend=="cuda"?"CUDA support is in preview. Full RTX image generation still needs hardware acceptance.":"Automatic AMD installation is verified on Radeon 8060S. Other AMD GPUs require a compatible existing environment."];
    public ObservableCollection<EnvironmentCandidate> Candidates {get;}=[];
    public EnvironmentCandidate? Selected {get;private set;}
    public Action<EnvironmentCandidate>? Chosen;
    public bool Busy {get=>busy||Driver.Busy;private set{Set(ref busy,value);Changed(nameof(CanInstall));Changed(nameof(Summary));}}
    public bool Installing {get=>installing;private set=>Set(ref installing,value);}
    public bool CanInstall=>ManagedSupported&&!Busy&&Driver.CanContinue;
    public bool Mirror {get=>mirror;set{if(Set(ref mirror,value)){Changed(nameof(OfficialSource));Changed(nameof(MirrorSource));}}}
    public bool OfficialSource {get=>!Mirror;set{if(value)Mirror=false;}}
    public bool MirrorSource {get=>Mirror;set{if(value)Mirror=true;}}
    public string Stage=>T[stage];
    public string Detail {get=>detail;private set=>Set(ref detail,value);}
    public string Error {get=>error;private set=>Set(ref error,value);}
    public double Percent {get=>percent;private set=>Set(ref percent,value);}
    public bool Searched=>searched;
    public string Summary=>Busy?T[Installing?"Preparing your environment":"Searching local environments"]:!searched?T["Search your computer for a compatible environment"]:string.Format(T["Found {0} compatible environments"],Candidates.Count(c=>c.Ready));
    public string Requirements=>"Python "+SetupManifest.Read(Backend).Python.Version+" · "+string.Join(" · ",new[]{"torch","transformers","diffusers","safetensors"}.Select(name=>SetupManifest.Read(Backend).Packages.Single(p=>p.Name==name)).Select(p=>p.Name+" "+p.Version));
    public string ManagedDescription=>"Python "+SetupManifest.Read(Backend).Python.Version+" · "+Backends.Label(Backend);
    public ParameterCommand SelectCommand {get;}
    public EnvironmentSelectionModel(AppSettings settings,L10n language,bool? managedSupported=null,IEnvironmentCatalog? catalog=null,DriverSetupModel? driver=null)
    {
        this.settings=settings.Clone();T=language;this.catalog=catalog??new NativeEnvironmentCatalog();supportOverride=managedSupported;Driver=driver??new DriverSetupModel(Backends.Resolve(settings.Backend),()=>this.settings.Workspace,language);Driver.PropertyChanged+=DriverChanged;Mirror=T.Language=="zh";
        this.settings.Backend=Driver.Backend;
        SelectCommand=new(value=>{if(value is EnvironmentCandidate candidate&&candidate.Ready&&candidate.Backend==Backend&&!Busy&&Candidates.Contains(candidate)){Selected=candidate;Chosen?.Invoke(candidate);}});
        T.PropertyChanged+=LanguageChanged;
    }
    void DriverChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e){Changed(nameof(Busy));Changed(nameof(CanInstall));Changed(nameof(ManagedSupported));Changed(nameof(Summary));}
    void LanguageChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e){Changed(nameof(Summary));Changed(nameof(Stage));Changed(nameof(BackendHint));}
    void Upsert(EnvironmentCandidate value)
    {
        var old=Candidates.FirstOrDefault(x=>x.Path.Equals(value.Path,StringComparison.OrdinalIgnoreCase));if(old!=null)Candidates.Remove(old);
        Candidates.Add(value);if(value.Ready&&value.Backend==Backend)Driver.ConfirmComputeReady();searched=true;Changed(nameof(Searched));Changed(nameof(Summary));
    }
    Task Start(bool install,Func<CancellationToken,IProgress<SetupProgress>,Task> action)
    {
        if(Busy||disposed)return Task.CompletedTask;
        Installing=install;Busy=true;Error="";Detail="";Percent=0;stage=install?"Preparing your environment":"Searching local environments";Changed(nameof(Stage));
        var source=new CancellationTokenSource();cancel=source;
        var progress=new Progress<SetupProgress>(p=>{if(disposed||cancel!=source||!Busy)return;stage=p.Stage;Changed(nameof(Stage));Detail=p.Detail;Percent=p.Percent;});
        return operation=Run(action,source,progress);
    }
    async Task Run(Func<CancellationToken,IProgress<SetupProgress>,Task> action,CancellationTokenSource source,IProgress<SetupProgress> progress)
    {
        try{settings.ValidateWorkspace();await action(source.Token,progress);}
        catch(OperationCanceledException) when(source.IsCancellationRequested){Error=T["Setup cancelled; progress is saved"];}
        catch(Exception e){Error=e.Message;}
        finally{source.Dispose();if(cancel==source)cancel=null;Busy=false;Installing=false;Changed(nameof(Summary));}
    }
    public Task SearchAsync()=>Start(false,async(token,progress)=>
    {
        var found=await catalog.Search(settings,progress,token);token.ThrowIfCancellationRequested();Candidates.Clear();foreach(var item in found.OrderByDescending(e=>e.Ready))Upsert(item);searched=true;Changed(nameof(Searched));Changed(nameof(Summary));
    });
    public Task CheckPythonAsync(string path)=>Start(false,async(token,_)=>{var candidate=await catalog.Probe(path,settings.Workspace,token,Backend);token.ThrowIfCancellationRequested();Upsert(candidate);});
    public Task InstallAsync()
    {
        if(!ManagedSupported){Error=T[Backends.Unsupported];return Task.CompletedTask;}
        return Start(true,async(token,progress)=>{if(!await Driver.EnsureReadyAsync(token))throw new InvalidOperationException(T["Complete the graphics software step first, then continue."]);var path=await catalog.Install(settings.Workspace,Mirror,progress,token,Backend);var candidate=await catalog.Probe(path,settings.Workspace,token,Backend);token.ThrowIfCancellationRequested();Upsert(candidate);if(!candidate.Ready)throw new InvalidOperationException(candidate.Detail);stage="Environment ready";Changed(nameof(Stage));Percent=100;});
    }
    public async Task CancelAsync(){cancel?.Cancel();await Driver.CancelAsync();if(operation!=null)await operation;}
    public void Dispose(){Driver.PropertyChanged-=DriverChanged;Driver.Dispose();disposed=true;cancel?.Cancel();T.PropertyChanged-=LanguageChanged;}
}
