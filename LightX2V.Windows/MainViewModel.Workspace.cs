namespace LightX2V.Windows;

public sealed partial class MainViewModel
{
    readonly Dictionary<string,CreationParameters> creationSettings=[];
    void RememberCreation()
    {
        if(loading||viewModelDisposed)return;
        creationSettings[creation]=new(){Tier=Tier,Ratio=Ratio,EnhancementEnabled=EnhancementEnabled};Persist();
    }
    void RestoreCreation(Generation? generation)
    {
        if(generation==null)return;
        if(!creationSettings.TryGetValue(generation.CreationId,out var parameters))
        {
            parameters=new(){EnhancementEnabled=generation.HasEnhancement};
            if(generation.Request.Resolution is int resolution){parameters.Tier=resolution==512?0:resolution==2048?2:1;}
            else foreach(var t in Enumerable.Range(0,3))foreach(var r in Presets.Ratios.Where(r=>r!="Adaptive"))
                if(Presets.Dimensions(t,r)==(generation.Request.Width,generation.Request.Height)){parameters.Tier=t;parameters.Ratio=r;}
        }
        RestoreParameters(parameters);
    }
    void RestoreParameters(CreationParameters parameters)
    {
        tier=parameters.Tier;ratio=parameters.Ratio;Settings.EnhancementEnabled=parameters.EnhancementEnabled;
        Changed(nameof(Tier));Changed(nameof(Ratio));Changed(nameof(EnhancementEnabled));Changed(nameof(SizeText));RefreshStatus();
    }

    public string PreferencesDirectory=>Path.GetDirectoryName(preferencesPath)!;
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
            if(Path.IsPathFullyQualified(Settings.Workspace))AppMaintenance.RegisterWorkspace(PreferencesDirectory,Settings);
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
            creationSettings.Clear();foreach(var pair in saved.Creations)creationSettings[pair.Key]=pair.Value;
            foreach (var generation in all.Where(item => item.Running))
            {
                generation.Status = "interrupted";
                if (generation.HasEnhancement && !generation.EnhancementCompleted) generation.EnhancementStatus = "interrupted";
                generation.Error = T["Interrupted message"];
                generation.Seconds = Math.Max(0, (DateTimeOffset.Now - generation.CreatedAt).TotalSeconds);
            }
            RefreshRecent();
            var activeId=saved.ActiveCreationId??all.LastOrDefault()?.CreationId;
            var current=all.LastOrDefault(g=>g.CreationId==activeId);
            selected=current;creation=activeId??Guid.NewGuid().ToString("N");
            if(current!=null)RestoreCreation(current);
            else if(activeId!=null&&creationSettings.TryGetValue(activeId,out var parameters))RestoreParameters(parameters);
            else RestoreParameters(new(){EnhancementEnabled=Settings.EnhancementEnabled});
            Changed(nameof(Selected));RefreshTurns();
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
            StateStore.SaveHistory(Settings.Workspace, all,creationSettings,creation);
            if (activeJob is { } generation) Files.Write(Path.Combine(generation.Directory, "generation.json"), generation);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Toast = T[error.Message];
            System.Diagnostics.Trace.WriteLine(error);
        }
    }
}
