using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace LightX2V.Windows;

internal static class DesktopShortcuts
{
    // Read the stored filesystem target directly. Resolve could locate a different
    // install or change the link when the original target has already been removed.
    internal static string? ApplicationTarget(string shortcut)
    {
        if (!Path.GetExtension(shortcut).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        IShellLinkW? link = null;
        try
        {
            if ((File.GetAttributes(shortcut) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return null;
            link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcut, 0);
            var target = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            var arguments = new StringBuilder(32768);
            link.GetArguments(arguments, arguments.Capacity);
            var path = Environment.ExpandEnvironmentVariables(target.ToString());
            return arguments.Length == 0 && Path.IsPathFullyQualified(path) ? AppMaintenance.Normalize(path) : null;
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Could not read a desktop shortcut: {shortcut}: {error.Message}");
            return null;
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr data, uint flags);
        void GetIdList(out IntPtr item);
        void SetIdList(IntPtr item);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
    }
}
