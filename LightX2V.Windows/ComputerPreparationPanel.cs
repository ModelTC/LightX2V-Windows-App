using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace LightX2V.Windows;

// The wizard owns one preparation flow. Settings keeps its independent environment chooser.
public sealed class ComputerPreparationPanel : StackPanel,IDisposable
{
    readonly SetupController setup;readonly Style choiceStyle;readonly DriverSetupCard driver;
    readonly StackPanel candidates=new(),components=new();readonly List<(RadioButton Radio,EnvironmentCandidate Candidate)> choices=[];
    readonly Button rescan,searchAction;
    bool refreshing;
    public ComputerPreparationPanel(Window window,SetupController setup)
    {
        this.setup=setup;choiceStyle=(Style)window.FindResource("SetupChoice");DataContext=setup;
        var scan=new StackPanel();var heading=new Grid();heading.ColumnDefinitions.Add(new());heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});scan.Children.Add(heading);
        heading.Children.Add(SetupUi.Text("1 · Check this computer",15,true));
        rescan=SetupUi.Button(window,"Scan again",false,"SetupScanComputer");rescan.Margin=new Thickness(10,0,0,0);Grid.SetColumn(rescan,1);heading.Children.Add(rescan);rescan.Click+=async(_,_)=>await setup.ScanComputerAsync();
        var recommendation=Bound("BackendRecommendation",13,true);recommendation.Foreground=SetupUi.Ink;scan.Children.Add(recommendation);
        var hardware=Bound("HardwareLabel");System.Windows.Automation.AutomationProperties.SetAutomationId(hardware,"SetupComputerHardware");scan.Children.Add(hardware);
        var resources=Bound("ResourceLabel");scan.Children.Add(resources);
        foreach(var value in new[]{hardware,resources})value.SetBinding(VisibilityProperty,new Binding("ComputerScanned"){Converter=(IValueConverter)Application.Current.FindResource("Bool")});
        var scanCard=SetupUi.Card(scan);System.Windows.Automation.AutomationProperties.SetAutomationId(scanCard,"SetupComputerScan");Children.Add(scanCard);
        driver=new DriverSetupCard(window,setup.Driver,false,"2 · Prepare your graphics card",false);Children.Add(driver);
        var title=SetupUi.Text("3 · Prepare the image-generation components",15,true);title.Margin=new Thickness(0,4,0,0);components.Children.Add(title);
        components.Children.Add(Bound("EnvironmentSummary"));
        candidates.Margin=new Thickness(0,12,0,0);System.Windows.Automation.AutomationProperties.SetAutomationId(candidates,"SetupInlineEnvironments");components.Children.Add(candidates);
        searchAction=SetupUi.Button(window,"Search local environments",false,"SetupEnvironmentSearchAction");
        searchAction.HorizontalAlignment=HorizontalAlignment.Left;
        searchAction.Click+=async(_,_)=>await setup.SearchEnvironmentsAsync();
        components.Children.Add(searchAction);
        var unsupported=SetupUi.Text(Backends.Unsupported,11,false,true);unsupported.SetBinding(VisibilityProperty,new Binding("ManagedSupported"){Converter=(IValueConverter)Application.Current.FindResource("NotBool")});components.Children.Add(unsupported);
        Children.Add(SetupUi.Card(components));
        setup.PropertyChanged+=Changed;setup.Driver.PropertyChanged+=Changed;setup.Owner.PropertyChanged+=Changed;setup.T.PropertyChanged+=Changed;setup.Environments.CollectionChanged+=CandidatesChanged;Rebuild();Refresh();
    }
    TextBlock Bound(string property,double size=11,bool bold=false)
    {
        var text=new TextBlock{FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Foreground=SetupUi.Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,9,0,0)};text.SetBinding(TextBlock.TextProperty,property);return text;
    }
    void CandidatesChanged(object? sender,NotifyCollectionChangedEventArgs e){Rebuild();Refresh();}
    void Rebuild()
    {
        candidates.Children.Clear();choices.Clear();
        foreach(var item in setup.Environments)
        {
            var radio=new RadioButton{Style=choiceStyle,GroupName="SetupEnvironment",Margin=new Thickness(0,0,0,10)};
            System.Windows.Automation.AutomationProperties.SetAutomationId(radio,"SetupEnvironmentCandidate");
            var body=new StackPanel();body.Children.Add(new TextBlock{Text=item.Name,FontSize=13,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            body.Children.Add(new TextBlock{Text=item.Path,FontFamily=new("Consolas"),FontSize=10,TextWrapping=TextWrapping.Wrap,Foreground=SetupUi.Muted,Margin=new Thickness(0,7,0,0)});
            body.Children.Add(new TextBlock{Text=item.RuntimeSummary,FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=SetupUi.Muted,Margin=new Thickness(0,6,0,0)});
            var state=SetupUi.Text(item.Ready&&item.Backend==setup.Backend?"Available":"Needs attention",11,true);state.Foreground=item.Ready&&item.Backend==setup.Backend?SetupUi.Green:SetupUi.Muted;state.Margin=new Thickness(0,6,0,0);body.Children.Add(state);
            if(!item.Ready)body.Children.Add(new TextBlock{Text=item.Detail,FontSize=10,Foreground=SetupUi.Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,0)});
            radio.Content=body;radio.Checked+=(_,e)=>{if(!refreshing&&e.OriginalSource==radio)setup.AcceptCandidate(item);};choices.Add((radio,item));candidates.Children.Add(radio);
        }
    }
    void Changed(object? sender,PropertyChangedEventArgs e)=>Refresh();
    void Refresh()
    {
        if(refreshing)return;refreshing=true;
        try
        {
            var busy=setup.Busy||setup.Driver.Busy||setup.Owner.IsChecking||setup.Owner.IsRunning||setup.Owner.IsDownloading;
            rescan.IsEnabled=!busy;
            driver.Visibility=components.Visibility=setup.ComputerScanned?Visibility.Visible:Visibility.Collapsed;
            driver.IsEnabled=!setup.Busy;
            searchAction.Visibility=setup.SearchingEnvironments||setup.CanRetryEnvironmentSearch?Visibility.Visible:Visibility.Collapsed;
            searchAction.Content=setup.T[setup.SearchingEnvironments?"Stop searching":"Search local environments"];
            searchAction.IsEnabled=!busy;
            foreach(var (radio,item) in choices){radio.IsEnabled=!busy&&item.Ready&&item.Backend==setup.Backend;radio.IsChecked=setup.UseExisting&&setup.Selected==item;}

        }
        finally{refreshing=false;}
    }
    public void Dispose(){setup.PropertyChanged-=Changed;setup.Driver.PropertyChanged-=Changed;setup.Owner.PropertyChanged-=Changed;setup.T.PropertyChanged-=Changed;setup.Environments.CollectionChanged-=CandidatesChanged;driver.Dispose();}
}
