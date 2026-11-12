using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightX2V.Windows;
internal static partial class Program
{
    static async Task DesktopChecks()
    {
        var fixture=ReadinessFixture("desktop-parity");var downloads=new FakeModelDownloader();
        using var vm=new MainViewModel(fixture.Preferences,downloads,new ReadinessCatalog{Hold=false},fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));
        vm.EnhancementEnabled=true;var window=new MainWindow(vm){WindowState=WindowState.Maximized};window.Show();window.Activate();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);actualDesktopScale=VisualTreeHelper.GetDpi(window).DpiScaleX;
        foreach(var language in new[]{"zh","en"})
        {
            vm.SetLanguage(language);var settings=new SettingsWindow(vm){Owner=window};settings.Show();await Task.Delay(500);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var picker=Children<ComboBox>((DependencyObject)settings.Content).Single();
            Check(picker.ActualHeight>=32&&picker.ActualHeight>=picker.FontSize*1.8,"The live "+language+" language picker has room above and below its text");
            Check(!Children<ScrollViewer>((DependencyObject)settings.Content).Any(s=>s.VerticalScrollBarVisibility==ScrollBarVisibility.Auto),"The live settings card stack has no vertical scrollbar");
            CaptureDesktopWindow(window,"desktop-settings-"+language);picker.IsDropDownOpen=true;await Task.Delay(350);CaptureDesktopWindow(window,"desktop-language-options-"+language);picker.IsDropDownOpen=false;settings.Close();
            foreach(var (scale,width,height) in new[]{(100,1280,800),(150,1067,650),(200,960,540)})
            {
                var small=new SettingsWindow(vm);await Render(small,Math.Min(640,width),Math.Min(490,height),"settings-layout-"+language+"-"+scale,scale/100.0);
                await Render(window,width,height,"workspace-layout-"+language+"-"+scale,scale/100.0);small.Close();
            }
        }
        await Render(window,(int)window.ActualWidth,(int)window.ActualHeight,"workspace-restored");
        vm.SetLanguage("zh");
        foreach(var (card,source) in new[]{(vm.TurboCard,"huggingFace"),(vm.T2ICard,"modelScope"),(vm.I2ICard,"auto")})
        {
            vm.UseAutomaticDownloadSource=source=="auto";if(source=="modelScope")vm.UseModelScope=true;if(source=="huggingFace")vm.UseHuggingFace=true;
            var anchor=Children<Button>((DependencyObject)window.Content).Single(b=>AutomationProperties.GetAutomationId(b)=="ModelDownloadGuide"&&ReferenceEquals(b.DataContext,card));
            ((IInvokeProvider)new ButtonAutomationPeer(anchor).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(350);var popup=vm.ActiveModelGuide!;
            Check(popup?.IsOpen==true&&ReferenceEquals(popup.PlacementTarget,anchor),"The "+card.Key+" card opens its anchored guide through the real button command");
            var command=Children<TextBox>((DependencyObject)popup!.Child).Single();
            var arrow=Children<System.Windows.Shapes.Path>((DependencyObject)popup.Child).Single();
            var tip=arrow.PointToScreen(new Point(8,8));var leftEdge=anchor.PointToScreen(new Point(0,anchor.ActualHeight/2));var dpi=VisualTreeHelper.GetDpi(anchor);
            Check(Math.Abs((leftEdge.X-tip.X)/dpi.DpiScaleX-4)<2&&Math.Abs(leftEdge.Y-tip.Y)/dpi.DpiScaleY<2,"The "+card.Key+" guide arrow meets the button's left-center with a four-DIP gap");
            Check(command.Text.Contains("'./models/"+card.Repository+"'")&&command.Text.StartsWith(source=="modelScope"?"modelscope ":"hf "),"The "+card.Key+" guide uses the selected source and relative PowerShell directory");
            Check(command.ViewportHeight>=command.FontSize*1.3,"The command viewport leaves a full text line above its horizontal scrollbar");
            CaptureDesktopWindow(window,"desktop-guide-"+card.Key);
            var clipboard=Clipboard.GetDataObject();
            try
            {
                var copy=Children<Button>((DependencyObject)popup.Child).Single(b=>AutomationProperties.GetAutomationId(b)=="CopyModelDownloadCommand");
                ((IInvokeProvider)new ButtonAutomationPeer(copy).GetPattern(PatternInterface.Invoke)).Invoke();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(Clipboard.GetText()==command.Text&&copy.Content?.ToString()==vm.T["Copied"],"Copy command reports success and copies the complete unwrapped command");
            }
            finally{if(clipboard==null)Clipboard.Clear();else Clipboard.SetDataObject(clipboard,true);}
            DesktopNative.keybd_event(0x1B,0,0,UIntPtr.Zero);DesktopNative.keybd_event(0x1B,0,2,UIntPtr.Zero);await Task.Delay(100);
            Check(!popup.IsOpen,"Escape closes the anchored "+card.Key+" guide");
        }
        Check(downloads.Requests.Count==0,"Opening guides and copying commands never starts a download or network source probe");
        var task=Path.Combine(fixture.Settings.Workspace,"outputs","desktop-log");Directory.CreateDirectory(task);
        var log=Path.Combine(task,"run.log");File.WriteAllLines(log,Enumerable.Range(0,3000).Select(i=>"Live log line "+i+" · Unicode 中文"));
        var generation=new Generation{Directory=task,Status="running"};vm.OpenLogCommand.Execute(generation);
        await Task.Delay(100);var viewer=(TextBox)window.FindName("LogViewer");viewer.ScrollToLine(10);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);var offset=viewer.VerticalOffset;
        File.AppendAllText(log,"New tail while reading\n");await Task.Delay(1300);
        Check(Math.Abs(viewer.VerticalOffset-offset)<2,"Live log updates preserve a reader's position above the end");
        viewer.ScrollToEnd();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);File.AppendAllText(log,"Follow the new final line\n");await Task.Delay(1300);
        Check(viewer.ExtentHeight-viewer.ViewportHeight-viewer.VerticalOffset<16,"Returning to the log bottom resumes following new output");
        CaptureDesktopWindow(window,"desktop-live-log");
        await WindowsShell.RevealAsync(log);
        Check(vm.Toast.Length==0,"The real desktop shell accepts the Unicode-safe log file selection request");
        generation.Status="completed";
        window.Close();
    }
    static void CaptureDesktopWindow(Window window,string name)
    {
        DesktopNative.GetWindowRect(new WindowInteropHelper(window).Handle,out var rect);var width=rect.Right-rect.Left;var height=rect.Bottom-rect.Top;
        var screen=DesktopNative.GetDC(IntPtr.Zero);var memory=DesktopNative.CreateCompatibleDC(screen);var bitmap=DesktopNative.CreateCompatibleBitmap(screen,width,height);var old=DesktopNative.SelectObject(memory,bitmap);
        try
        {
            if(!DesktopNative.BitBlt(memory,0,0,width,height,screen,rect.Left,rect.Top,0x40CC0020))throw new InvalidOperationException("Desktop capture failed");
            var source=Imaging.CreateBitmapSourceFromHBitmap(bitmap,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(source));using var file=File.Create(Path.Combine(root,name+".png"));png.Save(file);
        }
        finally{DesktopNative.SelectObject(memory,old);DesktopNative.DeleteObject(bitmap);DesktopNative.DeleteDC(memory);DesktopNative.ReleaseDC(IntPtr.Zero,screen);}
    }
    static class DesktopNative
    {
        [StructLayout(LayoutKind.Sequential)]internal struct Rect{public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")]internal static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
        [DllImport("user32.dll")]internal static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]internal static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
        [DllImport("user32.dll")]internal static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
        [DllImport("gdi32.dll")]internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")]internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc,int width,int height);
        [DllImport("gdi32.dll")]internal static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
        [DllImport("gdi32.dll")]internal static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")]internal static extern bool DeleteDC(IntPtr value);
        [DllImport("gdi32.dll")]internal static extern bool BitBlt(IntPtr to,int x,int y,int width,int height,IntPtr from,int fromX,int fromY,uint code);
    }
}
