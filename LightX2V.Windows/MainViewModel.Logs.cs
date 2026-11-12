using System.Text;

namespace LightX2V.Windows;

public sealed partial class MainViewModel
{
    public const int LogPreviewBytes=90000;
    public Generation? LogTarget {get=>logTarget;set{if(value!=null)OpenLog(value);}}
    Command? revealLogCommand;
    public Command RevealLogCommand=>revealLogCommand??=new(()=>_=RevealFullLogAsync());
    async Task RevealFullLogAsync()
    {
        if(logTarget==null)return;
        try
        {
            var path=new[]{"run.log","inference.log",Path.Combine("enhancement","llama.log")}.Select(p=>Path.Combine(logTarget.Directory,p)).FirstOrDefault(File.Exists)??logTarget.Directory;
            await WindowsShell.RevealAsync(path);
        }
        catch(Exception error){Toast=ShellFailure(error,"Unable to open Explorer");}
    }
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
        if(generation==null)return;logTarget=generation;Changed(nameof(LogTarget));Toast="";ShowLog=true;RefreshLogPanel();
    }
    public static string ReadLogTail(string path,int budget=LogPreviewBytes)
    {
        // Readers must share Write as well: a live Python/llama writer already
        // owns write access, even when that writer allows other readers.
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var count=(int)Math.Min(input.Length,budget);input.Seek(-count,SeekOrigin.End);var data=new byte[count];var read=0;
        while(read<count){var n=input.Read(data,read,count-read);if(n==0)break;read+=n;}
        var start=0;while(start<read&&(data[start]&0xC0)==0x80)start++;
        var end=read;
        if(end>start)
        {
            var lead=end-1;while(lead>=start&&(data[lead]&0xC0)==0x80)lead--;
            if(lead>=start){var first=data[lead];var length=first<0x80?1:(first&0xE0)==0xC0?2:(first&0xF0)==0xE0?3:(first&0xF8)==0xF0?4:1;if(lead+length>end)end=lead;}
        }
        return Encoding.UTF8.GetString(data,start,end-start).TrimStart('\uFEFF');
    }
    void RefreshLogPanel()
    {
        if(!ShowLog||logTarget==null)return;var parts=new List<string>();
        var files=File.Exists(Path.Combine(logTarget.Directory,"run.log"))?new[]{"run.log"}:new[]{Path.Combine("enhancement","llama.log"),"inference.log"};
        // Reserve the byte budget newest-first, then display the legacy stages chronologically.
        int remaining=LogPreviewBytes;
        foreach(var relative in files.Reverse())
        {
            if(remaining<=0)break;
            try{var text=ReadLogTail(Path.Combine(logTarget.Directory,relative),remaining);remaining-=Encoding.UTF8.GetByteCount(text);if(text.Length>0)parts.Insert(0,text);}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
            catch(IOException e){parts.Insert(0,T["Unable to read log"]+": "+e.Message);}
            catch(UnauthorizedAccessException e){parts.Insert(0,T["Unable to read log"]+": "+e.Message);}
        }
        if(parts.Count==0&&logTarget.Id==streamedLogId&&streamedLogs.Length>0)parts.Add(streamedLogs);
        if(!string.IsNullOrWhiteSpace(logTarget.Error))parts.Add(logTarget.Error);
        var textAll=string.Join(Environment.NewLine+Environment.NewLine,parts);
        var bytes=Encoding.UTF8.GetBytes(textAll);var start=Math.Max(0,bytes.Length-LogPreviewBytes);while(start<bytes.Length&&(bytes[start]&0xC0)==0x80)start++;
        Logs=textAll.Length==0?T[logTarget.Running?"Waiting for log output…":"No saved log output"]:Encoding.UTF8.GetString(bytes,start,bytes.Length-start);
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
