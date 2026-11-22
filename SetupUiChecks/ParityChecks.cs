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
        using(var vm=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            var a=new Generation{CreationId="a",Directory=Path.Combine(fixture.Settings.Workspace,"outputs","a"),Status="completed",Request=new(){Prompt="A"}};
            var b=new Generation{CreationId="b",Directory=Path.Combine(fixture.Settings.Workspace,"outputs","b"),Status="failed",Request=new(){Prompt="B"}};
            StateStore.SaveHistory(fixture.Settings.Workspace,[a,b]);
        }
        using(var vm=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            var a=vm.Recent.Single(g=>g.CreationId=="a");var b=vm.Recent.Single(g=>g.CreationId=="b");first=a.CreationId;second=b.CreationId;
            vm.Selected=a;vm.Tier=2;vm.Ratio="16:9";vm.EnhancementEnabled=false;
            vm.Selected=b;vm.Tier=0;vm.Ratio="1:1";vm.EnhancementEnabled=true;
            vm.Selected=a;Check(vm.Tier==2&&vm.Ratio=="16:9"&&!vm.EnhancementEnabled,"Switching history restores the creation's edited size, ratio and enhancement state");
            vm.Selected=b;vm.ReuseCommand.Execute(a);
            Check(vm.Selected==a&&vm.Turns.Single().CreationId==first&&vm.Prompt=="A"&&vm.RandomSeed,"Reuse selects the original creation and prepares its draft with a new random seed");
            vm.Selected=b;
        }
        using(var restored=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            Check(restored.Selected?.CreationId==second&&restored.Tier==0&&restored.Ratio=="1:1"&&restored.EnhancementEnabled,"Active creation and its parameters survive a full view-model restart");
            restored.NewCreation();Check(restored.Tier==1&&restored.Ratio=="Adaptive"&&restored.EnhancementEnabled,"A new creation has the Mac Standard defaults");
            restored.Prompt="Draft before settings";var original=restored.Settings.Workspace;var other=Path.Combine(root,"settings-preview-only");
            restored.Setup.Workspace=other;await restored.Setup.BeginWorkspaceAsync(commit:false);
            Check(restored.Settings.Workspace==original&&restored.Prompt=="Draft before settings","Searching a draft workspace does not commit settings or clear the current draft");
        }
        using(var pending=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]))){pending.NewCreation();pending.Tier=2;pending.Ratio="16:9";}
        using(var pending=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
            Check(pending.Selected==null&&pending.Tier==2&&pending.Ratio=="16:9","An unsent creation's parameters survive reopening without a generation record");
        var nativePython=Environment.GetEnvironmentVariable("LIGHTX2V_QA_PYTHON");
        if(!string.IsNullOrWhiteSpace(nativePython)&&File.Exists(nativePython))
        {
            using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));
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
        using (var held = new FileStream(protectedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var remove = AppMaintenance.ExecuteAsync(planFile, digest);
            await Task.Delay(250);
            held.Dispose();
            await remove;
        }
        Check(!File.Exists(protectedFile), "A transient Windows file lock is retried and the selected file is removed");
        var allRoot=Path.Combine(root,"uninstall-all-groups");var allSettings=AppSettings.Fresh(Path.Combine(allRoot,"workspace"));var allPrefs=Path.Combine(allRoot,"preferences");var allApp=Path.Combine(allRoot,"app");
        Directory.CreateDirectory(Path.Combine(allSettings.Workspace,"models"));Directory.CreateDirectory(Path.Combine(allSettings.Workspace,"outputs"));Directory.CreateDirectory(allPrefs);Directory.CreateDirectory(allApp);
        Files.Write(Path.Combine(allApp,"build-manifest.json"),new{sha256=new Dictionary<string,string>{{"LightX2V.Windows.exe","fixture"}}});File.WriteAllText(Path.Combine(allApp,"user-note.txt"),"Keep unowned app-folder data");File.WriteAllText(Path.Combine(allApp,"LightX2V.Windows.exe"),"Non-executable uninstall fixture");
        var managedPython=SetupInstaller.ManagedPython(allSettings.Workspace,"amd_rocm");
        var managed=Path.GetDirectoryName(managedPython)!;Directory.CreateDirectory(managed);File.WriteAllText(Path.Combine(managed,".lightx2v-environment.json"),"{}");
        File.WriteAllText(managedPython,"Managed Python discovery fixture");
        var unmanaged=Path.Combine(allSettings.Workspace,"environments","keep-unmanaged");Directory.CreateDirectory(unmanaged);File.WriteAllText(Path.Combine(unmanaged,"keep.txt"),"Keep this unmanaged environment");
        var groupedPlan = AppMaintenance.Discover(allSettings, allPrefs, allApp);
        var allPlanFile = Path.Combine(allRoot, "plan.json");
        async Task RemoveGroups(params string[] groups)
        {
            var plan = AppMaintenance.Select(groupedPlan, groups.ToHashSet());
            plan.ParentPid = int.MaxValue;
            Files.Write(allPlanFile, plan);
            await AppMaintenance.ExecuteAsync(allPlanFile, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(allPlanFile))));
        }
        await RemoveGroups("workspace", "application");
        Check(File.Exists(Path.Combine(allApp,"user-note.txt"))&&!File.Exists(Path.Combine(allApp,"LightX2V.Windows.exe"))&&
            !Directory.Exists(allPrefs)&&!Directory.Exists(Path.Combine(allSettings.Workspace,"outputs"))&&
            Directory.Exists(Path.Combine(allSettings.Workspace,"models"))&&File.Exists(managedPython),
            "Keeping model weights while removing workspace and app data also preserves the managed Python");
        var fresh = AppSettings.Fresh(allSettings.Workspace);
        Check(SetupDiscovery.PythonPaths(fresh).Contains(managedPython, StringComparer.OrdinalIgnoreCase),
            "First-use discovery finds the managed Python after preferences were removed");
        await RemoveGroups(AppMaintenance.ManagedEnvironmentGroup);
        Check(!Directory.Exists(managed)&&File.Exists(Path.Combine(unmanaged,"keep.txt"))&&
            Directory.Exists(Path.Combine(allSettings.Workspace,"models")),
            "Explicitly selecting managed environments removes only the owned Python environment");
        await UninstallShortcutChecks(maintenance);
        MaintenanceLaunchFailureChecks(maintenance);
        using(var recovered=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[])))
        {
            File.WriteAllText(Path.Combine(recovered.PreferencesDirectory,"workspaces.json"),"{broken registry");recovered.Prompt="Keep draft while recovering index";recovered.SetLanguage("en");
            Check(recovered.Prompt=="Keep draft while recovering index"&&Directory.GetFiles(recovered.PreferencesDirectory,"workspaces.json.corrupt-*").Length==1,"A damaged auxiliary workspace registry is preserved without interrupting language changes or drafts");
        }
        Check(InputImagePolicy.MaximumCount==8,"Desktop input limits read the shared eight-image policy");
    }

    static async Task UninstallShortcutChecks(string fixture)
    {
        var app = Path.Combine(fixture, "shortcut-app");
        var desktop = Path.Combine(fixture, "shortcut-desktop");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(desktop);
        var target = Path.Combine(app, "LightX2V.Windows.exe");
        var other = Path.Combine(fixture, "unrelated.exe");
        File.WriteAllText(target, "App fixture");
        File.WriteAllText(other, "Unrelated fixture");
        Files.Write(Path.Combine(app, "build-manifest.json"), new { sha256 = new Dictionary<string, string> { ["LightX2V.Windows.exe"] = "fixture" } });

        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
        void CreateShortcut(string path, string executable, string arguments = "")
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(path);
            try
            {
                shortcut.TargetPath = executable;
                shortcut.Arguments = arguments;
                shortcut.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
        }
        var owned = Path.Combine(desktop, "Renamed lightx2v.lnk");
        var retargeted = Path.Combine(desktop, "Changed link.lnk");
        var arguments = Path.Combine(desktop, "Launch with arguments.lnk");
        try
        {
            CreateShortcut(owned, target);
            CreateShortcut(retargeted, target);
            CreateShortcut(arguments, target, "--custom");
            var settings = AppSettings.Fresh(Path.Combine(fixture, "shortcut-workspace"));
            var plan = AppMaintenance.Select(AppMaintenance.Discover(settings, Path.Combine(fixture, "absent-preferences"), app, desktop), new HashSet<string> { "application" });
            Check(plan.Items.Count(item => item.ShortcutTarget != null) == 2, "Uninstall discovers only desktop shortcuts dedicated to this executable");
            CreateShortcut(retargeted, other);
            plan.ParentPid = int.MaxValue;
            var job = Path.Combine(fixture, "shortcut-job");
            Directory.CreateDirectory(job);
            var file = Path.Combine(job, "plan.json");
            Files.Write(file, plan);
            await AppMaintenance.ExecuteAsync(file, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        Check(!File.Exists(owned) && !File.Exists(target) && File.Exists(retargeted) && File.Exists(arguments) && File.Exists(other),
            "Uninstall removes the matching shortcut and preserves links modified after confirmation and links with arguments");
    }

    static void MaintenanceLaunchFailureChecks(string fixture)
    {
        var app = Path.Combine(fixture, "invalid-helper-package");
        Directory.CreateDirectory(app);
        var manifest = Path.Combine(app, "build-manifest.json");
        File.WriteAllText(Path.Combine(app, "LightX2V.Windows.exe"), "Modified package fixture");
        Files.Write(manifest, new { sha256 = new Dictionary<string, string> { ["LightX2V.Windows.exe"] = new string('0', 64) } });
        var job = MaintenanceProcess.NewJobDirectory();
        var rejected = false;
        try { MaintenanceProcess.Launch(job, PackageLayout.Find(app)!, "--maintenance", new UninstallPlan()); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(job), "Failed helper package validation cleans up the abandoned maintenance directory");
    }
}
