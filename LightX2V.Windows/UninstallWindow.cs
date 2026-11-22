using System.Windows;
using System.Windows.Controls;

namespace LightX2V.Windows;

public sealed class UninstallWindow : Window
{
    internal static async Task RunHelperAsync(string planFile, string hash)
    {
        var application = Application.Current;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        UninstallProgress? progress = null;
        var exitCode = 1;
        try
        {
            progress = new UninstallProgress(MaintenanceProcess.ValidatePlanFile(planFile));
            var plan = await AppMaintenance.BeginAsync(planFile, hash);
            while (true)
            {
                try
                {
                    await AppMaintenance.ExecuteAsync(plan, progress);
                    exitCode = 0;
                    break;
                }
                catch (Exception error)
                {
                    var details = progress.ReportFailure(error);
                    var message = L10n.Current["Uninstall could not finish. Your remaining files were preserved."] + "\n\n" +
                        details + "\n\n" + error.Message + "\n\n" +
                        L10n.Current["Close any program using these files and resolve any access issues. Retry now?"];
                    if (MessageBox.Show(message, "LightX2V", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                        break;
                }
            }
        }
        catch (Exception error)
        {
            var details = progress?.ReportFailure(error);
            MessageBox.Show(L10n.Current["Uninstall could not start."] + "\n\n" + error.Message + "\n\n" + details,
                "LightX2V", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            try { if (progress != null) MaintenanceProcess.ScheduleCleanup(planFile); }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            application.Shutdown(exitCode);
        }
    }

    public UninstallWindow(MainViewModel vm)
    {
        DataContext = vm;
        Title = vm.T["Uninstall"];
        Width = Math.Min(640, SystemParameters.WorkArea.Width);
        Height = Math.Min(560, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.White;
        SetupUi.Resources(this);

        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var close = SetupUi.Button(this, "Close", id: "CloseUninstall");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.IsCancel = true;
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        var icon = SetupUi.Logo();
        icon.Width = icon.Height = 28;
        header.Children.Add(icon);
        var title = SetupUi.Text("Uninstall", 18, true);
        title.Margin = new Thickness(10, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(SetupUi.Text("Choose what to remove", 15, true));
        var instruction = SetupUi.Text("Select the files to delete. Unchecked items will be kept.", 12, muted: true);
        instruction.Margin = new Thickness(0, 10, 0, 10);
        body.Children.Add(instruction);
        var groups = new Dictionary<string, CheckBox>();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = SetupUi.Muted };
        var remove = SetupUi.Button(this, "Delete selected items…", id: "ConfirmUninstall");
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        UninstallPlan? plan = null;
        var working = false;
        void Refresh()
        {
            remove.IsEnabled = plan != null && !working && groups.Values.Any(box => box.IsChecked == true);
            close.IsEnabled = !working;
            foreach (var pair in groups)
                pair.Value.IsEnabled = !working && plan?.Items.Any(item => item.Group == pair.Key) == true;
        }
        CheckBox Choice(string group, FrameworkElement label, bool defaultSelected)
        {
            var items = plan!.Items.Where(item => item.Group == group).ToArray();
            var box = new CheckBox
            {
                Content = label, Style = (Style)FindResource("UninstallChoice"),
                IsChecked = defaultSelected && items.Length > 0 && items.All(item => !item.External)
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, "Uninstall-" + group);
            groups.Add(group, box);
            box.Checked += (_, _) => Refresh();
            box.Unchecked += (_, _) => Refresh();
            return box;
        }

        try
        {
            plan = AppMaintenance.Discover(vm.Settings, vm.PreferencesDirectory, AppContext.BaseDirectory);
            foreach (var (group, label, detail) in new[]
            {
                ("models", "Uninstall model weights", "Image generation and prompt enhancement model weights."),
                ("workspace", "Uninstall workspace records", "Generated images, history, program files, caches and logs."),
                ("application", "Uninstall app and configuration", "The app, preferences, workspace location records and its shortcuts.")
            })
            {
                var text = new StackPanel { Margin = new Thickness(5, 0, 0, 0) };
                text.Children.Add(SetupUi.Text(label, 13, true));
                var subtitle = SetupUi.Text(detail, 11, muted: true);
                subtitle.Margin = new Thickness(0, 5, 0, 0);
                text.Children.Add(subtitle);
                if (plan.Items.Any(item => item.Group == group && item.External))
                    text.Children.Add(SetupUi.Text("Includes configured external folders.", 10, muted: true));
                var card = new StackPanel();
                card.Children.Add(Choice(group, text, defaultSelected: true));
                if (group == "workspace" && plan.Items.Any(item => item.Group == AppMaintenance.ManagedEnvironmentGroup))
                {
                    var environmentBody = new Border { Margin = new Thickness(20, 8, 0, 0) };
                    var environment = Choice(AppMaintenance.ManagedEnvironmentGroup,
                        SetupUi.Text("Also remove managed Python environments", 11, muted: true),
                        defaultSelected: false);
                    environmentBody.Child = environment;
                    card.Children.Add(environmentBody);
                }
                body.Children.Add(new Border
                {
                    Child = card, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 242)),
                    Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(10)
                });
            }
        }
        catch (Exception exception) { error.Text = vm.T[exception.Message]; }
        body.Children.Add(remove);
        error.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(error);
        Closing += (_, e) => { if (working) e.Cancel = true; };
        remove.Click += async (_, _) =>
        {
            if (plan == null || working || vm.IsPreparingSetup || vm.IsGenerationBusy || vm.IsDownloading || vm.IsImporting || vm.IsChecking)
                return;
            try
            {
                var selected = AppMaintenance.Select(plan, groups.Where(group => group.Value.IsChecked == true)
                    .Select(group => group.Key).ToHashSet());
                if (selected.Items.Count == 0 || !Confirm(vm, selected)) return;
                working = true;
                error.Text = vm.T["Preparing"];
                Refresh();
                await AppMaintenance.LaunchAsync(selected);
                working = false;
                Application.Current.Shutdown();
            }
            catch (Exception exception)
            {
                working = false;
                error.Text = vm.T[exception.Message];
                Refresh();
            }
        };
        Refresh();
    }

    bool Confirm(MainViewModel vm, UninstallPlan selected)
    {
        var dialog = new Window
        {
            Owner = this, Title = vm.T["Review selected files"], DataContext = vm, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Width = Math.Min(570, Width), Height = Math.Min(390, Height), Background = System.Windows.Media.Brushes.White
        };
        SetupUi.Resources(dialog);
        var content = new DockPanel { Margin = new Thickness(20) };
        dialog.Content = content;
        var note = SetupUi.Text("The selected files will be permanently deleted after LightX2V closes.");
        DockPanel.SetDock(note, Dock.Top);
        content.Children.Add(note);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom);
        content.Children.Add(actions);
        var cancel = SetupUi.Button(dialog, "Cancel");
        cancel.IsCancel = true;
        actions.Children.Add(cancel);
        var accept = SetupUi.Button(dialog, selected.RemoveApplication ? "Uninstall and quit" : "Delete and restart", true);
        accept.Margin = new Thickness(8, 0, 0, 0);
        accept.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(accept);
        content.Children.Add(new TextBox
        {
            Text = string.Join(Environment.NewLine, selected.Items.SelectMany(item => item.Files == null
                ? new[] { item.Path }
                : item.Files.Select(file => Path.Combine(item.Path, file)))),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 12)
        });
        return dialog.ShowDialog() == true;
    }
}
