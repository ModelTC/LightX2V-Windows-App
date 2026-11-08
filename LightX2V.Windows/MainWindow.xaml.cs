using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Threading;

namespace LightX2V.Windows;

public partial class MainWindow : Window
{
    bool closing;
    string lastContent = "";
    WindowBounds? bounds;
    public MainViewModel ViewModel { get; }
    public MainWindow(MainViewModel vm,Action? requestStartupSetup=null)
    {
        ViewModel = vm;
        DataContext = vm;
        InitializeComponent();
        // Fit before Show computes CenterScreen; changing size after creation leaves
        // the original off-screen center position behind on high-DPI displays.
        var initialArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, initialArea.Width);
        MinHeight = Math.Min(MinHeight, initialArea.Height);
        Width = Math.Min(Width, initialArea.Width);
        Height = Math.Min(Height, initialArea.Height);
        vm.RequestSetup = () => {if(!Application.Current.Windows.OfType<FirstRunWindow>().Any())new FirstRunWindow(vm){Owner=this}.ShowDialog();};
        vm.RequestSettings = () => new SettingsWindow(vm) { Owner = this }.ShowDialog();
        vm.ContentUpdated += () =>
        {
            var key = string.Join(",", vm.Turns.Select(g => g.Id + g.Status + g.EnhancementStatus));
            if (key == lastContent) return;
            lastContent = key;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Conversation.ScrollToEnd());
        };
        Loaded += (_, _) =>
        {
            // Opening a configured workspace never imports torch. The first send
            // validates its environment, with cancellation and visible progress.
            Action setup=requestStartupSetup??(()=>vm.RequestSetup?.Invoke());
            if(vm.NeedsInitialSetup)_ = Dispatcher.BeginInvoke(setup);
            else if(!vm.RequiredModelsReady){vm.Setup.ResumeModels();_ = Dispatcher.BeginInvoke(setup);}
        };
        Closing += async (_, e) =>
        {
            if(closing)return;
            if(vm.IsPreparingSetup){e.Cancel=true;closing=true;await vm.Setup.CancelAsync();Close();return;}
            if (!vm.IsGenerationBusy) return;
            e.Cancel = true;
            closing = true;
            try { await vm.StopAsync(); while (vm.IsGenerationBusy) await Task.Delay(25); }
            finally { Close(); }
        };
        Closed += (_, _) => { closing=true;bounds?.Dispose(); vm.Dispose(); };
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        // Install before WindowChrome's SourceInitialized handler so it cannot
        // replace our work-area bounds with the full monitor bounds.
        bounds = new WindowBounds(this);
        base.OnSourceInitialized(e);
    }
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome != null)
        {
            chrome.ResizeBorderThickness = new Thickness(WindowState == WindowState.Maximized ? 0 : 6);
            chrome.CornerRadius = new CornerRadius(WindowState == WindowState.Maximized ? 0 : 10);
        }
    }
    void RatioClicked(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string ratio }) ViewModel.Ratio = ratio; }
    void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void CloseWindow(object sender, RoutedEventArgs e) => Close();
    void LogChanged(object sender, TextChangedEventArgs e) { if (sender is TextBox box && box.IsVisible) box.ScrollToEnd(); }
    void EnhancementChanged(object sender, TextChangedEventArgs e) { if (sender is TextBox box && box.IsVisible) box.ScrollToEnd(); }
}
