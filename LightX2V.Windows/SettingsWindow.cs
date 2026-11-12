using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace LightX2V.Windows;

// General settings edits a draft; first-time preparation retains its dedicated Windows wizard.
public sealed class SettingsWindow : Window
{
    readonly MainViewModel vm;
    readonly TextBox workspace,python;
    readonly Button save,prepare,close;
    Button? uninstall;
    readonly TextBlock summary,error,workspaceState,pythonState;
    readonly ComputerPreparationPanel computer;
    readonly Expander preparation;
    bool refreshing,closing;
    public bool Completed {get;private set;}

    public SettingsWindow(MainViewModel vm)
    {
        this.vm=vm;DataContext=vm;SetupUi.Resources(this);
        Width=Math.Min(640,SystemParameters.WorkArea.Width);Height=Math.Min(520,SystemParameters.WorkArea.Height);
        MinWidth=Math.Min(620,Width);MinHeight=Math.Min(450,Height);
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        ShowInTaskbar=false;Background=Brushes.White;Foreground=SetupUi.Ink;FontFamily=new("Segoe UI, Microsoft YaHei UI");
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});Content=root;
        var header=new Grid{Margin=new Thickness(18,14,18,14)};header.ColumnDefinitions.Add(new(){Width=new GridLength(36)});header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});root.Children.Add(header);header.Children.Add(SetupUi.Logo());
        var titles=new StackPanel{Margin=new Thickness(12,0,0,0)};Grid.SetColumn(titles,1);header.Children.Add(titles);
        titles.Children.Add(SetupUi.Text("General settings",18,true));titles.Children.Add(SetupUi.Text("3 settings · Change the language anytime",11,false,true));
        summary=new TextBlock{FontSize=11,Foreground=SetupUi.Muted,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(summary,2);header.Children.Add(summary);
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll,1);root.Children.Add(scroll);var body=new StackPanel{Margin=new Thickness(18,0,18,12)};scroll.Content=body;
        var languageRow=Heading(1,"Language");
        var language=new ComboBox{Style=(Style)Application.Current.FindResource("LanguagePicker"),Width=180,Height=30,ItemsSource=new[]{"简体中文","English"},SelectedIndex=vm.T.Language=="zh"?0:1,HorizontalAlignment=HorizontalAlignment.Right};
        System.Windows.Automation.AutomationProperties.SetAutomationId(language,"SettingsLanguage");Grid.SetColumn(language,1);languageRow.Children.Add(language);language.SelectionChanged+=(_,_)=>vm.SetLanguage(language.SelectedIndex==0?"zh":"en");languageRow.Margin=new Thickness(0);body.Children.Add(SettingsCard(languageRow));
        var work=new StackPanel();var workHeading=Heading(2,"LightX2V workspace");workspaceState=StateLabel(workHeading);work.Children.Add(workHeading);
        workspace=PathField(work,vm.Settings.Workspace,"WorkspacePath",false);
        workHeading.SetBinding(ToolTipProperty,"T[Settings, history, images, logs and caches are stored here.]");body.Children.Add(SettingsCard(work));
        var environment=new StackPanel();var environmentHeading=Heading(3,"PyTorch environment");pythonState=StateLabel(environmentHeading);environment.Children.Add(environmentHeading);
        python=PathField(environment,vm.Settings.Python,"PythonPath",true);
        var search=SetupUi.Button(this,"Search local environments",false,"SearchEnvironment");
        var searchContent=new StackPanel{Orientation=Orientation.Horizontal};var searchIcon=SetupUi.Icon(SetupUi.Search,13);searchIcon.Margin=new Thickness(0,0,7,0);searchContent.Children.Add(searchIcon);searchContent.Children.Add(SetupUi.Text("Search local environments",11,true));search.Content=searchContent;search.Margin=new Thickness(0,6,0,0);search.MinHeight=28;search.Height=28;search.Padding=new Thickness(10,5,10,5);search.HorizontalAlignment=HorizontalAlignment.Left;search.Click+=async(_,_)=>await SearchEnvironmentAsync();environment.Children.Add(search);
        computer=new ComputerPreparationPanel(this,vm.Setup);
        var preparationBody=new StackPanel();preparationBody.Children.Add(computer);
        prepare=SetupUi.Button(this,"Prepare environment",true,"SettingsPrepareEnvironment");prepare.HorizontalAlignment=HorizontalAlignment.Left;prepare.Click+=async(_,_)=>await PrepareAsync();preparationBody.Children.Add(prepare);
        preparation=new Expander{Style=(Style)FindResource("SetupLog"),Content=preparationBody,IsExpanded=false,Margin=new Thickness(0,8,0,0)};preparation.SetBinding(HeaderedContentControl.HeaderProperty,"T[Prepare this computer]");preparation.SetBinding(VisibilityProperty,new System.Windows.Data.Binding("IsExpanded"){Source=preparation,Converter=(System.Windows.Data.IValueConverter)Application.Current.FindResource("Bool")});environment.Children.Add(preparation);
        var status=SetupUi.Text("",11,false,true);status.SetBinding(TextBlock.TextProperty,"Setup.Stage");status.SetBinding(VisibilityProperty,new System.Windows.Data.Binding("IsPreparingSetup"){Converter=(System.Windows.Data.IValueConverter)Application.Current.FindResource("Bool")});environment.Children.Add(status);
        var detail=SetupUi.Text("",11,false,true);detail.SetBinding(TextBlock.TextProperty,"Setup.Detail");detail.SetBinding(VisibilityProperty,new System.Windows.Data.Binding("IsPreparingSetup"){Converter=(System.Windows.Data.IValueConverter)Application.Current.FindResource("Bool")});environment.Children.Add(detail);
        var progress=new ProgressBar{Height=3,Maximum=100,Margin=new Thickness(0,8,0,0)};progress.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty,new System.Windows.Data.Binding("Setup.Percent"){Mode=System.Windows.Data.BindingMode.OneWay});progress.SetBinding(VisibilityProperty,new System.Windows.Data.Binding("IsPreparingSetup"){Converter=(System.Windows.Data.IValueConverter)Application.Current.FindResource("Bool")});environment.Children.Add(progress);
        var failure=SetupUi.Text("",11);failure.SetBinding(TextBlock.TextProperty,"Setup.Error");failure.SetBinding(VisibilityProperty,new System.Windows.Data.Binding("Setup.Error"){Converter=(System.Windows.Data.IValueConverter)Application.Current.FindResource("NonEmpty")});environment.Children.Add(failure);body.Children.Add(SettingsCard(environment));
        {
            var maintenance=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};
            var assistant=SetupUi.Button(this,"Setup assistant",false,"SettingsSetupAssistant");assistant.Margin=new Thickness(0,0,8,0);assistant.Click+=(_,_)=>{if(Busy)return;Close();vm.Setup.Restart();vm.RequestSetup?.Invoke();};maintenance.Children.Add(assistant);
            uninstall=SetupUi.Button(this,"Uninstall",false,"SettingsUninstall");uninstall.Click+=(_,_)=>new UninstallWindow(vm){Owner=this}.ShowDialog();maintenance.Children.Add(uninstall);maintenance.Margin=new Thickness(18,8,18,12);Grid.SetRow(maintenance,2);root.Children.Add(maintenance);
        }
        error=SetupUi.Text("",11);error.SetBinding(VisibilityProperty,new System.Windows.Data.Binding("Text"){Source=error,Converter=(System.Windows.Data.IValueConverter)Application.Current.FindResource("NonEmpty")});error.Margin=new Thickness(0,8,0,0);body.Children.Add(error);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        close=SetupUi.Button(this,"Close",false,"SettingsClose");close.Margin=new Thickness(0,0,10,0);close.Click+=async(_,_)=>{if(Busy){await vm.Setup.CancelAsync();}else Close();};actions.Children.Add(close);
        save=SetupUi.Button(this,"Save",true,"SettingsSave");save.Click+=async(_,_)=>await SaveAsync();actions.Children.Add(save);
        var footer=SetupUi.Footer(actions);Grid.SetRow(footer,3);root.Children.Add(footer);
        workspace.TextChanged+=(_,_)=>Refresh();python.TextChanged+=(_,_)=>Refresh();
        vm.PropertyChanged+=Changed;vm.Setup.PropertyChanged+=Changed;vm.T.PropertyChanged+=Changed;
        Closing+=async(_,e)=>{if(closing||!Busy)return;e.Cancel=true;closing=true;await vm.Setup.CancelAsync();Close();};
        Closed+=(_,_)=>{vm.Setup.StopEnvironmentSearch();computer.Dispose();vm.PropertyChanged-=Changed;vm.Setup.PropertyChanged-=Changed;vm.T.PropertyChanged-=Changed;};Refresh();
    }
    bool Busy=>vm.IsPreparingSetup||vm.IsGenerationBusy||vm.IsDownloading||vm.IsImporting||vm.IsChecking;
    static Border SettingsCard(UIElement child)=>new(){Child=child,Padding=new Thickness(12),Margin=new Thickness(0,0,0,8),Background=Brushes.White,BorderBrush=SetupUi.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12)};
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
        var field=new TextBox{Text=value,Padding=new Thickness(8),FontSize=11,BorderThickness=new Thickness(0),Background=Brushes.Transparent};System.Windows.Automation.AutomationProperties.SetAutomationId(field,id);grid.Children.Add(field);
        var choose=SetupUi.Button(this,"Choose…",false);choose.MinWidth=30;choose.Padding=new Thickness(2);choose.Content=SetupUi.Icon(SetupUi.Folder);choose.Click+=async(_,_)=>{if(executable){var picker=new OpenFileDialog{Filter="Python|python.exe"};if(picker.ShowDialog(this)==true){field.Text=picker.FileName;await SearchEnvironmentAsync();}}else{var picker=new OpenFolderDialog();if(picker.ShowDialog(this)==true)field.Text=picker.FolderName;}};Grid.SetColumn(choose,1);grid.Children.Add(choose);parent.Children.Add(new Border{BorderBrush=SetupUi.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Child=grid});return field;
    }
    async Task<bool> SyncWorkspaceAsync()
    {
        try{var path=workspace.Text.Trim();if(path!=vm.Setup.Workspace||!vm.Setup.ComputerScanned){vm.Setup.Workspace=path;await vm.Setup.BeginWorkspaceAsync(commit:false);}return vm.Setup.Error.Length==0;}
        catch(Exception ex){error.Text=vm.T[ex.Message];return false;}
    }
    public async Task SearchEnvironmentAsync()
    {
        if(Busy)return;preparation.IsExpanded=true;if(!await SyncWorkspaceAsync())return;
        if(File.Exists(python.Text.Trim()))await vm.Setup.SelectPythonAsync(python.Text.Trim());else await vm.Setup.SearchEnvironmentsAsync();Refresh();
    }
    async Task PrepareAsync()
    {
        if(Busy||!await SyncWorkspaceAsync())return;
        await vm.Setup.PrepareEnvironmentAsync(!vm.Setup.UseExisting,commit:false);
        if(vm.Setup.PreparedSettings is {} prepared)python.Text=prepared.Python;
        Refresh();
    }
    async Task SaveAsync()
    {
        try
        {
            var settings=vm.Settings.Clone();settings.Workspace=workspace.Text.Trim();settings.Python=python.Text.Trim();
            var changed=settings.Workspace!=vm.Settings.Workspace||settings.Python!=vm.Settings.Python;
            if(changed)
            {
                if(vm.Setup.Workspace!=settings.Workspace&&!await SyncWorkspaceAsync())return;
                if(vm.Setup.Selected?.Path!=settings.Python&&File.Exists(settings.Python))await vm.Setup.SelectPythonAsync(settings.Python);
                if(vm.Setup.PreparedSettings is not {} prepared||prepared.Workspace!=settings.Workspace||prepared.Python!=settings.Python||prepared.Backend!=vm.Setup.Backend||vm.Setup.PreparedEnvironment is not {Ready:true})
                {
                    if(vm.Setup.Selected is not {Ready:true} selected||selected.Path!=settings.Python)throw new InvalidOperationException("Choose a compatible environment");
                    await vm.Setup.PrepareEnvironmentAsync(false,commit:false);
                }
                if(vm.Setup.PreparedSettings is not {} ready||vm.Setup.PreparedEnvironment is not {Ready:true} probe||vm.Setup.Error.Length>0)throw new InvalidOperationException("Choose a compatible environment");
                if(ready.Workspace!=settings.Workspace||ready.Python!=settings.Python||ready.Backend!=vm.Setup.Backend)throw new InvalidOperationException("Choose a compatible environment");
                vm.ApplySettings(ready);vm.CommitVerifiedEnvironment(ready,probe);
            }
            else vm.ApplySettings(settings);
            Completed=true;Close();
        }
        catch(Exception ex){error.Text=vm.T[ex.Message];}
    }
    void Changed(object? sender,PropertyChangedEventArgs e)
    {
        if(ReferenceEquals(sender,vm)&&e.PropertyName==nameof(MainViewModel.Settings))python.Text=vm.Settings.Python;
        if(ReferenceEquals(sender,vm.Setup)&&e.PropertyName==nameof(SetupController.Selected)&&vm.Setup.Selected is {Ready:true} selected)python.Text=selected.Path;
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
            prepare.IsEnabled=!Busy&&vm.Setup.ComputerScanned&&(vm.Setup.UseExisting?vm.Setup.Selected?.Ready==true:vm.Setup.ManagedSupported);
            workspaceState.Text=vm.T[Path.IsPathFullyQualified(workspace.Text)&&Directory.Exists(workspace.Text)?"Configured":"Not configured"];
            pythonState.Text=vm.T[vm.Ready&&python.Text==vm.Settings.Python?"Check passed":"Not checked"];
            pythonState.Foreground=vm.Ready&&python.Text==vm.Settings.Python?SetupUi.Green:SetupUi.Muted;
            if(uninstall!=null)uninstall.IsEnabled=!Busy;
            close.Content=vm.T[Busy?"Stop":"Close"];
        }
        finally{refreshing=false;}
    }
}
