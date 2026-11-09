using System.Diagnostics;
using System.Text.Json;

namespace LightX2V.Windows;

public sealed record ModelDownloadRequest(string Resource,string Source,string Destination,string Python,string Workspace);
public sealed record ModelDownloadProgress(string Phase,string File,double Percent,double BytesPerSecond,string Source="",double? EtaSeconds=null);
public sealed record ModelDownloadResult(bool Completed,bool Cancelled=false,string Message="");
public interface IModelDownloader
{
    Task<ModelDownloadResult> DownloadAsync(ModelDownloadRequest request,IProgress<ModelDownloadProgress> progress,CancellationToken token);
}
public sealed class NativeModelDownloader : IModelDownloader
{
    public async Task<ModelDownloadResult> DownloadAsync(ModelDownloadRequest request,IProgress<ModelDownloadProgress> progress,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!File.Exists(request.Python))throw new InvalidOperationException(L10n.Current["Configure Python first"]);
        var folder=Path.Combine(request.Workspace,"downloads",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var signal=Path.Combine(folder,"cancel.signal");var input=Path.Combine(folder,"request.json");
        Files.Write(input,new{resource=request.Resource,source=request.Source,destination=request.Destination,cancelFile=signal});
        var start=new ProcessStartInfo(request.Python){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8};
        foreach(var arg in new[]{"-X","utf8","-u",Path.Combine(AppContext.BaseDirectory,"Backend","download_models.py"),input,"--parent",Environment.ProcessId.ToString()})start.ArgumentList.Add(arg);
        var temp=Path.Combine(request.Workspace,"tmp");Directory.CreateDirectory(temp);start.Environment["TEMP"]=temp;start.Environment["TMP"]=temp;
        using var log=File.CreateText(Path.Combine(folder,"download.log"));
        using var process=new Process{StartInfo=start};process.Start();
        using var watcher=CancellationTokenSource.CreateLinkedTokenSource(token);
        var stopping=StopOnCancellation(process,signal,token,watcher.Token);var errors=process.StandardError.ReadToEndAsync();
        var result=new ModelDownloadResult(false,Message:"The download did not finish. Please try again.");
        try
        {
            while(await process.StandardOutput.ReadLineAsync() is {} line)
            {
                await log.WriteLineAsync(line);if(token.IsCancellationRequested)continue;
                JsonDocument document;try{document=JsonDocument.Parse(line);}catch(JsonException){continue;}
                using(document)
                {
                    var value=document.RootElement;if(!value.TryGetProperty("type",out var kind))continue;
                    if(kind.GetString()=="download_progress")
                    {
                        var total=value.GetProperty("total").GetDouble();var done=value.GetProperty("completed").GetDouble();
                        double? eta=value.TryGetProperty("etaSeconds",out var estimate)&&estimate.ValueKind==JsonValueKind.Number?estimate.GetDouble():null;
                        progress.Report(new(value.GetProperty("phase").GetString()??"downloading",value.TryGetProperty("file",out var file)?file.GetString()??"":"",total>0?Math.Clamp(done/total*100,0,100):0,value.GetProperty("bytesPerSecond").GetDouble(),EtaSeconds:eta));
                    }
                    else if(kind.GetString()=="download_source")
                    {
                        var source=value.GetProperty("source").GetString()??"";
                        var fallback=value.TryGetProperty("fallback",out var changed)&&changed.GetBoolean();
                        progress.Report(new(source=="auto"?"selecting_source":fallback?"switching_source":"source_selected","",0,0,source));
                    }
                    else if(kind.GetString()=="download_done")
                    {
                        var status=value.GetProperty("status").GetString();result=new(status=="completed",status=="cancelled",value.TryGetProperty("message",out var message)?message.GetString()??"":"");
                    }
                }
            }
            await process.WaitForExitAsync();var diagnostic=await errors;await log.WriteAsync(diagnostic);
            if(token.IsCancellationRequested)return new(false,true);
            if(process.ExitCode!=0)return new(false,Message:result.Message.Length>0?result.Message:diagnostic);
            return result;
        }
        finally
        {
            watcher.Cancel();await stopping;
            if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}
        }
    }
    static async Task StopOnCancellation(Process process,string signal,CancellationToken token,CancellationToken watcher)
    {
        try{await Task.Delay(Timeout.Infinite,watcher);}catch(OperationCanceledException){}
        if(!token.IsCancellationRequested)return;
        try
        {
            try{File.WriteAllText(signal,"cancel");}catch(IOException){}catch(UnauthorizedAccessException){}
            var exit=process.WaitForExitAsync();
            if(await Task.WhenAny(exit,Task.Delay(8000))!=exit&&!process.HasExited)process.Kill(true);
            await exit;
        }
        catch(InvalidOperationException){} // The owned child may have finished while cancellation arrived.
    }
}
