using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace LightX2V.Windows;

public sealed record UninstallItem(string Path,string Group,bool External=false,List<string>? Files=null);
public sealed class UninstallPlan
{
    public List<UninstallItem> Items {get;set;}=[];
    public List<string> Retained {get;set;}=[];
    public string Language {get;set;}="en";
    public int ParentPid {get;set;}
    public long ParentStarted {get;set;}
    public string Application {get;set;}="";
    public bool RemoveApplication {get;set;}
    public string EntryPoint {get;set;}="LightX2V.Windows.exe";
}

public static class AppMaintenance
{
    public static string Normalize(string path)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Contains(string parent,string child)=>Normalize(child).Equals(Normalize(parent),StringComparison.OrdinalIgnoreCase)||Normalize(child).StartsWith(Normalize(parent)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    public static void ValidatePath(string path)
    {
        if(!Path.IsPathFullyQualified(path))throw new InvalidDataException("An uninstall path is not absolute.");
        var full=Normalize(path);
        var protectedPaths=new[]{Path.GetPathRoot(full)!,Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),Environment.GetFolderPath(Environment.SpecialFolder.Windows),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)};
        if(protectedPaths.Where(p=>p.Length>0).Any(p=>Contains(full,p)))throw new InvalidDataException("An uninstall path contains a protected folder: "+full);
        var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if(windows.Length>0&&Contains(windows,full))throw new InvalidDataException("System folders cannot be uninstalled: "+full);
        for(var current=new DirectoryInfo(full);current!=null;current=current.Parent)
            if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("An uninstall path contains a directory link: "+full);
    }
    static void ValidateTree(string path)
    {
        ValidatePath(path);
        if(!Directory.Exists(path)&&!File.Exists(path))return;
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Directory links cannot be removed by this plan: "+path);
        if(Directory.Exists(path))foreach(var child in Directory.EnumerateFileSystemEntries(path))ValidateTree(child);
    }
    static List<AppSettings> ReadRegistry(string path)
    {
        if(!File.Exists(path))return [];
        try
        {
            var records=Files.Read<List<AppSettings>>(path)??throw new InvalidDataException("Workspace registry is empty.");
            if(records.Any(s=>s is null||!Path.IsPathFullyQualified(s.Workspace)))throw new InvalidDataException("Workspace registry has an invalid path.");
            return records;
        }
        catch(Exception error) when(error is JsonException or InvalidDataException)
        {
            File.Copy(path,path+".corrupt-"+Guid.NewGuid().ToString("N"));Trace.WriteLine("Damaged workspace registry preserved: "+error.Message);return [];
        }
    }
    public static void RegisterWorkspace(string preferencesRoot,AppSettings settings)
    {
        var path=Path.Combine(preferencesRoot,"workspaces.json");
        var records=ReadRegistry(path);
        records.RemoveAll(s=>Normalize(s.Workspace).Equals(Normalize(settings.Workspace),StringComparison.OrdinalIgnoreCase));records.Add(settings.Clone());Files.Write(path,records);
    }
    public static UninstallPlan Discover(AppSettings settings,string preferencesRoot,string application)
    {
        var registry=Path.Combine(preferencesRoot,"workspaces.json");
        var records=ReadRegistry(registry);
        records.RemoveAll(s=>s.Workspace.Equals(settings.Workspace,StringComparison.OrdinalIgnoreCase));records.Add(settings);
        var layout=PackageLayout.Find(application);
        var plan=new UninstallPlan{Application=Normalize(layout?.Root??application),EntryPoint=layout?.EntryPoint??"LightX2V.Windows.exe"};
        void Add(string path,string group,bool external=false)
        {
            if(!Directory.Exists(path)&&!File.Exists(path))return;
            path=Normalize(path);ValidatePath(path);
            if(records.Any(s=>Normalize(s.Workspace)==path))throw new InvalidDataException("A resource points to an entire workspace: "+path);
            if(plan.Items.Any(i=>i.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))return;
            plan.Items.Add(new(path,group,external));
        }
        foreach(var record in records)
        {
            var root=Normalize(record.Workspace);ValidatePath(root);
            foreach(var folder in new[]{"models"})Add(Path.Combine(root,folder),"models");
            foreach(var folder in new[]{"outputs","imports","downloads","cache","tmp","logs","setup"})Add(Path.Combine(root,folder),"workspace");
            foreach(var (folder,marker) in new[]{("environments",".lightx2v-environment.json"),("runtimes",".lightx2v-source.json")})
            {
                var directory=Path.Combine(root,folder);
                if(Directory.Exists(directory))foreach(var child in Directory.EnumerateDirectories(directory))
                    if(File.Exists(Path.Combine(child,marker)))Add(child,"workspace");
            }
            foreach(var file in new[]{"workspace.json","workspace.json.bak"})Add(Path.Combine(root,file),"workspace");
            foreach(var model in new[]{record.Model,record.T2IDirectory,record.I2IDirectory}.Where(p=>!string.IsNullOrWhiteSpace(p)))
            {
                var external=!Contains(Path.Combine(root,"models"),model);
                if(external)
                {
                    // A configured folder must identify a supported model; never offer a broad arbitrary folder.
                    if(!new[]{"turbo","t2i","i2i"}.Any(key=>ModelCatalog.Get(key).RequiredFiles.Any(file=>File.Exists(Path.Combine(model,file)))))
                        throw new InvalidDataException("An external model folder cannot be identified safely: "+model);
                    Add(model,"models",true);
                }
            }
        }
        Add(preferencesRoot,"application");
        // Only a published app directory is eligible. A checkout/debug output is never an app target.
        if(layout!=null&&File.Exists(Path.Combine(layout.Root,layout.Executable)))
        {
            ValidatePath(layout.Root);
            using var manifest=JsonDocument.Parse(File.ReadAllText(layout.Manifest));
            var files=manifest.RootElement.GetProperty("sha256").EnumerateObject().Select(entry=>entry.Name).Append(Path.GetRelativePath(layout.Root,layout.Manifest)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var item=new UninstallItem(Normalize(layout.Root),"application",Files:files);
            foreach(var target in Targets(item))ValidatePath(target);
            plan.Items.Add(item);
        }
        foreach(var item in plan.Items)foreach(var target in Targets(item))if(records.Any(s=>Contains(target,s.Workspace)))throw new InvalidDataException("An uninstall item contains a workspace root: "+target);
        return plan;
    }
    public static UninstallPlan Select(UninstallPlan discovered,ISet<string> groups)
    {
        var selected=discovered.Items.Where(i=>groups.Contains(i.Group)).ToList();
        var retained=discovered.Items.Where(i=>!groups.Contains(i.Group)).SelectMany(Targets).ToList();
        foreach(var item in selected)
        {
            foreach(var target in Targets(item))
            {
                if(retained.Any(p=>Contains(target,p)||Contains(p,target)))throw new InvalidDataException("The selected folder contains files you chose to keep: "+target);
                ValidateTree(target);
            }
        }
        // Nested selected entries are represented by their exact selected ancestor once.
        selected=selected.Where(i=>!selected.Any(other=>other!=i&&other.Files==null&&Contains(other.Path,i.Path))).ToList();
        return new(){Items=selected,Retained=retained,Application=discovered.Application,EntryPoint=discovered.EntryPoint,RemoveApplication=selected.Any(i=>Contains(i.Path,discovered.Application))};
    }
    public static async Task<string> LaunchAsync(UninstallPlan plan)
    {
        if(plan.Items.Count==0)throw new InvalidOperationException("Choose what to remove first.");
        plan.Language=L10n.Current.Language;
        return await Task.Run(()=>
        {
            var job=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LightX2V-maintenance",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
            if(plan.Items.Any(i=>Contains(i.Path,job)))throw new InvalidDataException("The maintenance directory overlaps the uninstall plan.");
            var layout=PackageLayout.Find(AppContext.BaseDirectory)??throw new InvalidDataException("A packaged application is required for maintenance.");
            var app=layout.Root;
            using var manifest=JsonDocument.Parse(File.ReadAllText(layout.Manifest));
            var helper=Path.Combine(job,"helper");Directory.CreateDirectory(helper);
            foreach(var entry in manifest.RootElement.GetProperty("sha256").EnumerateObject())
            {
                var source=Normalize(Path.Combine(app,entry.Name));var target=Normalize(Path.Combine(helper,entry.Name));
                if(!Contains(app,source)||!Contains(helper,target))throw new InvalidDataException("Invalid packaged file path.");
                var bytes=File.ReadAllBytes(source);
                if(!Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.Value.GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Packaged application integrity check failed: "+entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllBytes(target,bytes);
            }
            using var parent=Process.GetCurrentProcess();plan.ParentPid=parent.Id;plan.ParentStarted=parent.StartTime.ToUniversalTime().Ticks;
            var file=Path.Combine(job,"plan.json");Files.Write(file,plan);var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            var start=new ProcessStartInfo(Path.Combine(helper,layout.Executable)){UseShellExecute=false,WorkingDirectory=job};
            foreach(var arg in new[]{"--maintenance",file,hash})start.ArgumentList.Add(arg);
            using var child=Process.Start(start)??throw new IOException("Could not start the maintenance helper.");
            return job;
        });
    }
    public static async Task ExecuteAsync(string file,string hash)
    {
        if(!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).Equals(hash,StringComparison.Ordinal))throw new InvalidDataException("The confirmed uninstall plan changed.");
        var plan=Files.Read<UninstallPlan>(file)??throw new InvalidDataException("Uninstall plan is missing.");
        L10n.Current.Use(plan.Language);
        try
        {
            using var parent=Process.GetProcessById(plan.ParentPid);
            if(parent.StartTime.ToUniversalTime().Ticks!=plan.ParentStarted)throw new IOException("The application process identity changed.");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));await parent.WaitForExitAsync(timeout.Token);
        }
        catch(ArgumentException){} // The original application has already exited.
        foreach(var item in plan.Items)
        {
            foreach(var target in Targets(item))
            {
                if(plan.Retained.Any(p=>Contains(target,p)||Contains(p,target)))throw new InvalidDataException("The uninstall plan overlaps retained data.");
                ValidateTree(target);
            }
        }
        foreach(var item in plan.Items)DeleteItem(item);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!,"result.txt"),"Uninstall completed.\n");
        if(plan.EntryPoint is not ("LightX2V.exe" or "LightX2V.Windows.exe"))throw new InvalidDataException("Invalid restart entry point.");
        if(!plan.RemoveApplication&&File.Exists(Path.Combine(plan.Application,plan.EntryPoint)))Process.Start(new ProcessStartInfo(Path.Combine(plan.Application,plan.EntryPoint)){UseShellExecute=true});
    }
    static IEnumerable<string> Targets(UninstallItem item)
    {
        ValidatePath(item.Path);
        if(item.Files==null){yield return item.Path;yield break;}
        foreach(var relative in item.Files)
        {
            if(string.IsNullOrWhiteSpace(relative)||Path.IsPathFullyQualified(relative))throw new InvalidDataException("Invalid application file in the uninstall manifest.");
            var target=Normalize(Path.Combine(item.Path,relative));
            if(target.Equals(Normalize(item.Path),StringComparison.OrdinalIgnoreCase)||!Contains(item.Path,target)||Directory.Exists(target))throw new InvalidDataException("An application file is not a contained regular file: "+target);
            yield return target;
        }
    }
    static void DeleteItem(UninstallItem item)
    {
        if(item.Files==null){DeleteTree(item.Path);return;}
        var targets=Targets(item).ToList();
        foreach(var target in targets)
        {
            ValidatePath(target);
            if(!File.Exists(target)&&!Directory.Exists(target))continue;
            if((File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0)throw new IOException("An application file became a link during uninstall: "+target);
            // A manifest file replaced by a directory must fail rather than expand the deletion scope.
            File.Delete(target);
        }
        var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Normalize(item.Path)};
        foreach(var target in targets)
            for(var parent=Path.GetDirectoryName(target);parent!=null&&Contains(item.Path,parent);parent=Path.GetDirectoryName(parent))parents.Add(parent);
        foreach(var parent in parents.OrderByDescending(path=>path.Length))
            if(Directory.Exists(parent)&&!Directory.EnumerateFileSystemEntries(parent).Any()){ValidatePath(parent);Directory.Delete(parent);}
    }
    public static void ScheduleHelperCleanup(string planFile)
    {
        var helper=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planFile))!,"helper");
        var encodedPath=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(helper));
        var script="$ErrorActionPreference='Stop'; $target=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+encodedPath+"')); "+
            "Wait-Process -Id "+Environment.ProcessId+" -Timeout 60 -ErrorAction SilentlyContinue; if(Get-Process -Id "+Environment.ProcessId+" -ErrorAction SilentlyContinue){exit 1}; Remove-Item -LiteralPath $target -Recurse -Force";
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(arg);
        using var cleanup=Process.Start(start);
    }
    static void DeleteTree(string path)
    {
        if(!File.Exists(path)&&!Directory.Exists(path))return;
        ValidatePath(path);
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("A directory link appeared during uninstall: "+path);
        if(Directory.Exists(path)){foreach(var child in Directory.EnumerateFileSystemEntries(path))DeleteTree(child);Directory.Delete(path);}
        else File.Delete(path);
    }
}
