using System.Diagnostics;
using System.Text.Json;
namespace LightX2V.Windows;

public sealed class BridgeClient : IDisposable
{
    Process? process; public bool Running => process is { HasExited: false };
    public async Task<int> RunAsync(string python, string mode, string request, string temp, Action<JsonElement> onEvent, CancellationToken token=default)
    {
        if (Running) throw new InvalidOperationException("A backend process is already running.");
        Directory.CreateDirectory(temp);
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var a in new[] { "-X", "utf8", "-u", Path.Combine(AppContext.BaseDirectory, "Backend", "bridge.py"), mode, request, "--parent", Environment.ProcessId.ToString() }) start.ArgumentList.Add(a);
        start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp; start.Environment["PYTHONIOENCODING"] = "utf-8";
        process = new Process { StartInfo = start }; process.Start();
        using var registration=token.Register(()=>{try{if(Running)process!.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}});
        async Task Read(StreamReader reader, bool errors) { while (await reader.ReadLineAsync(token) is { } line) { try { using var d = JsonDocument.Parse(line); onEvent(d.RootElement.Clone()); } catch (JsonException) { onEvent(JsonSerializer.SerializeToElement(new { type = "log", message = line }, Files.Json)); } } }
        await Task.WhenAll(Read(process.StandardOutput, false), Read(process.StandardError, true)); await process.WaitForExitAsync(token); return process.ExitCode;
    }
    public async Task StopAsync(string cancelFile)
    {
        if (!Running) return; File.WriteAllText(cancelFile, "cancel");
        var p = process!; var wait = p.WaitForExitAsync(); if (await Task.WhenAny(wait, Task.Delay(8000)) != wait && !p.HasExited) p.Kill(true);
        await wait;
    }
    public void Dispose() { if (Running) process!.Kill(true); process?.Dispose(); }
}
