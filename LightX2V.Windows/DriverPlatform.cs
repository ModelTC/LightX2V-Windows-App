using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace LightX2V.Windows;

public enum DriverCondition { Ready,Missing,Outdated,Restart,Disabled,Unknown,DeviceProblem,CheckFailed }
public sealed record DriverAssessment(string Backend,DriverCondition Condition,HardwareInfo Hardware,string Detail="")
{
    public bool Ready=>Condition==DriverCondition.Ready;
    public bool CanInstall=>Condition is DriverCondition.Missing or DriverCondition.Outdated or DriverCondition.DeviceProblem;
    public static DriverAssessment Assess(string backend,HardwareInfo hardware,string? cudaError=null,DriverCondition cudaCondition=DriverCondition.Outdated)
    {
        var adapters=hardware.All.Where(d=>d.Backend==backend).ToArray();
        if(adapters.Length==0)return new(backend,hardware.DetectionFailed?DriverCondition.CheckFailed:DriverCondition.Unknown,hardware,hardware.Diagnostics);
        var working=adapters.FirstOrDefault(d=>d.StatusKnown&&d.ProblemCode==0&&(d.Driver.Length>0||d.DriverInstalled==true)&&!Basic(d));
        var gpu=working??adapters[0];
        if(!gpu.StatusKnown)return new(backend,DriverCondition.CheckFailed,hardware,hardware.Diagnostics);
        if(gpu.ProblemCode==14)return new(backend,DriverCondition.Restart,hardware);
        if(gpu.ProblemCode==22)return new(backend,DriverCondition.Disabled,hardware);
        if(Basic(gpu)||gpu.ProblemCode is 28 or 31)return new(backend,DriverCondition.Missing,hardware);
        if(gpu.ProblemCode!=0)return new(backend,DriverCondition.DeviceProblem,hardware,$"Windows device code {gpu.ProblemCode}");
        if(gpu.Driver.Length==0&&gpu.DriverInstalled==null)return new(backend,DriverCondition.CheckFailed,hardware,hardware.Diagnostics);
        return new(backend,backend=="cuda"&&cudaError!=null?cudaCondition:DriverCondition.Ready,hardware,cudaError??"");
    }
    static bool Basic(GpuAdapter d)=>string.IsNullOrWhiteSpace(d.Driver)&&d.DriverInstalled==false||d.Name.Contains("Basic",StringComparison.OrdinalIgnoreCase)||d.Name.Contains("基本",StringComparison.OrdinalIgnoreCase);
}
public sealed class DriverAssistant
{
    public string Backend {get;set;}="";public string Name {get;set;}="";public string Page {get;set;}="";public string PageZh {get;set;}="";
    public string LocalizedPage=>L10n.Current.Language=="zh"&&PageZh.Length>0?PageZh:Page;public SetupArtifact Download {get;set;}=new();
    public static DriverAssistant Read(string backend)
    {
        var items=Files.Read<List<DriverAssistant>>(Path.Combine(AppContext.BaseDirectory,"Resources","driver-assistants.json"))??[];
        var item=items.Single(p=>p.Backend==backend);var uri=new Uri(item.Download.Url);if(uri.Scheme!="https"||uri.Host!=(backend=="cuda"?"us.download.nvidia.com":"drivers.amd.com")||item.Download.Size<=0||item.Download.Sha256.Length!=64||Path.GetFileName(item.Download.File)!=item.Download.File)throw new IOException("The official installer information is invalid. Use the official download page.");return item;
    }
}
public interface IDriverPlatform
{
    Task<DriverAssessment> Check(string backend,CancellationToken token);
    Task<string> Download(string backend,string workspace,IProgress<SetupProgress> progress,CancellationToken token);
    Task Launch(string backend,string installer,CancellationToken token);
    void OpenPage(string backend);
}
public sealed record InstallerSignature(string Status,string Publisher);
public static class DriverSignatures
{
    public static void RequirePublisher(string backend,InstallerSignature signature)
    {
        var publisher=new string(signature.Publisher.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var expected=backend=="cuda"?publisher=="nvidiacorporation":backend=="amd_rocm"&&publisher is "advancedmicrodevices" or "advancedmicrodevicesinc" or "advancedmicrodevicesincorporated";
        if(signature.Status!="Valid"||!expected)throw new InvalidOperationException("The installer could not be verified as an official AMD or NVIDIA program. Download it again using the official page.");
    }
    public static async Task<InstallerSignature> Inspect(string path,CancellationToken token)
    {
        var script="[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; $s=Get-AuthenticodeSignature -LiteralPath $env:LIGHTX2V_DRIVER_FILE; [pscustomobject]@{Status=$s.Status.ToString();Publisher=if($s.SignerCertificate){$s.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false)}else{''}} | ConvertTo-Json -Compress";
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
        foreach(var arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(arg);
        start.Environment["LIGHTX2V_DRIVER_FILE"]=Path.GetFullPath(path);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var process=Process.Start(start)??throw new IOException("Unable to check the official installer.");
        using var registration=timeout.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}});
        var output=process.StandardOutput.ReadToEndAsync(timeout.Token);var errors=process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);var text=await output;var error=await errors;if(process.ExitCode!=0)throw new IOException(error);
        return JsonSerializer.Deserialize<InstallerSignature>(text,Files.Json)??throw new IOException("Windows did not return an installer signature.");
    }
}
public sealed class NativeDriverPlatform : IDriverPlatform
{
    readonly Func<HardwareInfo> readHardware;readonly Action checkCuda;
    public NativeDriverPlatform(Func<HardwareInfo>? readHardware=null,Action? checkCuda=null){this.readHardware=readHardware??HardwareInfo.Refresh;this.checkCuda=checkCuda??CudaDriver.RequireCompatible;}
    public Task<DriverAssessment> Check(string backend,CancellationToken token)=>Task.Run(()=>
    {
        token.ThrowIfCancellationRequested();var hardware=readHardware();var result=DriverAssessment.Assess(backend,hardware);
        if(backend=="cuda"&&result.Ready){try{checkCuda();}catch(CudaDriverException e){result=DriverAssessment.Assess(backend,hardware,e.Message,e.Condition);}catch(Exception e){result=DriverAssessment.Assess(backend,hardware,e.Message,DriverCondition.CheckFailed);}}
        token.ThrowIfCancellationRequested();return result with{Detail=string.Join("\n",new[]{result.Detail,hardware.Diagnostics}.Where(s=>s.Length>0).Distinct())};
    },token);
    public async Task<string> Download(string backend,string workspace,IProgress<SetupProgress> progress,CancellationToken token)
    {
        var assistant=DriverAssistant.Read(backend);var cache=Path.Combine(workspace,"cache","driver-assistants",backend);
        var file=await SetupInstaller.Download(assistant.Download,cache,[],false,progress,token);
        DriverSignatures.RequirePublisher(backend,await DriverSignatures.Inspect(file,token));return file;
    }
    public async Task Launch(string backend,string installer,CancellationToken token)
    {
        if(!Path.IsPathFullyQualified(installer)||!installer.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new IOException("Choose an official Windows installer (.exe).");
        // Hold a read-only handle across verification and launch so the verified file cannot be replaced.
        using var guard=new FileStream(installer,FileMode.Open,FileAccess.Read,FileShare.Read);
        DriverSignatures.RequirePublisher(backend,await DriverSignatures.Inspect(installer,token));token.ThrowIfCancellationRequested();
        WindowsShell.Launch(new ProcessStartInfo(Path.GetFullPath(installer)){UseShellExecute=true,Verb="open"},WindowsShell.CurrentIntegrity(),request=>Process.Start(request)?.Dispose());
    }
    public void OpenPage(string backend)=>WindowsShell.OpenWebPage(new Uri(DriverAssistant.Read(backend).LocalizedPage));
}
