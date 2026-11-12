using System.IO;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task MaintenanceProcessChecks()
    {
        var removable=Path.Combine(root,"disposable-model");Directory.CreateDirectory(removable);File.WriteAllText(Path.Combine(removable,"fixture.txt"),"Delete only this isolated fixture");
        var retained=Path.Combine(root,"retained.txt");File.WriteAllText(retained,"Keep this file");
        var application=Path.Combine(root,"disposable-application");Directory.CreateDirectory(application);
        var payload=Path.Combine(application,"app");Directory.CreateDirectory(payload);
        var owned=Path.Combine(application,"LightX2V.exe");
        File.WriteAllText(Path.Combine(payload,"LightX2V.Windows.exe"),"Non-executable payload fixture");File.WriteAllText(owned,"Non-executable application-removal fixture");
        var unknown=Path.Combine(application,"user-note.txt");File.WriteAllText(unknown,"Keep this unlisted file");
        Files.Write(Path.Combine(payload,"build-manifest.json"),new{layoutVersion=2,entryPoint="LightX2V.exe",appExecutable="app/LightX2V.Windows.exe",sha256=new Dictionary<string,string>{{"LightX2V.exe","fixture"},{"app/LightX2V.Windows.exe","fixture"}}});
        var discovered=AppMaintenance.Discover(AppSettings.Fresh(Path.Combine(root,"workspace")),Path.Combine(root,"absent-preferences"),payload);
        discovered.Items.Add(new(removable,"models"));var plan=AppMaintenance.Select(discovered,new HashSet<string>{"models","application"});plan.Retained.Add(retained);
        var job=await AppMaintenance.LaunchAsync(plan);
        await Task.Delay(500);
        Check(Directory.Exists(removable)&&File.Exists(owned)&&File.Exists(retained),"The packaged maintenance helper waits for its parent application to exit");
        Files.Write(Path.Combine(root,"maintenance-job.json"),new{job,removable,retained,owned,unknown});
        // The caller checks result.txt, exact deletion, retained bytes and helper cleanup after this process exits.
    }
}
