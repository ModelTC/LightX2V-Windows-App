using System.Runtime.InteropServices;
using System.Text;

namespace LightX2V.Windows;

// Use the filesystem's short names for managed runtimes. AMD ships long kernel
// filenames; this avoids MAX_PATH without changing policy or creating links.
internal static class SetupPaths
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetShortPathName(string path,StringBuilder output,uint length);
    public static string Short(string path)
    {
        path=Path.GetFullPath(path);var buffer=new StringBuilder(32768);
        return GetShortPathName(path,buffer,(uint)buffer.Capacity)>0?buffer.ToString():path;
    }
    public static string Ensure(string directory)
    {
        var path=Short(directory);
        if(path.Length>80)throw new IOException("Choose a shorter workspace path (for example C:\\LightX2V) so Windows can load the AMD libraries.");
        return path;
    }
}
