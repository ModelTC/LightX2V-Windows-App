using LightX2V.Windows;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
internal static partial class Program
{
    static async Task WizardPreparationChecks()
    {
        var settings=AppSettings.Fresh(Path.Combine(root,"wizard-isolated"));settings.Backend="amd_rocm";
        var prefs=Path.Combine(root,"wizard-test-preferences");Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});
        var events=new List<string>();var platform=new FakeDriverPlatform{Events=events};
        var amd=Valid(@"C:\envs\amd\python.exe");var cuda=amd with{Path=@"C:\envs\cuda\python.exe",Backend="cuda",Device="NVIDIA RTX 4090",HipVersion="",CudaVersion="12.8",TorchVersion="2.11.0+cu128"};
        var catalog=new FakeCatalog{Events=events,Found=[cuda,amd]};
        using var vm=new MainViewModel(prefs,environmentCatalog:catalog,driverPlatform:platform);
        using var driver=new DriverSetupModel("amd_rocm",()=>settings.Workspace,L10n.Current,platform);using var controller=new SetupController(vm,catalog,driver);
        platform.DelayCheck=true;var pending=controller.ScanComputerAsync();
        Check(controller.Busy&&!controller.ComputerScanned&&events.SequenceEqual(new[]{"check:amd_rocm"}),"Step two starts with a computer check and never searches environments before scan completion");
        await controller.CancelAsync();await pending;Check(!controller.Busy&&!controller.ComputerScanned&&catalog.SearchBackend=="","A cancelled computer scan cannot enable component preparation or start environment discovery");platform.DelayCheck=false;events.Clear();
        await controller.ScanComputerAsync();
        Check(catalog.SearchCalls==0&&controller.Selected==null,"A completed hardware scan does not silently adopt a developer environment");
        await controller.SearchEnvironmentsAsync();
        Check(events.SequenceEqual(new[]{"check:amd_rocm","check:cuda","search:cuda"})&&controller.Backend=="cuda"&&controller.Selected==cuda,"Hybrid hardware selects NVIDIA automatically and searches against that backend only after checking it");
        Check(platform.Downloads==0&&platform.Launches==0,"Computer scans and automatic backend selection never install or launch software");
        controller.AcceptCandidate(amd);Check(controller.Selected==cuda,"Inline selection rejects a ready environment for a different backend");
        controller.Backend="amd_rocm";await controller.CheckBackendAsync();await controller.SearchEnvironmentsAsync();Check(controller.Selected==amd&&catalog.SearchBackend=="amd_rocm","An explicit backend override rechecks graphics software and refreshes the inline candidate list");
        controller.UseExisting=false;await controller.SearchEnvironmentsAsync();Check(controller.Selected==amd&&controller.UseExisting,"Searching again selects an existing compatible environment without opening another window");
        catalog.ProbeResult=amd;await controller.SelectPythonAsync(amd.Path);Check(controller.Environments.Count(e=>e.Path==amd.Path)==1,"Manually chosen Python programs are checked and merged inline without duplicate paths");
        platform.Computer=HardwareInfo.FromAdapters([new("AMD Radeon 8060S","32")]);catalog.Found=[];await controller.ScanComputerAsync();await controller.SearchEnvironmentsAsync();Check(controller.ComputerScanned&&controller.Selected==null&&!controller.UseExisting&&controller.ManagedSupported,"A fresh AMD computer without existing components offers automatic preparation directly");
        var checksBeforeRender=platform.Checks;
        var preview=new Window{Background=System.Windows.Media.Brushes.White};preview.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/LightX2V.Windows;component/Resources/SetupTheme.xaml",UriKind.Relative)});var panel=new ComputerPreparationPanel(preview,controller);var previewRoot=new Grid();previewRoot.Children.Add(new ScrollViewer{HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=panel,Margin=new Thickness(20)});preview.Content=previewRoot;
        await Render(preview,740,700,"wizard-integrated-empty");Check(platform.Checks==checksBeforeRender,"Constructing or rendering the inline backend choice never starts an extra background preparation job");await Render(preview,620,470,"wizard-integrated-empty-small");
        platform.Condition=DriverCondition.Missing;await controller.ScanComputerAsync();await Render(preview,740,800,"wizard-integrated-driver-missing");
        await driver.DownloadAndOpenAsync();await controller.ScanComputerAsync();Check(driver.Pending&&controller.Backend=="amd_rocm","Rescanning preserves a pending official installation and never switches its vendor");
        var overrideBackend=Children<Expander>((DependencyObject)preview.Content).Single(e=>e.Header?.ToString()==L10n.Current["Change backend (optional)"]);overrideBackend.IsExpanded=true;await Render(preview,740,800,"wizard-integrated-backend-locked");
        var backend=Children<ComboBox>((DependencyObject)preview.Content).Single();Check(!backend.IsEnabled,"The inline backend override remains locked while the official installer awaits completion");await Render(preview,740,800,"wizard-integrated-driver-pending");
        platform.Condition=DriverCondition.Ready;await driver.CheckAsync(true);Check(driver.CanContinue&&!driver.Pending,"An explicit driver recheck resumes the integrated preparation flow");
        platform.Computer=HardwareInfo.FromAdapters([]);await controller.ScanComputerAsync();Check(!controller.HasDetectedBackend&&!controller.ManagedSupported&&controller.BackendRecommendation.Contains(L10n.Current["No supported graphics card was identified. Check the graphics software below, then scan again."]),"Unknown hardware displays a clear result instead of claiming an automatically selected backend");
        platform.Computer=HardwareInfo.FromAdapters([new GpuAdapter("Microsoft Basic Display Adapter",""){PnpId=@"PCI\VEN_10DE&DEV_1234",ProblemCode=28}]);platform.Condition=DriverCondition.Missing;await controller.ScanComputerAsync();
        Check(controller.Backend=="cuda"&&controller.HasDetectedBackend&&driver.CanDownload,"A new NVIDIA computer without its vendor driver is identified by PCI ID and selects CUDA before offering the correct driver assistant");
        panel.Dispose();preview.Close();
    }
}
