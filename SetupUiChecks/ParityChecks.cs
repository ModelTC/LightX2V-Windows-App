using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task ParityChecks()
    {
        var fixture=ReadinessFixture("creation-settings-parity");
        string first,second;
        using(var vm=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform))
        {
            var a=new Generation{CreationId="a",Directory=Path.Combine(fixture.Settings.Workspace,"outputs","a"),Status="completed",Request=new(){Prompt="A"}};
            var b=new Generation{CreationId="b",Directory=Path.Combine(fixture.Settings.Workspace,"outputs","b"),Status="failed",Request=new(){Prompt="B"}};
            StateStore.SaveHistory(fixture.Settings.Workspace,[a,b]);
        }
        using(var vm=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform))
        {
            var a=vm.Recent.Single(g=>g.CreationId=="a");var b=vm.Recent.Single(g=>g.CreationId=="b");first=a.CreationId;second=b.CreationId;
            vm.Selected=a;vm.Tier=2;vm.Ratio="16:9";vm.EnhancementEnabled=false;
            vm.Selected=b;vm.Tier=0;vm.Ratio="1:1";vm.EnhancementEnabled=true;
            vm.Selected=a;Check(vm.Tier==2&&vm.Ratio=="16:9"&&!vm.EnhancementEnabled,"Switching history restores the creation's edited size, ratio and enhancement state");
            vm.Selected=b;vm.ReuseCommand.Execute(a);
            Check(vm.Selected==a&&vm.Turns.Single().CreationId==first&&vm.Prompt=="A"&&vm.RandomSeed,"Reuse selects the original creation and prepares its draft with a new random seed");
            vm.Selected=b;
        }
        using(var restored=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform))
        {
            Check(restored.Selected?.CreationId==second&&restored.Tier==0&&restored.Ratio=="1:1"&&restored.EnhancementEnabled,"Active creation and its parameters survive a full view-model restart");
            restored.NewCreation();Check(restored.Tier==1&&restored.Ratio=="Adaptive"&&restored.EnhancementEnabled,"A new creation has the Mac Standard defaults");
            restored.Prompt="Draft before settings";var original=restored.Settings.Workspace;var other=Path.Combine(root,"settings-preview-only");
            restored.Setup.Workspace=other;await restored.Setup.BeginWorkspaceAsync(commit:false);
            Check(restored.Settings.Workspace==original&&restored.Prompt=="Draft before settings","Searching a draft workspace does not commit settings or clear the current draft");
        }
        using(var pending=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform)){pending.NewCreation();pending.Tier=2;pending.Ratio="16:9";}
        using(var pending=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform))
            Check(pending.Selected==null&&pending.Tier==2&&pending.Ratio=="16:9","An unsent creation's parameters survive reopening without a generation record");
        var nativePython=Environment.GetEnvironmentVariable("LIGHTX2V_QA_PYTHON");
        if(!string.IsNullOrWhiteSpace(nativePython)&&File.Exists(nativePython))
        {
            using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform);
            var importSettings=vm.Settings.Clone();importSettings.Python=nativePython;vm.ApplySettings(importSettings);
            var source=Path.Combine(root,"参考图 source.png");var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[16],8)));using(var file=File.Create(source))encoder.Save(file);
            await vm.AddImagePathsAsync([source]);await vm.AddImagePathsAsync([source,source]);await vm.AddImagePathsAsync(Enumerable.Repeat(source,5));
            Check(vm.InputImages.Count==8&&vm.InputImages.Select(i=>i.Path).Distinct().Count()==8,"Real Python imports preserve eight deliberate references with independent identities");
            var count=Directory.GetFiles(Path.Combine(importSettings.Workspace,"imports")).Length;
            await vm.AddImagePathsAsync([source]);
            Check(vm.InputImages.Count==8&&Directory.GetFiles(Path.Combine(importSettings.Workspace,"imports")).Length==count,"A ninth reference is rejected and its staged import removed");
            vm.InputImages.Clear();await vm.AddImagePathsAsync([source]);
            var broken=Path.Combine(root,"damaged-reference.png");File.WriteAllText(broken,"Broken fixture");
            await vm.AddImagePathsAsync([source,broken]);
            Check(vm.InputImages.Count==1,"A mixed valid/damaged batch leaves the previous draft references unchanged");
            var one=vm.AddImagePathsAsync([source]);var two=vm.AddImagePathsAsync([source]);await Task.WhenAll(one,two);
            Check(vm.InputImages.Count==3&&!vm.IsImporting,"Concurrent import requests are queued and both complete before import readiness is restored");
            await vm.AddImagePathsAsync([source],deduplicate:true);
            Check(vm.InputImages.Count==3,"File-selection import deduplicates normalized image content without removing deliberate repeats");
        }
        var maintenance=Path.Combine(root,"uninstall-fixture");var settings=AppSettings.Fresh(Path.Combine(maintenance,"workspace"));var prefs=Path.Combine(maintenance,"preferences");var app=Path.Combine(maintenance,"application");Directory.CreateDirectory(app);Directory.CreateDirectory(prefs);
        var model=Path.Combine(settings.Workspace,"models","fixture");Directory.CreateDirectory(model);File.WriteAllText(Path.Combine(model,"weights.txt"),"Fixture only");
        var output=Path.Combine(settings.Workspace,"outputs");Directory.CreateDirectory(output);var kept=Path.Combine(output,"keep.txt");File.WriteAllText(kept,"Keep this creation");
        var discovered=AppMaintenance.Discover(settings,prefs,app);var selected=AppMaintenance.Select(discovered,new HashSet<string>{"models"});
        selected.ParentPid=int.MaxValue;var planFile=Path.Combine(maintenance,"plan.json");Files.Write(planFile,selected);var digest=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(planFile)));
        await AppMaintenance.ExecuteAsync(planFile,digest);
        Check(!Directory.Exists(model)&&File.ReadAllText(kept)=="Keep this creation"&&Directory.Exists(prefs),"Executing an exact models-only plan preserves workspace data and preferences");
        var nested=Path.Combine(output,"external-model");Directory.CreateDirectory(nested);File.WriteAllText(Path.Combine(nested,"model_index.json"),"{}");settings.Model=nested;
        discovered=AppMaintenance.Discover(settings,prefs,app);var rejected=false;
        try{AppMaintenance.Select(discovered,new HashSet<string>{"models"});}catch(InvalidDataException){rejected=true;}
        Check(rejected&&Directory.Exists(nested),"Deleting a selected child of an unchecked group is rejected before changing files");
        rejected=false;try{AppMaintenance.ValidatePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Uninstall rejects a profile root");
        rejected=false;try{AppMaintenance.ValidatePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32"));}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Uninstall refuses system-folder descendants without touching their files");
        var protectedFile=Path.Combine(maintenance,"locked.txt");File.WriteAllText(protectedFile,"Keep on failure");
        var lockedPlan=new UninstallPlan{ParentPid=int.MaxValue,Items=[new(protectedFile,"workspace")],Application=app};Files.Write(planFile,lockedPlan);
        rejected=false;try{await AppMaintenance.ExecuteAsync(planFile,"changed hash");}catch(InvalidDataException){rejected=true;}
        Check(rejected&&File.Exists(protectedFile),"A changed uninstall plan is rejected before deleting any data");
        digest=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(planFile)));
        using(var held=new FileStream(protectedFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            rejected=false;try{await AppMaintenance.ExecuteAsync(planFile,digest);}catch(IOException){rejected=true;}
            Check(rejected&&File.Exists(protectedFile),"An uninstall file-access failure preserves the locked file and reports failure");
        }
        var allRoot=Path.Combine(root,"uninstall-all-groups");var allSettings=AppSettings.Fresh(Path.Combine(allRoot,"workspace"));var allPrefs=Path.Combine(allRoot,"preferences");var allApp=Path.Combine(allRoot,"app");
        Directory.CreateDirectory(Path.Combine(allSettings.Workspace,"models"));Directory.CreateDirectory(Path.Combine(allSettings.Workspace,"outputs"));Directory.CreateDirectory(allPrefs);Directory.CreateDirectory(allApp);
        Files.Write(Path.Combine(allApp,"build-manifest.json"),new{sha256=new Dictionary<string,string>{{"LightX2V.Windows.exe","fixture"}}});File.WriteAllText(Path.Combine(allApp,"user-note.txt"),"Keep unowned app-folder data");File.WriteAllText(Path.Combine(allApp,"LightX2V.Windows.exe"),"Non-executable uninstall fixture");
        var managed=Path.Combine(allSettings.Workspace,"environments","owned");Directory.CreateDirectory(managed);File.WriteAllText(Path.Combine(managed,".lightx2v-environment.json"),"{}");
        var unmanaged=Path.Combine(allSettings.Workspace,"environments","keep-unmanaged");Directory.CreateDirectory(unmanaged);File.WriteAllText(Path.Combine(unmanaged,"keep.txt"),"Keep this unmanaged environment");
        var allPlan=AppMaintenance.Select(AppMaintenance.Discover(allSettings,allPrefs,allApp),new HashSet<string>{"models","workspace","application"});allPlan.ParentPid=int.MaxValue;
        var allPlanFile=Path.Combine(allRoot,"plan.json");Files.Write(allPlanFile,allPlan);await AppMaintenance.ExecuteAsync(allPlanFile,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(allPlanFile))));
        Check(File.Exists(Path.Combine(allApp,"user-note.txt"))&&!File.Exists(Path.Combine(allApp,"LightX2V.Windows.exe"))&&!Directory.Exists(allPrefs)&&!Directory.Exists(Path.Combine(allSettings.Workspace,"models"))&&!Directory.Exists(managed)&&File.Exists(Path.Combine(unmanaged,"keep.txt")),"All uninstall groups remove owned fixtures while retaining unlisted app-folder files and unmanaged environments");
        using(var recovered=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform))
        {
            File.WriteAllText(Path.Combine(recovered.PreferencesDirectory,"workspaces.json"),"{broken registry");recovered.Prompt="Keep draft while recovering index";recovered.SetLanguage("en");
            Check(recovered.Prompt=="Keep draft while recovering index"&&Directory.GetFiles(recovered.PreferencesDirectory,"workspaces.json.corrupt-*").Length==1,"A damaged auxiliary workspace registry is preserved without interrupting language changes or drafts");
        }
        Check(InputImagePolicy.MaximumCount==8,"Desktop input limits read the shared eight-image policy");
    }
}
