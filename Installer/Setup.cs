using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

internal static class Setup
{
    static readonly Assembly Binary = Assembly.GetExecutingAssembly();
    static readonly bool Chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
    static string Label(string chinese, string english) { return Chinese ? chinese : english; }

    [STAThread]
    static void Main()
    {
        Window window;
        using (var layout = Binary.GetManifestResourceStream("Setup.xaml"))
            window = (Window)XamlReader.Load(layout);
        var logo = new BitmapImage();
        logo.BeginInit();
        logo.StreamSource = Binary.GetManifestResourceStream("AppIcon.png");
        logo.EndInit();
        ((Image)window.FindName("Logo")).Source = logo;
        window.Icon = logo;
        var location = (Button)window.FindName("Location");
        var install = (Button)window.FindName("Install");
        var status = (TextBlock)window.FindName("Status");
        var progress = (ProgressBar)window.FindName("Progress");
        string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        string destination = Path.Combine(parent, "LightX2V");
        location.Content = Label("选择安装位置", "Choose install location");
        location.ToolTip = destination;
        install.Content = Label("立即安装", "Install now");
        bool busy = false;
        bool installed = false;
        window.Closing += delegate(object sender, CancelEventArgs args) { args.Cancel = busy; };
        location.Click += delegate {
            using (var picker = new Forms.FolderBrowserDialog()) {
                picker.Description = Label("选择用于安装 LightX2V 的父文件夹", "Choose the parent folder for LightX2V");
                picker.SelectedPath = parent;
                if (picker.ShowDialog() == Forms.DialogResult.OK) {
                    parent = picker.SelectedPath;
                    destination = Path.Combine(parent, "LightX2V");
                    status.Text = destination;
                    location.ToolTip = destination;
                }
            }
        };
        install.Click += async delegate {
            try {
                if (installed) {
                    System.Diagnostics.Process.Start(Path.Combine(destination, "LightX2V.exe"));
                    window.Close();
                    return;
                }
                busy = true;
                install.IsEnabled = location.IsEnabled = false;
                progress.Visibility = Visibility.Visible;
                status.Text = Label("正在安装…", "Installing…");
                var update = new Progress<int>(value => progress.Value = value);
                await System.Threading.Tasks.Task.Run(() => Extract(parent, destination, update));
                installed = true;
                location.Visibility = Visibility.Collapsed;
                install.Content = Label("打开 LightX2V", "Open LightX2V");
                status.Text = Label("安装完成", "Installation complete");
                AddDesktopShortcut(destination);
            } catch (Exception error) {
                status.Text = error.Message;
            } finally {
                busy = false;
                install.IsEnabled = location.IsEnabled = true;
                progress.Visibility = Visibility.Collapsed;
            }
        };
        new Application().Run(window);
    }

    static void AddDesktopShortcut(string destination)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string link = Path.Combine(desktop, "LightX2V.lnk");
        if (!Directory.Exists(desktop) || File.Exists(link)) return;
        string temporary = Path.Combine(desktop, Guid.NewGuid().ToString("N") + ".lnk");
        try {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            dynamic shortcut = shell.CreateShortcut(temporary);
            shortcut.TargetPath = Path.Combine(destination, "LightX2V.exe");
            shortcut.WorkingDirectory = destination;
            shortcut.Save();
            File.Move(temporary, link);
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    static void Extract(string parent, string destination, IProgress<int> progress)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException(Label("该位置已有 LightX2V。请选择其他位置，或打开现有应用进行更新。",
                "LightX2V already exists here. Choose another location or open the existing app to update."));
        string staging = Path.Combine(parent, ".LightX2V-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try {
            using (var stream = Binary.GetManifestResourceStream("package.zip"))
            using (var zip = new ZipArchive(stream)) {
                string prefix = zip.Entries.First().FullName.Split('/')[0] + "/";
                int count = 0;
                foreach (var entry in zip.Entries) {
                    string name = entry.FullName;
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException(name);
                    string target = Path.GetFullPath(Path.Combine(staging, name.Substring(prefix.Length)));
                    if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                        input.CopyTo(output);
                    progress.Report(++count * 100 / zip.Entries.Count);
                }
            }
            Directory.Move(staging, destination);
        } finally {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
