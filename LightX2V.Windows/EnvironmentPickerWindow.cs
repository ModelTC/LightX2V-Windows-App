using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace LightX2V.Windows;

public sealed class EnvironmentPickerWindow : Window
{
    public EnvironmentSelectionModel Model {get;}
    public EnvironmentCandidate? Result {get;private set;}
    readonly Button search,close,install,choose,manual;readonly Border source,activity,empty;readonly TextBlock emptyText;
    readonly DriverSetupCard driverCard;readonly ComboBox backendPicker;readonly ItemsControl candidates;readonly ProgressBar progress;bool closing;
    public EnvironmentPickerWindow(AppSettings settings,L10n language,EnvironmentSelectionModel? model=null)
    {
        Model=model??new EnvironmentSelectionModel(settings,language);DataContext=Model;SetupUi.Resources(this);
        SetBinding(TitleProperty,"T[Select PyTorch environment]");Width=Math.Min(840,SystemParameters.WorkArea.Width);Height=Math.Min(780,SystemParameters.WorkArea.Height);
        MinWidth=Math.Min(620,Width);MinHeight=Math.Min(520,Height);WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;Background=Brushes.White;Foreground=SetupUi.Ink;FontFamily=new("Segoe UI, Microsoft YaHei UI");
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=GridLength.Auto});Content=root;
        var header=new StackPanel{Margin=new Thickness(24,22,24,16)};header.Children.Add(SetupUi.Text("Select PyTorch environment",22,true));
        var summary=new TextBlock{FontSize=12,Foreground=SetupUi.Muted,Margin=new Thickness(0,7,0,0)};summary.SetBinding(TextBlock.TextProperty,"Summary");header.Children.Add(summary);root.Children.Add(header);
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll,1);root.Children.Add(scroll);var body=new StackPanel{Margin=new Thickness(24,0,24,20)};scroll.Content=body;
        driverCard=new DriverSetupCard(this,Model.Driver);body.Children.Add(driverCard);
        var backendRow=new Grid{Margin=new Thickness(0,0,0,12)};backendRow.ColumnDefinitions.Add(new());backendRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var backendTitle=SetupUi.Text("Inference backend",13,true);backendTitle.VerticalAlignment=VerticalAlignment.Center;backendRow.Children.Add(backendTitle);backendPicker=SetupUi.BackendPicker("Backend");Grid.SetColumn(backendPicker,1);backendRow.Children.Add(backendPicker);body.Children.Add(backendRow);
        var backendHint=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=SetupUi.Muted,Margin=new Thickness(0,0,0,12)};backendHint.SetBinding(TextBlock.TextProperty,"BackendHint");body.Children.Add(backendHint);
        var requirements=new StackPanel();requirements.Children.Add(SetupUi.Text("Pinned configuration · Native Windows",12,true));
        var versions=new TextBlock{TextWrapping=TextWrapping.Wrap,FontFamily=new("Consolas"),FontSize=11,Foreground=SetupUi.Muted,Margin=new Thickness(0,9,0,0)};versions.SetBinding(TextBlock.TextProperty,"Requirements");requirements.Children.Add(versions);
        body.Children.Add(new Border{Child=requirements,Background=SetupUi.Subtle,CornerRadius=new CornerRadius(13),Padding=new Thickness(16),Margin=new Thickness(0,0,0,18)});
        var actions=new Grid();actions.ColumnDefinitions.Add(new());actions.ColumnDefinitions.Add(new(){Width=new GridLength(26)});actions.ColumnDefinitions.Add(new());for(int i=0;i<4;i++)actions.RowDefinitions.Add(new(){Height=GridLength.Auto});
        actions.Children.Add(SetupUi.Text("Manual installation",15,true));var autoTitle=SetupUi.Text("Install automatically in workspace",15,true);Grid.SetColumn(autoTitle,2);actions.Children.Add(autoTitle);
        var manualHint=SetupUi.Text("Prepare your own native Python environment, then search again.",12,false,true);manualHint.Margin=new Thickness(0,9,0,14);Grid.SetRow(manualHint,1);actions.Children.Add(manualHint);
        var autoHint=SetupUi.Text("Prepare pinned Python and GPU PyTorch dependencies.",12,false,true);autoHint.Margin=new Thickness(0,9,0,14);Grid.SetRow(autoHint,1);Grid.SetColumn(autoHint,2);actions.Children.Add(autoHint);
        choose=SetupUi.Button(this,"Choose Python executable",false,"EnvironmentChoosePython");choose.HorizontalAlignment=HorizontalAlignment.Left;choose.VerticalAlignment=VerticalAlignment.Bottom;choose.Margin=new Thickness(0,0,0,14);choose.Click+=async(_,_)=>{var picker=new OpenFileDialog{Filter="Python executable|python.exe|Windows executable|*.exe"};if(picker.ShowDialog(this)==true)await Model.CheckPythonAsync(picker.FileName);};Grid.SetRow(choose,2);actions.Children.Add(choose);
        var sourceGroup=new StackPanel();var sourceTitle=SetupUi.Text("Installation source",11,false,true);sourceTitle.Margin=new Thickness(0,0,0,5);sourceGroup.Children.Add(sourceTitle);
        source=SetupUi.SourcePicker(this,value=>Model.Mirror=value,Model.Mirror);sourceGroup.Children.Add(source);sourceGroup.Margin=new Thickness(0,0,0,14);Grid.SetRow(sourceGroup,2);Grid.SetColumn(sourceGroup,2);actions.Children.Add(sourceGroup);
        manual=SetupUi.Button(this,"View installation instructions",false,"EnvironmentInstallGuide");manual.Click+=(_,_)=>ShowInstructions();Grid.SetRow(manual,3);actions.Children.Add(manual);
        install=SetupUi.Button(this,"Start automatic installation",true,"EnvironmentInstall");install.Click+=async(_,_)=>await Model.InstallAsync();Grid.SetRow(install,3);Grid.SetColumn(install,2);actions.Children.Add(install);
        body.Children.Add(SetupUi.Card(actions));
        {var note=SetupUi.Text(Backends.Unsupported,11,false,true);note.Margin=new Thickness(0,0,0,12);note.SetBinding(VisibilityProperty,new Binding("ManagedSupported"){Converter=(IValueConverter)Application.Current.FindResource("NotBool")});body.Children.Add(note);}
        var status=new StackPanel();var stage=new TextBlock{FontSize=12,FontWeight=FontWeights.SemiBold};stage.SetBinding(TextBlock.TextProperty,"Stage");status.Children.Add(stage);
        var detail=new TextBlock{FontSize=11,Foreground=SetupUi.Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,0)};detail.SetBinding(TextBlock.TextProperty,"Detail");status.Children.Add(detail);
        progress=new ProgressBar{Height=3,Maximum=100,Foreground=SetupUi.Ink,Background=SetupUi.Line,Margin=new Thickness(0,10,0,0)};progress.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty,new Binding("Percent"){Mode=BindingMode.OneWay});status.Children.Add(progress);activity=SetupUi.Card(status);body.Children.Add(activity);
        var error=new TextBlock{Foreground=new SolidColorBrush(Color.FromRgb(164,81,69)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};error.SetBinding(TextBlock.TextProperty,"Error");error.SetBinding(VisibilityProperty,new Binding("Error"){Converter=(IValueConverter)Application.Current.FindResource("NonEmpty")});body.Children.Add(error);
        var listTitle=SetupUi.Text("Local environments",13,true);listTitle.Margin=new Thickness(0,10,0,12);body.Children.Add(listTitle);
        candidates=new ItemsControl{ItemsSource=Model.Candidates,ItemTemplate=(DataTemplate)FindResource("EnvironmentCandidateCard")};System.Windows.Automation.AutomationProperties.SetAutomationId(candidates,"EnvironmentCandidates");body.Children.Add(candidates);
        emptyText=SetupUi.Text("No compatible environment found",12,false,true);empty=SetupUi.Card(emptyText);empty.Background=SetupUi.Subtle;body.Children.Add(empty);
        var footerRow=new Grid();footerRow.ColumnDefinitions.Add(new());footerRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var footer=SetupUi.Footer(footerRow);Grid.SetRow(footer,2);root.Children.Add(footer);
        search=SetupUi.Button(this,"Search local environment",false,"EnvironmentSearch");search.HorizontalAlignment=HorizontalAlignment.Left;search.Click+=async(_,_)=>await Model.SearchAsync();footerRow.Children.Add(search);
        close=SetupUi.Button(this,"Close",false,"EnvironmentClose");close.MinWidth=64;close.Click+=async(_,_)=>{if(Model.Busy)await Model.CancelAsync();else Close();};Grid.SetColumn(close,1);footerRow.Children.Add(close);
        Model.Chosen=candidate=>{Result=candidate;if(IsVisible)DialogResult=true;};Model.PropertyChanged+=Changed;Model.Candidates.CollectionChanged+=(_,_)=>Refresh();
        Loaded+=async(_,_)=>{if(!Model.Searched&&!Model.Busy)await Model.SearchAsync();};
        Closing+=async(_,e)=>{if(!Model.Busy||closing)return;e.Cancel=true;closing=true;await Model.CancelAsync();Close();};
        Closed+=(_,_)=>{Model.PropertyChanged-=Changed;driverCard.Dispose();Model.Dispose();};Refresh();
    }
    void Changed(object? sender,PropertyChangedEventArgs args)=>Refresh();
    void Refresh()
    {
        if(search==null)return;backendPicker.IsEnabled=!Model.Busy&&!Model.Driver.Pending;search.IsEnabled=!Model.Busy;choose.IsEnabled=!Model.Busy;manual.IsEnabled=!Model.Busy;install.IsEnabled=Model.CanInstall;source.IsEnabled=Model.CanInstall;SetupUi.RefreshSource(source,Model.Mirror);candidates.IsEnabled=!Model.Busy;
        activity.Visibility=Model.Busy?Visibility.Visible:Visibility.Collapsed;progress.IsIndeterminate=Model.Percent<=0;
        close.Content=Model.T[Model.Busy?"Stop":"Close"];empty.Visibility=!Model.Busy&&Model.Candidates.Count==0?Visibility.Visible:Visibility.Collapsed;
        emptyText.Text=Model.T[Model.Searched?"No compatible environment found. Choose Python manually or install in the workspace.":"Search your computer for a compatible environment"];
    }
    void ShowInstructions()
    {
        var dialog=new Window{Title=Model.T["Environment installation instructions"],Owner=this,DataContext=Model,Width=Math.Min(640,SystemParameters.WorkArea.Width),Height=Math.Min(560,SystemParameters.WorkArea.Height),WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,Background=Brushes.White};SetupUi.Resources(dialog);
        var root=new Grid();root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=GridLength.Auto});dialog.Content=root;
        var body=new StackPanel{Margin=new Thickness(24)};root.Children.Add(new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=body});body.Children.Add(SetupUi.Text("Environment installation instructions",20,true));
        foreach(var key in new[]{"Use native x64 Windows Python 3.12 and a compatible GPU driver. Choose the PyTorch wheel for the selected backend.","The bundled manifests list separate AMD and NVIDIA wheel URLs, hashes and pinned dependencies. CPU-only PyTorch environments are not compatible.","Choose python.exe after installation and run the environment search. Only environments that pass dependency and GPU checks can be selected."})
        {var text=SetupUi.Text(key,12,false,true);text.Margin=new Thickness(0,14,0,0);body.Children.Add(text);}
        body.Children.Add(new TextBox{Text=Model.Requirements,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontFamily=new("Consolas"),Margin=new Thickness(0,18,0,10)});
        var note=SetupUi.Text("Automatic installation uses the same manifest and verifies every download.",12,false,true);body.Children.Add(note);
        var state=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)};body.Children.Add(state);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var folder=SetupUi.Button(dialog,"Open dependency folder");folder.Click+=async(_,_)=>{try{await WindowsShell.RevealAsync(SetupManifest.Resources);}catch(Exception e){state.Text=e is DesktopIntegrationRestrictedException?Model.T["Launch location restricts desktop integration"]:e.Message;}};actions.Children.Add(folder);var done=SetupUi.Button(dialog,"Close");done.Margin=new Thickness(8,0,0,0);done.Click+=(_,_)=>dialog.Close();actions.Children.Add(done);var footer=SetupUi.Footer(actions);Grid.SetRow(footer,1);root.Children.Add(footer);dialog.ShowDialog();
    }
}
