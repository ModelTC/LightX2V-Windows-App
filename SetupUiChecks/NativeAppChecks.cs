using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task NativeDiscoveryChecks(AppSettings expected)
    {
        var parent=Path.GetDirectoryName(Path.GetDirectoryName(expected.Model))!;
        var workspace=Path.Combine(parent,".lightx2v-042-discovery-"+Guid.NewGuid().ToString("N"));
        var prefs=Path.Combine(root,"first-use-preferences");
        try
        {
            using var vm=new MainViewModel(prefs);var wizard=new FirstRunWindow(vm,true);
            vm.Setup.Workspace=workspace;await Render(wizard,760,660,"native-first-use-empty");
            Check(vm.NeedsInitialSetup&&!vm.Settings.ModelSelected,"An isolated real profile starts in first use without adopting existing settings");
            await vm.Setup.BeginWorkspaceAsync();
            for(var i=0;i<600&&vm.Setup.SearchingEnvironments;i++)await Task.Delay(100);
            Check(vm.Setup.Selected is {Ready:true}&&vm.Setup.UseExisting&&!Directory.Exists(Path.Combine(workspace,"environments")),"First use automatically selects a compatible installed AMD environment without installing software");
            await vm.Setup.PrepareEnvironmentAsync(false);
            for(var i=0;i<300&&(!vm.TurboCard.Ready||!vm.T2ICard.Ready||!vm.I2ICard.Ready);i++)await Task.Delay(100);
            Check(vm.Setup.Error==""&&vm.TurboCard.Path==expected.Model&&vm.T2ICard.Path==expected.T2IDirectory&&vm.I2ICard.Path==expected.I2IDirectory&&vm.Setup.CanContinue,"Entering model preparation automatically finds all three existing complete AMD models without a manual path");
            await Render(wizard,760,660,"native-first-use-models-found");wizard.Close();
        }
        finally{if(Directory.Exists(workspace))Directory.Delete(workspace,true);}
    }
    static async Task NativeAppChecks()
    {
        var path=Environment.GetEnvironmentVariable("LIGHTX2V_QA_NATIVE_SETTINGS")??throw new InvalidOperationException("Provide LIGHTX2V_QA_NATIVE_SETTINGS with read-only model/environment paths and an isolated workspace.");
        var settings=Files.Read<AppSettings>(path)??throw new InvalidDataException("Native settings are empty.");
        if(!AppMaintenance.Contains(root,settings.Workspace))throw new InvalidDataException("Native acceptance requires its workspace to be inside the fresh report directory.");
        await NativeDiscoveryChecks(settings);
        var prefs=Path.Combine(root,"preferences");Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});
        using var vm=new MainViewModel(prefs);vm.SelectStandardModel();vm.EnhancementEnabled=false;vm.Tier=0;vm.Ratio="1:1";
        vm.Prompt="A blue ceramic teapot on a wooden table beside a sunny window, warm natural light, realistic photography.";
        var window=new MainWindow(vm);await Render(window,1280,820,"native-app-before");
        Check(vm.CanGenerate,"The real Standard environment and image model are available through the app view model");
        await vm.GenerateAsync();var text=vm.Turns.Last();
        Check(text.Completed&&File.Exists(text.Request.Output),"The actual app generation entry completes a real 512 text-to-image task on "+text.Request.Backend);
        var png=new BitmapImage(new Uri(text.Request.Output));Check(png.PixelWidth==512&&png.PixelHeight==512,"The real app task writes a decodable 512-square PNG");
        await vm.AddImagePathsAsync(Enumerable.Repeat(text.Request.Output,8));
        Check(vm.InputImages.Count==8,"The app imports eight real references through its Python import pipeline");
        vm.Ratio="Adaptive";vm.Prompt="Add one red apple beside the blue teapot, keeping the original composition.";
        await vm.GenerateAsync();var editing=vm.Turns.Last();
        Check(editing.Completed&&editing.Request.InputImages.Count==8&&editing.Request.Resolution==512,"The real app completes eight-reference Adaptive editing through its normal request and backend flow");
        await Render(window,1280,820,"native-app-eight-reference-result");
        Check(!vm.IsGenerationBusy&&vm.Turns.Count==2,"Both real tasks restore app availability and remain in the same creation");
        vm.NewCreation();vm.EnhancementEnabled=true;vm.Tier=0;vm.Ratio="1:1";
        vm.Prompt="A yellow cup on a blue table.";await vm.GenerateAsync();var enhanced=vm.Turns.Last();
        Check(enhanced.Completed&&enhanced.EnhancementCompleted&&enhanced.EnhancedPrompt.Length>0,"Pinned 0.6.1 completes text-to-image with the real local enhancement model");
        await vm.AddImagePathsAsync([text.Request.Output]);vm.Ratio="Adaptive";vm.Prompt="Change the teapot to yellow.";
        await vm.GenerateAsync();var enhancedEdit=vm.Turns.Last();
        Check(enhancedEdit.Completed&&enhancedEdit.EnhancementCompleted&&enhancedEdit.Request.InputImages.Count==1,"Pinned 0.6.1 completes reference editing with the real I2I enhancement model");
        foreach(var generation in new[]{text,editing,enhanced,enhancedEdit})
        {
            var log=Path.Combine(generation.Directory,"run.log");
            Check(File.Exists(log)&&File.ReadAllText(log).Contains("Status: completed")&&System.Text.Encoding.UTF8.GetByteCount(MainViewModel.ReadLogTail(log))<=90000,"A real completed task saves its full run.log and exposes a bounded UTF-8 preview");
        }
        vm.NewCreation();vm.EnhancementEnabled=false;vm.Tier=0;vm.Ratio="1:1";vm.Prompt="A tiny red apple on a white plate.";
        var stopping=vm.GenerateAsync();for(var i=0;i<500&&!vm.IsRunning&&!stopping.IsCompleted;i++)await Task.Delay(20);
        await vm.StopAsync();await stopping;
        Check(vm.Turns.Last().Status=="cancelled"&&!vm.IsGenerationBusy&&File.Exists(Path.Combine(vm.Turns.Last().Directory,"run.log")),"Stopping a real Standard task restores availability and retains a task log");
        var cancelled=vm.Turns.Last().Id;window.Close();
        using var restored=new MainViewModel(prefs);Check(restored.Recent.Any(g=>g.Id==cancelled&&g.Status=="cancelled"),"Restarting preserves the cancelled real task and completed creation history");
    }
}
