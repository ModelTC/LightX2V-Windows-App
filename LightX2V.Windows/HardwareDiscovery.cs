using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LightX2V.Windows;

public sealed record HardwareProbeResult(string Source,IReadOnlyList<GpuAdapter> Adapters,string Error="",bool Available=true);
public interface IHardwareSources
{
    HardwareProbeResult Devices();
    HardwareProbeResult Nvidia();
    HardwareProbeResult Cim();
}
public static class HardwareDiscovery
{
    public static HardwareInfo Detect(IHardwareSources sources)
    {
        var adapters=new List<GpuAdapter>();var probes=new List<HardwareProbeResult>();
        void Read(string name,Func<HardwareProbeResult> read)
        {
            HardwareProbeResult result;try{result=read();}catch(Exception e){result=new(name,[],e.Message);}
            probes.Add(result);
            foreach(var incoming in result.Adapters)
            {
                // A malformed record must never discard other valid devices.
                if(string.IsNullOrWhiteSpace(incoming.Name)&&string.IsNullOrWhiteSpace(incoming.PnpId))continue;
                var index=adapters.FindIndex(old=>incoming.PnpId.Length>0&&old.PnpId.Equals(incoming.PnpId,StringComparison.OrdinalIgnoreCase)||
                    old.Name.Equals(incoming.Name,StringComparison.OrdinalIgnoreCase)&&(incoming.PnpId.Length==0||old.PnpId.Length==0));
                if(index<0){adapters.Add(incoming);continue;}
                var previous=adapters[index];
                // Never overwrite an explicit disabled/problem state with an unrelated healthy report.
                adapters[index]=previous with{Driver=previous.Driver.Length>0?previous.Driver:incoming.Driver,PnpId=previous.PnpId.Length>0?previous.PnpId:incoming.PnpId,
                    DriverInstalled=previous.DriverInstalled??incoming.DriverInstalled,StatusKnown=previous.StatusKnown||incoming.StatusKnown,
                    BusId=previous.BusId.Length>0?previous.BusId:incoming.BusId};
            }
        }
        bool Complete()=>adapters.Any(d=>d.Backend.Length>0)&&!adapters.Any(d=>d.Backend.Length>0&&(!d.StatusKnown||d.Name.Contains("Basic",StringComparison.OrdinalIgnoreCase)||d.Name.Contains("基本",StringComparison.OrdinalIgnoreCase)||d.ProblemCode==0&&d.Driver.Length==0&&d.DriverInstalled!=true));
        Read("Windows devices",sources.Devices);
        if(!Complete()||probes[0].Error.Length>0)Read("nvidia-smi",sources.Nvidia);
        if(!Complete()||probes[0].Error.Length>0&&probes[^1].Adapters.Count==0)Read("CIM",sources.Cim);
        return HardwareInfo.FromAdapters(adapters) with{Probes=probes,ScannedAt=DateTimeOffset.Now};
    }
    public static IReadOnlyList<GpuAdapter> ParseNvidia(string csv)
    {
        var result=new List<GpuAdapter>();
        foreach(var line in csv.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries))
        {
            var columns=Csv(line);if(columns.Count!=3)continue;
            var name=columns[0].Trim();var version=columns[1].Trim();var bus=columns[2].Trim();
            if(name.Length==0||!System.Text.RegularExpressions.Regex.IsMatch(bus,@"^[0-9a-f]{4,8}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)||!Version.TryParse(version,out _))continue;
            if(!name.Contains("NVIDIA",StringComparison.OrdinalIgnoreCase))name="NVIDIA "+name;
            result.Add(new(name,version){DriverInstalled=true,BusId=bus});
        }
        return result;
    }
    static List<string> Csv(string line)
    {
        var result=new List<string>();var text=new StringBuilder();bool quote=false;
        for(int i=0;i<line.Length;i++){var c=line[i];if(c=='"'){if(quote&&i+1<line.Length&&line[i+1]=='"'){text.Append('"');i++;}else quote=!quote;}else if(c==','&&!quote){result.Add(text.ToString());text.Clear();}else text.Append(c);}
        if(quote)return [];result.Add(text.ToString());return result;
    }
    public static IReadOnlyList<GpuAdapter> ParseCim(string json)
    {
        using var document=JsonDocument.Parse(json.TrimStart('\uFEFF'));
        var items=document.RootElement.ValueKind==JsonValueKind.Array?document.RootElement.EnumerateArray().ToArray():[document.RootElement];var result=new List<GpuAdapter>();
        foreach(var item in items)
        {
            if(item.ValueKind!=JsonValueKind.Object)continue;
            string Value(string key)=>item.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
            var name=Value("Name");var id=Value("PNPDeviceID");if(name.Length==0&&id.Length==0)continue;
            var code=0;var known=item.TryGetProperty("ConfigManagerErrorCode",out var status)&&status.ValueKind!=JsonValueKind.Null;
            if(known&&!int.TryParse(status.ToString(),out code))known=false;
            result.Add(new(name.Length>0?name:"Display adapter",Value("DriverVersion")){PnpId=id,ProblemCode=code,StatusKnown=known});
        }
        return result;
    }
}
public sealed class NativeHardwareSources : IHardwareSources
{
    public HardwareProbeResult Devices()=>NativeDisplayDevices.Read();
    public HardwareProbeResult Nvidia()
    {
        var candidates=new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"nvidia-smi.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NVIDIA Corporation","NVSMI","nvidia-smi.exe")};
        var path=candidates.FirstOrDefault(File.Exists);if(path==null)return new("nvidia-smi",[],Available:false);
        var result=Run(path,["--query-gpu=name,driver_version,pci.bus_id","--format=csv,noheader,nounits"],12000);
        if(result.Code!=0)return new("nvidia-smi",[],result.Error.Length>0?result.Error:result.Output);
        var adapters=HardwareDiscovery.ParseNvidia(result.Output);return new("nvidia-smi",adapters,adapters.Count==0?"No GPU rows were returned.":"");
    }
    public HardwareProbeResult Cim()
    {
        // Query providers separately: failure of the optional PnP query must not discard valid video controllers.
        var script="[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $items=@(); $issues=@(); try{$items+=@(Get-CimInstance Win32_VideoController -ErrorAction Stop | Select-Object Name,DriverVersion,PNPDeviceID,ConfigManagerErrorCode)}catch{$issues+=$_.Exception.Message}; try{$devices=Get-CimInstance Win32_PnPEntity -Filter \"PNPClass='Display' OR ConfigManagerErrorCode=28\" -ErrorAction Stop; foreach($d in $devices){if($d.PNPDeviceID -like 'PCI*' -and ($d.PNPClass -eq 'Display' -or ($d.CompatibleID -match 'PCI\\CC_03')) -and $d.PNPDeviceID -notin $items.PNPDeviceID){$items+=[pscustomobject]@{Name=$d.Name;DriverVersion='';PNPDeviceID=$d.PNPDeviceID;ConfigManagerErrorCode=$d.ConfigManagerErrorCode}}}}catch{$issues+=$_.Exception.Message}; [pscustomobject]@{Items=@($items);Issues=@($issues)} | ConvertTo-Json -Depth 4 -Compress";
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        var result=Run(path,["-NoLogo","-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],15000);
        if(result.Code!=0)return new("CIM",[],result.Error.Length>0?result.Error:result.Output);
        using var document=JsonDocument.Parse(result.Output.TrimStart('\uFEFF'));var root=document.RootElement;
        return new("CIM",HardwareDiscovery.ParseCim(root.GetProperty("Items").GetRawText()),string.Join("; ",root.GetProperty("Issues").EnumerateArray().Select(v=>v.GetString())));
    }
    static (int Code,string Output,string Error) Run(string path,string[] arguments,int milliseconds)
    {
        var start=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};foreach(var arg in arguments)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Unable to start hardware detection.");var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(milliseconds)){process.Kill(true);process.WaitForExit();return(-1,"","Hardware query timed out.");}
        return(process.ExitCode,output.GetAwaiter().GetResult().Trim(),errors.GetAwaiter().GetResult().Trim());
    }
}
internal static class NativeDisplayDevices
{
    static readonly Guid DisplayClass=new("4d36e968-e325-11ce-bfc1-08002be10318");
    [StructLayout(LayoutKind.Sequential)]struct DeviceInfo {public uint Size;public Guid ClassGuid;public uint Instance;public IntPtr Reserved;}
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid,string? enumerator,IntPtr parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)]static extern bool SetupDiEnumDeviceInfo(IntPtr set,uint index,ref DeviceInfo info);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set,ref DeviceInfo info,uint property,out uint type,byte[] data,uint size,out uint required);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set,ref DeviceInfo info,StringBuilder id,uint size,out uint required);
    [DllImport("setupapi.dll",SetLastError=true)]static extern IntPtr SetupDiOpenDevRegKey(IntPtr set,ref DeviceInfo info,uint scope,uint profile,uint type,uint access);
    [DllImport("setupapi.dll",SetLastError=true)]static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]static extern uint CM_Get_DevNode_Status(out uint status,out uint problem,uint instance,uint flags);
    public static HardwareProbeResult Read()
    {
        var set=SetupDiGetClassDevsW(IntPtr.Zero,null,IntPtr.Zero,0x2|0x4);if(set==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
        var result=new List<GpuAdapter>();var issues=new List<string>();
        try
        {
            for(uint index=0;;index++)
            {
                var info=new DeviceInfo{Size=(uint)Marshal.SizeOf<DeviceInfo>()};
                if(!SetupDiEnumDeviceInfo(set,index,ref info)){var error=Marshal.GetLastWin32Error();if(error!=259)issues.Add(new Win32Exception(error).Message);break;}
                var compatible=Property(set,ref info,2);
                if(info.ClassGuid!=DisplayClass&&!compatible.Contains("PCI\\CC_03",StringComparison.OrdinalIgnoreCase))continue;
                var id=new StringBuilder(4096);SetupDiGetDeviceInstanceIdW(set,ref info,id,(uint)id.Capacity,out _);
                var name=Property(set,ref info,12);if(name.Length==0)name=Property(set,ref info,0);if(name.Length==0)name="Display adapter";
                string version="";var key=SetupDiOpenDevRegKey(set,ref info,1,0,2,0x1);
                if(key!=new IntPtr(-1))
                {
                    try{using var handle=new SafeRegistryHandle(key,true);using var registry=RegistryKey.FromHandle(handle);version=registry.GetValue("DriverVersion") as string??"";}
                    catch(Exception e) when(e is UnauthorizedAccessException or IOException or System.Security.SecurityException){issues.Add(name+": "+e.Message);}
                }
                var known=CM_Get_DevNode_Status(out var status,out var problem,info.Instance,0)==0;
                var code=known&&(status&0x400)!=0?(int)problem:0; // DN_HAS_PROBLEM
                if(!known)issues.Add(name+": device status could not be read.");
                var instance=id.ToString();if(instance.Length==0)instance=Property(set,ref info,1).Split('\0')[0];
                result.Add(new(name,version){PnpId=instance,ProblemCode=code,StatusKnown=known,DriverInstalled=version.Length>0||known&&(status&0xA)!=0?true:code==28?false:null}); // DN_DRIVER_LOADED | DN_STARTED
            }
        }
        finally{SetupDiDestroyDeviceInfoList(set);}
        return new("Windows devices",result,string.Join("; ",issues));
    }
    static string Property(IntPtr set,ref DeviceInfo info,uint property)
    {
        var buffer=new byte[8192];if(!SetupDiGetDeviceRegistryPropertyW(set,ref info,property,out _,buffer,(uint)buffer.Length,out var size))return "";
        return Encoding.Unicode.GetString(buffer,0,(int)Math.Min(size,(uint)buffer.Length)).TrimEnd('\0');
    }
}
