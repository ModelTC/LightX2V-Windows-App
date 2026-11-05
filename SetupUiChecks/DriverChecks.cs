using LightX2V.Windows;
using System.IO;
using System.Windows;
using System.Threading;
internal static partial class Program
{
    sealed class FakeDriverPlatform : IDriverPlatform
    {
        public DriverCondition Condition=DriverCondition.Ready;public bool FailSignature,FailLaunch,Delay;public int Downloads,Launches,Checks;public string LastBackend="";public List<string>? Events;public HardwareInfo? Computer;public bool DelayCheck;
        public async Task<DriverAssessment> Check(string backend,CancellationToken token){Checks++;LastBackend=backend;Events?.Add("check:"+backend);if(DelayCheck)await Task.Delay(Timeout.Infinite,token);return new DriverAssessment(backend,Condition,Computer??HardwareInfo.FromAdapters([new("AMD Radeon 8060S","32.0.22018.5"),new("NVIDIA GeForce RTX 4090","32.0.15.9999")]));}
        public async Task<string> Download(string backend,string workspace,IProgress<SetupProgress> progress,CancellationToken token){Downloads++;progress.Report(new("",Percent:35));if(Delay)await Task.Delay(Timeout.Infinite,token);if(FailSignature)throw new InvalidOperationException("Signature not valid");return Path.Combine(workspace,"official-assistant.exe");}
        public Task Launch(string backend,string installer,CancellationToken token){if(FailLaunch)throw new System.ComponentModel.Win32Exception(1223);Launches++;return Task.CompletedTask;}
        public void OpenPage(string backend){LastBackend=backend;}
    }
    static async Task DriverChecks()
    {
        var missingNvidia=new GpuAdapter("Microsoft Basic Display Adapter",""){PnpId=@"PCI\VEN_10DE&DEV_1234",ProblemCode=28};var missingAmd=missingNvidia with{PnpId=@"PCI\VEN_1002&DEV_5678"};
        Check(missingNvidia.Backend=="cuda"&&missingAmd.Backend=="amd_rocm","PCI vendor IDs identify AMD and NVIDIA even when the vendor display driver is missing");
        Check(DriverAssessment.Assess("cuda",HardwareInfo.FromAdapters([missingNvidia])).Condition==DriverCondition.Missing,"A basic Windows display adapter is reported as needing graphics software");
        Check(DriverAssessment.Assess("cuda",HardwareInfo.FromAdapters([missingNvidia with{ProblemCode=14}])).Condition==DriverCondition.Restart,"A Windows restart requirement stays distinct from a missing driver");
        Check(DriverAssessment.Assess("cuda",HardwareInfo.FromAdapters([missingNvidia with{ProblemCode=22}])).Condition==DriverCondition.Disabled,"A disabled GPU is not treated as an automatic driver-download case");
        Check(!DriverAssessment.Assess("cuda",HardwareInfo.FromAdapters([new("AMD Radeon 8060S","32")])).CanInstall,"An undetected vendor never triggers the wrong installer");
        foreach(var signature in new[]{new InstallerSignature("NotSigned","NVIDIA Corporation"),new InstallerSignature("Valid","Unrelated Publisher"),new InstallerSignature("HashMismatch","Advanced Micro Devices Inc.")})
        {bool refused=false;try{DriverSignatures.RequirePublisher("cuda",signature);}catch(InvalidOperationException){refused=true;}Check(refused,"Untrusted or mismatched installer publisher is rejected: "+signature.Status+" / "+signature.Publisher);}
        DriverSignatures.RequirePublisher("cuda",new("Valid","NVIDIA Corporation"));DriverSignatures.RequirePublisher("amd_rocm",new("Valid","Advanced Micro Devices, Inc."));DriverSignatures.RequirePublisher("amd_rocm",new("Valid","Advanced Micro Devices"));Check(true,"Valid official publisher names match the selected vendor");
        var workspace=Path.Combine(root,"driver-help");var fake=new FakeDriverPlatform();using var model=new DriverSetupModel("amd_rocm",()=>workspace,L10n.Current,fake);
        await model.CheckAsync();await model.DownloadAndOpenAsync();Check(model.CanContinue&&fake.Downloads==0&&fake.Launches==0,"A compatible computer skips the driver installer without downloading or launching anything");
        var readyWindow=DriverPreview(model);await Render(readyWindow,740,540,"driver-ready");readyWindow.Close();
        fake.Condition=DriverCondition.Missing;await model.CheckAsync();Check(model.CanDownload&&!model.CanContinue,"A missing driver exposes the official install action and blocks environment preparation");
        var missingWindow=DriverPreview(model);await Render(missingWindow,740,650,"driver-missing-amd");await Render(missingWindow,620,460,"driver-missing-small");missingWindow.Close();
        await model.DownloadAndOpenAsync();Check(fake.Downloads==1&&fake.Launches==1&&model.Pending&&!model.CanContinue,"Opening the official helper does not claim that the graphics driver is installed");
        model.Backend="cuda";Check(model.Backend=="amd_rocm","The GPU choice cannot change while the official installer is still awaiting completion");
        using(var restored=new DriverSetupModel("cuda",()=>workspace,L10n.Current,fake)){Check(restored.Backend=="amd_rocm"&&restored.Pending,"The waiting-for-installation step survives restarting the app");fake.Condition=DriverCondition.Ready;await restored.CheckAsync();Check(restored.Pending&&!restored.CanContinue,"An automatic check does not dismiss an installation still awaiting user confirmation");await restored.CheckAsync(true);Check(restored.CanContinue&&!restored.Pending,"Explicit rechecking after installation resumes environment preparation");}
        await model.CheckAsync(true);
        fake.Condition=DriverCondition.Outdated;fake.FailSignature=true;await model.CheckAsync();var launched=fake.Launches;await model.DownloadAndOpenAsync();Check(fake.Launches==launched&&!model.Pending&&model.Error.Length>0,"A failed installer verification cannot launch a program or mark installation complete");fake.FailSignature=false;
        fake.FailLaunch=true;await model.DownloadAndOpenAsync();Check(!model.Pending&&model.Error.Length>0,"Cancelling the Windows launch prompt clears the pending install marker");fake.FailLaunch=false;
        await model.DownloadAndOpenAsync();Check(model.Pending,"A failed installation can be started again");model.ResetWaiting();Check(!model.Pending&&!model.CanContinue,"After closing the official installer, the user can reset the waiting step and retry");launched=fake.Launches;
        fake.Delay=true;var pending=model.DownloadAndOpenAsync();Check(model.Busy,"Driver download has an active cancellable state");await model.CancelAsync();await pending;Check(!model.Busy&&!model.Pending&&fake.Launches==launched,"Cancelling a helper download does not start the official installer");fake.Delay=false;
        model.Backend="cuda";await model.CheckAsync();model.OpenOfficialPage();Check(fake.LastBackend=="cuda","The official page follows the selected GPU vendor");var cudaWindow=DriverPreview(model);await Render(cudaWindow,740,650,"driver-update-nvidia");L10n.Current.Use("en");await Render(cudaWindow,740,650,"driver-update-en");L10n.Current.Use("zh");cudaWindow.Close();
        fake.Condition=DriverCondition.Restart;await model.CheckAsync();var restartWindow=DriverPreview(model);await Render(restartWindow,740,500,"driver-restart");
        Check(Children<System.Windows.Controls.TextBlock>((DependencyObject)restartWindow.Content).Single(t=>System.Windows.Automation.AutomationProperties.GetAutomationId(t)=="DriverInstructions").Visibility==Visibility.Collapsed,"Restart guidance does not tell users to click a download button that is hidden");restartWindow.Close();
        var actual=await new NativeDriverPlatform().Check("amd_rocm",CancellationToken.None);Check(actual.Ready&&actual.Hardware.All.Any(d=>d.Backend=="amd_rocm"),"Real AMD hardware is re-detected and its working driver skips installation");Files.Write(Path.Combine(root,"driver-native-check.json"),actual);
    }
    static async Task CheckOfficialAssistants()
    {
        var service=new NativeDriverPlatform();var signatures=new Dictionary<string,InstallerSignature>();
        foreach(var backend in new[]{"amd_rocm","cuda"})
        {
            var file=await service.Download(backend,Path.Combine(root,"official-downloads"),new Progress<SetupProgress>(),CancellationToken.None);
            var signature=await DriverSignatures.Inspect(file,CancellationToken.None);DriverSignatures.RequirePublisher(backend,signature);signatures[backend]=signature;
            Check(File.Exists(file)&&signature.Status=="Valid","Official helper download passes its pinned SHA256 and native Windows publisher verification: "+backend);
        }
        var unsigned=await DriverSignatures.Inspect(Path.Combine(AppContext.BaseDirectory,"LightX2V.Windows.exe"),CancellationToken.None);bool rejected=false;
        try{DriverSignatures.RequirePublisher("amd_rocm",unsigned);}catch(InvalidOperationException){rejected=true;}Check(rejected,"A real unsigned application cannot be opened as an official driver installer");
        Files.Write(Path.Combine(root,"installer-signatures.json"),signatures);
    }
    static Window DriverPreview(DriverSetupModel model)
    {
        var window=new Window{Background=System.Windows.Media.Brushes.White,DataContext=model};
        window.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/LightX2V.Windows;component/Resources/SetupTheme.xaml",UriKind.Relative)});
        var root=new System.Windows.Controls.Grid{Background=System.Windows.Media.Brushes.White};root.Children.Add(new System.Windows.Controls.ScrollViewer{HorizontalScrollBarVisibility=System.Windows.Controls.ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=System.Windows.Controls.ScrollBarVisibility.Auto,Content=new DriverSetupCard(window,model),Margin=new Thickness(20)});window.Content=root;return window;
    }
}
