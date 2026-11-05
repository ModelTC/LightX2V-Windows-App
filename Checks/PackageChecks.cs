using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task PackageRun(string executable)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false};
        start.Environment.Remove("DOTNET_ROOT");start.Environment.Remove("DOTNET_ROOT_X64");
        using var process=Process.Start(start)!;
        try
        {
            await Until(()=>{process.Refresh();return process.HasExited||process.MainWindowHandle!=IntPtr.Zero;},60);
            Assert(!process.HasExited,"Published app starts with its bundled desktop runtime");
            var window=AutomationElement.FromHandle(process.MainWindowHandle);
            var textCondition=new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text);
            string[] Texts()=>window.FindAll(TreeScope.Descendants,textCondition).Cast<AutomationElement>().Select(e=>e.Current.Name).ToArray();
            // The ready text is hidden when model preparation is collapsed.
            // The composer attachment control also reflects backend readiness.
            await Until(()=>window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"AddImages"))?.Current.IsEnabled==true,90);
            var radio=window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"Resolution2K"));
            Assert(radio!=null&&radio.Current.IsEnabled,"Published 2K selector is enabled");
            var selection=(SelectionItemPattern)radio!.GetCurrentPattern(SelectionItemPattern.Pattern);selection.Select();
            await Until(()=>selection.Current.IsSelected&&Texts().Contains("2048 × 2048 px"));
            Assert(true,"Published 2K selection updates the actual output dimensions");
            Files.Write(Path.Combine(root,"packaged-smoke.json"),new{executable,twoKChecked=true,noDotnetRoot=true,time=DateTimeOffset.Now});
        }
        finally
        {
            if(!process.HasExited){process.CloseMainWindow();var exit=process.WaitForExitAsync();if(await Task.WhenAny(exit,Task.Delay(15000))!=exit&&!process.HasExited)process.Kill(true);await exit;}
        }
    }
}
