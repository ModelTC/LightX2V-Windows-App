using System.IO;
using System.Text.Json;
using LightX2V.Windows;

internal static partial class Program
{
    static Generation SavedTask(string workspace, string id)
    {
        var task = new Generation { Id = id, CreationId = id, Directory = Path.Combine(workspace, "outputs", id), Status = "completed", Request = new() { Prompt = id } };
        Files.Write(Path.Combine(task.Directory, "generation.json"), task);
        return task;
    }

    static void RecoveryChecks()
    {
        var workspace = Path.Combine(root, "history-recovery");
        var first = SavedTask(workspace, "first");
        StateStore.SaveHistory(workspace, [first]);
        var second = SavedTask(workspace, "second");
        StateStore.SaveHistory(workspace, [first, second]);
        var index = Path.Combine(workspace, "workspace.json");
        File.WriteAllText(index, "{truncated");
        var recovered = StateStore.LoadHistory(workspace);
        Check(recovered.Recovered && recovered.Generations.Select(item => item.Id).Order().SequenceEqual(new[] { "first", "second" }), "Damaged history merges a valid backup with newer per-task records");
        Check(Directory.GetFiles(workspace, "workspace.json.corrupt-*").Length > 0 && File.Exists(Path.Combine(first.Directory, "generation.json")), "History recovery preserves the damaged index and original task files");
        StateStore.SaveHistory(workspace, recovered.Generations);
        Check(StateStore.LoadHistory(workspace).Generations.Count == 2, "Recovered history remains available after saving and reopening");
        File.Delete(index);
        var broken = Path.Combine(workspace, "outputs", "broken", "generation.json");
        Files.Write(broken, new { id = "broken", directory = Path.GetDirectoryName(broken), request = (object?)null });
        recovered = StateStore.LoadHistory(workspace);
        Check(recovered.SkippedRecords && recovered.Generations.Count == 2 && File.Exists(broken), "An invalid task record is reported and preserved without hiding valid history");
        foreach (var (id, status, seconds) in new[] { ("null-status", (string?)null, 0.0), ("overflow-duration", "completed", 1e300) })
        {
            var directory = Path.Combine(workspace, "outputs", id);
            Files.Write(Path.Combine(directory, "generation.json"), new { id, directory, status, seconds, request = new { prompt = id } });
        }
        recovered = StateStore.LoadHistory(workspace);
        Check(recovered.SkippedRecords && recovered.Generations.Count == 2, "Semantically invalid status and overflowing durations are rejected before history bindings can crash");

        Files.Write(index, new { schemaVersion = 999, generations = new object[0] });
        var future = File.ReadAllText(index);
        try { StateStore.LoadHistory(workspace); throw new Exception("Future schema accepted"); }
        catch (UnsupportedStateVersionException) { }
        try { StateStore.SaveHistory(workspace, []); throw new Exception("Future schema overwritten"); }
        catch (UnsupportedStateVersionException) { }
        Check(File.ReadAllText(index) == future, "A newer workspace schema is rejected without overwriting its contents");

        var staleWorkspace = Path.Combine(root, "history-stale-index");
        var stale = SavedTask(staleWorkspace, "previously-running");
        stale.Status = "running";
        StateStore.SaveHistory(staleWorkspace, [stale]);
        var staleIndex = Path.Combine(staleWorkspace, "workspace.json");
        File.SetLastWriteTimeUtc(staleIndex, DateTime.UtcNow.AddMinutes(-1));
        stale.Status = "completed";
        Files.Write(Path.Combine(stale.Directory, "generation.json"), stale);
        var orphan = SavedTask(staleWorkspace, "new-task-after-index-failure");
        var removed = SavedTask(staleWorkspace, "older-removed-task");
        removed.CreatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        Files.Write(Path.Combine(removed.Directory, "generation.json"), removed);
        var reconciled = StateStore.LoadHistory(staleWorkspace);
        Check(reconciled.Recovered && reconciled.Generations.Single(item => item.Id == stale.Id).Completed && reconciled.Generations.Any(item => item.Id == orphan.Id), "A valid but stale index recovers newer terminal records and new tasks after an index write failure");
        Check(reconciled.Generations.All(item => item.Id != removed.Id), "Reconciling a valid index does not resurrect old records deliberately removed from history");

        var preferences = Path.Combine(root, "recovery-preferences", "preferences.json");
        var settings = AppSettings.Fresh(Path.Combine(root, "preference-workspace"));
        StateStore.SavePreferences(preferences, new() { Settings = settings, Language = "en" });
        StateStore.SavePreferences(preferences, new() { Settings = settings, Language = "zh" });
        File.WriteAllText(preferences, "{bad JSON");
        var configuration = StateStore.LoadPreferences(preferences);
        Check(configuration.Preferences?.Language == "en" && configuration.Notice is not null, "Corrupt preferences restore the previous valid configuration with a recovery notice");
        File.WriteAllText(preferences + ".bak", "null");
        configuration = StateStore.LoadPreferences(preferences);
        Check(configuration.Preferences is null && configuration.Notice is not null && Directory.GetFiles(Path.GetDirectoryName(preferences)!, "preferences.json.corrupt-*").Length > 0, "Unrecoverable preferences preserve input and explicitly request fresh configuration");
        Files.Write(preferences, new { settings = new { workspace = (string?)null, python = (string?)null, backend = "unsupported" }, language = "invalid" });
        configuration = StateStore.LoadPreferences(preferences);
        Check(configuration.Preferences is { Language: "zh" } && configuration.Preferences.Settings.Python == "" && configuration.Preferences.Settings.Backend == "auto", "Legacy preferences normalize null paths and unsupported policy values safely");

        var legacy = Path.Combine(root, "legacy-import");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "baseline-root.json"), "{broken");
        string? notice = null;
        Check(LegacyWorkspaceImporter.TryRead(legacy, message => notice = message) is null && notice is not null, "A damaged legacy baseline falls back with a diagnostic instead of failing before startup");
        Files.Write(Path.Combine(legacy, "baseline-root.json"), new { model_workspace = legacy });
        Check(LegacyWorkspaceImporter.TryRead(legacy)?.Model == Path.Combine(legacy, "models", "Qwen-Image-2.1-Turbo"), "A valid legacy workspace keeps its original model layout");

        var fixture = ReadinessFixture("history-viewmodel");
        var task = SavedTask(fixture.Settings.Workspace, "restored-viewmodel-task");
        File.WriteAllText(Path.Combine(fixture.Settings.Workspace, "workspace.json"), "[]");
        using var model = new MainViewModel(fixture.Preferences, environmentCatalog: new ReadinessCatalog(), driverPlatform: fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));
        Check(model.Recent.Count == 1 && model.Recent[0].Id == task.Id && model.Toast.Length > 0, "The real view model rebuilds damaged history and exposes a recovery notice without GPU probing");
        var unsupported = model.Settings.Clone();
        unsupported.Workspace = workspace;
        try { model.ApplySettings(unsupported); throw new Exception("Unsupported workspace selected"); }
        catch (UnsupportedStateVersionException) { }
        try { model.CommitSetup(unsupported); throw new Exception("Unsupported setup workspace selected"); }
        catch (UnsupportedStateVersionException) { }
        Check(model.Settings.Workspace == fixture.Settings.Workspace && model.Recent.Single().Id == task.Id &&
            Files.Read<Preferences>(Path.Combine(fixture.Preferences, "preferences.json"))!.Settings.Workspace == fixture.Settings.Workspace,
            "Settings and setup validate a new workspace schema before replacing the current configuration or history");
    }
}
