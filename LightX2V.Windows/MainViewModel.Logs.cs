using System.Text;

namespace LightX2V.Windows;

public sealed partial class MainViewModel
{
    Generation? logTarget;string streamedLogId="",streamedLogs="";
    void BeginGenerationLogs(Generation generation){streamedLogId=generation.Id;streamedLogs="";logTarget=generation;Logs="";}
    void ResetLogViewer(){ShowLog=false;logTarget=null;Logs="";}
    void Log(string text)
    {
        streamedLogs+=text+Environment.NewLine;if(streamedLogs.Length>100000)streamedLogs=streamedLogs[^80000..];
        if(logTarget==null)Logs=streamedLogs;
    }
    void ToggleLog(){if(ShowLog)ShowLog=false;else OpenLog(logTarget??Turns.LastOrDefault()??activeJob);}
    void OpenLog(Generation? generation)
    {
        if(generation==null)return;logTarget=generation;Toast="";ShowLog=true;RefreshLogPanel();
    }
    static string ReadLogTail(string path)
    {
        // Readers must share Write as well: a live Python/llama writer already
        // owns write access, even when that writer allows other readers.
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var count=(int)Math.Min(input.Length,128000);input.Seek(-count,SeekOrigin.End);var data=new byte[count];var read=0;
        while(read<count){var n=input.Read(data,read,count-read);if(n==0)break;read+=n;}
        var start=0;while(start<read&&(data[start]&0xC0)==0x80)start++;
        return Encoding.UTF8.GetString(data,start,read-start).TrimStart('\uFEFF');
    }
    void RefreshLogPanel()
    {
        if(!ShowLog||logTarget==null)return;var parts=new List<string>();
        foreach(var (relative,title) in new[]{(Path.Combine("enhancement","llama.log"),"Prompt enhancement"),("inference.log","Image generation")})
        {
            try{var text=ReadLogTail(Path.Combine(logTarget.Directory,relative));if(text.Length>0)parts.Add(T[title]+Environment.NewLine+text);}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
            catch(IOException e){parts.Add(T["Unable to read log"]+": "+e.Message);}
            catch(UnauthorizedAccessException e){parts.Add(T["Unable to read log"]+": "+e.Message);}
        }
        if(parts.Count==0&&logTarget.Id==streamedLogId&&streamedLogs.Length>0)parts.Add(streamedLogs);
        if(!string.IsNullOrWhiteSpace(logTarget.Error))parts.Add(logTarget.Error);
        var textAll=string.Join(Environment.NewLine+Environment.NewLine,parts);
        Logs=textAll.Length==0?T[logTarget.Running?"Waiting for log output…":"No saved log output"]:textAll.Length>100000?textAll[^100000..]:textAll;
    }
    void RefreshLiveLog(){if(ShowLog&&logTarget?.Running==true)RefreshLogPanel();}
    async Task RevealAsync(Generation? generation)
    {
        if(generation==null)return;
        try
        {
            var target=generation.Completed&&File.Exists(generation.Request.Output)?generation.Request.Output:Path.Combine(generation.Directory,"request.json");
            if(!File.Exists(target))target=generation.Directory;
            if(!File.Exists(target)&&!Directory.Exists(target))throw new DirectoryNotFoundException(T["Output folder is missing"]);
            await WindowsShell.RevealAsync(target);Toast="";
        }
        catch(Exception e){Toast=ShellFailure(e,"Unable to open Explorer");}
    }
    string ShellFailure(Exception error,string action)=>T[action]+": "+(error is DesktopIntegrationRestrictedException
        ?T["Launch location restricts desktop integration"]:error.Message);
}
