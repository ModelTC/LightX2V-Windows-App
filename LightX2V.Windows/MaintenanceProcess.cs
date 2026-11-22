using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LightX2V.Windows;

internal static class MaintenanceProcess
{
    static string JobRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightX2V-maintenance");

    internal static string NewJobDirectory()
    {
        var job = Path.Combine(JobRoot, Guid.NewGuid().ToString("N"));
        ValidateJob(job);
        Directory.CreateDirectory(job);
        return job;
    }

    internal static string Launch<T>(string job, PackageLayout layout, string mode, T plan)
    {
        ValidateJob(job);
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(layout.Manifest));
            var helper = Path.Combine(job, "helper");
            Directory.CreateDirectory(helper);
            foreach (var entry in manifest.RootElement.GetProperty("sha256").EnumerateObject())
            {
                var source = AppMaintenance.Normalize(Path.Combine(layout.Root, entry.Name));
                var target = AppMaintenance.Normalize(Path.Combine(helper, entry.Name));
                if (!AppMaintenance.Contains(layout.Root, source) || !AppMaintenance.Contains(helper, target))
                    throw new InvalidDataException("Invalid packaged file path.");
                var bytes = File.ReadAllBytes(source);
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Packaged application integrity check failed: " + entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }
            var planFile = Path.Combine(job, "plan.json");
            Files.Write(planFile, plan);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(planFile)));
            var start = new ProcessStartInfo(Path.Combine(helper, layout.Executable)) { UseShellExecute = false, WorkingDirectory = job };
            foreach (var arg in new[] { mode, planFile, hash }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new IOException("Could not start the maintenance helper.");
            return job;
        }
        catch
        {
            DeleteJob(job);
            throw;
        }
    }

    internal static T ReadPlan<T>(string file, string hash) where T : class
    {
        var bytes = File.ReadAllBytes(file);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.Ordinal))
            throw new InvalidDataException("The confirmed maintenance plan changed.");
        return JsonSerializer.Deserialize<T>(bytes, Files.Json)
            ?? throw new InvalidDataException("The maintenance plan is missing.");
    }

    internal static async Task WaitForParentAsync(int pid, long started)
    {
        Process parent;
        try { parent = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; } // The original application has already exited.
        using (parent)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            if (parent.StartTime.ToUniversalTime().Ticks != started)
                throw new IOException("The application process identity changed.");
            await parent.WaitForExitAsync(timeout.Token);
        }
    }

    internal static void DeleteJob(string job)
    {
        try
        {
            ValidateJob(job);
            if (Directory.Exists(job)) Directory.Delete(job, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine("Could not remove the maintenance job: " + error);
        }
    }

    internal static void ScheduleCleanup(string planFile)
    {
        var job = ValidatePlanFile(planFile);
        var helper = Path.Combine(job, "helper");
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(helper));
        var script = "$ErrorActionPreference='Stop'; $target=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedPath + "')); " +
            "Wait-Process -Id " + Environment.ProcessId + " -Timeout 60 -ErrorAction SilentlyContinue; if(Get-Process -Id " + Environment.ProcessId + " -ErrorAction SilentlyContinue){exit 1}; Remove-Item -LiteralPath $target -Recurse -Force";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        using var cleanup = Process.Start(start);
    }

    internal static string ValidatePlanFile(string planFile)
    {
        var file = AppMaintenance.Normalize(planFile);
        var job = Path.GetDirectoryName(file)!;
        if (!string.Equals(Path.GetFileName(file), "plan.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid maintenance plan filename.");
        ValidateJob(job);
        AppMaintenance.ValidatePath(file);
        if ((File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The maintenance plan is not a regular file.");
        return job;
    }

    static void ValidateJob(string path)
    {
        var job = AppMaintenance.Normalize(path);
        if (!string.Equals(Path.GetDirectoryName(job), JobRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(job), "N", out _))
            throw new InvalidDataException("Invalid maintenance job directory.");
        AppMaintenance.ValidatePath(job);
    }
}
