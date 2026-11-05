using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task FirstRunAcceptance(string? packageCache=null)
    {
        var workspace=Path.Combine(root,"首次安装 空白工作区");var settings=AppSettings.Fresh(workspace);
        var preferences=Path.Combine(root,"preferences");Files.Write(Path.Combine(preferences,"preferences.json"),new Preferences{Settings=settings});
        Assert(!File.Exists(SetupInstaller.ManagedPython(workspace))&&!Directory.Exists(SetupInstaller.SourceDirectory(workspace)),"The workspace starts without Python, installed packages or inference source");
        if(packageCache!=null)
        {
            var cache=Path.Combine(workspace,"cache","packages");Directory.CreateDirectory(cache);
            foreach(var input in Directory.EnumerateFiles(packageCache).Where(p=>!p.EndsWith(".partial")))
                if(!CreateHardLinkCold(Path.Combine(cache,Path.GetFileName(input)),input,IntPtr.Zero))throw new System.ComponentModel.Win32Exception();
            Console.WriteLine("Reusing downloaded package artifacts; Python and site-packages remain absent.");
        }
        RecordBackendHashes();
        using var vm=new MainViewModel(preferences);var window=new MainWindow(vm);window.Show();
        await Until(()=>Application.Current.Windows.OfType<FirstRunWindow>().Any(),30);var wizard=Application.Current.Windows.OfType<FirstRunWindow>().Single();Shot(wizard,"welcome");
        Assert(vm.NeedsInitialSetup&&!vm.Ready,"Missing Python automatically opens the first-run assistant");
        await vm.Setup.BeginWorkspaceAsync();Assert(vm.Setup.Page==1,"Choosing a workspace advances to environment preparation");Shot(wizard,"environments");
        Assert(vm.Setup.Hardware.ManagedSupported,"The validated Radeon 8060S profile is detected");
        var preparing=vm.Setup.PrepareEnvironmentAsync(true);
        await Until(()=>vm.Setup.Busy||preparing.IsCompleted,10);if(vm.Setup.Busy){await Task.Delay(1000);Shot(wizard,"installing");}await preparing;
        Assert(vm.Setup.EnvironmentReady&&vm.Ready,"A newly installed isolated ROCm environment passes BF16 GPU verification: "+vm.Setup.Error);
        Assert(vm.Settings.Python==SetupInstaller.ManagedPython(workspace)&&File.Exists(Path.Combine(Path.GetDirectoryName(vm.Settings.Python)!,"Lib","site-packages","torch","__init__.py")),"The app uses freshly installed packages rather than the pre-existing Python environment");
        Assert(vm.Settings.Repository==SetupInstaller.SourceDirectory(workspace)&&File.Exists(Path.Combine(vm.Settings.Repository,"lightx2v","infer.py")),"The app unpacks and selects its versioned native source");
        var source=vm.TurboCard.Path;Assert(vm.TurboCard.Ready,"Existing Qwen weights are discovered and reused without downloading another full model");Shot(wizard,"models");
        vm.TurboCard.Path=Path.Combine(workspace,"missing-model");await Until(()=>vm.Ready&&!vm.IsChecking,90);
        Assert(vm.Ready&&!vm.TurboCard.Ready,"Environment verification succeeds independently when the generation model is missing");
        vm.ApplySettings(vm.Settings.Clone());await Until(()=>vm.Ready&&!vm.IsChecking,90);Assert(vm.Ready,"General settings can be saved before a model exists");
        var target=Path.Combine(workspace,"models","Qwen","Qwen-Image-2.1-Turbo");Directory.CreateDirectory(target);
        foreach(var input in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(source,input);if(relative=="model_index.json"||relative.Contains(".cache")||relative.Contains(".lightx2v"))continue;
            var output=Path.Combine(target,relative);Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if(new FileInfo(input).Length>1024*1024){if(!CreateHardLinkCold(output,input,IntPtr.Zero))throw new System.ComponentModel.Win32Exception();}else File.Copy(input,output);
        }
        vm.TurboCard.Path=target;Assert(!vm.TurboCard.Ready,"A missing model index is detected before download");vm.UseModelScope=true;
        await vm.Setup.DownloadModelsAsync();Assert(vm.TurboCard.Ready&&File.Exists(Path.Combine(target,"model_index.json")),"The first-run assistant verifies reused model files and downloads the missing model index: "+vm.Setup.Error);
        vm.Setup.Page=3;Shot(wizard,"setup-completed");wizard.Close();
        var firstImage=await GenerateWorkspaceAcceptance(vm);
        Assert(firstImage.Completed,"An explicit workspace acceptance completes a native image: "+firstImage.Error);var image=ImageLoader.Load(firstImage.Request.Output,0);
        Assert(image.PixelWidth==512&&image.PixelHeight==512,"The workspace acceptance image is a valid 512 PNG");Files.Write(Path.Combine(root,"first-image.json"),firstImage);
        using(var restored=new MainViewModel(preferences)){Assert(restored.Settings.Python==vm.Settings.Python&&restored.Recent.Any(g=>g.Completed),"Managed environment settings and the first creation restore on next launch");}
        using(var bridge=new BridgeClient())
        {
            var request=Path.Combine(root,"environment-only.json");Files.Write(request,new{protocol=1});bool ready=false;await bridge.RunAsync(vm.Settings.Python,"check",request,Path.Combine(workspace,"tmp"),e=>{if(e.GetProperty("type").GetString()=="ready")ready=true;});Assert(ready,"The backend environment probe does not require model or source paths");
        }
        window.Close();
    }
    static async Task<Generation> GenerateWorkspaceAcceptance(MainViewModel vm)
    {
        vm.NewCreation();vm.Tier=0;vm.Ratio="1:1";vm.Prompt="A blue ceramic teapot on a wooden table beside a sunny window, warm natural light, realistic photography.";
        await vm.GenerateAsync();return vm.Turns.Last();
    }
    [DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateHardLinkCold(string name,string existing,IntPtr attributes);
}
