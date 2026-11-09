using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LightX2V.Windows;

public sealed class BridgeClient : IDisposable
{
    static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(8);
    Process? process;
    public bool Running => process is { HasExited: false };

    public Task<int> RunAsync(string python, string mode, string request, string temp, Action<JsonElement> onEvent, CancellationToken token = default)
    {
        Directory.CreateDirectory(temp);
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-X", "utf8", "-u", Path.Combine(AppContext.BaseDirectory, "Backend", "bridge.py"), mode, request, "--parent", Environment.ProcessId.ToString() })
            start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        return RunProcessAsync(start, onEvent, token);
    }

    internal async Task<int> RunProcessAsync(ProcessStartInfo start, Action<JsonElement> onEvent, CancellationToken token = default)
    {
        if (Running) throw new InvalidOperationException("A backend process is already running.");
        token.ThrowIfCancellationRequested();
        process?.Dispose();
        var child = new Process { StartInfo = start };
        process = child;
        try { child.Start(); }
        catch { process = null; child.Dispose(); throw; }
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var registration = token.Register(() => TryTerminate(child));
        async Task Read(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync(reading.Token) is { } line)
                    onEvent(BackendEvent.ParseLine(line));
            }
            catch
            {
                // A failed callback must not leave the other reader waiting on a
                // live process indefinitely. Preserve the error for the caller.
                TryTerminate(child);
                reading.Cancel();
                throw;
            }
        }
        try
        {
            await Task.WhenAll(Read(child.StandardOutput), Read(child.StandardError));
            await child.WaitForExitAsync(token);
            return child.ExitCode;
        }
        catch
        {
            TryTerminate(child);
            reading.Cancel();
            throw;
        }
    }

    public async Task StopAsync(string cancelFile)
    {
        if (!Running) return;
        var child = process!;
        try { File.WriteAllText(cancelFile, "cancel"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Unable to signal backend cancellation; using process timeout: {error}");
        }
        var exited = child.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(StopGrace)) != exited) TryTerminate(child);
        await exited.WaitAsync(TimeSpan.FromSeconds(5));
    }

    static void TryTerminate(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception error) { Trace.WriteLine(error); }
    }

    public void Dispose()
    {
        if (process is not { } child) return;
        TryTerminate(child);
        child.Dispose();
        process = null;
    }
}
