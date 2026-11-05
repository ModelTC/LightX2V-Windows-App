using System.IO;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using LightX2V.Windows;

internal static partial class Program
{
    static Button DiagnosticButton(MainWindow window,string id)=>Descendants<Button>(window).First(b=>AutomationProperties.GetAutomationId(b)==id);
    static async Task DiagnosticsRun(string fixture,bool onlyLogs=false,bool onlyExplorer=false)
    {
        RecordBackendHashes();var previous=Files.Read<Generation>(fixture)!;var settings=AppSettings.Detect();settings.EnhancementEnabled=false;settings.Workspace=Path.Combine(root,"工作区 中文, 空格");
        var generation=new Generation{Status="completed",Directory=Path.Combine(settings.Workspace,"输出 中文, 空格 "+Path.GetFileName(root)),Request=new(){Prompt="Diagnostic fixture",Output=Path.Combine(settings.Workspace,"输出 中文, 空格 "+Path.GetFileName(root),"试验, 图片.png"),PromptEnhancement=new(){Enabled=true}},EnhancementStatus="completed",EnhancedPrompt="A ceramic teapot in warm window light.",EnhancementThinking="Fixture",ShowThinking=false};
        Directory.CreateDirectory(generation.Directory);File.Copy(previous.Request.Output,generation.Request.Output);Files.Write(Path.Combine(generation.Directory,"request.json"),generation.Request);
        Files.Write(Path.Combine(settings.Workspace,"workspace.json"),new WorkspaceState{Generations=[generation]});var prefs=Path.Combine(root,"preferences");Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});
        using var vm=new MainViewModel(prefs);var window=new MainWindow(vm);window.Show();await Until(()=>vm.Ready&&!vm.IsChecking,120);var g=vm.Recent.Single();vm.Selected=g;await Task.Delay(200);
        Assert(!Descendants<Button>(window).Any(b=>b.Command==vm.CopyEnhancedCommand),"The enhanced prompt has no redundant copy action");
        g.Status="running";await Task.Delay(100);Invoke(DiagnosticButton(window,"GenerationProgressLog"));await Until(()=>vm.ShowLog);
        Assert(vm.Logs==vm.T["Waiting for log output…"],"The loading-card log action opens a visible panel before the first log file exists");
        Directory.CreateDirectory(Path.Combine(g.Directory,"enhancement"));
        using(var inference=new FileStream(Path.Combine(g.Directory,"inference.log"),FileMode.Create,FileAccess.Write,FileShare.Read))
        using(var enhancement=new FileStream(Path.Combine(g.Directory,"enhancement","llama.log"),FileMode.Create,FileAccess.Write,FileShare.Read))
        {
            var first=Encoding.UTF8.GetBytes("模型加载开始 中文\n");inference.Write(first);inference.Flush(true);enhancement.Write(Encoding.UTF8.GetBytes("增强模型诊断\n"));enhancement.Flush(true);
            await Until(()=>vm.Logs.Contains("模型加载开始 中文")&&vm.Logs.Contains("增强模型诊断"),10);
            Assert(vm.Toast.Length==0,"Both enhancement and inference logs can be read while their writers keep the files open");
            vm.ShowLog=false;Invoke(DiagnosticButton(window,"GenerationLog"));await Until(()=>vm.ShowLog);
            inference.Write(Encoding.UTF8.GetBytes("step_index: 2 / 8 更新\n"));inference.Flush(true);await Until(()=>vm.Logs.Contains("step_index: 2 / 8 更新"),10);
            Assert(((TextBox)window.FindName("LogViewer")).Text.Contains("更新"),"The footer log action and the visible text box follow new output automatically");Shot(window,"live-locked-logs");
            var history=new Generation{Status="completed",Directory=Path.Combine(root,"history")};Directory.CreateDirectory(history.Directory);File.WriteAllText(Path.Combine(history.Directory,"inference.log"),"history-only");
            vm.OpenLogCommand.Execute(history);inference.Write(Encoding.UTF8.GetBytes("other-task-output\n"));inference.Flush(true);await Task.Delay(1200);
            Assert(vm.Logs.Contains("history-only")&&!vm.Logs.Contains("other-task-output"),"Viewing a historical task does not mix in another task's live log");
            vm.OpenLogCommand.Execute(g);if(!onlyLogs)await RevealAndCheck(window,g,Path.Combine(g.Directory,"request.json"),"running");
        }
        g.Status="completed";
        if(onlyExplorer){await RevealAndCheck(window,g,g.Request.Output,"completed");window.Close();return;}
        vm.NewCreation();vm.EnhancementEnabled=false;vm.Tier=0;vm.Ratio="1:1";vm.Prompt="A blue ceramic teapot beside a sunny window, realistic photography.";
        var pending=vm.GenerateAsync();await Until(()=>vm.IsRunning&&vm.Turns.LastOrDefault()?.BackendPid>0,90);var live=vm.Turns.Last();
        Invoke(DiagnosticButton(window,"GenerationProgressLog"));await Until(()=>vm.ShowLog&&vm.Logs.Contains("lightx2v"),120);Shot(window,"native-loading-log");
        await Until(()=>live.Step>=2||!vm.IsRunning,300);Assert(vm.IsRunning&&vm.Logs.Contains("step_index:"),"Real native image sampling updates the open log viewer during generation");Shot(window,"native-sampling-log");await pending;
        Assert(live.Completed&&vm.Logs.Contains("step_index: 8")&&vm.Toast.Length==0,"Real 512 generation completes with all sampling steps retained in the log panel");
        if(!onlyLogs)await RevealAndCheck(window,live,live.Request.Output,"completed");Shot(window,"completed-with-log");Files.Write(Path.Combine(root,"result.json"),live);window.Close();
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    static async Task RevealAndCheck(MainWindow window,Generation generation,string expected,string label)
    {
        var before=AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ClassNameProperty,"CabinetWClass")).Cast<AutomationElement>().Select(w=>w.Current.NativeWindowHandle).ToHashSet();
        var vm=window.ViewModel;var button=DiagnosticButton(window,"RevealGeneration");
        Assert(ReferenceEquals(button.Command,vm.RevealCommand)&&ReferenceEquals(button.CommandParameter,generation),"The Explorer button binds to the requested task");
        var clicked=false;button.Click+=(_,_)=>clicked=true;Invoke(button);await Until(()=>clicked,10);await Task.Delay(1000);
        Files.Write(Path.Combine(root,"explorer-action-"+label+".json"),new{vm.Toast,expected});
        AutomationElement? found=null;
        var name=Path.GetFileName(generation.Directory);
        await Until(()=>{
            var windows=AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ClassNameProperty,"CabinetWClass"));
            foreach(AutomationElement candidate in windows)
            {
                if(!candidate.Current.Name.Contains(name,StringComparison.Ordinal))continue;
                found=candidate;return true;
            }
            return false;
        },30);
        try
        {
            Assert(vm.Toast.Length==0&&found!=null,"Explorer opens the exact "+label+" task folder, including Unicode, spaces and commas");
            Files.Write(Path.Combine(root,"explorer-"+label+".json"),new{directory=generation.Directory,expected,windowTitle=found!.Current.Name});
        }
        finally{if(found!=null&&!before.Contains(found.Current.NativeWindowHandle))PostMessage(new IntPtr(found.Current.NativeWindowHandle),0x0010,IntPtr.Zero,IntPtr.Zero);}
    }
}
