using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task NativeAppChecks()
    {
        var path=Environment.GetEnvironmentVariable("LIGHTX2V_QA_NATIVE_SETTINGS")??throw new InvalidOperationException("Provide LIGHTX2V_QA_NATIVE_SETTINGS with read-only model/environment paths and an isolated workspace.");
        var settings=Files.Read<AppSettings>(path)??throw new InvalidDataException("Native settings are empty.");
        if(!AppMaintenance.Contains(root,settings.Workspace))throw new InvalidDataException("Native acceptance requires its workspace to be inside the fresh report directory.");
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
        window.Close();
    }
}
