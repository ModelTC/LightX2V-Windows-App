using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace LightX2V.Windows;

// General settings edits a draft; first-time preparation retains its dedicated Windows wizard.
public sealed class SettingsWindow : Window
{
    readonly MainViewModel vm;readonly Grid contentRoot;
    readonly TextBox workspace,python;
    readonly Button save,close;
    Button? uninstall;
    readonly TextBlock summary,error,workspaceState,pythonState;

    bool refreshing,closing,draftBusy;string draftBackend;CancellationTokenSource? draftCancellation;TaskCompletionSource? draftFinished;
    public bool Completed {get;private set;}

    public SettingsWindow(MainViewModel vm)
    {
        this.vm=vm;draftBackend=vm.Settings.Backend;DataContext=vm;SetupUi.Resources(this);
        Width=Math.Min(640,SystemParameters.WorkArea.Width);Height=Math.Min(520,SystemParameters.WorkArea.Height);
        MinWidth=Math.Min(620,Width);MinHeight=Math.Min(450,Height);
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        ShowInTaskbar=false;Background=Brushes.White;Foreground=SetupUi.Ink;FontFamily=new("Segoe UI, Microsoft YaHei UI");
        var root=new Grid();contentRoot=root;root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});Content=root;
        var header=new Grid{Margin=new Thickness(12,10,12,14)};header.ColumnDefinitions.Add(new(){Width=new GridLength(36)});header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});root.Children.Add(header);header.Children.Add(SetupUi.Logo());
        var titles=new StackPanel{Margin=new Thickness(12,0,0,0)};Grid.SetColumn(titles,1);header.Children.Add(titles);
        titles.Children.Add(SetupUi.Text("General settings",18,true));titles.Children.Add(SetupUi.Text("3 settings · Change the language anytime",11,false,true));
        summary=new TextBlock{FontSize=11,Foreground=SetupUi.Muted,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(summary,2);header.Children.Add(summary);
        var body=new StackPanel{Margin=new Thickness(12,0,12,8)};Grid.SetRow(body,1);root.Children.Add(body);
        var languageRow=Heading(1,"Language");
        var language=new ComboBox{Style=(Style)Application.Current.FindResource("LanguagePicker"),Width=180,MinHeight=32,Height=32,FontSize=12,VerticalContentAlignment=VerticalAlignment.Center,ItemsSource=new[]{"简体中文","English"},SelectedIndex=vm.T.Language=="zh"?0:1,HorizontalAlignment=HorizontalAlignment.Right};
        System.Windows.Automation.AutomationProperties.SetAutomationId(language,"SettingsLanguage");var languageBox=new StackPanel{Orientation=Orientation.Horizontal};var globe=SetupUi.Icon(SetupUi.Globe,14);globe.Margin=new Thickness(0,0,8,0);languageBox.Children.Add(globe);languageBox.Children.Add(language);Grid.SetColumn(languageBox,1);languageRow.Children.Add(languageBox);language.SelectionChanged+=(_,_)=>vm.SetLanguage(language.SelectedIndex==0?"zh":"en");languageRow.Margin=new Thickness(0);body.Children.Add(SettingsCard(languageRow));
        var work=new StackPanel();var workHeading=Heading(2,"LightX2V workspace");workspaceState=StateLabel(workHeading);work.Children.Add(workHeading);
        workspace=PathField(work,vm.Settings.Workspace,"WorkspacePath",false);
        workHeading.SetBinding(ToolTipProperty,"T[Settings, history, images, logs and caches are stored here.]");body.Children.Add(SettingsCard(work));
        var environment=new StackPanel();var environmentHeading=Heading(3,"PyTorch environment");pythonState=StateLabel(environmentHeading);environment.Children.Add(environmentHeading);
        python=PathField(environment,vm.Settings.Python,"PythonPath",true);
        var search=SetupUi.Button(this,"Search local environments",false,"SearchEnvironment");
        var searchContent=new StackPanel{Orientation=Orientation.Horizontal};var searchIcon=SetupUi.Icon(SetupUi.Search,13);searchIcon.Margin=new Thickness(0,0,7,0);searchContent.Children.Add(searchIcon);searchContent.Children.Add(SetupUi.Text("Search local environments",11,true));search.Content=searchContent;search.Margin=new Thickness(0,6,0,0);search.MinHeight=26;search.Height=26;search.Padding=new Thickness(10,5,10,5);search.HorizontalAlignment=HorizontalAlignment.Left;search.Click+=async(_,_)=>await SearchEnvironmentAsync();environment.Children.Add(search);
        /* Environment preparation lives in a separate dialog, leaving this page compact. */
        body.Children.Add(SettingsCard(environment));
        {
            var maintenance=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};
            uninstall=SetupUi.Button(this,"Uninstall",false,"SettingsUninstall");uninstall.Click+=(_,_)=>new UninstallWindow(vm){Owner=this}.ShowDialog();uninstall.MinHeight=26;uninstall.Height=26;uninstall.Padding=new Thickness(10,4,10,4);maintenance.Children.Add(uninstall);maintenance.Margin=new Thickness(18,8,18,12);Grid.SetRow(maintenance,2);root.Children.Add(maintenance);
        }
        error=SetupUi.Text("",11);error.SetBinding(VisibilityProperty,new System.Windows.Data.Binding("Text"){Source=error,Converter=(System.Windows.Data.IValueConverter)Application.Current.FindResource("NonEmpty")});error.Margin=new Thickness(0,8,0,0);body.Children.Add(error);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        close=SetupUi.Button(this,"Close",false,"SettingsClose");close.MinHeight=26;close.Height=26;close.Padding=new Thickness(10,4,10,4);close.Margin=new Thickness(0,0,10,0);close.Click+=async(_,_)=>{if(Busy){await StopOperationsAsync();}else Close();};actions.Children.Add(close);
        save=SetupUi.Button(this,"Save",true,"SettingsSave");save.MinHeight=26;save.Height=26;save.Padding=new Thickness(10,4,10,4);save.Click+=async(_,_)=>await SaveAsync();actions.Children.Add(save);
        var footer=SetupUi.Footer(actions);Grid.SetRow(footer,3);root.Children.Add(footer);
        workspace.TextChanged+=(_,_)=>Refresh();python.TextChanged+=(_,_)=>Refresh();
        vm.PropertyChanged+=Changed;vm.Setup.PropertyChanged+=Changed;vm.T.PropertyChanged+=Changed;
        Closing+=async(_,e)=>{if(closing||!Busy)return;e.Cancel=true;closing=true;await StopOperationsAsync();Close();};
        root.Width=624;
        root.Measure(new Size(624,double.PositiveInfinity));
        var naturalHeight=root.DesiredSize.Height;root.Height=naturalHeight;
        Content=null;
        Content=new Viewbox{Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,Child=root,VerticalAlignment=VerticalAlignment.Top};
        Height=Math.Min(naturalHeight+SystemParameters.CaptionHeight+16,SystemParameters.WorkArea.Height);
        MinHeight=Math.Min(Height,400);
        Closed+=(_,_)=>{vm.PropertyChanged-=Changed;vm.Setup.PropertyChanged-=Changed;vm.T.PropertyChanged-=Changed;};Refresh();
    }
    bool Busy=>draftBusy||vm.IsPreparingSetup||vm.IsGenerationBusy||vm.IsDownloading||vm.IsImporting||vm.IsChecking;
    static Border SettingsCard(UIElement child)=>new(){Child=child,Padding=new Thickness(12,10,12,10),Margin=new Thickness(0,0,0,8),Background=Brushes.White,BorderBrush=SetupUi.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12)};
    static TextBlock StateLabel(Grid row)
    {
        var label=new TextBlock{FontSize=11,Foreground=SetupUi.Muted,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(label,1);row.Children.Add(label);return label;
    }
    static Grid Heading(int step,string title)
    {
        var row=new Grid{MinHeight=22,Margin=new Thickness(0,0,0,8)};row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var left=new StackPanel{Orientation=Orientation.Horizontal};left.Children.Add(new Border{Width=22,Height=22,CornerRadius=new CornerRadius(11),Background=SetupUi.Subtle,Child=new TextBlock{Text=step.ToString(),FontSize=12,Foreground=SetupUi.Muted,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}});
        var label=SetupUi.Text(title,15,true);label.Margin=new Thickness(9,0,0,0);left.Children.Add(label);
        if(step>1){var help=new TextBlock{Text="ⓘ",FontSize=13,Foreground=SetupUi.Muted,Margin=new Thickness(9,0,0,0),VerticalAlignment=VerticalAlignment.Center};help.SetBinding(ToolTipProperty,step==2?"T[Workspace help]":"T[Python help]");left.Children.Add(help);}
        row.Children.Add(left);System.Windows.Automation.AutomationProperties.SetAutomationId(row,"SettingsCard"+step);return row;
    }
    TextBox PathField(Panel parent,string value,string id,bool executable)
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=new GridLength(38)});
        var field=new TextBox{Text=value,MinHeight=44,VerticalContentAlignment=VerticalAlignment.Center,Padding=new Thickness(8),FontSize=12,BorderThickness=new Thickness(0),Background=Brushes.Transparent};System.Windows.Automation.AutomationProperties.SetAutomationId(field,id);grid.Children.Add(field);
        var choose=SetupUi.Button(this,"Choose…",false);choose.Style=(Style)FindResource("IconButton");choose.Width=30;choose.Height=30;choose.Margin=new Thickness(0,0,5,0);choose.MinWidth=30;choose.Padding=new Thickness(2);choose.Content=SetupUi.Icon(SetupUi.Folder);choose.Click+=(_,_)=>{if(executable){var picker=new OpenFileDialog{Filter="Python|python.exe"};if(picker.ShowDialog(this)==true)field.Text=picker.FileName;}else{var picker=new OpenFolderDialog();if(picker.ShowDialog(this)==true)field.Text=picker.FolderName;}};Grid.SetColumn(choose,1);grid.Children.Add(choose);parent.Children.Add(new Border{BorderBrush=SetupUi.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Child=grid});return field;
    }
    public Task SearchEnvironmentAsync()
    {
        if(Busy)return Task.CompletedTask;
        try
        {
            var draft=vm.Settings.Clone();draft.Workspace=workspace.Text.Trim();draft.Python=python.Text.Trim();draft.Backend=draftBackend;draft.ValidateWorkspace();
            var picker=new EnvironmentPickerWindow(draft,vm.T,vm.Setup.CreateEnvironmentSelection(draft)){Owner=this};
            if(picker.ShowDialog()==true&&picker.Result is {Ready:true} selected){python.Text=selected.Path;draftBackend=selected.Backend;}
        }
        catch(Exception ex){error.Text=vm.T[ex.Message];}
        Refresh();return Task.CompletedTask;
    }
    async Task SaveAsync()
    {
        try
        {
            draftBusy=true;draftCancellation=new();draftFinished=new(TaskCreationOptions.RunContinuationsAsynchronously);Refresh();
            var settings=vm.Settings.Clone();settings.Workspace=workspace.Text.Trim();settings.Python=python.Text.Trim();settings.Backend=draftBackend;
            var changed=settings.Workspace!=vm.Settings.Workspace||settings.Python!=vm.Settings.Python||settings.Backend!=vm.Settings.Backend;
            if(changed)
            {
                var (ready,probe)=await vm.Setup.VerifyDraftAsync(settings,draftCancellation.Token);
                if(closing||draftCancellation.IsCancellationRequested)return;
                vm.CommitVerifiedEnvironment(ready,probe);
            }
            else vm.ApplySettings(settings);
            Completed=true;Close();
        }
        catch(OperationCanceledException) when(draftCancellation?.IsCancellationRequested==true){}
        catch(Exception ex){error.Text=vm.T[ex.Message];}
        finally{draftBusy=false;draftCancellation?.Dispose();draftCancellation=null;draftFinished?.TrySetResult();Refresh();}
    }
    async Task StopOperationsAsync()
    {
        draftCancellation?.Cancel();
        if(draftFinished is {} finished)await finished.Task;
        await vm.Setup.CancelAsync();
    }
    void Changed(object? sender,PropertyChangedEventArgs e)
    {
        if(ReferenceEquals(sender,vm)&&e.PropertyName==nameof(MainViewModel.Settings))python.Text=vm.Settings.Python;
        Refresh();
    }
    void Refresh()
    {
        if(refreshing||save==null)return;refreshing=true;
        try
        {
            Title="LightX2V · "+vm.T["General settings"];
            var configured=(Path.IsPathFullyQualified(workspace.Text)?1:0)+(File.Exists(python.Text)?1:0)+1;summary.Text=vm.T["Configured"]+$" {configured} / 3";
            workspace.IsEnabled=python.IsEnabled=!Busy&&!vm.Setup.Driver.Pending;save.IsEnabled=!Busy&&Path.IsPathFullyQualified(workspace.Text)&&!vm.Setup.Driver.Pending;
            workspaceState.Text=vm.T[Path.IsPathFullyQualified(workspace.Text)&&Directory.Exists(workspace.Text)?"Configured":"Not configured"];
            pythonState.Text=vm.T[vm.Ready&&python.Text==vm.Settings.Python?"Check passed":"Not checked"];
            pythonState.Foreground=vm.Ready&&python.Text==vm.Settings.Python?SetupUi.Green:SetupUi.Muted;
            if(uninstall!=null)uninstall.IsEnabled=!Busy;
            close.Content=vm.T[Busy?"Stop":"Close"];
            if(contentRoot.Width==624){contentRoot.Height=double.NaN;contentRoot.Measure(new Size(624,double.PositiveInfinity));contentRoot.Height=contentRoot.DesiredSize.Height;Height=Math.Min(contentRoot.Height+SystemParameters.CaptionHeight+16,SystemParameters.WorkArea.Height);}
        }
        finally{refreshing=false;}
    }
}
