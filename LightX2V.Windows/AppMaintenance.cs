using System.Diagnostics;
using System.Text.Json;

namespace LightX2V.Windows;

public sealed record UninstallItem(string Path,string Group,bool External=false,List<string>? Files=null,string? ShortcutTarget=null);
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
    public static UninstallPlan Discover(AppSettings settings,string preferencesRoot,string application) =>
        Discover(settings, preferencesRoot, application, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    internal static UninstallPlan Discover(AppSettings settings,string preferencesRoot,string application,string desktop)
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
            var owned = Targets(item).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var target in owned)ValidatePath(target);
            plan.Items.Add(item);
            if (Directory.Exists(desktop) && (File.GetAttributes(desktop) & FileAttributes.ReparsePoint) == 0)
            {
                var applications = new[] { layout.EntryPoint, layout.Executable }.Select(file => Normalize(Path.Combine(layout.Root, file)))
                    .Where(owned.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var shortcut in Directory.EnumerateFiles(desktop, "*.lnk"))
                {
                    var target = DesktopShortcuts.ApplicationTarget(shortcut);
                    if (target != null && applications.Contains(target))
                        plan.Items.Add(new(Normalize(shortcut), "application", ShortcutTarget: target));
                }
            }
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
        selected=selected.Where(i=>!selected.Any(other=>other!=i&&other.Files==null&&other.ShortcutTarget==null&&Contains(other.Path,i.Path))).ToList();
        return new(){Items=selected,Retained=retained,Application=discovered.Application,EntryPoint=discovered.EntryPoint,RemoveApplication=selected.Any(i=>Contains(i.Path,discovered.Application))};
    }
    public static async Task<string> LaunchAsync(UninstallPlan plan)
    {
        if(plan.Items.Count==0)throw new InvalidOperationException("Choose what to remove first.");
        plan.Language=L10n.Current.Language;
        return await Task.Run(()=>
        {
            var layout=PackageLayout.Find(AppContext.BaseDirectory)??throw new InvalidDataException("A packaged application is required for maintenance.");
            var job = MaintenanceProcess.NewJobDirectory();
            if (plan.Items.Any(item => Contains(item.Path, job)))
            {
                MaintenanceProcess.DeleteJob(job);
                throw new InvalidDataException("The maintenance directory overlaps the uninstall plan.");
            }
            using var parent = Process.GetCurrentProcess();
            plan.ParentPid = parent.Id;
            plan.ParentStarted = parent.StartTime.ToUniversalTime().Ticks;
            return MaintenanceProcess.Launch(job, layout, "--maintenance", plan);
        });
    }
    public static async Task ExecuteAsync(string file,string hash)
    {
        var plan = await BeginAsync(file, hash);
        await ExecuteAsync(plan, new UninstallProgress(Path.GetDirectoryName(file)!));
    }

    internal static async Task<UninstallPlan> BeginAsync(string file, string hash)
    {
        var plan = MaintenanceProcess.ReadPlan<UninstallPlan>(file, hash);
        L10n.Current.Use(plan.Language);
        if (plan.EntryPoint is not ("LightX2V.exe" or "LightX2V.Windows.exe"))
            throw new InvalidDataException("Invalid restart entry point.");
        await MaintenanceProcess.WaitForParentAsync(plan.ParentPid, plan.ParentStarted);
        return plan;
    }

    internal static async Task ExecuteAsync(UninstallPlan plan, UninstallProgress progress)
    {
        var entries = plan.Items.Select(item => (Item: item, Targets: Targets(item).ToList())).ToList();
        progress.SetSelectedTargets(entries.SelectMany(entry => entry.Targets));
        var applicationPaths = new[] { plan.EntryPoint, "LightX2V.Windows.exe", Path.Combine("app", "LightX2V.Windows.exe") }
            .Select(entry => Normalize(Path.Combine(plan.Application, entry))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, targets) in entries)
        {
            if (item.ShortcutTarget != null &&
                (!plan.RemoveApplication || item.Group != "application" || item.Files != null ||
                !applicationPaths.Contains(item.ShortcutTarget) ||
                !entries.Any(entry => entry.Item.Files != null && entry.Item.Group == "application" && Normalize(entry.Item.Path).Equals(Normalize(plan.Application), StringComparison.OrdinalIgnoreCase) &&
                    entry.Targets.Contains(item.ShortcutTarget, StringComparer.OrdinalIgnoreCase))))
                throw new InvalidDataException("A shortcut target is outside the installed application's file list.");
            foreach (var target in targets)
            {
                if(plan.Retained.Any(p=>Contains(target,p)||Contains(p,target)))throw new InvalidDataException("The uninstall plan overlaps retained data.");
                ValidateTree(target);
            }
        }

        // A package lock should stop removal before touching settings and workspace data.
        foreach (var (item, targets) in entries.OrderBy(entry => entry.Item.Files == null))
        {
            if (item.ShortcutTarget == null)
                await DeleteItemAsync(item, targets, plan.EntryPoint, progress);
            else
                await DeleteFileAsync(item.Path, progress, item.ShortcutTarget);
        }
        progress.Complete();
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
    static async Task DeleteItemAsync(UninstallItem item, List<string> targets, string entryPoint, UninstallProgress progress)
    {
        if (item.Files == null)
        {
            await DeleteTreeAsync(item.Path, progress);
            return;
        }
        // Keep the launcher, payload entry point and manifest until payload deletion succeeds.
        var launcher = Normalize(Path.Combine(item.Path, entryPoint));
        foreach (var target in targets.OrderBy(target => Path.GetFileName(target).Equals("build-manifest.json", StringComparison.OrdinalIgnoreCase) ? 2 :
            target.Equals(launcher, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(target).Equals("LightX2V.Windows.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            await DeleteFileAsync(target, progress);
        var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Normalize(item.Path)};
        foreach(var target in targets)
            for(var parent=Path.GetDirectoryName(target);parent!=null&&Contains(item.Path,parent);parent=Path.GetDirectoryName(parent))parents.Add(parent);
        foreach(var parent in parents.OrderByDescending(path=>path.Length))
            if(Directory.Exists(parent)&&!Directory.EnumerateFileSystemEntries(parent).Any())DeleteDirectory(parent, progress);
    }

    static async Task DeleteFileAsync(string path, UninstallProgress progress, string? shortcutTarget = null)
    {
        int[] retryDelays = [150, 350, 700, 1400];
        for (var attempt = 0; ; attempt++)
        {
            ValidatePath(path);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("An uninstall file became a link or directory: " + path);
            if (shortcutTarget != null && !shortcutTarget.Equals(DesktopShortcuts.ApplicationTarget(path), StringComparison.OrdinalIgnoreCase))
            {
                progress.PreserveShortcut(path);
                return;
            }
            try
            {
                File.Delete(path);
                progress.Removed(path);
                return;
            }
            catch (Exception error) when (attempt < retryDelays.Length &&
                (error is IOException or UnauthorizedAccessException) && (error.HResult & 0xFFFF) is 5 or 32 or 33)
            {
                await Task.Delay(retryDelays[attempt]);
            }
        }
    }

    static async Task DeleteTreeAsync(string path, UninstallProgress progress)
    {
        if(!File.Exists(path)&&!Directory.Exists(path))return;
        ValidatePath(path);
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("A directory link appeared during uninstall: "+path);
        if (!Directory.Exists(path))
        {
            await DeleteFileAsync(path, progress);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) await DeleteTreeAsync(child, progress);
        DeleteDirectory(path, progress);
    }

    static void DeleteDirectory(string path, UninstallProgress progress)
    {
        ValidatePath(path);
        Directory.Delete(path);
        progress.Removed(path);
    }
}
