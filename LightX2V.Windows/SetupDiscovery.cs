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
    public static IEnumerable<string> PythonPaths(AppSettings settings)
    {
        var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string value){try{if(!value.Contains("WindowsApps",StringComparison.OrdinalIgnoreCase)&&Path.IsPathFullyQualified(value)&&File.Exists(value)&&value.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))found.Add(Path.GetFullPath(value));}catch(IOException){}}
        void Prefix(string value){Add(Path.Combine(value,"python.exe"));Add(Path.Combine(value,"Scripts","python.exe"));}
        IEnumerable<string> Children(string root){try{return Directory.GetDirectories(root);}catch{return [];}}
        if(settings.Python.Length>0)Add(settings.Python);
        foreach(var backend in new[]{"amd_rocm","cuda"})Add(SetupInstaller.ManagedPython(settings.Workspace,backend));
        var legacy=AppSettings.FindBackend();if(legacy.Length>0)Prefix(Path.Combine(legacy,"python312"));
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
        void Scan(string root,int depth){if(depth>3||visited++>700)return;foreach(var name in new[]{".venv","venv","env","python312"})Prefix(Path.Combine(root,name));foreach(var child in Children(root)){if(ignored.Contains(Path.GetFileName(child)))continue;try{if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0)Scan(child,depth+1);}catch(IOException){}}}
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
            var code=await bridge.RunAsync(python,"check",request,Path.Combine(workspace,"tmp"),e=>{lock(gate)File.AppendAllText(Path.Combine(folder,"probe.ndjson"),JsonSerializer.Serialize(e)+Environment.NewLine);var type=e.GetProperty("type").GetString();if(type=="ready"){ready=true;detail=e.GetProperty("device").GetString()+" · "+e.GetProperty("torch").GetString();metadata=ReadCandidate(python,e);}else if(type=="error")detail=e.GetProperty("message").GetString()??"";},timeout.Token);
            return (metadata??new EnvironmentCandidate(python,false,"")) with{Ready=ready&&code==0,Detail=detail.Length>0?detail:$"Python exited without a ready event (code {code})."};
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(OperationCanceledException){return new(python,false,$"Environment check timed out after {timeoutSeconds} seconds.");}
        catch(Exception e){return new(python,false,e.Message);}
    }
    internal static EnvironmentCandidate ReadCandidate(string python,JsonElement value)
    {
        string Text(string key)=>value.TryGetProperty(key,out var item)?item.GetString()??"":"";
        long Number(string key)=>value.TryGetProperty(key,out var item)&&item.TryGetInt64(out var result)?result:0;
        var packages=value.TryGetProperty("packages",out var items)?items.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetString()??""):new Dictionary<string,string>();
        return new(python,true,Text("device")+" · "+Text("torch")){PythonVersion=Text("python"),TorchVersion=Text("torch"),HipVersion=Text("hip"),CudaVersion=Text("cuda"),Backend=Text("backend") is "cuda"?"cuda":"amd_rocm",GpuFree=Number("gpuFree"),GpuTotal=Number("gpuTotal"),RamAvailable=Number("ramAvailable"),RamTotal=Number("ramTotal"),Device=Text("device"),Packages=packages};
    }
    public static async Task<List<EnvironmentCandidate>> Search(AppSettings settings,IProgress<SetupProgress> progress,CancellationToken token)
    {
        var paths=await Task.Run(()=>PythonPaths(settings).ToArray(),token);var results=new List<EnvironmentCandidate>();var done=0;
        foreach(var batch in paths.Chunk(2))
        {
            token.ThrowIfCancellationRequested();var candidates=await Task.WhenAll(batch.Select(p=>Probe(p,settings.Workspace,token,settings.Backend)));results.AddRange(candidates);done+=batch.Length;
            progress.Report(new("Searching local environments",$"{done} / {paths.Length}",paths.Length>0?100.0*done/paths.Length:100));
        }
        return results.OrderByDescending(p=>p.Ready).ThenBy(p=>p.Path).ToList();
    }
    public static Dictionary<string,string> Models(AppSettings settings,CancellationToken token)
    {
        var roots=new List<string>{settings.Model,settings.T2IDirectory,settings.I2IDirectory,Path.Combine(settings.Workspace,"models")};
        var old=AppSettings.Detect();roots.AddRange([old.Model,old.T2IDirectory,old.I2IDirectory]);
        var legacy=AppSettings.FindBackend();if(legacy.Length>0)roots.Add(Path.Combine(legacy,"output","app-models"));
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
    public static bool Ready(string directory,string key)
    {
        try
        {
            if(!Path.IsPathFullyQualified(directory))return false;
            if(key!="turbo")
            {
                var name="Qwen-Image-2.1-PE-"+key.ToUpperInvariant();var system=Path.Combine(directory,"system_prompt.txt");
                if(!Gguf(Path.Combine(directory,name+"-Q8_0.gguf"))||!File.Exists(system)||new FileInfo(system).Length==0)return false;
                return key!="i2i"||Gguf(Path.Combine(directory,"mmproj-"+name+"-BF16.gguf"))||Gguf(Path.Combine(directory,"mmproj-"+name+"-F16.gguf"));
            }
            using var index=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"model_index.json")));
            if(!index.RootElement.TryGetProperty("sample_sigmas",out var sigmas)||sigmas.GetArrayLength()!=8)return false;
            foreach(var name in new[]{"transformer","text_encoder","vae","scheduler","processor"})if(!Directory.Exists(Path.Combine(directory,name)))return false;
            foreach(var component in new[]{"transformer","text_encoder","vae"})
            {
                var root=Path.Combine(directory,component);var files=Directory.GetFiles(root,"*.safetensors");if(files.Length==0)return false;
                foreach(var file in files)if(!Safetensors(file))return false;
                foreach(var path in Directory.GetFiles(root,"*.index.json"))
                {
                    using var map=JsonDocument.Parse(File.ReadAllText(path));
                    if(map.RootElement.TryGetProperty("weight_map",out var weights))foreach(var weight in weights.EnumerateObject())if(!File.Exists(Path.Combine(root,weight.Value.GetString()!)))return false;
                }
            }
            return true;
        }
        catch{return false;}
    }
    public static bool Gguf(string path)
    {
        if(!File.Exists(path))return false;var size=new FileInfo(path).Length;
        if(path.EndsWith("-Q8_0.gguf",StringComparison.OrdinalIgnoreCase)&&size!=9527501536)return false;
        if(size<100*1024*1024)return false;
        using var f=File.OpenRead(path);Span<byte> magic=stackalloc byte[4];return f.Read(magic)==4&&magic.SequenceEqual("GGUF"u8);
    }
    static bool Safetensors(string path)
    {
        using var file=File.OpenRead(path);if(file.Length<10)return false;using var reader=new BinaryReader(file);var length=reader.ReadUInt64();if(length>64*1024*1024||length+8>(ulong)file.Length)return false;
        using var json=JsonDocument.Parse(reader.ReadBytes((int)length));long end=0;
        foreach(var item in json.RootElement.EnumerateObject())if(item.Name!="__metadata__")end=Math.Max(end,item.Value.GetProperty("data_offsets")[1].GetInt64());
        return end+8+(long)length==file.Length;
    }
}
