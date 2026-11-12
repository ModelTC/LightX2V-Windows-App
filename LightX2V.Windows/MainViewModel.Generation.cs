using System.Text.Json;

namespace LightX2V.Windows;

public sealed partial class MainViewModel
{
    bool HandleGenerationEvent(Generation generation, JsonElement message)
    {
        var terminal = false;
        switch (message.GetProperty("type").GetString())
        {
            case "enhancement_child_started":
                generation.EnhancementPid = message.GetProperty("pid").GetInt32();
                break;
            case "enhancement_phase":
                var phase = message.GetProperty("phase").GetString()!;
                if (generation.EnhancementStatus == "thinking" && phase != "thinking") generation.ShowThinking = false;
                generation.EnhancementStatus = phase;
                generation.Phase = "enhancement";
                break;
            case "enhancement_thinking": generation.EnhancementThinking += message.GetProperty("delta").GetString(); break;
            case "enhancement_prompt": generation.EnhancedPrompt += message.GetProperty("delta").GetString(); break;
            case "enhancement_result":
                generation.EnhancementStatus = "completed";
                generation.EnhancedPrompt = message.GetProperty("prompt").GetString()!;
                generation.EnhancementRatio = message.GetProperty("whRatio").GetString()!;
                generation.EnhancementRatioFollow = message.GetProperty("ratioFollow").GetString()!;
                generation.EnhancementSeconds = message.GetProperty("duration").GetDouble();
                break;
            case "effective_request":
                generation.Request.Width = message.GetProperty("width").GetInt32();
                generation.Request.Height = message.GetProperty("height").GetInt32();
                generation.Changed(nameof(Generation.Request));
                break;
            case "child_started": generation.BackendPid = message.GetProperty("pid").GetInt32(); break;
            case "log": Log(message.GetProperty("message").GetString()!); break;
            case "step":
                generation.Step = message.GetProperty("step").GetInt32();
                if (message.TryGetProperty("total", out var total) && total.GetInt32() > 0) generation.StepTotal = total.GetInt32();
                generation.Phase = "sampling";
                break;
            case "status": generation.Phase = message.GetProperty("phase").GetString()!; break;
            case "error":
                generation.Error = message.GetProperty("message").GetString();
                Log(generation.Error!);
                break;
            case "done":
                terminal = true;
                generation.Seconds = message.TryGetProperty("seconds", out var seconds) ? seconds.GetDouble() : (DateTimeOffset.Now - generation.CreatedAt).TotalSeconds;
                generation.Status = message.GetProperty("status").GetString()!;
                if (message.TryGetProperty("message", out var detail)) generation.Error = detail.GetString();
                if (generation.Status == "cancelled") generation.Error = T["Generation cancelled"];
                SynchronizeEnhancementStatus(generation);
                Persist();
                break;
        }
        generation.Refresh();
        ContentUpdated?.Invoke();
        return terminal;
    }

    static void SynchronizeEnhancementStatus(Generation generation)
    {
        if (generation.HasEnhancement && !generation.EnhancementCompleted && !generation.Running)
            generation.EnhancementStatus = generation.Status;
    }

    void FinishGeneration(Generation generation, BridgeClient client)
    {
        try
        {
            generation.Seconds = Math.Max(generation.Seconds, (DateTimeOffset.Now - generation.CreatedAt).TotalSeconds);
            if (generation.Running)
            {
                generation.Status = "interrupted";
                generation.Error = T["Interrupted message"];
            }
            SynchronizeEnhancementStatus(generation);
            generation.Refresh();
            File.AppendAllText(Path.Combine(generation.Directory,"run.log"),$"Status: {generation.Status}\nDuration: {generation.Seconds:F2} s\n",new System.Text.UTF8Encoding(false));
            Files.Write(Path.Combine(generation.Directory, "generation.json"), generation);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Toast = error.Message;
            Log(error.Message);
        }
        finally
        {
            // Clear UI ownership even if writing the terminal task record fails.
            try { client.Dispose(); }
            finally
            {
                if (ReferenceEquals(active, client)) { active = null; activeJob = null; }
                IsStopping = false;
                RefreshLogPanel();
                RefreshRecent();
                if (creation == generation.CreationId)
                {
                    selected = Recent.FirstOrDefault(item => item.CreationId == creation);
                    Changed(nameof(Selected));
                    RefreshTurns();
                }
                Persist();
                RefreshStatus();
            }
        }
    }

    public async Task StopAsync()
    {
        if (IsPreflighting)
        {
            IsStopping = true;
            preflightCancellation?.Cancel();
            CancelEnvironmentCheck();
            return;
        }
        if (active is not { } client || activeJob is not { } generation || IsStopping) return;
        IsStopping = true;
        try { await client.StopAsync(Path.Combine(generation.Directory, "cancel.signal")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            Toast = T["Unable to stop generation"] + ": " + error.Message;
            Log(error.Message);
            IsStopping = false;
            RefreshStatus();
        }
    }
}
