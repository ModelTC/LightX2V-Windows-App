using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LightX2V.Windows;

internal sealed record UpdateRelease(string Channel, string Commit, string Digest, long Size, Uri Download);

internal sealed record UpdatePlan(string Application, string CurrentCommit, string UpdateCommit,
    string Language, int ParentPid, long ParentStarted);

internal static partial class AppUpdater
{
    const string Repository = "ModelTC/LightX2V-Windows-App";
    const string AssetName = "LightX2V-APP-Windows-x64.zip";
    const string ManifestPath = "app/build-manifest.json";
    static readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static AppUpdater()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LightX2V-Windows-App");
        http.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
    }

    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"(?:^|;\s*)commit:([0-9a-f]{40})\z", RegexOptions.CultureInvariant)]
    private static partial Regex AssetCommitPattern();
    [GeneratedRegex(@"\A(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNamePattern();

    internal static string? CurrentCommit(PackageLayout? layout)
    {
        if (layout?.EntryPoint != "LightX2V.exe") return null;
        var manifest = JsonNode.Parse(File.ReadAllText(layout.Manifest));
        var commit = (string?)manifest?["sourceCommit"];
        return commit != null && CommitPattern().IsMatch(commit) && (bool?)manifest?["sourceDirty"] == false
            ? commit : null;
    }

    internal static async Task<(UpdateRelease Release, bool Available)> CheckAsync(string current, CancellationToken token)
    {
        var channel = "stable";
        var release = await GitHubAsync("releases/tags/stable", token, missingOk: true);
        if (release == null)
        {
            channel = "latest";
            release = await GitHubAsync("releases/tags/latest", token);
        }

        var expectedUrl = new Uri($"https://github.com/{Repository}/releases/download/{channel}/{AssetName}");
        var assets = release?["assets"]?.AsArray().Where(asset =>
            (string?)asset?["name"] == AssetName && (string?)asset?["state"] == "uploaded").ToArray();
        if ((string?)release?["tag_name"] != channel || (bool?)release?["draft"] != false ||
            (bool?)release?["prerelease"] != false || assets?.Length != 1)
            throw new InvalidDataException("The release is missing verified package metadata.");
        var asset = assets[0]!;
        var match = AssetCommitPattern().Match((string?)asset["label"] ?? "");
        var digest = (string?)asset["digest"] ?? "";
        var size = (long?)asset["size"] ?? 0;
        if (!match.Success || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !DigestPattern().IsMatch(digest[7..]) || size <= 0 ||
            (string?)asset["browser_download_url"] != expectedUrl.AbsoluteUri)
            throw new InvalidDataException("The release is missing verified package metadata.");
        var commit = match.Groups[1].Value;
        var tag = await GitHubAsync($"git/ref/tags/{channel}", token);
        if ((string?)tag?["object"]?["type"] != "commit" || (string?)tag?["object"]?["sha"] != commit)
            throw new InvalidDataException("The release tag does not match its package commit. Check again shortly.");
        var available = false;
        if (current != commit)
        {
            var comparison = await GitHubAsync($"compare/{current}...{commit}?per_page=1", token);
            available = (string?)comparison?["status"] == "ahead";
            if (!available && (string?)comparison?["status"] is not ("behind" or "identical"))
                throw new InvalidDataException("This build is outside the release history. Download the app from GitHub.");
        }
        return (new(channel, commit, digest[7..], size, expectedUrl), available);
    }

    static async Task<JsonNode?> GitHubAsync(string path, CancellationToken token, bool missingOk = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/{path}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, token);
        if (missingOk && response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(token);
        return await JsonNode.ParseAsync(content, cancellationToken: token);
    }

    internal static async Task PrepareAsync(PackageLayout layout, string current, UpdateRelease release,
        AppSettings settings, string language, IProgress<double> progress, CancellationToken token)
    {
        ValidateDestination(layout, settings);
        var job = MaintenanceProcess.NewJobDirectory();
        var launched = false;
        try
        {
            var archive = Path.Combine(job, "update.zip");
            await DownloadAsync(archive, release, progress, token);
            progress.Report(-1);
            await Task.Run(() =>
            {
                StageArchive(archive, Path.Combine(job, "package"), release.Commit, token);
                File.Delete(archive);
                using var parent = Process.GetCurrentProcess();
                var plan = new UpdatePlan(layout.Root, current, release.Commit, language,
                    parent.Id, parent.StartTime.ToUniversalTime().Ticks);
                token.ThrowIfCancellationRequested();
                MaintenanceProcess.Launch(job, layout, "--update", plan);
            }, token);
            launched = true;
        }
        finally
        {
            if (!launched) MaintenanceProcess.DeleteJob(job);
        }
    }

    static void ValidateDestination(PackageLayout layout, AppSettings settings)
    {
        AppMaintenance.ValidatePath(layout.Root);
        foreach (var path in new[] { settings.Workspace, settings.Python, settings.Repository, settings.Model,
                     settings.T2IDirectory, settings.I2IDirectory, AppSettings.PreferencesRoot() })
            if (Path.IsPathFullyQualified(path) && AppMaintenance.Contains(layout.Root, path))
                throw new InvalidOperationException("Move the app outside your workspace and component folders before updating.");
    }

    static async Task DownloadAsync(string path, UpdateRelease release, IProgress<double> progress, CancellationToken token)
    {
        using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException("The downloaded update does not match the release checksum.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(path);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        var clock = Stopwatch.StartNew();
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            received += read;
            if (received > release.Size) throw new InvalidDataException("The downloaded update does not match the release checksum.");
            digest.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            if (clock.ElapsedMilliseconds >= 100)
            {
                progress.Report((double)received / release.Size * 100);
                clock.Restart();
            }
        }
        if (received != release.Size || !DigestMatches(digest.GetHashAndReset(), release.Digest))
            throw new InvalidDataException("The downloaded update does not match the release checksum.");
    }

    internal static void StageArchive(string archive, string destination, string commit, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        var entries = zip.Entries.ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
        var entry = zip.Entries.SingleOrDefault(item =>
            item.FullName.EndsWith("/" + ManifestPath, StringComparison.Ordinal));
        if (entry == null || entry.Length > 2_000_000)
            throw new InvalidDataException("The update has an invalid package manifest.");
        using var manifest = entry.Open();
        var (version, hashes) = ReadManifest(JsonNode.Parse(manifest), commit, requireClean: true);
        var root = $"LightX2V-Windows-{version}";
        var prefix = entry.FullName[..^ManifestPath.Length];
        if (prefix != root + "/" && prefix != root + "-" + commit[..7] + "/" ||
            entries.Count != hashes.Count + 1 || hashes.Keys.Any(path => !entries.ContainsKey(prefix + path)))
            throw new InvalidDataException("The update contains unexpected package files.");

        foreach (var (relative, hash) in hashes)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entries[prefix + relative].ExtractToFile(path);
            VerifyHash(path, hash);
        }
        entry.ExtractToFile(Path.Combine(destination, ManifestPath));
    }

    static (string Version, Dictionary<string, string> Hashes) ReadManifest(JsonNode? manifest, string commit, bool requireClean)
    {
        var version = (string?)manifest?["version"] ?? "";
        if (!VersionPattern().IsMatch(version) || !CommitPattern().IsMatch(commit) ||
            (string?)manifest?["sourceCommit"] != commit || (requireClean && (bool?)manifest?["sourceDirty"] != false) ||
            (string?)manifest?["target"] != "win-x64" || (int?)manifest?["layoutVersion"] != 2 ||
            (string?)manifest?["entryPoint"] != "LightX2V.exe" || (string?)manifest?["appExecutable"] != "app/LightX2V.Windows.exe" ||
            manifest?["sha256"] is not JsonObject items)
            throw new InvalidDataException("The update has an invalid package manifest.");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, value) in items)
        {
            var digest = (string?)value ?? "";
            if (path.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase) || !ValidPayloadPath(path) ||
                !DigestPattern().IsMatch(digest) || !hashes.TryAdd(path, digest))
                throw new InvalidDataException("The update has an invalid package manifest.");
        }
        if (!hashes.ContainsKey("LightX2V.exe") || !hashes.ContainsKey("app/LightX2V.Windows.exe"))
            throw new InvalidDataException("The update has an invalid package manifest.");
        return (version, hashes);
    }

    static bool ValidPayloadPath(string path) =>
        (path == "LightX2V.exe" || path.StartsWith("app/", StringComparison.Ordinal) || path.StartsWith("docs/", StringComparison.Ordinal)) &&
        path.Split('/').All(part => part.Length > 0 && !part.EndsWith('.') && !part.EndsWith(' ') &&
            !part.Contains('~') && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !DeviceNamePattern().IsMatch(part));

    static bool DigestMatches(byte[] bytes, string digest) => Convert.ToHexString(bytes).Equals(digest, StringComparison.OrdinalIgnoreCase);

    static void VerifyHash(string file, string digest)
    {
        using var data = File.OpenRead(file);
        if (!DigestMatches(SHA256.HashData(data), digest))
            throw new InvalidDataException("An update file failed checksum verification: " + Path.GetFileName(file));
    }

    internal static async Task RunHelperAsync(string planFile, string hash)
    {
        var job = MaintenanceProcess.ValidatePlanFile(planFile);
        var package = Path.Combine(job, "package");
        try
        {
            var plan = MaintenanceProcess.ReadPlan<UpdatePlan>(planFile, hash);
            L10n.Current.Use(plan.Language);
            using var instance = new Mutex(false, "Local\\LightX2V.Windows.App");
            await MaintenanceProcess.WaitForParentAsync(plan.ParentPid, plan.ParentStarted);
            await Task.Run(() =>
            {
                bool ownsInstance;
                try { ownsInstance = instance.WaitOne(0); }
                catch (AbandonedMutexException) { ownsInstance = true; }
                if (!ownsInstance) throw new IOException("Close the running LightX2V app and try again.");
                try
                {
                    Install(package, plan, () =>
                    {
                        instance.ReleaseMutex();
                        ownsInstance = false;
                        instance.Dispose();
                        Restart(plan.Application);
                    });
                }
                finally { if (ownsInstance) instance.ReleaseMutex(); }
            });
        }
        finally
        {
            try { if (Directory.Exists(package)) Directory.Delete(package, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
        }
    }

    internal static void Install(string package, UpdatePlan plan, Action restart)
    {
        AppMaintenance.ValidatePath(plan.Application);
        AppMaintenance.ValidatePath(package);
        var currentManifest = Path.Combine(plan.Application, ManifestPath);
        var nextManifest = Path.Combine(package, ManifestPath);
        var (_, oldHashes) = ReadManifest(JsonNode.Parse(File.ReadAllText(currentManifest)), plan.CurrentCommit, requireClean: true);
        var (_, newHashes) = ReadManifest(JsonNode.Parse(File.ReadAllText(nextManifest)), plan.UpdateCommit, requireClean: true);
        var removed = oldHashes.Keys.Append(ManifestPath).ToArray();
        var added = newHashes.Keys.Append(ManifestPath).ToArray();
        foreach (var relative in removed.Concat(added).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.Combine(plan.Application, relative);
            AppMaintenance.ValidatePath(target);
            if (Directory.Exists(target) || IsReparsePoint(target) ||
                (File.Exists(target) && !oldHashes.ContainsKey(relative) && relative != ManifestPath))
                throw new IOException("An update file conflicts with an existing path: " + target);
        }

        // Keep both sets on the installation volume so each file move is atomic.
        var transaction = Path.Combine(plan.Application, ".lightx2v-update-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(transaction, "backup");
        var ready = Path.Combine(transaction, "ready");
        var saved = new List<string>();
        var installed = new List<string>();
        var directories = new List<string>();
        var recovered = true;
        try
        {
            foreach (var relative in added)
            {
                var source = Path.Combine(package, relative);
                AppMaintenance.ValidatePath(source);
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("An update file became a link.");
                var target = Path.Combine(ready, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                if (newHashes.TryGetValue(relative, out var digest)) VerifyHash(target, digest);
            }
            foreach (var relative in removed)
            {
                var source = Path.Combine(plan.Application, relative);
                if (!File.Exists(source)) continue;
                var target = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
                saved.Add(relative);
            }
            foreach (var relative in added)
            {
                var target = Path.Combine(plan.Application, relative);
                var directory = Path.GetDirectoryName(target)!;
                for (var missing = directory; !Directory.Exists(missing); missing = Path.GetDirectoryName(missing)!)
                    directories.Add(missing);
                Directory.CreateDirectory(directory);
                File.Move(Path.Combine(ready, relative), target);
                installed.Add(relative);
            }
            restart();
        }
        catch (Exception error)
        {
            try
            {
                foreach (var relative in installed.AsEnumerable().Reverse())
                    File.Delete(Path.Combine(plan.Application, relative));
                foreach (var relative in saved.AsEnumerable().Reverse())
                    File.Move(Path.Combine(backup, relative), Path.Combine(plan.Application, relative));
                foreach (var directory in directories.Distinct().OrderByDescending(path => path.Length))
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
            }
            catch (Exception rollbackError)
            {
                recovered = false;
                throw new IOException("Update rollback needs manual recovery. Backup location: " + backup,
                    new AggregateException(error, rollbackError));
            }
            throw;
        }
        finally
        {
            if (recovered && Directory.Exists(transaction))
            {
                try { Directory.Delete(transaction, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
            }
        }
    }

    static void Restart(string root)
    {
        var launcher = Path.Combine(root, "LightX2V.exe");
        SHChangeNotify(0x2000, 0x2005, launcher, IntPtr.Zero); // SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT
        using var process = Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = root })
            ?? throw new IOException("Could not restart the updated app.");
        if (process.WaitForExit(5000) && process.ExitCode != 0)
            throw new IOException("The updated launcher failed to start.");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string path, IntPtr unused);

    static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
