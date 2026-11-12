using System.IO;
using System.Text;
using LightX2V.Windows;
internal static partial class Program
{
    sealed class InlineProgress<T>(Action<T> action):IProgress<T>{public void Report(T value)=>action(value);}
    sealed class DeferredModels(string path) : IModelDiscovery
    {
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModelSearchResult> SearchAsync(AppSettings settings,IProgress<ModelSearchProgress> progress,CancellationToken token)
        {
            Started.TrySetResult();await Release.Task;
            var candidate=new ModelCandidate("turbo",path,true);progress.Report(new("Usable models found",candidate));
            return new([candidate],1,0,true);
        }
    }
    static async Task DiscoveryChecks()
    {
        var fixture=ReadinessFixture("discovery-source");var lab=Path.Combine(root,"model-search");
        var models=Path.Combine(lab,"Models");var model=Path.Combine(models,"Qwen-Image-2.1-Turbo");
        void Copy(string from,string to){Directory.CreateDirectory(to);foreach(var file in Directory.GetFiles(from))File.Copy(file,Path.Combine(to,Path.GetFileName(file)));foreach(var child in Directory.GetDirectories(from))Copy(child,Path.Combine(to,Path.GetFileName(child)));}
        Copy(fixture.Settings.Model,model);
        var settings=AppSettings.Fresh(Path.Combine(lab,"Workspace"));
        var found=await Task.Run(()=>new ModelDiscovery(fixedRoots:[]).Search(settings,null,CancellationToken.None));
        Check(found.Candidates.Single(c=>c.Ready&&c.Key=="turbo").Path==model,"Automatic discovery finds a complete model beside the workspace without a prefilled model path");
        var deep=Path.Combine(lab,"Disk","a","b","c","d","e","f","g","h","model");Copy(model,deep);
        found=await Task.Run(()=>new ModelDiscovery([], [Path.Combine(lab,"Disk")]).Search(settings,null,CancellationToken.None));
        Check(found.Candidates.Single(c=>c.Ready&&c.Key=="turbo").Path==deep,"Background fixed-disk discovery finds complete models deeper than the quick-search limit");
        var duplicate=Path.Combine(lab,"Disk","z-model");Copy(model,duplicate);
        found=new ModelDiscovery([], [Path.Combine(lab,"Disk")]).Search(settings,null,CancellationToken.None);
        Check(found.Candidates.First(c=>c.Ready&&c.Key=="turbo").Path==deep,"Same-priority candidates are selected by stable full-path ordering");
        var incomplete=Path.Combine(lab,"Disk","0-incomplete");Copy(model,incomplete);
        File.Delete(Path.Combine(incomplete,"processor","tokenizer.json"));
        found=new ModelDiscovery([], [Path.Combine(lab,"Disk")]).Search(settings,null,CancellationToken.None);
        Check(found.Candidates.Any(c=>!c.Ready&&c.Path==incomplete)&&found.Candidates.Any(c=>c.Ready&&c.Path==deep),"An incomplete candidate does not hide a complete model later in the search");
        using(var cancel=new CancellationTokenSource())
        {
            var observed=new List<ModelCandidate>();
            var progress=new InlineProgress<ModelSearchProgress>(p=>{if(p.Candidate is {} candidate){observed.Add(candidate);cancel.Cancel();}});
            try{new ModelDiscovery([model],[]).Search(settings,progress,cancel.Token);throw new Exception("Cancelled discovery returned success");}
            catch(OperationCanceledException){Check(observed.Count==1,"Discovery observes cancellation after publishing a candidate and does not claim completed search");}
        }
        foreach(var cancelSearch in new[]{false,true})
        {
            var service=new DeferredModels(model);
            using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));
            using var setup=new SetupController(vm,driverPlatform:fixture.Platform,modelDiscovery:service);
            setup.Page=2;await service.Started.Task;
            var manual=Path.Combine(lab,"manual-selection");vm.TurboCard.Path=manual;
            if(cancelSearch)setup.Workspace=Path.Combine(lab,"changed-workspace");
            service.Release.TrySetResult();await Task.Delay(50);
            Check(vm.TurboCard.Path==manual,cancelSearch?"Late model results cannot cross a workspace change":"Background discovery cannot replace a manually edited model path");
            setup.StopModelSearch();
        }
        var snapshot=Path.Combine(lab,"hf","hub","models--Qwen--Qwen-Image-2.1-Turbo","snapshots","fixture");
        bool linkSupport=true;
        try
        {
            foreach(var source in Directory.GetFiles(model,"*",SearchOption.AllDirectories))
            {
                var destination=Path.Combine(snapshot,Path.GetRelativePath(model,source));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.CreateSymbolicLink(destination,source);
            }
        }
        catch(Exception error) when(error is UnauthorizedAccessException||error is IOException&&((error.HResult&0xffff)==1314))
        {linkSupport=false;File.WriteAllText(Path.Combine(root,"file-link-fixture-skipped.txt"),"File symbolic links are unavailable at the current user privilege. No Windows policy was changed.");}
        if(linkSupport)
        {
            Check(ModelValidation.Ready(snapshot,"turbo"),"A complete HF-style snapshot with file links passes the same model completeness policy");
            var cached=new ModelDiscovery([Path.Combine(lab,"hf")],[]).Search(settings,null,CancellationToken.None);
            Check(cached.Candidates.Any(c=>c.Key=="turbo"&&c.Ready&&c.Path==snapshot),"Automatic cache discovery accepts snapshot file links while keeping directory traversal read-only");
            var tokenizer=Path.Combine(model,"processor","tokenizer.json");var wrong=Path.Combine(lab,"wrong-tokenizer.json");
            File.WriteAllText(wrong,File.ReadAllText(tokenizer).Replace("1.0","9.0"));File.SetLastWriteTimeUtc(wrong,File.GetLastWriteTimeUtc(tokenizer));
            var link=Path.Combine(snapshot,"processor","tokenizer.json");File.Delete(link);File.CreateSymbolicLink(link,wrong);
            Check(!ModelValidation.Ready(snapshot,"turbo"),"Repointing a tokenizer link invalidates cached validation even when length and timestamp match");
            File.Delete(link);File.CreateSymbolicLink(link,Path.Combine(lab,"missing-tokenizer.json"));
            Check(!ModelValidation.Ready(snapshot,"turbo"),"A broken snapshot file link is never accepted as a complete model");
            var gguf=Path.Combine(lab,"probe.gguf");using(var stream=File.Create(gguf)){stream.Write("GGUF"u8);stream.SetLength(ModelCatalog.MinimumGgufBytes);}
            var ggufLink=Path.Combine(lab,"linked.gguf");File.CreateSymbolicLink(ggufLink,gguf);
            Check(ModelValidation.Gguf(ggufLink),"PE file-link checks use the target length rather than the Windows link's zero length");
        }
        var alias=Path.Combine(lab,"selected-model-alias");
        void Junction(string target)
        {
            var start=new System.Diagnostics.ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var argument in new[]{"/c","mklink","/J",alias,target})start.ArgumentList.Add(argument);
            using var process=System.Diagnostics.Process.Start(start)!;process.WaitForExit();
            if(process.ExitCode!=0)throw new IOException("Could not create the isolated directory-link fixture: "+process.StandardError.ReadToEnd());
        }
        Junction(model);
        try
        {
            Check(ModelValidation.Ready(alias,"turbo"),"An explicitly selected directory alias remains compatible with model validation");
            var wrongModel=Path.Combine(lab,"wrong-model");Copy(model,wrongModel);
            var originalTokenizer=Path.Combine(model,"processor","tokenizer.json");var wrongTokenizer=Path.Combine(wrongModel,"processor","tokenizer.json");
            File.WriteAllText(wrongTokenizer,File.ReadAllText(originalTokenizer).Replace("1.0","9.0"));
            File.SetLastWriteTimeUtc(wrongTokenizer,File.GetLastWriteTimeUtc(originalTokenizer));
            Directory.Delete(alias);Junction(wrongModel);
            Check(!ModelValidation.Ready(alias,"turbo"),"Repointing a directory alias cannot reuse tokenizer validation with matching length and timestamp");
        }
        finally{Directory.Delete(alias);}
        var old=AppSettings.Fresh(Path.Combine(lab,"old-workspace"));
        var relocated=old.AtWorkspace(Path.Combine(lab,"new-workspace"));
        Check(AppMaintenance.Contains(relocated.Workspace,relocated.Model)&&AppMaintenance.Contains(relocated.Workspace,relocated.T2IDirectory)&&AppMaintenance.Contains(relocated.Workspace,relocated.I2IDirectory),"Workspace changes relocate all managed model defaults through one settings rule");
        old.Model=model;Check(old.AtWorkspace(relocated.Workspace).Model==model,"Workspace changes retain explicitly external model directories");
        var bytes=Encoding.UTF8.GetBytes(new string('界',40000));var log=Path.Combine(lab,"run.log");File.WriteAllBytes(log,bytes);
        var tail=MainViewModel.ReadLogTail(log);
        Check(Encoding.UTF8.GetByteCount(tail)<=90000&&!tail.Contains('\uFFFD')&&tail.All(c=>c=='界'),"The unified log preview enforces a 90,000-byte UTF-8 boundary");
        using(var append=new FileStream(log,FileMode.Append)){append.Write(new byte[]{0xE7,0x95});}
        Check(!MainViewModel.ReadLogTail(log).Contains('\uFFFD'),"A live log ending inside a UTF-8 character waits for the complete character");
        Check(DownloadDisplay.Format(L10n.Current,"Installing dependencies",1000000,1)=="","Installation never displays a stale download countdown");
        var rate=new DownloadRate();rate.Observe(0);await Task.Delay(350);var speed=rate.Observe(350000);
        Check(speed>0&&rate.Observe(0)==0,"Measured download throughput resets when the transfer byte counter restarts");
    }
}
