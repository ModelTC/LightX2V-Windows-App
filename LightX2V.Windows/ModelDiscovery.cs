namespace LightX2V.Windows;

public sealed record ModelCandidate(string Key,string Path,bool Ready);
public sealed record ModelSearchProgress(string Stage,ModelCandidate? Candidate=null,int Visited=0,int Skipped=0);
public sealed record ModelSearchResult(IReadOnlyList<ModelCandidate> Candidates,int Visited,int Skipped,bool Complete);

public interface IModelDiscovery
{
    Task<ModelSearchResult> SearchAsync(AppSettings settings,IProgress<ModelSearchProgress> progress,CancellationToken token);
}

// Read-only discovery owns traversal and completeness checks. The caller owns selection and persistence.
public sealed class ModelDiscovery : IModelDiscovery
{
    readonly string[]? quickRoots,fixedRoots;
    public ModelDiscovery(IEnumerable<string>? quickRoots=null,IEnumerable<string>? fixedRoots=null)
    {this.quickRoots=quickRoots?.ToArray();this.fixedRoots=fixedRoots?.ToArray();}
    public Task<ModelSearchResult> SearchAsync(AppSettings settings,IProgress<ModelSearchProgress> progress,CancellationToken token)=>Task.Run(()=>Search(settings,progress,token),token);
    public static IEnumerable<string> PreferredRoots(AppSettings settings)
    {
        yield return settings.Model;yield return settings.T2IDirectory;yield return settings.I2IDirectory;
        yield return Path.Combine(settings.Workspace,"models");
        var parent=Path.GetDirectoryName(settings.Workspace.TrimEnd(Path.DirectorySeparatorChar));
        if(parent!=null)yield return Path.Combine(parent,"Models");
        var app=AppContext.BaseDirectory;
        foreach(var root in new[]{app,Path.GetDirectoryName(app.TrimEnd(Path.DirectorySeparatorChar)),Path.GetDirectoryName(Path.GetDirectoryName(app.TrimEnd(Path.DirectorySeparatorChar)))})
            if(root!=null)yield return Path.Combine(root,"Models");
        var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach(var name in new[]{"HUGGINGFACE_HUB_CACHE","HF_HUB_CACHE","HF_HOME","MODELSCOPE_CACHE"})
            if(Environment.GetEnvironmentVariable(name) is {Length:>0} value)yield return value;
        yield return Path.Combine(home,".cache","huggingface","hub");
        yield return Path.Combine(home,".cache","modelscope","hub");
    }
    static IEnumerable<string> FixedDisks(string workspace)=>DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed&&d.IsReady)
        .OrderBy(d=>string.Equals(d.Name,Path.GetPathRoot(workspace),StringComparison.OrdinalIgnoreCase)?0:1)
        .ThenBy(d=>d.Name,StringComparer.OrdinalIgnoreCase).Select(d=>d.RootDirectory.FullName);
    public ModelSearchResult Search(AppSettings settings,IProgress<ModelSearchProgress>? progress,CancellationToken token,bool includeFixedDisks=true)
    {
        var found=new List<ModelCandidate>();var ready=new HashSet<string>();var checkedPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int visited=0,skipped=0;var last=System.Diagnostics.Stopwatch.StartNew();
        var ignored=new HashSet<string>(new[]{"$Recycle.Bin","System Volume Information","Recovery","Config.Msi",".git","node_modules","site-packages","blobs",".lightx2v-downloads"},StringComparer.OrdinalIgnoreCase);
        var protectedRoots=new[]{Environment.SpecialFolder.Windows,Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86}
            .Select(Environment.GetFolderPath).Where(p=>p.Length>0).Select(Path.GetFullPath).ToArray();
        bool AllFound()=>ready.Count==ModelCatalog.Models.Count;
        void Report(string stage,ModelCandidate? candidate=null)
        {if(candidate!=null||last.ElapsedMilliseconds>200){progress?.Report(new(stage,candidate,visited,skipped));last.Restart();}}
        void Check(string directory)
        {
            if(!checkedPaths.Add(directory))return;
            foreach(var (key,definition) in ModelCatalog.Models)
            {
                if(ready.Contains(key))continue;
                // Checking only recognizable roots avoids opening model metadata in every unrelated directory.
                if(!File.Exists(Path.Combine(directory,definition.RequiredFiles[0])))continue;
                var candidate=new ModelCandidate(key,directory,ModelValidation.Ready(directory,key));
                found.Add(candidate);if(candidate.Ready)ready.Add(key);
                Report(candidate.Ready?"Usable models found":"Incomplete model candidate",candidate);
            }
        }
        void Scan(string root,int? maxDepth,string stage)
        {
            token.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root))return;
            if(root.StartsWith(@"\\",StringComparison.Ordinal))return;
            try{if(new DriveInfo(Path.GetPathRoot(root)!).DriveType is DriveType.Network or DriveType.Removable)return;}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException){skipped++;return;}
            var pending=new SortedSet<(string Path,int Depth)>(Comparer<(string Path,int Depth)>.Create((a,b)=>StringComparer.OrdinalIgnoreCase.Compare(a.Path,b.Path)));pending.Add((Path.GetFullPath(root),0));
            var traversed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while(pending.Count>0&&!AllFound())
            {
                var current=pending.Min;pending.Remove(current);
                token.ThrowIfCancellationRequested();
                if(!traversed.Add(current.Path))continue;
                try
                {
                    var info=new DirectoryInfo(current.Path);if(!info.Exists)continue;
                    if((info.Attributes&FileAttributes.ReparsePoint)!=0)continue;
                    visited++;Check(current.Path);Report(stage);
                    if(maxDepth.HasValue&&current.Depth>=maxDepth.Value)continue;
                    foreach(var child in info.GetDirectories().OrderBy(d=>d.FullName,StringComparer.OrdinalIgnoreCase))
                        if(!ignored.Contains(child.Name)&&!protectedRoots.Any(p=>child.FullName.Equals(p,StringComparison.OrdinalIgnoreCase))&&(child.Attributes&FileAttributes.ReparsePoint)==0)
                            pending.Add((child.FullName,current.Depth+1));
                }
                catch(Exception error) when(error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {skipped++;Report(stage);}
            }
        }
        foreach(var root in quickRoots??PreferredRoots(settings).ToArray()){if(AllFound())break;Scan(root,6,"Searching known model locations");}
        if(includeFixedDisks&&!AllFound())foreach(var root in fixedRoots??FixedDisks(settings.Workspace).ToArray())
        {if(AllFound())break;Scan(root,null,"Searching local disks for models");}
        token.ThrowIfCancellationRequested();
        return new(found,visited,skipped,skipped==0);
    }
}
