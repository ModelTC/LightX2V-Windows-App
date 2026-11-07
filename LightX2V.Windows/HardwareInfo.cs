using Microsoft.Win32;
using System.Runtime.InteropServices;
namespace LightX2V.Windows;

public sealed record GpuAdapter(string Name,string Driver)
{
    public string PnpId {get;init;}="";public int ProblemCode {get;init;}public bool StatusKnown {get;init;}=true;public bool? DriverInstalled {get;init;}public string BusId {get;init;}="";
    public string Backend=>PnpId.Contains("VEN_10DE",StringComparison.OrdinalIgnoreCase)?"cuda":PnpId.Contains("VEN_1002",StringComparison.OrdinalIgnoreCase)?"amd_rocm":Name.Contains("NVIDIA",StringComparison.OrdinalIgnoreCase)?"cuda":Name.Contains("AMD",StringComparison.OrdinalIgnoreCase)||Name.Contains("Radeon",StringComparison.OrdinalIgnoreCase)?"amd_rocm":"";
}
public sealed record HardwareInfo(string Name,string Driver)
{
    public IReadOnlyList<GpuAdapter> Adapters {get;init;}=[];
    public IReadOnlyList<HardwareProbeResult> Probes {get;init;}=[];public DateTimeOffset? ScannedAt {get;init;}
    public bool DetectionFailed=>!All.Any(d=>d.Backend.Length>0)&&Probes.Any(p=>p.Error.Length>0);
    public string Diagnostics=>string.Join("\n",Probes.Select(p=>p.Source+": "+(!p.Available?"not available":p.Adapters.Count+" display device(s)")+(p.Error.Length>0?" · "+p.Error:"")));
    public string Backend=>All.FirstOrDefault(d=>d.Backend=="cuda")?.Backend??All.FirstOrDefault(d=>d.Backend=="amd_rocm")?.Backend??"";
    public bool ManagedSupported=>Supports(Backend);
    public bool Supports(string backend)=>Environment.Is64BitOperatingSystem&&RuntimeInformation.ProcessArchitecture==Architecture.X64&&
        All.Any(d=>backend=="cuda"?d.Backend=="cuda":backend=="amd_rocm"&&d.Name.Contains("8060S",StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<GpuAdapter> All=>Adapters.Count>0?Adapters:[new(Name,Driver)];
    public GpuAdapter? Preferred(string backend)=>All.Where(d=>d.Backend==backend).OrderByDescending(d=>d.StatusKnown&&d.ProblemCode==0&&(d.Driver.Length>0||d.DriverInstalled==true)&&!d.Name.Contains("Basic",StringComparison.OrdinalIgnoreCase)&&!d.Name.Contains("基本",StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
    public static HardwareInfo FromAdapters(IEnumerable<GpuAdapter> adapters)
    {
        var all=adapters.Distinct().ToArray();var chosen=all.Where(d=>d.Backend=="cuda").OrderByDescending(d=>d.StatusKnown&&d.ProblemCode==0&&(d.Driver.Length>0||d.DriverInstalled==true)&&!d.Name.Contains("Basic",StringComparison.OrdinalIgnoreCase)&&!d.Name.Contains("基本",StringComparison.OrdinalIgnoreCase)).FirstOrDefault()??all.FirstOrDefault(d=>d.Name.Contains("8060S",StringComparison.OrdinalIgnoreCase))??all.FirstOrDefault(d=>d.Backend=="amd_rocm")??all.FirstOrDefault();
        return new(chosen?.Name??"Unknown GPU",chosen?.Driver??""){Adapters=all};
    }
    static HardwareInfo? current;
    public static HardwareInfo Read()=>current??Refresh();
    public static HardwareInfo Refresh()=>current=Detect();
    static HardwareInfo Detect()=>OperatingSystem.IsWindows()?HardwareDiscovery.Detect(new NativeHardwareSources()):FromAdapters([]);

}
public static class Backends
{
    public static string Resolve(string backend,HardwareInfo? hardware=null)=>backend switch{"cuda"=>"cuda","amd_rocm"=>"amd_rocm","auto" or ""=>(hardware??HardwareInfo.Read()).Backend is "cuda"?"cuda":"amd_rocm",_=>throw new InvalidOperationException("Unknown backend.")};
    public static string Label(string backend)=>backend=="cuda"?"NVIDIA CUDA":"AMD ROCm";
    public const string Unsupported="Automatic installation needs a detected NVIDIA GPU or the verified Radeon 8060S. You can check an existing compatible environment.";
}
public sealed record ResourceSnapshot(ulong TotalRam,ulong AvailableRam,long DiskFree)
{
    [StructLayout(LayoutKind.Sequential)]struct MemoryStatus {public uint Length,Load;public ulong TotalPhys,AvailPhys,TotalPage,AvailPage,TotalVirtual,AvailVirtual,AvailExtended;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GlobalMemoryStatusEx(ref MemoryStatus value);
    public static ResourceSnapshot Read(string workspace)
    {
        var memory=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};if(!GlobalMemoryStatusEx(ref memory))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new(memory.TotalPhys,memory.AvailPhys,FreeDisk(workspace));
    }
    public static long FreeDisk(string directory){var path=Path.GetFullPath(directory);while(!Directory.Exists(path))path=Path.GetDirectoryName(path)??throw new IOException("Choose a local workspace drive.");return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;}
    public string Summary=> $"RAM {TotalRam/1073741824.0:F1} GiB · {AvailableRam/1073741824.0:F1} GiB free · Disk {DiskFree/1073741824.0:F1} GiB free";
}

public sealed class CudaDriverException(DriverCondition condition,string message) : InvalidOperationException(message)
{public DriverCondition Condition {get;}=condition;}
public static class CudaDriver
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate int Initialize(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate int DriverVersion(out int version);
    public static void ValidateVersion(int version)
    {
        if(version<12080)throw new CudaDriverException(DriverCondition.Outdated,"This bundled CUDA 12.8 profile requires a driver exposing CUDA 12.8 or newer. Update the NVIDIA display driver, then retry. The app does not modify system drivers.");
    }
    public static void RequireCompatible()
    {
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"nvcuda.dll");
        if(!File.Exists(path))throw new CudaDriverException(DriverCondition.Missing,"The NVIDIA CUDA driver was not found. Install the NVIDIA display driver before preparing this environment.");
        var library=NativeLibrary.Load(path);
        try
        {
            var initialize=Marshal.GetDelegateForFunctionPointer<Initialize>(NativeLibrary.GetExport(library,"cuInit"));
            var version=Marshal.GetDelegateForFunctionPointer<DriverVersion>(NativeLibrary.GetExport(library,"cuDriverGetVersion"));
            var result=initialize(0);if(result!=0)throw new CudaDriverException(DriverCondition.CheckFailed,$"The NVIDIA driver could not initialize (CUDA error {result}). Check the GPU and display driver before installing.");
            result=version(out var value);if(result!=0)throw new CudaDriverException(DriverCondition.CheckFailed,$"Unable to query the NVIDIA CUDA driver (error {result}).");ValidateVersion(value);
        }
        finally{NativeLibrary.Free(library);}
    }
}
