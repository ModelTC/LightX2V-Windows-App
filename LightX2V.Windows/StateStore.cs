using System.Text.Json;

namespace LightX2V.Windows;

public sealed class UnsupportedStateVersionException() : IOException("This workspace was created by a newer version. Update LightX2V before saving changes.") { }

// The index is a cache of task records. Preserve damaged input before rebuilding it,
// and never rewrite a schema that this application does not understand.
public static class StateStore
{
    public sealed record History(List<Generation> Generations, bool Recovered, bool SkippedRecords) {public Dictionary<string,CreationParameters> Creations {get;init;}=[];public string? ActiveCreationId {get;init;}}
    public sealed record Configuration(Preferences? Preferences, string? Notice);

    static bool Recoverable(Exception error) => error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException;

    static T Read<T>(string path, Action<T> validate)
    {
        var value = Files.Read<T>(path) ?? throw new InvalidDataException("Saved state is empty.");
        validate(value);
        return value;
    }

    static void Preserve(string path)
    {
        var preserved = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        File.Copy(path, preserved);
    }

    static void ValidatePreferences(Preferences value)
    {
        if (value.SchemaVersion != 1) throw new UnsupportedStateVersionException();
        if (value.Settings is null) throw new InvalidDataException("Saved settings are missing.");
        var settings = value.Settings;
        settings.Workspace ??= AppSettings.Fresh().Workspace;
        settings.Python ??= "";
        settings.Repository ??= "";
        settings.Model ??= "";
        settings.T2IDirectory ??= "";
        settings.I2IDirectory ??= "";
        if (settings.Backend is not ("auto" or "cuda" or "amd_rocm")) settings.Backend = "auto";
        if (settings.DownloadSource is not ("auto" or "huggingFace" or "modelScope")) settings.DownloadSource = "auto";
        if (value.Language is not ("zh" or "en")) value.Language = "zh";
    }

    public static Preferences ReadPreferences(string path) => Read<Preferences>(path, ValidatePreferences);

    public static Configuration LoadPreferences(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new(null, null);
        if (File.Exists(path))
        {
            try { return new(Read<Preferences>(path, ValidatePreferences), null); }
            catch (UnsupportedStateVersionException) { throw; }
            catch (Exception error) when (Recoverable(error)) { Preserve(path); }
        }
        try
        {
            return new(Read<Preferences>(path + ".bak", ValidatePreferences), "Settings were restored from the last valid backup.");
        }
        catch (UnsupportedStateVersionException) { throw; }
        catch (Exception error) when (Recoverable(error))
        {
            return new(null, "Saved settings could not be read. Your original file was preserved.");
        }
    }

    public static void SavePreferences(string path, Preferences value) => Save(path, value, ValidatePreferences);

    static void ValidateGeneration(Generation value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Id) || value.Request is null || string.IsNullOrWhiteSpace(value.Directory))
            throw new InvalidDataException("Saved task is incomplete.");
        if (value.Status is not ("running" or "completed" or "failed" or "cancelled" or "interrupted") ||
            !double.IsFinite(value.Seconds) || value.Seconds < 0 || value.Seconds >= TimeSpan.MaxValue.TotalSeconds - 1)
            throw new InvalidDataException("Saved task status or duration is invalid.");
        if (string.IsNullOrWhiteSpace(value.Phase)) value.Phase = "loading";
        if (value.StepTotal <= 0) value.StepTotal = 8;
        // Older records may not have conversation IDs. Keep them independently usable.
        if (string.IsNullOrWhiteSpace(value.CreationId)) value.CreationId = value.Id;
        value.Request.Prompt ??= "";
        value.Request.Output ??= "";
        value.Request.InputImages ??= [];
        if (value.Request.InputImages.Any(image => image is null || image.Path is null || image.Width <= 0 || image.Height <= 0))
            throw new InvalidDataException("Saved reference images are incomplete.");
        value.EnhancementThinking ??= "";
        value.EnhancedPrompt ??= "";
        value.EnhancementStatus ??= "";
    }

    static void ValidateHistory(WorkspaceState value)
    {
        if (value.SchemaVersion != 1) throw new UnsupportedStateVersionException();
        if (value.Generations is null) throw new InvalidDataException("Saved history is missing.");
        value.Creations ??= [];
        foreach(var item in value.Creations.Values)
            if(item is null||item.Tier is <0 or >2||!Presets.Ratios.Contains(item.Ratio))throw new InvalidDataException("Saved creation settings are invalid.");
        foreach (var generation in value.Generations) ValidateGeneration(generation);
    }

    public static History LoadHistory(string workspace)
    {
        var path = Path.Combine(workspace, "workspace.json");
        var recovered = File.Exists(path);
        DateTime? indexSavedAt = null;
        var records = new Dictionary<string, Generation>();
        WorkspaceState? index=null;
        if (File.Exists(path))
        {
            try
            {
                index=Read<WorkspaceState>(path, ValidateHistory);
                foreach (var item in index.Generations) records[item.Id] = item;
                indexSavedAt = File.GetLastWriteTimeUtc(path);
                recovered = false;
            }
            catch (UnsupportedStateVersionException) { throw; }
            catch (Exception error) when (Recoverable(error)) { Preserve(path); }
        }

        var skipped = false;
        if (indexSavedAt is null && File.Exists(path + ".bak"))
        {
            try
            {
                index=Read<WorkspaceState>(path + ".bak", ValidateHistory);
                foreach (var item in index.Generations) records[item.Id] = item;
                recovered = true;
            }
            catch (UnsupportedStateVersionException) { throw; }
            catch (Exception error) when (Recoverable(error)) { skipped = true; }
        }
        var outputs = Path.Combine(workspace, "outputs");
        if (Directory.Exists(outputs))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var task in Directory.EnumerateFiles(outputs, "generation.json", options))
            {
                try
                {
                    if (indexSavedAt is { } savedAt && File.GetLastWriteTimeUtc(task) <= savedAt) continue;
                    var item = Read<Generation>(task, ValidateGeneration);
                    // Reconcile interrupted index writes. Old records deliberately
                    // removed from a valid index must stay removed from history.
                    if (indexSavedAt is { } saved && !records.ContainsKey(item.Id) && item.CreatedAt.UtcDateTime <= saved) continue;
                    records[item.Id] = item; // Per-task state can be newer than the backup index.
                    recovered = true;
                }
                catch (Exception error) when (Recoverable(error)) { skipped = true; }
            }
        }
        return new(records.Values.OrderBy(item => item.CreatedAt).ToList(), recovered, skipped){Creations=index?.Creations??[],ActiveCreationId=index?.ActiveCreationId};
    }

    public static void SaveHistory(string workspace, List<Generation> generations,Dictionary<string,CreationParameters>? creations=null,string? activeCreationId=null) =>
        Save(Path.Combine(workspace, "workspace.json"), new WorkspaceState { Generations = generations,Creations=creations??[],ActiveCreationId=activeCreationId }, ValidateHistory);

    static void Save<T>(string path, T value, Action<T> validate)
    {
        validate(value);
        if (File.Exists(path))
        {
            try { Files.Write(path + ".bak", Read<T>(path, validate)); }
            catch (UnsupportedStateVersionException) { throw; }
            catch (Exception error) when (error is JsonException or InvalidDataException) { Preserve(path); }
        }
        else if (!File.Exists(path + ".bak")) Files.Write(path + ".bak", value);
        Files.Write(path, value);
    }
}
