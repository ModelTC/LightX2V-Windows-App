using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace LightX2V.Windows;

public sealed class FirstRunWindow : Window
{
    readonly ComputerPreparationPanel computer;readonly MainViewModel owner;readonly SetupController setup;readonly StackPanel[] pages=new StackPanel[4];
    readonly Button next,back,close;readonly ProgressBar progress;readonly List<Control> locked=[];
    readonly Border[] stepFaces=new Border[4],stepDots=new Border[4];readonly TextBlock[] steps=new TextBlock[4],numbers=new TextBlock[4];
    readonly TextBlock counter;readonly Border statusCard;readonly TextBlock detail;bool closing,refreshing;
    public bool Completed {get;private set;}
    public FirstRunWindow(MainViewModel owner,bool standalone=false)
    {
        this.owner=owner;setup=owner.Setup;DataContext=owner;Title="LightX2V · "+owner.T["First-time setup"];SetupUi.Resources(this);
        Width=Math.Min(800,SystemParameters.WorkArea.Width);Height=Math.Min(760,SystemParameters.WorkArea.Height);MinWidth=Math.Min(640,Width);MinHeight=Math.Min(520,Height);WindowStartupLocation=standalone?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner;ShowInTaskbar=standalone;Background=SetupUi.Subtle;Foreground=SetupUi.Ink;FontFamily=new("Segoe UI, Microsoft YaHei UI");
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/LightX2V.Windows;component/Resources/ModelPreparation.xaml",UriKind.Relative)});
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=GridLength.Auto});Content=root;
        var headerBody=new StackPanel{Margin=new Thickness(24,20,24,18)};root.Children.Add(new Border{Background=Brushes.White,BorderBrush=SetupUi.Line,BorderThickness=new Thickness(0,0,0,1),Child=headerBody});
        var heading=new Grid();heading.ColumnDefinitions.Add(new(){Width=new GridLength(36)});heading.ColumnDefinitions.Add(new());heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});heading.Children.Add(SetupUi.Logo());
        var titles=new StackPanel{Margin=new Thickness(12,0,0,0)};titles.Children.Add(SetupUi.Text("First-time setup",20,true));var subtitle=SetupUi.Text("Four steps to your first local creation",11,false,true);subtitle.Margin=new Thickness(0,5,0,0);titles.Children.Add(subtitle);Grid.SetColumn(titles,1);heading.Children.Add(titles);
        counter=new TextBlock{FontSize=11,Foreground=SetupUi.Muted,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(counter,2);heading.Children.Add(counter);headerBody.Children.Add(heading);
        var stepper=new System.Windows.Controls.Primitives.UniformGrid{Columns=4,Margin=new Thickness(-3,20,-3,0)};headerBody.Children.Add(stepper);
        for(int i=0;i<4;i++)
        {
            var row=new StackPanel{Orientation=Orientation.Horizontal};numbers[i]=new TextBlock{Text=(i+1).ToString(),FontSize=11,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};stepDots[i]=new Border{Width=23,Height=23,CornerRadius=new CornerRadius(12),Child=numbers[i]};row.Children.Add(stepDots[i]);
            steps[i]=SetupUi.Text(new[]{"Save location","Prepare this computer","Download image files","Setup complete"}[i],11,true);steps[i].Margin=new Thickness(7,0,0,0);steps[i].VerticalAlignment=VerticalAlignment.Center;row.Children.Add(steps[i]);
            stepFaces[i]=new Border{CornerRadius=new CornerRadius(9),Padding=new Thickness(9),Margin=new Thickness(3,0,3,0),Child=row};System.Windows.Automation.AutomationProperties.SetAutomationId(stepFaces[i],"SetupStep"+(i+1));stepper.Children.Add(stepFaces[i]);
        }
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll,1);root.Children.Add(scroll);var body=new StackPanel{Margin=new Thickness(24,20,24,18)};scroll.Content=body;
        for(int i=0;i<4;i++){pages[i]=new StackPanel();var card=i==1?new Border{Child=pages[i]}:SetupUi.Card(pages[i]);card.SetBinding(VisibilityProperty,new Binding("Visibility"){Source=pages[i]});body.Children.Add(card);}
        pages[0].Children.Add(SetupUi.Text("Choose where your creations live",17,true));AddHint(pages[0],"All downloads and images are saved here. You can keep the suggested folder.");
        var pathGrid=new Grid();pathGrid.ColumnDefinitions.Add(new());pathGrid.ColumnDefinitions.Add(new(){Width=new GridLength(40)});
        var workspace=new TextBox{MinHeight=42,BorderThickness=new Thickness(0),Background=Brushes.Transparent,Padding=new Thickness(12,10,12,10)};workspace.SetBinding(TextBox.TextProperty,new Binding("Setup.Workspace"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});System.Windows.Automation.AutomationProperties.SetAutomationId(workspace,"SetupWorkspace");pathGrid.Children.Add(workspace);
        var choose=new Button{Style=(Style)FindResource("IconButton"),Content=SetupUi.Icon(SetupUi.Folder),VerticalAlignment=VerticalAlignment.Center};choose.SetBinding(ToolTipProperty,"T[Choose workspace]");choose.Click+=(_,_)=>{var picker=new OpenFolderDialog();if(picker.ShowDialog(this)==true)setup.Workspace=picker.FolderName;};Grid.SetColumn(choose,1);pathGrid.Children.Add(choose);pages[0].Children.Add(new Border{BorderBrush=SetupUi.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Margin=new Thickness(0,16,0,0),Child=pathGrid});
        var languageRow=new Grid{Margin=new Thickness(0,20,0,0)};languageRow.ColumnDefinitions.Add(new());languageRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var languageLabel=SetupUi.Text("Language",12,true);languageLabel.VerticalAlignment=VerticalAlignment.Center;languageRow.Children.Add(languageLabel);
        var language=new ComboBox{Style=(Style)Application.Current.FindResource("LanguagePicker"),Width=180,Height=30,ItemsSource=new[]{"简体中文","English"},SelectedIndex=owner.T.Language=="zh"?0:1};language.SelectionChanged+=(_,_)=>{owner.SetLanguage(language.SelectedIndex==0?"zh":"en");Refresh();};Grid.SetColumn(language,1);languageRow.Children.Add(language);pages[0].Children.Add(languageRow);
        computer=new ComputerPreparationPanel(this,setup);pages[1].Children.Add(computer);
        pages[2].Children.Add(SetupUi.Text("Download the files used to create images",17,true));AddHint(pages[2],"These files are needed to create images. The first download is about 32 GB. Keep your internet connection on; downloaded files are kept for next time.");
        var modelActions=new Grid{Margin=new Thickness(0,14,0,14)};modelActions.ColumnDefinitions.Add(new());modelActions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var modelSources=new System.Windows.Controls.Primitives.UniformGrid{Columns=2,Width=240};foreach(var (name,modelScope) in new[]{("Hugging Face",false),("ModelScope",true)}){var radio=new RadioButton{Content=name,GroupName="ModelSource",Style=(Style)FindResource("SetupSegment"),IsChecked=owner.UseModelScope==modelScope};radio.Checked+=(_,_)=>{if(modelScope)owner.UseModelScope=true;else owner.UseHuggingFace=true;};locked.Add(radio);modelSources.Children.Add(radio);}modelActions.Children.Add(new Border{CornerRadius=new CornerRadius(7),BorderBrush=SetupUi.Line,BorderThickness=new Thickness(1),Padding=new Thickness(2),Child=modelSources,HorizontalAlignment=HorizontalAlignment.Left});
        var discover=SetupUi.Button(this,"Find existing models",false,"SetupFindModels");discover.Click+=async(_,_)=>await setup.DiscoverModelsAsync();Grid.SetColumn(discover,1);modelActions.Children.Add(discover);pages[2].Children.Add(modelActions);
        var downloadBox=new StackPanel();var automatic=SetupUi.Button(this,"Download required files automatically",true,"SetupDownloadModels");automatic.Command=owner.RequiredModelsDownloadCommand;automatic.SetBinding(ContentControl.ContentProperty,"RequiredDownloadLabel");automatic.HorizontalAlignment=HorizontalAlignment.Left;automatic.MinWidth=190;downloadBox.Children.Add(automatic);
        var downloadSummary=new TextBlock{FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=SetupUi.Muted,Margin=new Thickness(0,10,0,0)};downloadSummary.SetBinding(TextBlock.TextProperty,"ModelDownloadSummary");System.Windows.Automation.AutomationProperties.SetAutomationId(downloadSummary,"SetupDownloadSummary");downloadBox.Children.Add(downloadSummary);pages[2].Children.Add(SetupUi.Card(downloadBox));
        var template=(DataTemplate)FindResource("ModelCardTemplate");pages[2].Children.Add(new ContentControl{Content=owner.TurboCard,ContentTemplate=template});
        var enhance=new CheckBox{Margin=new Thickness(0,3,0,14)};enhance.SetBinding(ContentControl.ContentProperty,"T[Also prepare prompt enhancement models]");enhance.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,new Binding("EnhancementEnabled"){Mode=BindingMode.TwoWay});pages[2].Children.Add(enhance);
        var optionalModels=new StackPanel();optionalModels.SetBinding(VisibilityProperty,new Binding("EnhancementEnabled"){Converter=(IValueConverter)Application.Current.FindResource("Bool")});foreach(var card in new[]{owner.T2ICard,owner.I2ICard})optionalModels.Children.Add(new ContentControl{Content=card,ContentTemplate=template});pages[2].Children.Add(optionalModels);
        var celebration=new StackPanel{Margin=new Thickness(8,24,8,24)};System.Windows.Automation.AutomationProperties.SetAutomationId(celebration,"SetupCompletion");
        var emblem=new Grid{Width=76,Height=76,HorizontalAlignment=HorizontalAlignment.Center};emblem.Children.Add(new Border{Width=64,Height=64,CornerRadius=new CornerRadius(19),Background=SetupUi.Subtle,Child=new Image{Source=(ImageSource)Application.Current.FindResource("Sparkles"),Width=32,Height=38}});
        emblem.Children.Add(new Border{Width=24,Height=24,CornerRadius=new CornerRadius(12),Background=SetupUi.Green,BorderBrush=Brushes.White,BorderThickness=new Thickness(2),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Child=new TextBlock{Text="✓",FontSize=14,Foreground=Brushes.White,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center}});celebration.Children.Add(emblem);
        var congratulations=SetupUi.Text("You're all set. Let your ideas take shape!",21,true);congratulations.TextAlignment=TextAlignment.Center;congratulations.Margin=new Thickness(0,22,0,0);System.Windows.Automation.AutomationProperties.SetAutomationId(congratulations,"SetupCongratulations");celebration.Children.Add(congratulations);
        var ready=SetupUi.Text("Congratulations! Your local environment and model files are ready.",12,false,true);ready.TextAlignment=TextAlignment.Center;ready.Margin=new Thickness(0,14,0,0);celebration.Children.Add(ready);
        var inspiration=SetupUi.Text("Your next creation starts with a spark of imagination.",13);inspiration.TextAlignment=TextAlignment.Center;inspiration.Margin=new Thickness(0,16,0,0);celebration.Children.Add(inspiration);
        var startHint=SetupUi.Text("Choose Start creating to enter the workspace and describe the image you imagine.",11,false,true);startHint.TextAlignment=TextAlignment.Center;startHint.Margin=new Thickness(0,20,0,0);celebration.Children.Add(startHint);pages[3].Children.Add(celebration);
        var statusBody=new StackPanel();var status=new TextBlock{FontSize=12,FontWeight=FontWeights.SemiBold};status.SetBinding(TextBlock.TextProperty,"Setup.Stage");statusBody.Children.Add(status);
        detail=new TextBlock{FontSize=11,Foreground=SetupUi.Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,0)};detail.SetBinding(TextBlock.TextProperty,"Setup.Detail");statusBody.Children.Add(detail);
        progress=new ProgressBar{Height=3,Maximum=100,Foreground=SetupUi.Ink,Background=SetupUi.Line,Margin=new Thickness(0,10,0,0)};progress.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty,new Binding("Setup.Percent"){Mode=BindingMode.OneWay});statusBody.Children.Add(progress);statusCard=SetupUi.Card(statusBody);body.Children.Add(statusCard);
        var error=new TextBlock{Foreground=new SolidColorBrush(Color.FromRgb(164,81,69)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};error.SetBinding(TextBlock.TextProperty,"Setup.Error");error.SetBinding(VisibilityProperty,new Binding("Setup.Error"){Converter=(IValueConverter)Application.Current.FindResource("NonEmpty")});body.Children.Add(error);
        var footerRow=new Grid();footerRow.ColumnDefinitions.Add(new());footerRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});back=SetupUi.Button(this,"Back",false,"SetupBack");back.HorizontalAlignment=HorizontalAlignment.Left;back.Click+=(_,_)=>{setup.Page--;Refresh();};footerRow.Children.Add(back);
        var right=new StackPanel{Orientation=Orientation.Horizontal};Grid.SetColumn(right,1);footerRow.Children.Add(right);close=SetupUi.Button(this,"Close",false,"SetupClose");close.Margin=new Thickness(0,0,10,0);close.Click+=async(_,_)=>{if(setup.Busy||setup.Driver.Busy||owner.IsRunning||owner.IsDownloading)await setup.CancelAsync();else Close();};right.Children.Add(close);next=SetupUi.Button(this,"Continue",true,"SetupContinue");next.MinWidth=100;next.Click+=async(_,_)=>await Next();right.Children.Add(next);var footer=SetupUi.Footer(footerRow);Grid.SetRow(footer,2);root.Children.Add(footer);
        locked.AddRange([workspace,choose,language,discover,enhance]);setup.PropertyChanged+=Changed;owner.PropertyChanged+=Changed;owner.T.PropertyChanged+=Changed;
        Closing+=async(_,e)=>{if(closing||!setup.Busy&&!setup.Driver.Busy&&!owner.IsDownloading&&!owner.IsRunning)return;e.Cancel=true;closing=true;await setup.CancelAsync();while(setup.Busy||setup.Driver.Busy||owner.IsRunning||owner.IsDownloading)await Task.Delay(50);Close();};
        Closed+=(_,_)=>{computer.Dispose();setup.PropertyChanged-=Changed;owner.PropertyChanged-=Changed;owner.T.PropertyChanged-=Changed;};Loaded+=async(_,_)=>{if(standalone&&!owner.NeedsInitialSetup)await setup.ResumeStartupAsync();if(!closing&&setup.Page==1&&!setup.ComputerScanned&&!setup.Busy)await setup.ScanComputerAsync();};Refresh();
    }
    static void AddHint(Panel panel,string key,double size=12){var text=SetupUi.Text(key,size,false,true);text.Margin=new Thickness(0,8,0,0);panel.Children.Add(text);}
    void Changed(object? sender,PropertyChangedEventArgs e)=>Refresh();
    void Refresh()
    {
        if(next==null||refreshing)return;refreshing=true;
        try
        {
            Title="LightX2V · "+owner.T["First-time setup"];counter.Text=string.Format(owner.T["Step {0} of {1}"],setup.Page+1,4);
            for(int i=0;i<4;i++)
            {
                pages[i].Visibility=i==setup.Page?Visibility.Visible:Visibility.Collapsed;stepFaces[i].Background=i==setup.Page?SetupUi.Subtle:Brushes.Transparent;
                stepDots[i].Background=i==setup.Page?SetupUi.Ink:i<setup.Page?SetupUi.Green:SetupUi.Subtle;numbers[i].Foreground=i<=setup.Page?Brushes.White:SetupUi.Muted;numbers[i].Text=i<setup.Page?"✓":(i+1).ToString();steps[i].Foreground=i<=setup.Page?SetupUi.Ink:SetupUi.Muted;
            }
            var busy=setup.Busy||setup.Driver.Busy||owner.IsRunning||owner.IsDownloading||owner.IsChecking;foreach(var control in locked)control.IsEnabled=!busy&&!(System.Windows.Automation.AutomationProperties.GetAutomationId(control)=="BackendPicker"&&setup.Driver.Pending);back.IsEnabled=!busy&&setup.Page>0&&!setup.Driver.Pending;
            statusCard.Visibility=busy&&!(setup.Page==2&&owner.IsDownloading)?Visibility.Visible:Visibility.Collapsed;detail.Visibility=busy?Visibility.Visible:Visibility.Collapsed;progress.Visibility=busy?Visibility.Visible:Visibility.Collapsed;progress.IsIndeterminate=owner.IsRunning||owner.IsDownloading||owner.IsChecking||setup.Busy&&setup.Percent<=0;
            close.Visibility=setup.Page==3?Visibility.Collapsed:Visibility.Visible;
            close.Content=owner.T[setup.Busy||setup.Driver.Busy||owner.IsRunning||owner.IsDownloading?"Stop":"Close"];
            next.IsEnabled=!busy&&(setup.Page!=1||!setup.ComputerScanned||!setup.Driver.CanContinue||setup.ManagedSupported||setup.Selected?.Ready==true);
            next.Content=owner.T[setup.Page==0?"Continue":setup.Page==1?(!setup.ComputerScanned?"Scan this computer":!setup.Driver.CanContinue?(setup.Driver.Pending?"I have finished installing · Check again":setup.Driver.CanDownload?"Download and open the official assistant":"Check this computer again"):"Prepare image-generation components"):setup.Page==2?(setup.CanContinue?"Continue":"Download required files automatically"):"Start creating"];
        }
        finally{refreshing=false;}
    }
    async Task Next()
    {
        if(setup.Page==0)await setup.BeginWorkspaceAsync();else if(setup.Page==1){if(!setup.ComputerScanned){await setup.ScanComputerAsync();return;}if(!setup.Driver.Checked)await setup.Driver.CheckAsync();if(!setup.Driver.CanContinue){if(setup.Driver.CanDownload)await setup.Driver.DownloadAndOpenAsync();else await setup.Driver.CheckAsync(true);}else await setup.PrepareEnvironmentAsync(!setup.UseExisting);}else if(setup.Page==2){if(setup.CanContinue)setup.Page=3;else await setup.DownloadModelsAsync();}else{Completed=true;Close();}Refresh();
    }
}
