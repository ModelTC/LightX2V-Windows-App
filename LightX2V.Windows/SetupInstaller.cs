using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace LightX2V.Windows;

public sealed class SetupArtifact
{
    public string Name {get;set;}="";public string Version {get;set;}="";public string File {get;set;}="";public string Url {get;set;}="";public string? Mirror {get;set;}public string Sha256 {get;set;}="";public long Size {get;set;}public string Group {get;set;}="";
}
public sealed class SetupSource {public string Revision {get;set;}="";public string Archive {get;set;}="";public string Sha256 {get;set;}="";}
public sealed class SetupManifest
{
    public int Schema {get;set;}public string Profile {get;set;}="";public SetupArtifact Python {get;set;}=new();public List<SetupArtifact> Packages {get;set;}=[];public SetupSource Source {get;set;}=new();
    public static string Resources=>Path.Combine(AppContext.BaseDirectory,"Resources","Setup");
    public static SetupManifest Read(string backend="amd_rocm")=>Files.Read<SetupManifest>(Path.Combine(Resources,backend=="cuda"?"manifest-cuda.json":"manifest.json"))??throw new InvalidOperationException("Missing setup manifest.");
}
public sealed record SetupProgress(string Stage,string Detail="",double Percent=0,string? Log=null,EnvironmentCandidate? Candidate=null);
public static class SetupInstaller
{
    sealed class DirectProgress(Action<SetupProgress> callback):IProgress<SetupProgress>{public void Report(SetupProgress value)=>callback(value);}
    static readonly HttpClient http=new(new HttpClientHandler{AutomaticDecompression=DecompressionMethods.None}){Timeout=Timeout.InfiniteTimeSpan};
    public static string EnvironmentDirectory(string workspace,string backend="amd_rocm")=>Path.Combine(workspace,"environments",SetupManifest.Read(backend).Profile);
    public static string ManagedPython(string workspace,string backend="amd_rocm")=>Path.Combine(SetupPaths.Short(EnvironmentDirectory(workspace,backend)),"python.exe");
    public static string SourceDirectory(string workspace)=>SetupPaths.Short(Path.Combine(workspace,"runtimes","lightx2v-native-"+SetupManifest.Read().Source.Sha256[..12]));
    public static async Task<bool> Matches(string path,SetupArtifact artifact,CancellationToken token)
    {
        if(!System.IO.File.Exists(path)||new FileInfo(path).Length!=artifact.Size)return false;
        await using var f=System.IO.File.OpenRead(path);return Convert.ToHexString(await SHA256.HashDataAsync(f,token)).Equals(artifact.Sha256,StringComparison.OrdinalIgnoreCase);
    }
    public static async Task<string> Download(SetupArtifact artifact,string cache,IEnumerable<string> seedDirectories,bool mirror,IProgress<SetupProgress> progress,CancellationToken token)
    {
        Directory.CreateDirectory(cache);var target=Path.Combine(cache,artifact.File);
        progress.Report(new("Downloading environment",artifact.File,0));
        if(await Matches(target,artifact,token))return target;
        foreach(var root in seedDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var seed=Path.Combine(root,artifact.File);
            if(Path.GetFullPath(seed).Equals(Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase)||!await Matches(seed,artifact,token))continue;
            progress.Report(new("Reusing verified downloads",artifact.File));
            await using(var src=System.IO.File.OpenRead(seed))await using(var dst=System.IO.File.Create(target+".copy"))await src.CopyToAsync(dst,token);
            System.IO.File.Move(target+".copy",target,true);return target;
        }
        var partial=target+".partial";var url=mirror&&artifact.Mirror!=null?artifact.Mirror:artifact.Url;
        for(int attempt=0;attempt<3;attempt++)
        {
            token.ThrowIfCancellationRequested();
            if(attempt==2&&mirror&&artifact.Mirror!=null){url=artifact.Url;progress.Report(new("Downloading environment",artifact.File,Log:"Mirror retry failed; using the official source for "+artifact.File));}
            try
            {
                var offset=System.IO.File.Exists(partial)?new FileInfo(partial).Length:0;
                if(offset>artifact.Size){System.IO.File.Delete(partial);offset=0;}
                if(offset!=artifact.Size)
                {
                    using var request=new HttpRequestMessage(HttpMethod.Get,url);if(artifact.Group=="driver"){request.Headers.UserAgent.ParseAdd("LightX2V/0.3.1");request.Headers.Referrer=new Uri(new Uri(url).Host=="drivers.amd.com"?"https://www.amd.com/en/support/download/drivers.html":"https://www.nvidia.com/en-us/software/nvidia-app/");}if(offset>0)request.Headers.Range=new RangeHeaderValue(offset,null);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                    response.EnsureSuccessStatusCode();
                    if(response.StatusCode==HttpStatusCode.PartialContent)
                    {
                        if(response.Content.Headers.ContentRange?.From!=offset||response.Content.Headers.ContentRange?.Length!=artifact.Size)throw new IOException("Invalid download byte range.");
                    }
                    else offset=0;
                    await using var input=await response.Content.ReadAsStreamAsync(token);
                    await using var output=new FileStream(partial,offset>0?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.Read,1024*1024,true);
                    var buffer=new byte[1024*1024];long last=0;var watch=Stopwatch.StartNew();
                    while(true)
                    {
                        using var readTimeout=CancellationTokenSource.CreateLinkedTokenSource(token);readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                        var n=await input.ReadAsync(buffer,readTimeout.Token);if(n==0)break;
                        offset+=n;if(offset>artifact.Size)throw new IOException("Download exceeds its declared size.");
                        await output.WriteAsync(buffer.AsMemory(0,n),token);
                        if(watch.ElapsedMilliseconds-last>200){progress.Report(new("Downloading environment",artifact.File,100.0*offset/artifact.Size));last=watch.ElapsedMilliseconds;}
                    }
                    await output.FlushAsync(token);
                }
                progress.Report(new("Checking downloads",artifact.File));
                if(!await Matches(partial,artifact,token)){System.IO.File.Delete(partial);throw new IOException("Downloaded file checksum does not match the setup manifest.");}
                System.IO.File.Move(partial,target,true);return target;
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch when(attempt<2){await Task.Delay(500,token);}
        }
        throw new IOException("Unable to download "+artifact.File);
    }
    static void Extract(string archive,string target,CancellationToken token)
    {
        Directory.CreateDirectory(target);var root=Path.GetFullPath(target)+Path.DirectorySeparatorChar;
        using var zip=ZipFile.OpenRead(archive);
        foreach(var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if((entry.ExternalAttributes>>16&0xF000)==0xA000)throw new IOException("Unexpected symbolic link in bundled archive.");
            var destination=Path.GetFullPath(Path.Combine(target,entry.FullName.Replace('/',Path.DirectorySeparatorChar)));
            if(!destination.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Invalid archive destination.");
            if(entry.FullName.EndsWith('/')){Directory.CreateDirectory(destination);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);entry.ExtractToFile(destination,true);
        }
    }
    public static async Task<string> PrepareSource(string workspace,CancellationToken token)
    {
        var manifest=SetupManifest.Read();var target=SourceDirectory(workspace);var marker=Path.Combine(target,".lightx2v-source.json");
        var archive=Path.Combine(SetupManifest.Resources,manifest.Source.Archive);
        await using(var f=System.IO.File.OpenRead(archive))if(!Convert.ToHexString(await SHA256.HashDataAsync(f,token)).Equals(manifest.Source.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Bundled source checksum mismatch.");
        if(Directory.Exists(target))
        {
            var stamp=System.IO.File.Exists(marker)?Files.Read<Dictionary<string,string>>(marker):null;
            if(stamp?.GetValueOrDefault("sha256")!=manifest.Source.Sha256)throw new IOException("The runtime directory contains unmanaged files: "+target);
            using var zip=ZipFile.OpenRead(archive);
            foreach(var entry in zip.Entries.Where(e=>!e.FullName.EndsWith('/')))
            {
                token.ThrowIfCancellationRequested();var file=Path.Combine(target,entry.FullName.Replace('/',Path.DirectorySeparatorChar));
                if(!System.IO.File.Exists(file)||new FileInfo(file).Length!=entry.Length)throw new IOException("The managed LightX2V source is incomplete: "+file);
                await using var expected=entry.Open();await using var actual=System.IO.File.OpenRead(file);
                var expectedHash=await SHA256.HashDataAsync(expected,token);var actualHash=await SHA256.HashDataAsync(actual,token);
                if(!expectedHash.SequenceEqual(actualHash))throw new IOException("The managed LightX2V source was changed: "+file);
            }
            return target;
        }
        var stage=target+".staging-"+Guid.NewGuid().ToString("N");
        try{await Task.Run(()=>Extract(archive,stage,token),token);Files.Write(Path.Combine(stage,".lightx2v-source.json"),new{sha256=manifest.Source.Sha256,revision=manifest.Source.Revision});Directory.CreateDirectory(Path.GetDirectoryName(target)!);Directory.Move(stage,target);}
        finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
        return SetupPaths.Short(target);
    }
    public static async Task<string> Install(string workspace,bool mirror,IEnumerable<string> seedDirectories,IProgress<SetupProgress> progress,CancellationToken token,string backend="amd_rocm")
    {
        if(!HardwareInfo.Read().Supports(backend))throw new InvalidOperationException(Backends.Unsupported);
        if(backend=="cuda")CudaDriver.RequireCompatible();
        var manifest=SetupManifest.Read(backend);var env=EnvironmentDirectory(workspace,backend);var marker=Path.Combine(env,".lightx2v-environment.json");
        if(Directory.Exists(env)&&Directory.EnumerateFileSystemEntries(env).Any()&&(!System.IO.File.Exists(marker)||Files.Read<Dictionary<string,string>>(marker)?.GetValueOrDefault("profile")!=manifest.Profile))throw new IOException("The environment directory contains files not managed by this app: "+env);
        var required=Math.Max(8L*1024*1024*1024,manifest.Packages.Sum(p=>p.Size)*3+1024L*1024*1024);
        if(ResourceSnapshot.FreeDisk(workspace)<required)throw new IOException($"Environment setup needs at least {required/1073741824.0:F1} GiB of free disk space, including downloads and unpacked packages.");
        Directory.CreateDirectory(env);Files.Write(marker,new{profile=manifest.Profile,state="preparing"});
        var runtime=SetupPaths.Ensure(env);
        var cache=Path.Combine(workspace,"cache","packages");var seeds=seedDirectories.ToArray();
        var pythonZip=await Download(manifest.Python,cache,seeds,mirror,progress,token);
        progress.Report(new("Preparing Python"));await Task.Run(()=>Extract(pythonZip,env,token),token);
        var site=Path.Combine(env,"Lib","site-packages");Directory.CreateDirectory(site);
        await System.IO.File.WriteAllLinesAsync(Path.Combine(env,"python312._pth"),["python312.zip",".",@"Lib\site-packages","import site"],new UTF8Encoding(false),token);
        var downloaded=new System.Collections.Concurrent.ConcurrentDictionary<string,string>();
        var completed=new System.Collections.Concurrent.ConcurrentDictionary<string,long>();
        var total=manifest.Packages.Sum(p=>p.Size);
        await Parallel.ForEachAsync(manifest.Packages,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=token},async(artifact,ct)=>{
            var reporting=new DirectProgress(value=>{
                if(value.Percent>0)completed[artifact.Name]=(long)(artifact.Size*value.Percent/100);
                progress.Report(value with{Percent=100.0*completed.Values.Sum()/total});
            });
            downloaded[artifact.Name]=await Download(artifact,cache,seeds,mirror,reporting,ct);
            completed[artifact.Name]=artifact.Size;
            progress.Report(new("Downloading environment",artifact.File,100.0*completed.Values.Sum()/total));
        });
        progress.Report(new("Installing dependencies"));
        Extract(downloaded["pip"],site,token);
        var python=Path.Combine(runtime,"python.exe");
        var common=new[]{"-X","utf8","-u","-m","pip","install","--no-index","--no-deps","--no-build-isolation","--no-compile","--force-reinstall","--no-warn-script-location","--disable-pip-version-check"};
        var logs=Path.Combine(workspace,"setup","logs");Directory.CreateDirectory(logs);
        var bootstrap=manifest.Packages.Where(p=>p.Group=="bootstrap"&&p.Name!="pip").Select(p=>downloaded[p.Name]);
        await Run(python,common.Concat(bootstrap),workspace,Path.Combine(logs,"install-bootstrap.log"),progress,token);
        var requirements=Path.Combine(workspace,"setup","requirements-"+manifest.Profile+".lock");
        await System.IO.File.WriteAllLinesAsync(requirements,manifest.Packages.Select(p=>p.Name+"=="+p.Version),token);
        await Run(python,common.Concat(["--find-links",cache,"-r",requirements]),workspace,Path.Combine(logs,"install.log"),progress,token);
        progress.Report(new("Checking environment"));
        await Run(python,["-X","utf8","-m","pip","check"],workspace,Path.Combine(logs,"pip-check.log"),progress,token);
        Files.Write(marker,new{profile=manifest.Profile,state="installed"});return python;
    }
    public static async Task Run(string executable,IEnumerable<string> arguments,string workspace,string log,IProgress<SetupProgress> progress,CancellationToken token)
    {
        var temp=Path.Combine(workspace,"tmp");Directory.CreateDirectory(temp);Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var a in arguments)start.ArgumentList.Add(a);
        foreach(var key in start.Environment.Keys.Where(k=>k.StartsWith("PIP_",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(key);
        start.Environment.Remove("PYTHONUSERBASE");start.Environment.Remove("PYTHONPATH");
        start.Environment["TEMP"]=temp;start.Environment["TMP"]=temp;start.Environment["PYTHONIOENCODING"]="utf-8";start.Environment["PYTHONUTF8"]="1";start.Environment["PIP_CONFIG_FILE"]="NUL";start.Environment["PIP_CACHE_DIR"]=Path.Combine(workspace,"cache","pip");start.WorkingDirectory=workspace;start.Environment.Remove("PYTHONHOME");
        using var job=new SetupJob();
        using var p=Process.Start(start)??throw new IOException("Unable to start environment setup.");
        job.Assign(p);
        using var registration=token.Register(()=>{try{if(!p.HasExited)p.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}});
        await using var output=new StreamWriter(log,false,new UTF8Encoding(false));var gate=new SemaphoreSlim(1);
        async Task Read(StreamReader input){while(await input.ReadLineAsync(token) is {} line){await gate.WaitAsync(token);try{await output.WriteLineAsync(line);await output.FlushAsync(token);progress.Report(new("",Log:line));}finally{gate.Release();}}}
        try{await Task.WhenAll(Read(p.StandardOutput),Read(p.StandardError),p.WaitForExitAsync(token));if(p.ExitCode!=0)throw new IOException("Environment setup exited with code "+p.ExitCode+". "+log);}
        finally{if(!p.HasExited){p.Kill(true);await p.WaitForExitAsync();}gate.Dispose();}
    }
}
