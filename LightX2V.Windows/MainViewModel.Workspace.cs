namespace LightX2V.Windows;

public sealed partial class MainViewModel
{
    void SavePreferences()
    {
        try
        {
            StateStore.SavePreferences(preferencesPath, new Preferences
            {
                Language = T.Language,
                Settings = Settings.Clone(),
                ModelExpanded = ModelExpanded,
                ParametersExpanded = ParametersExpanded
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Toast = T["Unable to save settings"] + ": " + T[error.Message];
            System.Diagnostics.Trace.WriteLine(error);
        }
    }

    void LoadWorkspace(StateStore.History? restored = null)
    {
        workspacePersistenceUnavailable = false;
        if (string.IsNullOrWhiteSpace(Settings.Workspace)) return;
        try
        {
            var saved = restored ?? StateStore.LoadHistory(Settings.Workspace);
            all.AddRange(saved.Generations);
            foreach (var generation in all.Where(item => item.Running))
            {
                generation.Status = "interrupted";
                if (generation.HasEnhancement && !generation.EnhancementCompleted) generation.EnhancementStatus = "interrupted";
                generation.Error = T["Interrupted message"];
                generation.Seconds = Math.Max(0, (DateTimeOffset.Now - generation.CreatedAt).TotalSeconds);
            }
            RefreshRecent();
            Persist();
            if (saved.Recovered) Toast = T["Creation history was recovered from saved task records."];
            if (saved.SkippedRecords) Toast += (Toast.Length > 0 ? Environment.NewLine : "") + T["Some saved task records could not be read; their files were preserved."];
        }
        catch (UnsupportedStateVersionException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Do not let a later generation overwrite an index we could not read.
            workspacePersistenceUnavailable = true;
            Toast = error.Message;
            System.Diagnostics.Trace.WriteLine(error);
        }
        Changed(nameof(WorkspaceAvailable));
    }

    bool workspacePersistenceUnavailable;
    public bool WorkspaceAvailable => !workspacePersistenceUnavailable;

    void Persist()
    {
        if (loading || workspacePersistenceUnavailable || string.IsNullOrWhiteSpace(Settings.Workspace)) return;
        try
        {
            StateStore.SaveHistory(Settings.Workspace, all);
            if (activeJob is { } generation) Files.Write(Path.Combine(generation.Directory, "generation.json"), generation);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Toast = T[error.Message];
            System.Diagnostics.Trace.WriteLine(error);
        }
    }
}
