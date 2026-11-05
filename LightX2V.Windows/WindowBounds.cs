using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LightX2V.Windows;

// WindowChrome extends content into the native frame. Explicitly use this monitor's
// work area for maximization, including DPI changes and taskbars on any screen edge.
internal sealed class WindowBounds : IDisposable
{
    readonly Window window;
    readonly HwndSource source;
    readonly nint handle;
    public WindowBounds(Window window)
    {
        this.window = window;
        handle = new WindowInteropHelper(window).Handle;
        source = HwndSource.FromHwnd(handle);
        source.AddHook(Hook);
        FitMinimumAndInitialSize();
        window.Loaded += OnLoaded;
    }
    void FitMinimumAndInitialSize()
    {
        var monitor = GetMonitor();
        var scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
        var width = (monitor.Work.Right - monitor.Work.Left) / scale;
        var height = (monitor.Work.Bottom - monitor.Work.Top) / scale;
        window.MinWidth = Math.Min(1100, width);
        window.MinHeight = Math.Min(720, height);
        window.MaxWidth = width;
        window.MaxHeight = height;
        if (window.WindowState == WindowState.Normal)
        {
            window.Width = Math.Min(window.Width, width);
            window.Height = Math.Min(window.Height, height);
        }
    }
    void OnLoaded(object sender, RoutedEventArgs e) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,FitNormalPosition);
    void FitNormalPosition()
    {
        if (window.WindowState != WindowState.Normal || !GetWindowRect(handle,out var rect)) return;
        var work=GetMonitor().Work;
        var x=Math.Clamp(rect.Left,work.Left,Math.Max(work.Left,work.Right-(rect.Right-rect.Left)));
        var y=Math.Clamp(rect.Top,work.Top,Math.Max(work.Top,work.Bottom-(rect.Bottom-rect.Top)));
        if(x!=rect.Left||y!=rect.Top)SetWindowPos(handle,0,x,y,0,0,0x0015);
    }
    void UpdateWorkArea(){FitMinimumAndInitialSize();FitNormalPosition();}
    MonitorInfo GetMonitor()
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(MonitorFromWindow(handle, 2), ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }
    nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0024) // WM_GETMINMAXINFO: physical pixels, never WPF DIPs.
        {
            var m = GetMonitor();
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaxPosition = new Point(m.Work.Left - m.Monitor.Left, m.Work.Top - m.Monitor.Top);
            info.MaxSize = new Point(m.Work.Right - m.Work.Left, m.Work.Bottom - m.Work.Top);
            info.MaxTrackSize = info.MaxSize;
            var scale = Math.Max(96,GetDpiForWindow(handle))/96.0;
            info.MinTrackSize = new Point(
                Math.Min((int)Math.Ceiling(window.MinWidth*scale),info.MaxSize.X),
                Math.Min((int)Math.Ceiling(window.MinHeight*scale),info.MaxSize.Y));
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
        }
        else if (message == 0x02E0 || message == 0x001A) // DPI / work area changed.
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateWorkArea);
        return 0;
    }
    public void Dispose() {window.Loaded -= OnLoaded;source.RemoveHook(Hook); }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; public Point(int x, int y) { X=x; Y=y; } }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError=true)] static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
}
