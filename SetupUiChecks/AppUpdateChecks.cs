using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using LightX2V.Windows;

internal static partial class Program
{
    static void AppUpdateChecks()
    {
        const string before = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string after = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var fixture = Path.Combine(root, "app-update");
        var app = Path.Combine(fixture, "app-before");
        var source = Path.Combine(fixture, "app-after");
        var stage = Path.Combine(fixture, "stage");
        var archive = Path.Combine(fixture, "update.zip");
        string Put(string directory, string relative, string content)
        {
            var path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        void Manifest(string directory, string commit, string obsoleteOrNew)
        {
            var sha256 = new Dictionary<string, string>
            {
                ["LightX2V.exe"] = Put(directory, "LightX2V.exe", commit),
                ["app/LightX2V.Windows.exe"] = Put(directory, "app/LightX2V.Windows.exe", commit),
                [obsoleteOrNew] = Put(directory, obsoleteOrNew, commit)
            };
            var manifest = new Dictionary<string, object>
            {
                ["layoutVersion"] = 2, ["target"] = "win-x64", ["entryPoint"] = "LightX2V.exe",
                ["appExecutable"] = "app/LightX2V.Windows.exe", ["sourceCommit"] = commit,
                ["sourceDirty"] = false, ["sha256"] = sha256
            };
            if (commit == before) manifest["version"] = "0.4.3";
            Files.Write(Path.Combine(directory, "app/build-manifest.json"), manifest);
        }
        Manifest(app, before, "app/obsolete.dat");
        Manifest(source, after, "docs/new.md");
        Put(app, "docs/user.txt", "preserve");
        ZipFile.CreateFromDirectory(source, archive);
        var rejected = false;
        try { AppUpdater.StageArchive(archive, stage, after, CancellationToken.None); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(stage), "The updater rejects an archive missing its release root before extraction");

        File.Delete(archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(path, "LightX2V-Windows-bbbbbbb/" + Path.GetRelativePath(source, path).Replace('\\', '/'));
        AppUpdater.StageArchive(archive, stage, after, CancellationToken.None);
        var plan = new UpdatePlan(app, before, after, "en", 0, 0);
        var launcher = Path.Combine(app, "LightX2V.exe");
        rejected = false;
        try { AppUpdater.Install(stage, plan, () => throw new IOException("Simulated restart failure")); }
        catch (IOException error) when (error.Message == "Simulated restart failure") { rejected = true; }
        Check(rejected && File.ReadAllText(launcher) == before &&
            File.Exists(Path.Combine(app, "app/obsolete.dat")) && !File.Exists(Path.Combine(app, "docs/new.md")),
            "An update restart failure restores the complete previous package");

        var started = false;
        AppUpdater.Install(stage, plan, () => started = true);
        Check(started && File.ReadAllText(launcher) == after &&
            !File.Exists(Path.Combine(app, "app/obsolete.dat")) && File.Exists(Path.Combine(app, "docs/new.md")),
            "Updating a numbered installation to a commit-named build removes obsolete application files");
        Check(File.ReadAllText(Path.Combine(app, "docs/user.txt")) == "preserve" &&
            AppUpdater.CurrentCommit(PackageLayout.Find(Path.Combine(app, "app"))) == after,
            "The update preserves unlisted files and records the new build");
    }
}
