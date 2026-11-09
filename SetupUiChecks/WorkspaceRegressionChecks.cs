using System.Diagnostics;
using System.IO;
using System.Text;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task WorkspaceRegressionChecks()
    {
        var fixture = StartupFixture("workspace-regressions");
        using (var model = new MainViewModel(fixture.Preferences, environmentCatalog: fixture.Catalog, driverPlatform: fixture.Platform))
        {
            model.Prompt = "Preserve the sidebar draft";
            model.Tier = 0;
            model.Ratio = "16:9";
            Check(model.ModelExpanded && model.ParametersExpanded, "Both sidebar sections start expanded");
            model.ModelExpanded = false;
            Check(model.ParametersExpanded, "Collapsing models preserves the parameter section's state");
            model.ParametersExpanded = false;
            model.ModelExpanded = true;
            Check(!model.ParametersExpanded, "Expanding models preserves the collapsed parameter section");
            Check(model.Prompt == "Preserve the sidebar draft" && model.Tier == 0 && model.Ratio == "16:9",
                "Sidebar state changes preserve the draft and generation parameters");
            model.ModelExpanded = false;

            var generation = new Generation { Directory = Path.Combine(fixture.Settings.Workspace, "日志 中文, 空格"), Status = "running" };
            model.OpenLogCommand.Execute(generation);
            Check(model.ShowLog && model.Logs == model.T["Waiting for log output…"],
                "A running task's log viewer opens before its log files exist");
            Directory.CreateDirectory(Path.Combine(generation.Directory, "enhancement"));
            using var inference = new FileStream(Path.Combine(generation.Directory, "inference.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
            using var enhancement = new FileStream(Path.Combine(generation.Directory, "enhancement", "llama.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
            inference.Write(Encoding.UTF8.GetBytes("模型加载开始 中文\n"));
            inference.Flush(true);
            enhancement.Write(Encoding.UTF8.GetBytes("增强模型诊断\n"));
            enhancement.Flush(true);
            await WaitForWorkspaceLog(() => model.Logs.Contains("模型加载开始 中文") && model.Logs.Contains("增强模型诊断"));
            Check(model.Toast.Length == 0, "Inference and enhancement logs update while their writers keep the files open");
            inference.Write(Encoding.UTF8.GetBytes("step_index: 2 / 8 更新\n"));
            inference.Flush(true);
            await WaitForWorkspaceLog(() => model.Logs.Contains("step_index: 2 / 8 更新"));
            Check(model.Logs.Contains("模型加载开始 中文"), "Live log refresh retains earlier output when new lines arrive");

            var history = new Generation { Directory = Path.Combine(fixture.Settings.Workspace, "historical-log"), Status = "completed" };
            Directory.CreateDirectory(history.Directory);
            File.WriteAllText(Path.Combine(history.Directory, "inference.log"), "history-only");
            model.OpenLogCommand.Execute(history);
            inference.Write(Encoding.UTF8.GetBytes("other-task-output\n"));
            inference.Flush(true);
            // Allow the view model's one-second refresh timer to run after changing targets.
            await Task.Delay(1200);
            Check(model.Logs.Contains("history-only") && !model.Logs.Contains("other-task-output"),
                "A historical task's log stays isolated from another task's live output");
            model.OpenLogCommand.Execute(generation);
            Check(model.Logs.Contains("other-task-output") && !model.Logs.Contains("history-only"),
                "Returning to the running task reads its current log without historical output");
        }

        using var restored = new MainViewModel(fixture.Preferences, environmentCatalog: fixture.Catalog, driverPlatform: fixture.Platform);
        Check(!restored.ModelExpanded && !restored.ParametersExpanded, "Both sidebar choices persist when the workspace is reopened");
        restored.ParametersExpanded = true;
        Check(!restored.ModelExpanded, "Restoring one sidebar section leaves the other section folded");
        Check(fixture.Catalog.ProbeCalls == 0 && fixture.Catalog.InstalledProbeCalls == 0,
            "Workspace log and preference regressions complete without environment probing");
    }

    static async Task WaitForWorkspaceLog(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
        Check(condition(), "The workspace log viewer receives the expected file update");
    }
}
