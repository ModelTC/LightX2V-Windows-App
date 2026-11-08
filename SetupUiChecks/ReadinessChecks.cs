using System.IO;
using System.Reflection;
using System.Windows.Threading;
using LightX2V.Windows;

internal static partial class Program
{
    sealed class ReadinessCatalog : IEnvironmentCatalog
    {
        public int Calls;public bool Hold=true;public bool IgnoreCancellation;public bool Succeed=true;
        public readonly TaskCompletionSource<bool> Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<List<EnvironmentCandidate>> Search(AppSettings settings,IProgress<SetupProgress> progress,CancellationToken token)=>Task.FromResult(new List<EnvironmentCandidate>());
        public Task<string> Install(string workspace,bool mirror,IProgress<SetupProgress> progress,CancellationToken token,string backend="amd_rocm")=>throw new NotSupportedException();
        public async Task<EnvironmentCandidate> Probe(string path,string workspace,CancellationToken token,string backend="auto")
        {
            Calls++;if(Hold){if(IgnoreCancellation)await Release.Task;else await Release.Task.WaitAsync(token);}
            return Valid(path) with{Ready=Succeed,Backend=backend,Detail=Succeed?"GPU fixture ready":"Fixture GPU validation failed"};
        }
    }
    static (AppSettings Settings,string Preferences,FakeDriverPlatform Platform) ReadinessFixture(string name)
    {
        var f=StartupFixture(name);f.Settings.Python=f.Catalog.InstallResult;f.Settings.EnhancementEnabled=false;
        f.Settings.Repository=Path.Combine(f.Settings.Workspace,"source-fixture");Directory.CreateDirectory(Path.Combine(f.Settings.Repository,"lightx2v"));File.WriteAllText(Path.Combine(f.Settings.Repository,"lightx2v","infer.py"),"# Never executed by readiness checks");
        TinyTurbo(f.Settings.Model);Files.Write(Path.Combine(f.Preferences,"preferences.json"),new Preferences{Settings=f.Settings});return(f.Settings,f.Preferences,f.Platform);
    }
    static Task<bool> EnsureForCheck(MainViewModel vm)=> (Task<bool>)typeof(MainViewModel).GetMethod("EnsureGenerationEnvironmentAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,[CancellationToken.None])!;
    static async Task Cancelled(Task task){try{await task;}catch(OperationCanceledException){}}
    static async Task ReadinessChecks()
    {
        var f=ReadinessFixture("readiness-reopen");var catalog=new ReadinessCatalog();
        using(var vm=new MainViewModel(f.Preferences,environmentCatalog:catalog,driverPlatform:f.Platform))
        {
            var host=new StartupHost();await new StartupCoordinator(vm,host).StartAsync();
            Check(host.WorkspaceShows==1&&catalog.Calls==0&&!vm.IsChecking&&!vm.Ready,"A complete saved workspace opens immediately without a startup GPU probe or a persisted readiness claim");
            Check(vm.SettingsCommand.CanExecute(null)&&vm.CanConfigureModels&&vm.CanAddImages&&!vm.CanGenerate,"Reopened settings, model controls and reference import are immediately available while an empty draft cannot send");
            vm.Ratio="1:1";vm.Tier=0;vm.Prompt="Preserve submitted draft";
            Check(vm.CanGenerate&&vm.EnvironmentStatus==vm.T["Ready to create"],"A configured typed draft can request generation before its first-session GPU validation");
            var sending=vm.GenerateAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.IsPreflighting&&vm.IsGenerationBusy&&vm.IsChecking&&!vm.IsRunning&&catalog.Calls==1,"First send exposes cancellable validation without creating a running generation");
            Check(!vm.SettingsCommand.CanExecute(null)&&!vm.CanConfigureModels&&!vm.CanAddImages&&vm.GenerateCommand.CanExecute(null),"Preflight prevents environment changes and double-send while keeping the stop action enabled");
            vm.Prompt="Next edited draft";await vm.StopAsync();await sending;
            Check(!vm.IsPreflighting&&!vm.IsGenerationBusy&&!vm.IsChecking&&!vm.Ready&&vm.Prompt=="Next edited draft"&&vm.Turns.Count==0,"Cancelling first-send validation preserves the edited draft and creates no fake conversation task");
            Check(!Directory.Exists(Path.Combine(f.Settings.Workspace,"outputs"))||!Directory.EnumerateDirectories(Path.Combine(f.Settings.Workspace,"outputs")).Any(),"Cancelled validation does not allocate an output generation directory");
        }
        f=ReadinessFixture("readiness-failed");catalog=new(){Hold=false,Succeed=false};
        using(var vm=new MainViewModel(f.Preferences,environmentCatalog:catalog,driverPlatform:f.Platform))
        {
            vm.Ratio="1:1";vm.Prompt="Keep after failed GPU check";await vm.GenerateAsync();
            Check(!vm.IsGenerationBusy&&!vm.Ready&&vm.Turns.Count==0&&vm.Prompt=="Keep after failed GPU check"&&vm.Toast.Contains("Fixture GPU validation failed"),"Failed first-send validation preserves the draft, reports the reason and leaves no running task");
        }
        f=ReadinessFixture("readiness-session");catalog=new(){Hold=false};
        using(var vm=new MainViewModel(f.Preferences,environmentCatalog:catalog,driverPlatform:f.Platform))
        {
            Check(await EnsureForCheck(vm)&&await EnsureForCheck(vm)&&catalog.Calls==1,"A verified unchanged environment is reused for later sends within the session");
            var same=vm.Settings.Clone();same.EnhancementEnabled=!same.EnhancementEnabled;vm.ApplySettings(same);
            Check(vm.Ready&&await EnsureForCheck(vm)&&catalog.Calls==1,"Changing prompt enhancement preferences retains matching session environment verification");
            File.AppendAllText(vm.Settings.Python,"changed executable");Check(await EnsureForCheck(vm)&&catalog.Calls==2,"Changing the Python executable invalidates the session verification fingerprint");
        }
        f=ReadinessFixture("readiness-settings-race");catalog=new(){IgnoreCancellation=true};
        using(var vm=new MainViewModel(f.Preferences,environmentCatalog:catalog,driverPlatform:f.Platform))
        {
            var oldCheck=vm.CheckAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.IsChecking&&vm.SettingsCommand.CanExecute(null)&&vm.CanConfigureModels,"An explicit background environment check leaves settings and draft configuration interactive");
            var changed=vm.Settings.Clone();changed.Python=Path.Combine(changed.Workspace,"replacement-python.exe");File.WriteAllText(changed.Python,"replacement fixture");vm.ApplySettings(changed);
            Check(!vm.Ready&&!vm.IsChecking&&catalog.Calls==1,"Applying settings cancels a pending check without starting another GPU import");
            catalog.Release.TrySetResult(true);await Cancelled(oldCheck);
            Check(!vm.Ready&&!vm.IsChecking&&vm.Settings.Python==changed.Python,"An old probe that ignores cancellation cannot overwrite a newer environment configuration");
        }
        f=ReadinessFixture("readiness-new-check-race");catalog=new(){IgnoreCancellation=true};
        using(var vm=new MainViewModel(f.Preferences,environmentCatalog:catalog,driverPlatform:f.Platform))
        {
            var oldCheck=vm.CheckAsync();var changed=vm.Settings.Clone();changed.Python=Path.Combine(changed.Workspace,"new-check-python.exe");File.WriteAllText(changed.Python,"new check fixture");vm.ApplySettings(changed);
            var newCheck=vm.CheckAsync();Check(vm.IsChecking&&catalog.Calls==2,"A changed environment can start a new check while the old cancelled probe unwinds");
            catalog.Release.TrySetResult(true);await Cancelled(oldCheck);await newCheck;
            Check(vm.Ready&&!vm.IsChecking&&vm.Settings.Python==changed.Python,"A superseded check cannot clear or replace the newer check result");
        }
        f=ReadinessFixture("readiness-dispose-race");catalog=new(){IgnoreCancellation=true};var disposedVm=new MainViewModel(f.Preferences,environmentCatalog:catalog,driverPlatform:f.Platform);
        var pendingCheck=disposedVm.CheckAsync();disposedVm.Dispose();catalog.Release.TrySetResult(true);await Cancelled(pendingCheck);
        Check(!disposedVm.Ready&&!disposedVm.IsChecking,"Closing the app cancels readiness and discards any late probe result");
    }
}
