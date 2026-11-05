using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task LayoutRun(string fixture)
    {
        var settings=AppSettings.Detect();
        settings.Workspace=Path.Combine(root,"workspace");
        var state=Files.Read<WorkspaceState>(fixture)!;
        state.Generations=state.Generations.Where(g=>g.Completed).ToList();
        Assert(state.Generations.Count>0,"Real completed image fixture available");
        Files.Write(Path.Combine(settings.Workspace,"workspace.json"),state);
        var prefs=Path.Combine(root,"preferences");
        Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});
        using var vm=new MainViewModel(prefs);
        var window=new MainWindow(vm);window.Show();
        await Until(()=>!vm.IsChecking&&vm.Ready,60);
        vm.NewCreation();await Task.Delay(300);
        var area=WorkArea(window);
        var rootElement=(FrameworkElement)window.FindName("LayoutRoot");
        var settingsButton=(Button)window.FindName("SettingsButton");
        var composer=(FrameworkElement)window.FindName("Composer");
        var bolt=(Image)window.FindName("SidebarBolt");
        var parent=(FrameworkElement)bolt.Parent;
        var boltCenter=bolt.TranslatePoint(new System.Windows.Point(bolt.ActualWidth/2,bolt.ActualHeight/2),parent);
        Assert(Math.Abs(boltCenter.X-parent.ActualWidth/2)<0.6&&Math.Abs(boltCenter.Y-parent.ActualHeight/2)<0.6,"Brand bolt centered in both axes");
        Assert(((DrawingImage)bolt.Source).Drawing.Bounds.X==0&&((DrawingImage)bolt.Source).Drawing.Bounds.Y==0,"Bolt geometry has no leading coordinate space");
        var glyph=Descendants<TextBlock>(settingsButton).Single();
        Assert(glyph.ActualWidth<=settingsButton.ActualWidth&&glyph.ActualHeight<=settingsButton.ActualHeight,"Full settings glyph fits its hit target");
        Assert(settingsButton.Padding==new Thickness(0),"Settings hit target has no clipping padding");
        Assert(Descendants<Button>(composer).All(b=>b.Command!=vm.LogCommand),"Unrequested composer log shortcut removed");
        var heading=(FrameworkElement)window.FindName("ModelHeading");
        Assert(heading.TranslatePoint(new System.Windows.Point(),rootElement).Y<70,"Model section begins in aligned top header");
        Shot(window,"welcome");Fit(window,area,"normal");
        var mmiPointer=Marshal.AllocHGlobal(40);
        try {Marshal.Copy(new byte[40],0,mmiPointer,40);SendMessageW(new WindowInteropHelper(window).Handle,0x0024,0,mmiPointer);var mmi=new int[10];Marshal.Copy(mmiPointer,mmi,0,10);var dpi=VisualTreeHelper.GetDpi(window);Assert(mmi[6]>=window.MinWidth*dpi.DpiScaleX-1&&mmi[7]>=window.MinHeight*dpi.DpiScaleY-1,"Native resize constraints preserve the minimum usable layout");}
        finally {Marshal.FreeHGlobal(mmiPointer);}

        window.Width=1100;window.Height=720;await Task.Delay(300);Fit(window,area,"minimum");Shot(window,"minimum");
        window.Width=1240;window.Height=780;await Task.Delay(200);
        var example=Descendants<Button>(window).First(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="ExampleWonder");
        example.Command!.Execute(example.CommandParameter);await Task.Delay(100);
        var generate=(Button)window.FindName("GenerateButton");
        Assert(generate.IsEnabled&&generate.ActualWidth==generate.ActualHeight,"Compact circular generate control is enabled for a valid prompt");
        var peer=new ButtonAutomationPeer(settingsButton);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        await Until(()=>Application.Current.Windows.OfType<SettingsWindow>().Any());
        var dialog=Application.Current.Windows.OfType<SettingsWindow>().Single();
        Shot(dialog,"settings-opened");dialog.Close();
        Assert(true,"Settings icon opens the real settings window");
        vm.NewCreation();vm.Selected=vm.Recent.First();await Task.Delay(300);
        foreach(var size in new[]{(1100,720),(1240,780)})
        {
            window.Width=size.Item1;window.Height=size.Item2;await Task.Delay(200);Fit(window,area,$"conversation-{size.Item1}");
        }
        window.Width=1240;window.Height=780;
        for(int cycle=0;cycle<3;cycle++)
        {
            if(cycle==1)SendMessageW(new WindowInteropHelper(window).Handle,0x0112,0xF030,0);
            else
            {
                var maximize=new ButtonAutomationPeer((Button)window.FindName("MaximizeButton"));
                ((IInvokeProvider)maximize.GetPattern(PatternInterface.Invoke)!).Invoke();
            }
            await Until(()=>window.WindowState==WindowState.Maximized);await Task.Delay(400);
            Fit(window,area,$"maximized-{cycle}");
            var bounds=ScreenRect(rootElement);
            Assert(Math.Abs(bounds.Width-(area.Right-area.Left))<=2&&Math.Abs(bounds.Height-(area.Bottom-area.Top))<=2,"Maximized content equals this monitor's work area");
            if(cycle==0){Shot(window,"maximized");vm.ShowLog=true;await Task.Delay(150);Fit(window,area,"maximized-with-log");Shot(window,"maximized-with-log");vm.ShowLog=false;}
            window.WindowState=WindowState.Normal;await Task.Delay(300);Fit(window,area,$"restored-{cycle}");
        }
        vm.SetLanguage("en");await Task.Delay(200);Shot(window,"english");vm.SetLanguage("zh");
        vm.ShowInspector=false;await Task.Delay(150);Fit(window,area,"inspector-hidden");Shot(window,"inspector-hidden");vm.ShowInspector=true;
        ((ScrollViewer)window.FindName("Conversation")).ScrollToTop();await Task.Delay(200);Shot(window,"conversation");
        Files.Write(Path.Combine(root,"geometry.json"),new{dpi=VisualTreeHelper.GetDpi(window).DpiScaleX,workArea=new{area.Left,area.Top,area.Right,area.Bottom},window=new{window.ActualWidth,window.ActualHeight},root=ScreenRect(rootElement),composer=ScreenRect(composer),settings=ScreenRect(settingsButton)});
        vm.NewCreation();vm.ExampleCommand.Execute("light");
        await Until(()=>((Button)window.FindName("GenerateButton")).IsEnabled);
        var submit=new ButtonAutomationPeer((Button)window.FindName("GenerateButton"));
        ((IInvokeProvider)submit.GetPattern(PatternInterface.Invoke)!).Invoke();
        await Until(()=>vm.IsRunning&&vm.Turns.Count>0&&vm.Turns[^1].BackendPid>0,30);
        var job=vm.Turns[^1];
        Assert(!((Button)window.FindName("GenerateButton")).IsVisible&&((Button)window.FindName("StopButton")).IsVisible,"Send arrow starts a real task and becomes stop control");
        window.WindowState=WindowState.Maximized;await Task.Delay(250);Fit(window,area,"maximized-running");Shot(window,"running");
        await Until(()=>((Button)window.FindName("StopButton")).IsEnabled);
        var stop=new ButtonAutomationPeer((Button)window.FindName("StopButton"));
        ((IInvokeProvider)stop.GetPattern(PatternInterface.Invoke)!).Invoke();
        await Until(()=>!vm.IsRunning,30);
        Assert(job.Status=="cancelled"&&!Alive(job.BackendPid),"Stop control cancels the owned native inference process");
        window.Close();
    }
    static void Fit(MainWindow window, NativeRect area, string name)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle,out var native);
        Files.Write(Path.Combine(root,"geometry-"+name+".json"),new{native=new{native.Left,native.Top,native.Right,native.Bottom},windowLeft=window.Left,windowTop=window.Top,dpi=VisualTreeHelper.GetDpi(window).DpiScaleX,work=new{area.Left,area.Top,area.Right,area.Bottom},root=ScreenRect((FrameworkElement)window.FindName("LayoutRoot"))});
        foreach(var id in new[]{"LayoutRoot","Composer","SettingsButton","GenerateButton","Inspector"})
        {
            var el=(FrameworkElement)window.FindName(id);if(!el.IsVisible)continue;var r=ScreenRect(el);
            Assert(r.Left>=area.Left-1&&r.Top>=area.Top-1&&r.Right<=area.Right+1&&r.Bottom<=area.Bottom+1,$"{name}: {id} remains within visible work area");
        }
    }
    static Rect ScreenRect(FrameworkElement el)=>new(el.PointToScreen(new System.Windows.Point()),el.PointToScreen(new System.Windows.Point(el.ActualWidth,el.ActualHeight)));
    static NativeRect WorkArea(Window window){var info=new NativeMonitor{Size=Marshal.SizeOf<NativeMonitor>()};if(!GetMonitorInfoW(MonitorFromWindow(new WindowInteropHelper(window).Handle,2),ref info))throw new Exception("Cannot query work area.");return info.Work;}
    [StructLayout(LayoutKind.Sequential)] public struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct NativeMonitor{public int Size;public NativeRect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")]static extern nint MonitorFromWindow(nint hwnd,uint flags);
    [DllImport("user32.dll")]static extern bool GetMonitorInfoW(nint monitor,ref NativeMonitor info);
    [DllImport("user32.dll")]static extern bool GetWindowRect(nint hwnd,out NativeRect rect);
    [DllImport("user32.dll")]static extern nint SendMessageW(nint hwnd,int message,nint wParam,nint lParam);
}
