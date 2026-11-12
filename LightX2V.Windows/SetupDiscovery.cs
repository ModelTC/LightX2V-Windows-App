using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace LightX2V.Windows;

public sealed record EnvironmentCandidate(string Path,bool Ready,string Detail)
{
    public string PythonVersion {get;init;}="";
    public string TorchVersion {get;init;}="";
    public string HipVersion {get;init;}="";
    public string CudaVersion {get;init;}="";public string Backend {get;init;}="amd_rocm";
    public long GpuFree {get;init;}public long GpuTotal {get;init;}public long RamAvailable {get;init;}public long RamTotal {get;init;}
    public string Device {get;init;}="";
    public long PythonLength {get;init;}
    public long PythonModifiedUtcTicks {get;init;}
    public string BackendLabel=>Backends.Label(Backend);
    public string ResourceSummary=>GpuTotal>0?$"GPU {GpuTotal/1073741824.0:F1} GiB · {L10n.Current["Available"]} {GpuFree/1073741824.0:F1} GiB · RAM {RamTotal/1073741824.0:F1} GiB":"";
    public IReadOnlyDictionary<string,string> Packages {get;init;}=new Dictionary<string,string>();
    public string Name {get{var folder=System.IO.Path.GetDirectoryName(Path)??Path;return new DirectoryInfo(System.IO.Path.GetFileName(folder).Equals("Scripts",StringComparison.OrdinalIgnoreCase)?System.IO.Path.GetDirectoryName(folder)!:folder).Name;}}
    public string RuntimeSummary=>string.Join(" · ",new[]{PythonVersion.Length>0?"Python "+PythonVersion:"",TorchVersion.Length>0?"PyTorch "+TorchVersion:"",HipVersion.Length>0?"ROCm "+HipVersion:CudaVersion.Length>0?"CUDA "+CudaVersion:""}.Where(x=>x.Length>0));
    public IReadOnlyList<EnvironmentPackage> VersionRows=>new[]{"transformers","diffusers","safetensors","accelerate"}.Where(Packages.ContainsKey).Select(name=>new EnvironmentPackage(name,Packages[name])).ToArray();
    public override string ToString()=>(Ready?"✓ ":"○ ")+Path+"\n"+Detail;
}
public sealed record EnvironmentPackage(string Name,string Version);
public static class SetupDiscovery
{
    public static TimeSpan EnvironmentSearchTimeout {get;}=TimeSpan.FromSeconds(45);
    static IEnumerable<string> PreferredPythonPaths(AppSettings settings)
    {
        if(settings.Python.Length>0)yield return settings.Python;
        var backends=settings.Backend=="cuda"?new[]{"cuda","amd_rocm"}:new[]{"amd_rocm","cuda"};
        foreach(var backend in backends)yield return SetupInstaller.ManagedPython(settings.Workspace,backend);
    }
    public static IEnumerable<string> PythonPaths(AppSettings settings,CancellationToken token=default)
    {
        var found=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string value){token.ThrowIfCancellationRequested();try{if(!value.Contains("WindowsApps",StringComparison.OrdinalIgnoreCase)&&Path.IsPathFullyQualified(value)&&File.Exists(value)&&value.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)&&seen.Add(Path.GetFullPath(value)))found.Add(Path.GetFullPath(value));}catch(IOException){}catch(UnauthorizedAccessException){}}
        void Prefix(string value){Add(Path.Combine(value,"python.exe"));Add(Path.Combine(value,"Scripts","python.exe"));}
        IEnumerable<string> Children(string root){try{return Directory.GetDirectories(root);}catch{return [];}}
        foreach(var path in PreferredPythonPaths(settings))Add(path);
        var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach(var root in new[]{home,Path.Combine(home,"AppData","Local"),@"C:\ProgramData"})foreach(var name in new[]{"miniconda3","anaconda3","miniforge3"})
        {
            var manager=Path.Combine(root,name);Prefix(manager);foreach(var env in Children(Path.Combine(manager,"envs")))Prefix(env);
        }
        var registry=Path.Combine(home,".conda","environments.txt");if(File.Exists(registry))foreach(var line in File.ReadLines(registry))Prefix(line.Trim());
        foreach(var directory in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries))Prefix(directory);
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Python\PythonCore"))foreach(var version in key?.GetSubKeyNames()??[]){using var install=key!.OpenSubKey(version+@"\InstallPath");var directory=install?.GetValue("")?.ToString();if(directory!=null)Prefix(directory);}
        using(var key=Registry.LocalMachine.OpenSubKey(@"Software\Python\PythonCore"))foreach(var version in key?.GetSubKeyNames()??[]){using var install=key!.OpenSubKey(version+@"\InstallPath");var directory=install?.GetValue("")?.ToString();if(directory!=null)Prefix(directory);}
        var visited=0;var ignored=new HashSet<string>(["models","outputs","cache","node_modules","site-packages",".git","Lib","downloads","dist"],StringComparer.OrdinalIgnoreCase);
        void Scan(string root,int depth){token.ThrowIfCancellationRequested();if(depth>3||visited++>700)return;foreach(var name in new[]{".venv","venv","env","python312"})Prefix(Path.Combine(root,name));foreach(var child in Children(root)){if(ignored.Contains(Path.GetFileName(child)))continue;try{if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0)Scan(child,depth+1);}catch(IOException){}catch(UnauthorizedAccessException){}}}
        Scan(settings.Workspace,0);Scan(Path.Combine(home,".virtualenvs"),0);Scan(Path.Combine(home,".pyenv","pyenv-win","versions"),0);
        foreach(var root in new[]{"Documents","Desktop","Projects","Developer"})Scan(Path.Combine(home,root),0);
        return found.Take(40).ToArray();
    }
    public static async Task<EnvironmentCandidate> Probe(string python,string workspace,CancellationToken token,string backend="auto",int timeoutSeconds=90)
    {
        var folder=Path.Combine(workspace,"setup","probes",Guid.NewGuid().ToString("N"));var request=Path.Combine(folder,"request.json");Files.Write(request,new{protocol=1,backend});
        using var bridge=new BridgeClient();var ready=false;var detail="";var gate=new object();EnvironmentCandidate? metadata=null;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var executable=new FileInfo(python);var length=executable.Length;var modified=executable.LastWriteTimeUtc.Ticks;
            var code=await bridge.RunAsync(python,"check",request,Path.Combine(workspace,"tmp"),e=>{lock(gate)File.AppendAllText(Path.Combine(folder,"probe.ndjson"),JsonSerializer.Serialize(e)+Environment.NewLine);var type=e.GetProperty("type").GetString();if(type=="ready"){ready=true;detail=e.GetProperty("device").GetString()+" · "+e.GetProperty("torch").GetString();metadata=ReadCandidate(python,e);}else if(type=="error")detail=e.GetProperty("message").GetString()??"";},timeout.Token);
            executable.Refresh();
            if(!executable.Exists||length!=executable.Length||modified!=executable.LastWriteTimeUtc.Ticks)return new(python,false,"The Python executable changed during verification. Check this environment again.");
            return (metadata??new EnvironmentCandidate(python,false,"")) with{Ready=ready&&code==0,Detail=detail.Length>0?detail:$"Python exited without a ready event (code {code}).",PythonLength=length,PythonModifiedUtcTicks=modified};
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(OperationCanceledException){return new(python,false,$"Environment check timed out after {timeoutSeconds} seconds.");}
        catch(Exception e){return new(python,false,e.Message);}
    }
    public static bool CandidateIsCurrent(EnvironmentCandidate candidate,string backend)
    {
        if(!candidate.Ready||candidate.Backend!=backend||candidate.PythonLength<=0)return false;
        try
        {
            var file=new FileInfo(candidate.Path);
            return file.Exists&&file.Length==candidate.PythonLength&&file.LastWriteTimeUtc.Ticks==candidate.PythonModifiedUtcTicks;
        }
        catch(IOException){return false;}
        catch(UnauthorizedAccessException){return false;}
    }
    internal static EnvironmentCandidate ReadCandidate(string python,JsonElement value)
    {
        string Text(string key)=>value.TryGetProperty(key,out var item)?item.GetString()??"":"";
        long Number(string key)=>value.TryGetProperty(key,out var item)&&item.TryGetInt64(out var result)?result:0;
        var packages=value.TryGetProperty("packages",out var items)?items.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetString()??""):new Dictionary<string,string>();
        return new(python,true,Text("device")+" · "+Text("torch")){PythonVersion=Text("python"),TorchVersion=Text("torch"),HipVersion=Text("hip"),CudaVersion=Text("cuda"),Backend=Text("backend") is "cuda"?"cuda":"amd_rocm",GpuFree=Number("gpuFree"),GpuTotal=Number("gpuTotal"),RamAvailable=Number("ramAvailable"),RamTotal=Number("ramTotal"),Device=Text("device"),Packages=packages};
    }
    public static Task<List<EnvironmentCandidate>> Search(AppSettings settings,IProgress<SetupProgress> progress,CancellationToken token)=>Task.Run(async()=>
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(EnvironmentSearchTimeout);
        var searchToken=budget.Token;
        var results=new List<EnvironmentCandidate>();
        var queued=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths=new Queue<string>();
        var pending=new List<Task<EnvironmentCandidate>>();
        void Enqueue(IEnumerable<string> candidates)
        {
            foreach(var path in candidates)if(File.Exists(path)&&queued.Add(path))paths.Enqueue(path);
        }
        async Task<EnvironmentCandidate> CheckPath(string path)
        {
            try{return await Probe(path,settings.Workspace,searchToken,settings.Backend,30);}
            catch(OperationCanceledException){return new(path,false,"Environment search stopped before this check finished. Choose this Python program to check it again.");}
        }
        Enqueue(PreferredPythonPaths(settings));
        Task<string[]>? discovery=Task.Run(()=>PythonPaths(settings,searchToken).ToArray(),searchToken);
        try
        {
            while(paths.Count>0||pending.Count>0||discovery!=null)
            {
                searchToken.ThrowIfCancellationRequested();
                while(paths.Count>0&&pending.Count<2)pending.Add(CheckPath(paths.Dequeue()));
                var waiting=pending.Cast<Task>().ToList();
                if(discovery!=null)waiting.Add(discovery);
                var completed=await Task.WhenAny(waiting).WaitAsync(searchToken);
                if(completed==discovery)
                {
                    try{Enqueue(await discovery!);}
                    catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        progress.Report(new("Searching local environments",Log:"Additional environment discovery could not finish: "+error.Message));
                    }
                    discovery=null;
                    continue;
                }
                var probe=(Task<EnvironmentCandidate>)completed;
                pending.Remove(probe);
                var candidate=await probe;results.Add(candidate);
                progress.Report(new("Searching local environments",$"{results.Count} / {queued.Count}",Candidate:candidate));
            }
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested)
        {
            progress.Report(new("Environment search time limit reached"));
        }
        finally{budget.Cancel();}
        token.ThrowIfCancellationRequested();
        return results.OrderByDescending(p=>p.Ready).ToList();
    },token).WaitAsync(EnvironmentSearchTimeout+TimeSpan.FromSeconds(2),token);
    public static Dictionary<string,string> Models(AppSettings settings,CancellationToken token)
    {
        var roots=new List<string>{settings.Model,settings.T2IDirectory,settings.I2IDirectory,Path.Combine(settings.Workspace,"models")};
        var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.AddRange([Path.Combine(home,".cache","huggingface","hub"),Path.Combine(home,".cache","modelscope","hub"),Path.Combine(home,".cache","modelscope","hub","models")]);
        foreach(var name in new[]{"HF_HOME","HUGGINGFACE_HUB_CACHE","MODELSCOPE_CACHE"})if(Environment.GetEnvironmentVariable(name) is {} value)roots.Add(value);
        var found=new Dictionary<string,string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var visited=0;
        void Scan(string root,int depth)
        {
            token.ThrowIfCancellationRequested();if(depth>5||visited++>1200||string.IsNullOrWhiteSpace(root)||!Directory.Exists(root)||!seen.Add(root))return;
            foreach(var key in new[]{"turbo","t2i","i2i"})if(!found.ContainsKey(key)&&ModelValidation.Ready(root,key))found[key]=root;
            if(found.Values.Contains(root))return;
            try{foreach(var child in Directory.EnumerateDirectories(root)){if(Path.GetFileName(child) is "blobs" or ".git" or "outputs" or ".lightx2v-downloads")continue;Scan(child,depth+1);}}catch(UnauthorizedAccessException){}catch(IOException){}
        }
        foreach(var root in roots)Scan(root,0);return found;
    }
}
public static class ModelValidation
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, long Modified)> validatedTokenizers = new(StringComparer.OrdinalIgnoreCase);

    public static bool Ready(string directory, string key)
    {
        if (!ModelCatalog.Models.TryGetValue(key, out var model)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(directory)) return false;
            foreach (var relative in model.RequiredFiles)
            {
                var path = ChildPath(directory, relative);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) return false;
                if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
                if (relative == "processor/tokenizer.json" && validatedTokenizers.TryGetValue(path, out var cached) && cached == stamp)
                    continue;
                using var stream = File.OpenRead(path);
                using var json = JsonDocument.Parse(stream);
                if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
                if (model.JsonProperties.TryGetValue(relative, out var expected))
                    foreach (var (name, value) in expected)
                        if (!json.RootElement.TryGetProperty(name, out var actual) || actual.GetString() != value) return false;
                if (relative == "processor/tokenizer.json")
                {
                    // This only caches a lightweight format check. Explicit repair still hashes every file.
                    info.Refresh();
                    if (stamp != (info.Length, info.LastWriteTimeUtc.Ticks)) return false;
                    if (validatedTokenizers.Count >= 64) validatedTokenizers.Clear();
                    validatedTokenizers[path] = stamp;
                }
            }
            if (model.SampleSigmas.Length > 0)
            {
                using var index = JsonDocument.Parse(File.ReadAllText(ChildPath(directory, "model_index.json")));
                var sigmas = index.RootElement.GetProperty("sample_sigmas");
                if (sigmas.GetArrayLength() != model.SampleSigmas.Length) return false;
                for (var i = 0; i < model.SampleSigmas.Length; i++)
                    if (Math.Abs(sigmas[i].GetDouble() - model.SampleSigmas[i]) > 0.000001) return false;
            }
            foreach (var (component, layout) in model.WeightComponents)
                if (!WeightComponentReady(ChildPath(directory, component), layout)) return false;
            if (model.ModelFile.Length > 0 && !Gguf(ChildPath(directory, model.ModelFile))) return false;
            return model.ProjectorFiles.Length == 0 || model.ProjectorFiles.Any(file => Gguf(ChildPath(directory, file)));
        }
        catch (Exception)
        {
            return false;
        }
    }

    static string ChildPath(string directory, string relative)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A model file points outside its component directory.");
        return path;
    }

    static bool WeightComponentReady(string directory, ModelWeightComponent layout)
    {
        var indexPath = ChildPath(directory, layout.Index);
        if (!File.Exists(indexPath)) return Safetensors(ChildPath(directory, layout.File));
        using var index = JsonDocument.Parse(File.ReadAllText(indexPath));
        var weights = index.RootElement.GetProperty("weight_map");
        var files = weights.EnumerateObject().Select(weight => weight.Value.GetString()!).Distinct().ToArray();
        return files.Length > 0 && files.All(file => file.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
            && Safetensors(ChildPath(directory, file)));
    }

    public static bool Gguf(string path)
    {
        if (!File.Exists(path)) return false;
        var size = new FileInfo(path).Length;
        if (size < ModelCatalog.MinimumGgufBytes) return false;
        var name = Path.GetFileName(path);
        foreach (var model in ModelCatalog.Models.Values)
            if (model.ExpectedGgufSizes.TryGetValue(name, out var expected) && size != expected) return false;
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        return stream.Read(magic) == 4 && magic.SequenceEqual("GGUF"u8);
    }

    static bool Safetensors(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 10) return false;
        using var reader = new BinaryReader(file);
        var headerLength = reader.ReadUInt64();
        if (headerLength > 64 * 1024 * 1024 || headerLength + 8 > (ulong)file.Length) return false;
        using var json = JsonDocument.Parse(reader.ReadBytes((int)headerLength));
        var dataLength = file.Length - 8 - (long)headerLength;
        long lastOffset = 0;
        var hasTensor = false;
        foreach (var item in json.RootElement.EnumerateObject())
        {
            if (item.Name == "__metadata__") continue;
            var tensor = item.Value;
            if (string.IsNullOrEmpty(tensor.GetProperty("dtype").GetString())) return false;
            if (tensor.GetProperty("shape").EnumerateArray().Any(dimension => dimension.GetInt64() < 0)) return false;
            var offsets = tensor.GetProperty("data_offsets");
            if (offsets.GetArrayLength() != 2) return false;
            var first = offsets[0].GetInt64();
            var last = offsets[1].GetInt64();
            if (first < 0 || last < first || last > dataLength) return false;
            hasTensor |= last > first;
            lastOffset = Math.Max(lastOffset, last);
        }
        return hasTensor && lastOffset == dataLength;
    }
}
