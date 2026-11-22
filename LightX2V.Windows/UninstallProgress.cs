using System.Diagnostics;

namespace LightX2V.Windows;

internal sealed class UninstallProgress(string job)
{
    readonly HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> preservedShortcuts = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<string> selected = [];

    internal void SetSelectedTargets(IEnumerable<string> targets) => selected = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal void Removed(string path) => removed.Add(path);
    internal void PreserveShortcut(string path) => preservedShortcuts.Add(path);

    internal void Complete()
    {
        var result = new List<string> { "Uninstall completed." };
        if (preservedShortcuts.Count > 0)
        {
            result.Add("Shortcuts kept because their targets changed:");
            result.AddRange(preservedShortcuts);
        }
        File.WriteAllLines(Path.Combine(job, "result.txt"), result);
        File.Delete(Path.Combine(job, "failure.txt"));
    }

    internal string ReportFailure(Exception error)
    {
        var remaining = selected.Where(PathMayExist).ToList();
        var report = Path.Combine(job, "failure.txt");
        var reportWritten = false;
        try
        {
            File.WriteAllLines(report, new[] { error.ToString(), "", "Removed files and folders:" }
                .Concat(removed)
                .Append("")
                .Append("Selected targets still present or inaccessible:")
                .Concat(remaining));
            reportWritten = true;
        }
        catch (Exception reportError) when (reportError is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine("Could not write the uninstall report: " + reportError);
        }
        var text = string.Format(L10n.Current["Removed {0} files and folders. {1} selected targets remain."], removed.Count, remaining.Count);
        return reportWritten ? text + "\n" + string.Format(L10n.Current["Uninstall report is at {0}"], report) : text;
    }

    static bool PathMayExist(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }
}
