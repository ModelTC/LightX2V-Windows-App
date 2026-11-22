using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;

namespace LightX2V.Windows;

internal sealed class AppUpdateWindow : Window
{
    readonly MainViewModel vm;
    readonly PackageLayout? layout;
    readonly string? current;
    readonly TextBlock status;
    readonly ProgressBar progress;
    readonly Button check, install, close;
    UpdateRelease? available;
    CancellationTokenSource? operation;

    internal AppUpdateWindow(MainViewModel vm)
    {
        this.vm = vm;
        DataContext = vm;
        SetupUi.Resources(this);
        Title = vm.T["Application updates"];
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = Math.Min(480, SystemParameters.WorkArea.Width);
        Background = SetupUi.Subtle;
        try
        {
            layout = PackageLayout.Find(AppContext.BaseDirectory);
            current = AppUpdater.CurrentCommit(layout);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Trace.WriteLine(error);
        }

        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        close = SetupUi.Button(this, "Close");
        close.Click += (_, _) => { if (operation is {} task) task.Cancel(); else Close(); };
        actions.Children.Add(close);
        check = SetupUi.Button(this, "Check for updates", true, "CheckAppUpdates");
        check.Margin = new Thickness(8, 0, 0, 0);
        check.Click += async (_, _) => await RunAsync(CheckAsync);
        actions.Children.Add(check);
        install = SetupUi.Button(this, "Install and restart", true, "InstallAppUpdate");
        install.Margin = new Thickness(8, 0, 0, 0);
        install.Click += async (_, _) => await ConfirmInstallAsync();
        actions.Children.Add(install);

        var card = new StackPanel();
        card.Children.Add(SetupUi.Text("Application updates", 17, true));
        card.Children.Add(new TextBlock
        {
            Text = current == null
                ? vm.T["Build information is unavailable. Use a packaged LightX2V app."]
                : string.Format(vm.T["Current build: {0}"], current[..7]),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = SetupUi.Muted
        });
        status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        card.Children.Add(status);
        progress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 10, 0, 0) };
        card.Children.Add(progress);
        root.Children.Add(SetupUi.Card(card));
        Closing += (_, e) => { if (operation is {} task) { e.Cancel = true; task.Cancel(); } };
        Refresh();
    }

    void Refresh()
    {
        check.IsEnabled = current != null && operation == null;
        install.IsEnabled = operation == null && !vm.IsPreparingSetup && !vm.IsGenerationBusy &&
            !vm.IsDownloading && !vm.IsImporting && !vm.IsChecking;
        install.Visibility = available == null ? Visibility.Collapsed : Visibility.Visible;
        progress.Visibility = operation == null ? Visibility.Collapsed : Visibility.Visible;
        close.Content = vm.T[operation == null ? "Close" : "Stop"];
    }

    async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation != null) return;
        operation = new CancellationTokenSource();
        progress.IsIndeterminate = true;
        Refresh();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { status.Text = vm.T["The update was cancelled."]; }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            status.Text = error is HttpRequestException
                ? vm.T["The update could not be checked. Check your network and try again."]
                : vm.T["The app update could not finish."] + " " + vm.T[error.Message];
        }
        finally
        {
            operation.Dispose();
            operation = null;
            Refresh();
        }
    }

    async Task CheckAsync(CancellationToken token)
    {
        available = null;
        Refresh();
        status.Text = vm.T["Checking for updates…"];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var (release, newer) = await AppUpdater.CheckAsync(current!, timeout.Token);
            var channel = release.Channel == "stable" ? "Stable" : "Latest";
            if (newer) available = release;
            status.Text = newer
                ? string.Format(vm.T["{0} build {1} is available."], channel, release.Commit[..7])
                : string.Format(vm.T["You already have this {0} build or a newer one."], channel);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            status.Text = vm.T["The update could not be checked. Check your network and try again."];
        }
    }

    async Task ConfirmInstallAsync()
    {
        if (!install.IsEnabled || available is not {} release) return;
        var message = vm.T["Download and verify the update, then close and restart LightX2V to install it? Save any changes in Settings first."];
        if (MessageBox.Show(this, message, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question,
                MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        var launched = false;
        await RunAsync(async token =>
        {
            status.Text = vm.T["Downloading the update…"];
            var downloadProgress = new Progress<double>(value =>
            {
                progress.IsIndeterminate = value < 0;
                if (value < 0) status.Text = vm.T["Verifying the update…"];
                else progress.Value = value;
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            try
            {
                await AppUpdater.PrepareAsync(layout!, current!, release, vm.Settings, vm.T.Language, downloadProgress, timeout.Token);
                launched = true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                status.Text = vm.T["The update timed out. Check your network and try again."];
            }
        });
        if (launched) Application.Current.Shutdown();
    }
}
