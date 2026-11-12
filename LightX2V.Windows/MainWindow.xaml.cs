using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Threading;

namespace LightX2V.Windows;

public partial class MainWindow : Window
{
    bool closing;bool followConversation=true;bool followLog=true;bool updatingLog;string? lastLogTarget;double logScrollOffset;
    string lastContent = "";
    WindowBounds? bounds;
    public MainViewModel ViewModel { get; }
    public MainWindow(MainViewModel vm,Action? requestStartupSetup=null)
    {
        ViewModel = vm;
        DataContext = vm;
        InitializeComponent();InitializeComposer();
        // Fit before Show computes CenterScreen; changing size after creation leaves
        // the original off-screen center position behind on high-DPI displays.
        var initialArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, initialArea.Width);
        MinHeight = Math.Min(MinHeight, initialArea.Height);
        Width = Math.Min(Width, initialArea.Width);
        Height = Math.Min(Height, initialArea.Height);
        vm.RequestSetup = () => {if(!Application.Current.Windows.OfType<FirstRunWindow>().Any())new FirstRunWindow(vm){Owner=this}.ShowDialog();};
        vm.RequestSettings = () => new SettingsWindow(vm) { Owner = this }.ShowDialog();
        Conversation.ScrollChanged+=(_,e)=>
        {
            if(e.ExtentHeightChange==0&&e.ViewportHeightChange==0&&e.VerticalChange!=0)
                followConversation=Conversation.ScrollableHeight-Conversation.VerticalOffset<32;
            else if(followConversation&&(e.ExtentHeightChange!=0||e.ViewportHeightChange!=0))
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>Conversation.ScrollToEnd());
        };
        LogViewer.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((_,e)=>
        {
            if(updatingLog||e.OriginalSource is not ScrollViewer scroll)return;
            if(e.ExtentHeightChange==0&&e.VerticalChange!=0){logScrollOffset=scroll.VerticalOffset;followLog=scroll.ScrollableHeight-scroll.VerticalOffset<16;}
        }));
        vm.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(MainViewModel.ShowLog)&&vm.ShowLog||e.PropertyName==nameof(MainViewModel.LogTarget)){followLog=true;Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>LogViewer.ScrollToEnd());}};
        vm.ContentUpdated+=()=>
        {
            var key=string.Join(",",vm.Turns.Select(g=>g.Id));
            var changed=key!=lastContent;lastContent=key;
            if(changed)followConversation=true;
            if(followConversation)Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>Conversation.ScrollToEnd());
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
            if(vm.IsRunning&&MessageBox.Show(this,vm.T["The current image is unfinished. Quitting stops inference and releases model memory. Completed images are preserved."],vm.T["Stop generation and quit?"],MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
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
    void LogChanged(object sender, TextChangedEventArgs e)
    {
        if(sender is not TextBox box)return;
        var id=ViewModel.LogTarget?.Id;if(lastLogTarget!=id){lastLogTarget=id;followLog=true;}
        if(!box.IsVisible)return;
        var offset=logScrollOffset;updatingLog=true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>{if(followLog)box.ScrollToEnd();else box.ScrollToVerticalOffset(offset);updatingLog=false;});
    }
    void EnhancementChanged(object sender, TextChangedEventArgs e) { if (sender is TextBox box && box.IsVisible) box.ScrollToEnd(); }
}
