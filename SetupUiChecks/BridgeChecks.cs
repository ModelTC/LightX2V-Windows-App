using System.Diagnostics;
using System.IO;
using System.Text;
using LightX2V.Windows;

internal static partial class Program
{
    static ProcessStartInfo ShellFixture(string script)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(argument);
        return start;
    }

    static async Task BridgeChecks()
    {
        foreach (var input in new[] { "plain log", "[]", "null", "{\"warning\":\"ordinary JSON\"}", "{\"protocol\":1,\"type\":\"step\",\"step\":\"bad\"}", "{\"protocol\":\"1\",\"type\":\"done\"}", "{\"protocol\":2,\"type\":\"done\"}" })
            Check(BackendEvent.ParseLine(input).GetProperty("message").GetString() == input, "Ordinary or malformed protocol input remains a text log: " + input);
        var ready = BackendEvent.ParseLine("{\"protocol\":1,\"type\":\"ready\",\"device\":\"GPU\",\"torch\":\"2.11\",\"hip\":null,\"cuda\":\"12.8\"}");
        Check(ready.GetProperty("type").GetString() == "ready", "Native probe events accept the null runtime metadata used by CUDA and ROCm");
        foreach (var line in new[] { "{\"protocol\":1,\"type\":\"done\",\"status\":\"completed\",\"seconds\":1e300}", "{\"protocol\":1,\"type\":\"done\",\"status\":\"running\"}" })
            Check(BackendEvent.ParseLine(line).GetProperty("type").GetString() == "log", "Invalid terminal status and overflowing durations cannot corrupt generation state");

        using (var bridge = new BridgeClient())
        {
            var events = new List<string>();
            var code = await bridge.RunProcessAsync(ShellFixture("Write-Output '{\"warning\":\"ordinary\"}'; Write-Output '[]'; Write-Output '{\"protocol\":1,\"type\":\"done\",\"status\":\"completed\"}'"), value => events.Add(value.GetProperty("type").GetString()!)).WaitAsync(TimeSpan.FromSeconds(10));
            Check(code == 0 && events.SequenceEqual(new[] { "log", "log", "done" }), "A real process can mix JSON dependency logs and protocol events without losing completion");
        }
        using (var bridge = new BridgeClient())
        {
            var operation = bridge.RunProcessAsync(ShellFixture("Write-Output '{\"protocol\":1,\"type\":\"status\",\"phase\":\"loading\"}'; Start-Sleep -Seconds 60"), _ => throw new InvalidDataException("Fixture callback failure"));
            try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Callback failure swallowed"); }
            catch (InvalidDataException error) { Check(error.Message == "Fixture callback failure" && !bridge.Running, "A failed protocol callback terminates the process and other stream reader"); }
        }
        using (var bridge = new BridgeClient())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = bridge.RunProcessAsync(ShellFixture("Write-Output '{\"protocol\":1,\"type\":\"status\",\"phase\":\"loading\"}'; Start-Sleep -Seconds 60"), _ => started.TrySetResult());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var blockedSignal = Path.Combine(root, "cancel-signal-is-a-directory");
            Directory.CreateDirectory(blockedSignal);
            await bridge.StopAsync(blockedSignal).WaitAsync(TimeSpan.FromSeconds(15));
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!bridge.Running && Directory.Exists(blockedSignal), "Failure to write cancellation still reaches bounded process termination without deleting user files");
        }
        using (var bridge = new BridgeClient())
        using (var cancellation = new CancellationTokenSource())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = bridge.RunProcessAsync(ShellFixture("Write-Output 'started'; Start-Sleep -Seconds 60"), _ => started.TrySetResult(), cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            Check(!bridge.Running, "Cancelling a live process tears down both redirected readers promptly");
        }
    }
}
