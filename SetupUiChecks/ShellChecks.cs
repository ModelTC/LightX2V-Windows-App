using System.ComponentModel;
using System.IO;
using LightX2V.Windows;

internal static partial class Program
{
    static void ShellChecks()
    {
        foreach (var model in ModelCatalog.Models.Values)
        foreach (var modelScope in new[] { false, true })
        {
            var address = ModelPage.Address(model.Repository, modelScope);
            var expected = (modelScope ? "https://modelscope.cn/models/" : "https://huggingface.co/") + model.Repository;
            var launches = 0;
            WindowsShell.Launch(WindowsShell.WebRequest(address), 0x2000, request =>
            {
                launches++;
                Check(request.FileName == expected && request.UseShellExecute && request.Verb == "open" && request.ArgumentList.Count == 0,
                    "Model-page dispatch preserves the catalog repository and selected source: " + expected);
            });
            Check(launches == 1, "A model-page request invokes shell dispatch exactly once: " + expected);
        }

        var directory = Path.Combine(root, "shell-paths", "输出 中文, 空格");
        Directory.CreateDirectory(directory);
        var image = Path.Combine(directory, "图像, 1.png");
        File.WriteAllText(image, "Shell path fixture");
        foreach (var path in new[] { directory, image })
        {
            var request = WindowsShell.FolderRequest(path);
            Check(request.FileName == directory && request.UseShellExecute && request.Verb == "open" && request.ArgumentList.Count == 0,
                "Folder dispatch preserves Unicode, spaces and commas: " + Path.GetFileName(path));
        }

        try
        {
            WindowsShell.FolderRequest(Path.Combine(directory, "missing"));
            throw new Exception("A missing shell path was accepted.");
        }
        catch (DirectoryNotFoundException)
        {
            Check(true, "Missing output paths are rejected before shell dispatch");
        }

        foreach (var address in new[]
        {
            new Uri("file:///C:/Windows/system32/cmd.exe"),
            new Uri("ms-settings:defaultapps"),
            new Uri("relative", UriKind.Relative)
        })
        {
            try
            {
                WindowsShell.WebRequest(address);
                throw new Exception("An unsupported shell URI was accepted.");
            }
            catch (ArgumentException)
            {
                Check(true, "Non-web URI is rejected: " + address);
            }
        }

        var page = WindowsShell.WebRequest(ModelPage.Address(ModelCatalog.Get("turbo").Repository, true));
        foreach (var integrity in new[] { 0, 0x1000 })
        {
            var launched = false;
            var rejected = false;
            try
            {
                WindowsShell.Launch(page, integrity, _ => launched = true);
            }
            catch (DesktopIntegrationRestrictedException)
            {
                rejected = true;
            }
            Check(rejected && !launched, "Restricted integrity rejects the request before invoking shell dispatch: " + integrity);
        }

        try
        {
            WindowsShell.Launch(page, 0x2000, _ => throw new Win32Exception(1155));
            throw new Exception("A native shell error was swallowed.");
        }
        catch (Win32Exception error)
        {
            Check(error.NativeErrorCode == 1155, "Missing-handler errors propagate to the caller with their native error code");
        }
    }
}
