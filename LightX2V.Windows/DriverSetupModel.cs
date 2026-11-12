using System.ComponentModel;
namespace LightX2V.Windows;

public sealed record DriverHelpProgress(string Backend,DateTimeOffset StartedAt);
public sealed class DriverSetupModel : Observable,IDisposable
{
    readonly Func<string> workspace;readonly Func<bool> canRestore;readonly IDriverPlatform platform;CancellationTokenSource? cancellation;Task? operation;bool disposed,busy;string backend;
    string messageKey="Checking your graphics software",error="",detail="";double percent;
    public L10n T {get;}
    public HardwareInfo Hardware {get;private set;}=HardwareInfo.Read();
    public DriverAssessment? Assessment {get;private set;}
    public bool Pending {get;private set;}
    public bool Checked=>Assessment!=null;
    public bool Busy {get=>busy;private set{Set(ref busy,value);Notify();}}
    public bool CanContinue=>Assessment?.Ready==true&&!Pending&&!Busy;
    public bool CanDownload=>!Busy&&!Pending&&Assessment?.CanInstall==true;
    public bool NeedsHelp=>!CanContinue;
    public string Backend {get=>backend;set{if(Busy||Pending||backend==value)return;backend=value;Assessment=null;messageKey="Checking your graphics software";error="";Restore();Notify();}}
    public string Vendor=>backend=="cuda"?"NVIDIA":"AMD";
    public string DeviceName=>Hardware.Preferred(Backend)?.Name??T["Graphics card not identified yet"];
    public string Message=>T[messageKey];public string Error=>T[error];public string Detail=>detail;public double Percent=>percent;
    public string Status=>T[Busy?"Working":Pending?"Finish the official installer":CanContinue?"Ready to continue":"Action needed"];
    string Marker=>Path.Combine(workspace(),"setup","driver-help.json");
    public DriverSetupModel(string backend,Func<string> workspace,L10n language,IDriverPlatform? platform=null,Func<bool>? canRestore=null)
    {this.canRestore=canRestore??(()=>true);this.backend=backend;this.workspace=workspace;T=language;this.platform=platform??new NativeDriverPlatform();RestoreWorkspace();T.PropertyChanged+=LanguageChanged;}
    public void RestoreWorkspace()
    {
        if(Busy)return;Pending=false;
        if(canRestore())try{var saved=File.Exists(Marker)?Files.Read<DriverHelpProgress>(Marker):null;if(saved?.Backend is "cuda" or "amd_rocm")backend=saved.Backend;}catch{}
        Restore();Notify();
    }
    void Restore(){if(!canRestore()){Pending=false;return;}try{Pending=File.Exists(Marker)&&Files.Read<DriverHelpProgress>(Marker)?.Backend==Backend;}catch{Pending=false;}}
    void ClearPending(){if(File.Exists(Marker))File.Delete(Marker);Pending=false;}
    void LanguageChanged(object? sender,PropertyChangedEventArgs e)=>Notify();
    void Notify(){foreach(var name in new[]{nameof(CanContinue),nameof(CanDownload),nameof(NeedsHelp),nameof(Checked),nameof(Pending),nameof(Message),nameof(Error),nameof(Detail),nameof(Percent),nameof(Status),nameof(DeviceName),nameof(Vendor)})Changed(name);}
    Task Start(Func<CancellationToken,Task> action)
    {
        if(disposed||Busy)return Task.CompletedTask;error="";detail="";percent=0;Busy=true;var source=new CancellationTokenSource();cancellation=source;return operation=Run(action,source);
    }
    async Task Run(Func<CancellationToken,Task> action,CancellationTokenSource source)
    {
        try{await action(source.Token);}
        catch(OperationCanceledException) when(source.IsCancellationRequested){error="This step was stopped. You can try again later.";}
        catch(System.ComponentModel.Win32Exception e) when(e.NativeErrorCode==1223){error="Installation was cancelled. You can start it again when you are ready.";ClearPending();}
        catch(DesktopIntegrationRestrictedException){error="Close this copy of LightX2V, then open it from a normal extracted folder or your desktop shortcut and try again.";}
        catch(Exception e){error="This step could not be completed. Try again or use the official download page.";detail=e.Message;}
        finally{source.Dispose();if(cancellation==source)cancellation=null;Busy=false;}
    }
    public Task CheckAsync(bool userRequested=false)=>Start(async token=>
    {
        Assessment=null;messageKey="Checking your graphics software";Notify();var result=await platform.Check(Backend,token);token.ThrowIfCancellationRequested();if(disposed)return;
        Assessment=result;Hardware=result.Hardware;detail=result.Detail;
        try{Files.Write(Path.Combine(workspace(),"setup","hardware-detection.json"),result);}catch(IOException){}catch(UnauthorizedAccessException){}
        if(userRequested&&result.Ready)ClearPending();
        messageKey=Pending?"Finish the official installation, then return here and check again.":result.Condition switch
        {
            DriverCondition.CheckFailed=>"Graphics detection could not finish. Your driver may already be installed. Scan again.",
            DriverCondition.Ready=>"Your graphics software is installed. Continue to prepare the image-generation components.",
            DriverCondition.Missing=>"Your computer needs graphics software before it can generate images. The official assistant will choose it for you.",
            DriverCondition.Outdated=>"Your graphics software needs an update. Use the official assistant below, then check again.",
            DriverCondition.Restart=>"Windows needs a restart to finish setting up the graphics card. Save your files, restart, then reopen LightX2V.",
            DriverCondition.Disabled=>"Windows has disabled this graphics card. Enable it in Device Manager or ask your computer maker for help.",
            DriverCondition.DeviceProblem=>"Windows cannot use the graphics card yet. Try restarting, or follow the official installation steps below.",
            _=>"We have not identified a matching graphics card. Check again, or use your computer maker's support page."
        };Notify();
    });
    public async Task<bool> EnsureReadyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!Checked&&!Busy)await CheckAsync();token.ThrowIfCancellationRequested();return CanContinue;
    }
    public void ConfirmComputeReady()
    {
        if(Pending||Busy)return;Assessment=new(Backend,DriverCondition.Ready,Hardware);messageKey="Your graphics software is installed. Continue to prepare the image-generation components.";Notify();
    }
    public Task DownloadAndOpenAsync()=>Start(async token=>
    {
        // Recheck immediately before downloading. A working computer never needs a reinstall.
        var result=await platform.Check(Backend,token);Assessment=result;Hardware=result.Hardware;
        if(result.Ready&&!Pending){messageKey="Your graphics software is installed. Continue to prepare the image-generation components.";return;}
        if(Pending||!result.CanInstall){messageKey="Finish the official installation, then return here and check again.";return;}
        messageKey="Downloading the official installation assistant";Notify();
        var progress=new Progress<SetupProgress>(p=>{if(disposed||!Busy||cancellation?.Token!=token)return;percent=p.Percent;Notify();});
        var file=await platform.Download(Backend,workspace(),progress,token);token.ThrowIfCancellationRequested();
        await Open(file,token);
    });
    public Task OpenDownloadedAsync(string file)=>CanContinue||Pending?Task.CompletedTask:Start(async token=>{var check=await platform.Check(Backend,token);Assessment=check;Hardware=check.Hardware;if(check.Ready){messageKey="Your graphics software is installed. Continue to prepare the image-generation components.";return;}if(!check.CanInstall){messageKey="We have not identified a matching graphics card. Check again, or use your computer maker's support page.";return;}await Open(file,token);});
    async Task Open(string file,CancellationToken token)
    {
        if(CanContinue)return;
        Files.Write(Marker,new DriverHelpProgress(Backend,DateTimeOffset.Now));Pending=true;Notify();
        try{await platform.Launch(Backend,file,token);}
        catch{ClearPending();throw;}
        messageKey="The official assistant is open. Follow its steps, then return here and check again.";Notify();
    }
    public void ResetWaiting(){if(Busy||!Pending)return;ClearPending();Assessment=null;messageKey="Close the official installer, then check this computer again.";Notify();}
    public void LinkCopied(){messageKey="Official link copied. Paste it into your browser address bar to open it.";error="";Notify();}
    public void OpenOfficialPage(){try{platform.OpenPage(Backend);messageKey="The official download page has been requested. Download the Windows installation assistant, then open it.";error="";}catch(Exception e){error="Could not open the browser. Copy the official link and open it yourself.";detail=e.Message;}Notify();}
    public async Task CancelAsync(){cancellation?.Cancel();if(operation!=null)await operation;}
    public void Dispose(){disposed=true;cancellation?.Cancel();T.PropertyChanged-=LanguageChanged;}
}
