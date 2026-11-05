using System.IO;
using System.Windows;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task SetupResume(string fixture)
    {
        RecordBackendHashes();var settings=Files.Read<Preferences>(fixture)!.Settings.Clone();var model=settings.Model;
        settings.Workspace=Path.Combine(root,"继续首次设置");settings.Model=Path.Combine(settings.Workspace,"missing-model");settings.Repository="";
        var preferences=Path.Combine(root,"preferences");Files.Write(Path.Combine(preferences,"preferences.json"),new Preferences{Settings=settings});
        using(var vm=new MainViewModel(preferences))
        {
            var window=new MainWindow(vm);window.Show();await Until(()=>vm.Ready&&!vm.IsChecking,120);
            await Until(()=>Application.Current.Windows.OfType<FirstRunWindow>().Any(),30);var wizard=Application.Current.Windows.OfType<FirstRunWindow>().Single();
            Assert(vm.Setup.Page==2&&vm.Ready&&!vm.TurboCard.Ready,"Restarting with a prepared environment and missing model resumes at model preparation");
            Assert(vm.Settings.Repository==SetupInstaller.SourceDirectory(settings.Workspace),"An absent inference source is restored from the verified bundled runtime");vm.SetLanguage("en");Assert(vm.Setup.PageTitle=="Prepare models"&&wizard.Title.Contains("First-time setup"),"The first-run assistant follows language changes");vm.SetLanguage("zh");Shot(wizard,"resume-models");
            vm.TurboCard.Path=model;await Until(()=>vm.Ready&&!vm.IsChecking,120);Assert(vm.TurboCard.Ready,"Choosing the verified local model restores generation readiness");
            vm.Setup.Page=3;Shot(wizard,"setup-completed");wizard.Close();var firstImage=await GenerateWorkspaceAcceptance(vm);Assert(firstImage.Completed,"The workspace completes a real image using the managed environment: "+firstImage.Error);
            var bitmap=ImageLoader.Load(firstImage.Request.Output,0);Assert(bitmap.PixelWidth==512&&bitmap.PixelHeight==512,"The final managed-environment image is a valid 512 PNG");
            Files.Write(Path.Combine(root,"first-image.json"),firstImage);window.Close();
        }
        using(var restored=new MainViewModel(preferences))
        {
            var window=new MainWindow(restored);window.Show();await Until(()=>restored.Ready&&!restored.IsChecking,120);await System.Threading.Tasks.Task.Delay(300);
            Assert(!Application.Current.Windows.OfType<FirstRunWindow>().Any()&&restored.Recent.Any(g=>g.Completed),"A completed first run restores history and opens the workspace without another setup wizard");
            restored.Setup.Restart();Assert(restored.Setup.Page==0,"Reopening the assistant from settings starts with workspace selection");Shot(window,"restored-workspace");window.Close();
        }
    }
}
