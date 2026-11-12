using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightX2V.Windows;

internal static partial class Program
{
    static bool startupOnly,nativeOnly,maintenanceOnly;
    static string root = "";
    static readonly List<string> passed = [];
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        passed.Add(message);
    }

    static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }

    static async Task Render(Window window, int width, int height, string name)
    {
        var content = (FrameworkElement)window.Content;
        content.DataContext = window.DataContext;
        content.Width = width;
        content.Height = height;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        content.UpdateLayout();
        foreach (var id in new[] { "SettingsSave", "SettingsClose", "SetupContinue", "SetupBack", "SetupClose", "EnvironmentSearch", "EnvironmentClose" })
        {
            var button = Children<Button>(content).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == id);
            if (button == null) continue;
            var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
            Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= width + 1 && bounds.Bottom <= height + 1,
                "Footer action stays inside " + name + ": " + id);
        }
        foreach (var scroll in Children<ScrollViewer>(content).Where(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto))
            Check(scroll.ExtentWidth <= scroll.ViewportWidth + 2, "Content fits the horizontal viewport in " + name);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, width, height)
            }, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(root, name + ".png"));
        png.Save(file);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("Usage: SetupUiChecks.exe <output-directory> [core|startup|native|maintenance]");
            return 2;
        }
        var mode = args.Length == 2 ? args[1] : "core";
        if (mode is not ("core" or "startup" or "native" or "maintenance"))
        {
            Console.Error.WriteLine($"Unknown check mode '{mode}'. Supported modes are core, startup, native and maintenance.");
            return 2;
        }
        startupOnly = mode == "startup";nativeOnly=mode=="native";maintenanceOnly=mode=="maintenance";
        root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LightX2V.Windows;component/Resources/Theme.xaml")
        });
        var result = 1;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(Path.Combine(root, "bindings.log")));
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Run();
                File.WriteAllText(Path.Combine(root, "checks.json"), JsonSerializer.Serialize(new
                {
                    passed,
                    completedAt = DateTimeOffset.Now,
                    offscreenRendering = true,
                    realUiAutomation = false
                }, new JsonSerializerOptions { WriteIndented = true }));
                result = 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString());
            }
            finally
            {
                Trace.Flush();
                app.Shutdown();
            }
        });
        app.Run();
        return result;
    }

    static async Task Run()
    {
        if(maintenanceOnly){await MaintenanceProcessChecks();return;}
        if(nativeOnly){await NativeAppChecks();return;}
        await StartupChecks();
        await ReadinessChecks();
        if (startupOnly) return;

        await ParityChecks();
        await VisualParityChecks();
        RecoveryChecks();
        await BridgeChecks();
        await ModelDownloadChecks();
        await HardwareDetectionChecks();
        await DriverChecks();
        await WizardPreparationChecks();
        await CompletionChecks();
        await EnvironmentSelectionChecks();
        ShellChecks();
        await InstallerRegressionChecks();
        await WorkspaceRegressionChecks();
    }
}
