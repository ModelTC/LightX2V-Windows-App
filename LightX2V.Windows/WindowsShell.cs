using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LightX2V.Windows;

internal static class WindowsShell
{
    internal static ProcessStartInfo FolderRequest(string path)
    {
        path=Path.GetFullPath(path);
        if(!Directory.Exists(path)&&!File.Exists(path))throw new DirectoryNotFoundException("The requested folder or file is missing.");
        var directory=Directory.Exists(path)?path:Path.GetDirectoryName(path)!;
        return new(directory){UseShellExecute=true,Verb="open"};
    }
    internal static ProcessStartInfo WebRequest(Uri address)
    {
        if(!address.IsAbsoluteUri||address.Scheme is not ("https" or "http"))
            throw new ArgumentException("Only HTTP or HTTPS model pages can be opened.",nameof(address));
        return new(address.AbsoluteUri){UseShellExecute=true,Verb="open"};
    }
    internal static void Launch(ProcessStartInfo request,int integrity,Action<ProcessStartInfo> launch)
    {
        // Respect the Windows integrity boundary; never elevate the caller.
        if(integrity<0x2000)throw new DesktopIntegrationRestrictedException();
        launch(request);
    }
    static void Start(ProcessStartInfo request)=>Process.Start(request)?.Dispose();
    public static void OpenWebPage(Uri address)=>Launch(WebRequest(address),CurrentIntegrity(),Start);
    public static Task RevealAsync(string path)
    {
        path=Path.GetFullPath(path);
        if(File.Exists(path))
        {
            if(CurrentIntegrity()<0x2000)throw new DesktopIntegrationRestrictedException();
            var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>
            {
                var initialized=CoInitializeEx(IntPtr.Zero,2);IntPtr item=IntPtr.Zero;
                try
                {
                    Marshal.ThrowExceptionForHR(initialized);
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(path,IntPtr.Zero,out item,0,out _));
                    // A full item PIDL with cidl=0 opens its parent and selects the file.
                    Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item,0,IntPtr.Zero,0));done.TrySetResult();
                }
                catch(Exception error){done.TrySetException(error);}
                finally{if(item!=IntPtr.Zero)Marshal.FreeCoTaskMem(item);if(initialized>=0)CoUninitialize();}
            }){IsBackground=true,Name="Reveal task file"};
            thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
        }
        Launch(FolderRequest(path),CurrentIntegrity(),Start);
        return Task.CompletedTask;
    }
    internal static int CurrentIntegrity()
    {
        using var identity=WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        GetTokenInformation(identity.AccessToken,25,IntPtr.Zero,0,out var length);
        if(length<=0)throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer=Marshal.AllocHGlobal(length);
        try
        {
            if(!GetTokenInformation(identity.AccessToken,25,buffer,length,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
            var sid=Marshal.ReadIntPtr(buffer);
            var count=Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if(count==0)throw new InvalidOperationException("Windows returned an invalid integrity label.");
            return Marshal.ReadInt32(GetSidSubAuthority(sid,(uint)(count-1)));
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    [DllImport("ole32.dll")]static extern int CoInitializeEx(IntPtr reserved,uint mode);
    [DllImport("ole32.dll")]static extern void CoUninitialize();
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SHParseDisplayName(string path,IntPtr context,out IntPtr item,uint attributes,out uint actualAttributes);
    [DllImport("shell32.dll")]static extern int SHOpenFolderAndSelectItems(IntPtr item,uint count,IntPtr children,uint flags);
    [DllImport("advapi32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(SafeAccessTokenHandle token,int informationClass,IntPtr buffer,int length,out int returned);
    [DllImport("advapi32.dll")]static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")]static extern IntPtr GetSidSubAuthority(IntPtr sid,uint index);
}

internal sealed class DesktopIntegrationRestrictedException : InvalidOperationException
{
    public DesktopIntegrationRestrictedException():base("Windows desktop integration is unavailable at low process integrity."){}
}
