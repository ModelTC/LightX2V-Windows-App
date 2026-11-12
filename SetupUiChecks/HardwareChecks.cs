using LightX2V.Windows;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
internal static partial class Program
{
    sealed class FakeHardwareSources : IHardwareSources
    {
        public HardwareProbeResult DevicesResult=new("Windows devices",[]),NvidiaResult=new("nvidia-smi",[],Available:false),CimResult=new("CIM",[]);public bool ThrowDevices,ThrowCim;public List<string> Calls=[];
        public HardwareProbeResult Devices(){Calls.Add("devices");if(ThrowDevices)throw new UnauthorizedAccessException("Device enumeration denied");return DevicesResult;}
        public HardwareProbeResult Nvidia(){Calls.Add("nvidia");return NvidiaResult;}
        public HardwareProbeResult Cim(){Calls.Add("cim");if(ThrowCim)throw new IOException("CIM provider unavailable");return CimResult;}
    }
    static async Task HardwareDetectionChecks()
    {
        const string userName="NVIDIA GeForce RTX 4070 Laptop GPU";
        var rows=HardwareDiscovery.ParseNvidia("NVIDIA GeForce RTX 4070 Laptop GPU, 621.01, 00000000:01:00.0\r\n");
        Check(rows.Count==1&&rows[0].Name==userName&&rows[0].Driver=="621.01"&&rows[0].BusId=="00000000:01:00.0"&&rows[0].DriverInstalled==true,"The reported RTX 4070 / 621.01 / PCI 01:00.0 nvidia-smi row is recognized as an installed NVIDIA driver");
        Check(HardwareDiscovery.ParseNvidia("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.").Count==0&&HardwareDiscovery.ParseNvidia("name, driver_version, pci.bus_id").Count==0,"nvidia-smi errors and headers cannot become a false GPU or ready driver");
        Check(HardwareDiscovery.ParseNvidia("\"NVIDIA RTX, QA\", 621.01, 0000:01:00.0").Count==1,"Quoted GPU names and both PCI domain formats are parsed correctly");
        var source=new FakeHardwareSources{ThrowDevices=true,NvidiaResult=new("nvidia-smi",rows),ThrowCim=true};var hardware=HardwareDiscovery.Detect(source);
        Check(hardware.Backend=="cuda"&&hardware.Supports("cuda")&&source.Calls.SequenceEqual(new[]{"devices","nvidia"}),"A failed Windows enumeration recovers through nvidia-smi without depending on CIM");
        Check(hardware.Probes.Any(p=>p.Error.Contains("denied"))&&!hardware.DetectionFailed,"Fallback success keeps the original detection error for diagnostics without claiming that the GPU is missing");
        var cudaChecks=0;var platform=new NativeDriverPlatform(()=>hardware,()=>{cudaChecks++;CudaDriver.ValidateVersion(13050);});var assessed=await platform.Check("cuda",CancellationToken.None);
        Check(assessed.Ready&&cudaChecks==1,"The supplied CUDA driver API 13.5 passes the app's CUDA 12.8 compatibility floor after RTX detection");
        var apiFailure=await new NativeDriverPlatform(()=>hardware,()=>throw new InvalidOperationException("Driver API query denied")).Check("cuda",CancellationToken.None);
        Check(apiFailure.Condition==DriverCondition.CheckFailed&&!apiFailure.CanInstall,"An unreadable CUDA driver API is not mistaken for an outdated driver or a reason to reinstall it");
        var oldDriver=await new NativeDriverPlatform(()=>hardware,()=>CudaDriver.ValidateVersion(12060)).Check("cuda",CancellationToken.None);
        Check(oldDriver.Condition==DriverCondition.Outdated&&oldDriver.CanInstall,"An actually older CUDA driver remains distinguishable from a failed query");
        var fake=new FakeDriverPlatform{Computer=hardware};var workspace=Path.Combine(root,"rtx-detection-fixture");using var driver=new DriverSetupModel("cuda",()=>workspace,L10n.Current,fake);await driver.CheckAsync();await driver.DownloadAndOpenAsync();
        Check(driver.CanContinue&&fake.Downloads==0&&fake.Launches==0,"A working RTX 4070 fixture skips downloading or launching a driver installer");
        Check(File.Exists(Path.Combine(workspace,"setup","hardware-detection.json")),"Driver checks save the complete detection report in the chosen workspace");
        var preview=DriverPreview(driver);await Render(preview,740,540,"hardware-rtx4070-ready");preview.Close();
        source=new FakeHardwareSources{DevicesResult=new("Windows devices",[],"Hardware query timed out."),NvidiaResult=new("nvidia-smi",rows)};hardware=HardwareDiscovery.Detect(source);
        Check(hardware.Backend=="cuda"&&hardware.Diagnostics.Contains("timed out"),"A timeout preserves its diagnostic cause and recovers through the independent NVIDIA query");
        source=new FakeHardwareSources{DevicesResult=new("Windows devices",[new("AMD Radeon 8060S","32")],"A second device could not be read"),CimResult=new("CIM",rows)};hardware=HardwareDiscovery.Detect(source);
        Check(hardware.Backend=="cuda"&&hardware.All.Any(d=>d.Backend=="amd_rocm")&&source.Calls.Contains("cim"),"Partial native enumeration keeps AMD data and still checks CIM for a previously unreadable NVIDIA device");
        var versionOnly=new GpuAdapter(userName,"32.0.16.2101"){PnpId=@"PCI\VEN_10DE&DEV_2820",DriverInstalled=true};
        source=new FakeHardwareSources{DevicesResult=new("Windows devices",[versionOnly]),ThrowCim=true};hardware=HardwareDiscovery.Detect(source);
        Check(source.Calls.SequenceEqual(new[]{"devices"})&&DriverAssessment.Assess("cuda",hardware).Ready,"Native Windows driver version 32.0.16.2101 is enough to identify the installed driver without PowerShell or NVIDIA App");
        var incomplete=versionOnly with{Driver="",DriverInstalled=null,StatusKnown=false};source=new FakeHardwareSources{DevicesResult=new("Windows devices",[incomplete]),NvidiaResult=new("nvidia-smi",rows)};hardware=HardwareDiscovery.Detect(source);
        Check(hardware.All.Count==1&&hardware.All[0].PnpId==versionOnly.PnpId&&hardware.All[0].Driver=="621.01"&&DriverAssessment.Assess("cuda",hardware).Ready,"A partial native record is enriched by nvidia-smi while retaining its PCI identity");
        var basic=new GpuAdapter("Microsoft Basic Display Adapter","10.0.26100.1"){PnpId=@"PCI\VEN_10DE&DEV_2820",ProblemCode=28,DriverInstalled=false};source=new FakeHardwareSources{DevicesResult=new("Windows devices",[basic]),NvidiaResult=new("nvidia-smi",rows)};hardware=HardwareDiscovery.Detect(source);
        Check(DriverAssessment.Assess("cuda",hardware).Ready&&hardware.Preferred("cuda")?.Name==userName,"A responding NVIDIA GPU takes precedence over a generic basic-adapter record without requesting driver installation");
        var cim=HardwareDiscovery.ParseCim("[{\"Name\":\"NVIDIA GeForce RTX 4070 Laptop GPU\",\"DriverVersion\":\"32.0.16.2101\",\"PNPDeviceID\":\"PCI\\\\VEN_10DE&DEV_2820\",\"ConfigManagerErrorCode\":0},{\"Name\":null,\"DriverVersion\":null},null,17]");
        Check(cim.Count==1&&cim[0].Backend=="cuda"&&DriverAssessment.Assess("cuda",HardwareInfo.FromAdapters(cim)).Ready,"Malformed or null optional CIM rows cannot discard an otherwise valid RTX controller");
        source=new FakeHardwareSources{ThrowDevices=true,CimResult=new("CIM",cim,"Optional PnP query failed")};hardware=HardwareDiscovery.Detect(source);
        Check(DriverAssessment.Assess("cuda",hardware).Ready&&hardware.Diagnostics.Contains("Optional PnP"),"A secondary CIM provider failure preserves valid video controller information");
        source=new FakeHardwareSources{ThrowDevices=true,ThrowCim=true};hardware=HardwareDiscovery.Detect(source);var failed=DriverAssessment.Assess("cuda",hardware);
        Check(hardware.DetectionFailed&&failed.Condition==DriverCondition.CheckFailed&&!failed.CanInstall,"Total detection failure is distinct from an absent driver and never enables automatic driver installation");
        fake=new FakeDriverPlatform{Computer=hardware,Condition=DriverCondition.CheckFailed};using var failure=new DriverSetupModel("cuda",()=>Path.Combine(root,"failed-hardware-report"),L10n.Current,fake);await failure.CheckAsync();
        preview=DriverPreview(failure);await Render(preview,740,600,"hardware-check-failed");
        Check(failure.Message.Contains(L10n.Current["Graphics detection could not finish. Your driver may already be installed. Scan again."]),"Detection failure gives a recheck message instead of telling the user their driver is missing");
        Check(!Children<Expander>((DependencyObject)preview.Content).Any(),"Driver cards do not expose the removed help and computer-information module");
        Check(File.Exists(Path.Combine(root,"failed-hardware-report","setup","hardware-detection.json")),"Detection diagnostics remain saved to the workspace without the removed help module");preview.Close();
        var noVersion=versionOnly with{Driver="",DriverInstalled=true};Check(DriverAssessment.Assess("cuda",HardwareInfo.FromAdapters([noVersion])).Ready,"A started device remains recognized when Windows prevents reading its version string");
        noVersion=noVersion with{DriverInstalled=null};Check(DriverAssessment.Assess("cuda",HardwareInfo.FromAdapters([noVersion])).Condition==DriverCondition.CheckFailed,"An unreadable driver version alone does not prove a missing driver");
        Check(DriverAssessment.Assess("cuda",HardwareInfo.FromAdapters([versionOnly with{ProblemCode=22}])).Condition==DriverCondition.Disabled,"An explicitly disabled device retains its Windows state instead of being marked ready");
        Files.Write(Path.Combine(root,"rtx4070-detection-fixture.json"),new{evidence="User-reported identity and driver data; fallback and CUDA API calls use injected fixtures, not physical RTX hardware",name=userName,version="621.01",windowsVersion="32.0.16.2101",cudaDriverApi=13050});
    }
}
