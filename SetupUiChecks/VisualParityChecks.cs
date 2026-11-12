using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task VisualParityChecks()
    {
        var fixture=ReadinessFixture("visual-parity");var directory=Path.Combine(fixture.Settings.Workspace,"outputs","visual");Directory.CreateDirectory(directory);
        var output=Path.Combine(directory,"image.png");var preview=Environment.GetEnvironmentVariable("LIGHTX2V_QA_PREVIEW");
        if(!string.IsNullOrWhiteSpace(preview)&&File.Exists(preview))File.Copy(preview,output);
        else
        {
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8,8,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat(new byte[]{160,90,30,255},64).SelectMany(p=>p).ToArray(),32)));using(var file=File.Create(output))encoder.Save(file);
        }
        var generation=new Generation{CreationId="visual",Directory=directory,Status="completed",Seconds=88,CreatedAt=new DateTimeOffset(2026,11,11,13,53,0,TimeSpan.FromHours(8)),Request=new(){Prompt="A blue rose growing in the garden",Width=512,Height=512,Output=output}};
        StateStore.SaveHistory(fixture.Settings.Workspace,[generation]);
        using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));vm.Selected=vm.Recent.Single();vm.EnhancementEnabled=false;vm.SetLanguage("en");
        var window=new MainWindow(vm);
        foreach(var (models,parameters,name) in new[]{(true,true,"both-expanded"),(false,false,"both-collapsed"),(false,true,"generation-expanded"),(true,false,"models-expanded")})
        {
            vm.ModelExpanded=models;vm.ParametersExpanded=parameters;await Render(window,1280,820,"mac-reference-"+name);
        }
        var image=Children<Image>((DependencyObject)window.Content).Single(i=>AutomationProperties.GetAutomationId(i)=="GeneratedImage");var width=image.ActualWidth;
        Check(Math.Abs(width-371)<2,"A square result follows the Mac preview-height rule using the full content height");
        foreach(var index in Enumerable.Range(1,8))vm.InputImages.Add(new(){Path=output,Name="Reference "+index,Width=512,Height=512});
        await Render(window,1280,820,"mac-reference-eight-attachments");
        Check(Math.Abs(image.ActualWidth-width)<1,"Adding eight draft attachments does not rescale an existing reply");
        var settings=new SettingsWindow(vm);await Render(settings,640,490,"mac-reference-general-settings");settings.Close();window.Close();
    }
}
