using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task FirstUseChecks()
    {
        var fixture=StartupFixture("explicit-import");
        var source=Path.Combine(fixture.Preferences,"preferences.json");var bytes=File.ReadAllBytes(source);
        var isolated=Path.Combine(root,"fresh-preferences");
        using(var vm=new MainViewModel(isolated,environmentCatalog:fixture.Catalog,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            Check(vm.NeedsInitialSetup&&vm.Setup.Page==0&&vm.Settings.Python==""&&vm.Settings.Repository==""&&!vm.HasSelectedModel,"No saved preferences opens save-location setup with no environment or model selection");
            Check(fixture.Catalog.SearchCalls==0&&!File.Exists(Path.Combine(isolated,"preferences.json")),"Constructing a new profile does not search environments or save imported settings");
            var window=new FirstRunWindow(vm,true);await Render(window,620,470,"first-use-clean");
            Check(!Children<Button>((DependencyObject)window.Content).Any(b=>AutomationProperties.GetAutomationId(b)=="SetupImportSettings"),"The first wizard page has no legacy import action");window.Close();
            Check(File.ReadAllBytes(source).SequenceEqual(bytes),"A fresh profile leaves old preferences untouched");
            vm.Setup.Workspace=fixture.Settings.Workspace;
            await vm.Setup.BeginWorkspaceAsync();
            await WaitForSearch(vm.Setup);
            Check(vm.Settings.Workspace==fixture.Settings.Workspace&&File.Exists(Path.Combine(isolated,"preferences.json"))&&fixture.Catalog.SearchCalls==1,"Confirming a workspace saves it and automatically searches environments");
        }
        using(var restored=new MainViewModel(isolated,environmentCatalog:fixture.Catalog,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
            Check(restored.Settings.Workspace==fixture.Settings.Workspace,"A saved current profile restores normally on the next launch");
        var unchosen=Path.Combine(root,"unchosen-workspace");Directory.CreateDirectory(unchosen);var history=Path.Combine(unchosen,"workspace.json");File.WriteAllText(history,"{an old index that must not be read before workspace confirmation");
        var languagePrefs=Path.Combine(root,"language-only-preferences");Files.Write(Path.Combine(languagePrefs,"preferences.json"),new Preferences{WorkspaceConfirmed=false,Settings=AppSettings.Fresh(unchosen)});
        var historyBytes=File.ReadAllBytes(history);
        Files.Write(Path.Combine(unchosen,"setup","driver-help.json"),new DriverHelpProgress("cuda",DateTimeOffset.Now));
        using(var vm=new MainViewModel(languagePrefs,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]))){vm.SetLanguage("en");Check(vm.NeedsInitialSetup&&!vm.WorkspaceAvailable&&vm.Recent.Count==0&&File.ReadAllBytes(history).SequenceEqual(historyBytes)&&Directory.GetFiles(unchosen).Length==1&&vm.Setup.Page==0&&!vm.Setup.Driver.Pending,"Language-only preferences do not read, recover or overwrite an unconfirmed workspace");}
        using(var vm=new MainViewModel(languagePrefs,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))Check(vm.NeedsInitialSetup&&!vm.WorkspaceAvailable&&File.ReadAllBytes(history).SequenceEqual(historyBytes),"Reopening after a first-run language change still waits for workspace confirmation");
        var ready=ReadinessFixture("pinned-source-override");
        using(var vm=new MainViewModel(ready.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:ready.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            var old=vm.Settings.Repository;await vm.CheckAsync();
            Check(vm.Ready&&vm.Settings.Repository==SetupInstaller.SourceDirectory(vm.Settings.Workspace)&&vm.Settings.Repository!=old,"Environment checks replace an external source selection with the verified bundled revision");
            Check(File.Exists(Path.Combine(old,"lightx2v","infer.py")),"Pinning the source preserves the user's external checkout");
        }
        var package=Path.Combine(root,"layout-v2-fixture");var app=Path.Combine(package,"app");Directory.CreateDirectory(app);Directory.CreateDirectory(Path.Combine(package,"docs"));
        File.WriteAllText(Path.Combine(package,"LightX2V.exe"),"Launcher fixture");File.WriteAllText(Path.Combine(app,"LightX2V.Windows.exe"),"App fixture");File.WriteAllText(Path.Combine(package,"user-note.txt"),"Keep this");
        Files.Write(Path.Combine(app,"build-manifest.json"),new{layoutVersion=2,entryPoint="LightX2V.exe",appExecutable="app/LightX2V.Windows.exe",sha256=new Dictionary<string,string>{{"LightX2V.exe","fixture"},{"app/LightX2V.Windows.exe","fixture"}}});
        var plan=AppMaintenance.Select(AppMaintenance.Discover(AppSettings.Fresh(Path.Combine(root,"layout-v2-workspace")),Path.Combine(root,"layout-v2-prefs"),app),new HashSet<string>{"application"});
        Check(plan.Application==package&&plan.EntryPoint=="LightX2V.exe"&&plan.RemoveApplication,"Layout v2 maintenance owns the full package root and its public launcher");plan.ParentPid=int.MaxValue;
        var file=Path.Combine(root,"layout-v2-plan.json");Files.Write(file,plan);await AppMaintenance.ExecuteAsync(file,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        Check(!File.Exists(Path.Combine(package,"LightX2V.exe"))&&!Directory.Exists(app)&&File.ReadAllText(Path.Combine(package,"user-note.txt"))=="Keep this","Layout v2 uninstall removes manifest-listed launcher and payload while preserving extra root files");
    }
}
