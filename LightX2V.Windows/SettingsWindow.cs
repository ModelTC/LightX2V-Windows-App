using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace LightX2V.Windows;

public sealed class SettingsWindow : Window
{
    readonly MainViewModel vm;
    readonly Dictionary<string,TextBox> fields=[];
    readonly TextBlock error=new(){TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
    readonly TextBlock configured=new(){FontWeight=FontWeights.SemiBold}, workspaceState=new(), pythonState=new();
    readonly Button search=new(), save=new();
    bool verified,checking;string selectedBackend;
    public SettingsWindow(MainViewModel vm)
    {
        this.vm=vm;selectedBackend=vm.Settings.Backend;DataContext=vm;Title="LightX2V · "+vm.T["General settings"];
        Width=Math.Min(720,SystemParameters.WorkArea.Width);Height=Math.Min(560,SystemParameters.WorkArea.Height);
        MinWidth=Math.Min(630,Width);MinHeight=Math.Min(450,Height);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        ShowInTaskbar=false;Background=Brushes.White;Foreground=Ink;FontFamily=new("Segoe UI, Microsoft YaHei UI");
        verified=vm.Ready;
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});Content=root;
        var header=new Grid{Margin=new Thickness(22,18,22,16)};root.Children.Add(header);
        header.ColumnDefinitions.Add(new(){Width=new GridLength(38)});header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(new Border{Width=36,Height=36,CornerRadius=new CornerRadius(11),Background=new SolidColorBrush(Color.FromRgb(21,21,21)),Child=new Image{Source=(ImageSource)Application.Current.FindResource("BrandBolt"),Width=18,Height=25}});
        var headings=new StackPanel{Margin=new Thickness(12,0,0,0)};Grid.SetColumn(headings,1);header.Children.Add(headings);
        headings.Children.Add(Label("General settings",20,FontWeights.SemiBold));var desc=Label("Settings summary",11);desc.Foreground=Muted;desc.Margin=new Thickness(0,5,0,0);headings.Children.Add(desc);
        configured.FontSize=11;configured.Foreground=Muted;configured.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(configured,2);header.Children.Add(configured);
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll,1);root.Children.Add(scroll);
        var content=new StackPanel{Margin=new Thickness(22,0,22,16)};scroll.Content=content;
        var languageCard=Card();content.Children.Add(languageCard);
        var languageRow=TitleRow(1,"Language");languageCard.Child=languageRow;
        var language=new ComboBox{Style=(Style)Application.Current.FindResource("LanguagePicker"),Width=180,Height=28,Background=Brushes.White,BorderBrush=Line,ItemsSource=new[]{"简体中文","English"},SelectedIndex=vm.T.Language=="zh"?0:1,HorizontalAlignment=HorizontalAlignment.Right};
        System.Windows.Automation.AutomationProperties.SetAutomationId(language,"SettingsLanguage");
        language.SelectionChanged+=(_,_)=>{vm.SetLanguage(language.SelectedIndex==0?"zh":"en");Title="LightX2V · "+vm.T["General settings"];UpdateStates();};var languageControls=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        languageControls.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M3,12 H21 M12,3 C6,8 6,16 12,21 C18,16 18,8 12,3"),Width=14,Height=14,Stretch=Stretch.Uniform,Stroke=Muted,StrokeThickness=1.2,Margin=new Thickness(0,0,10,0),VerticalAlignment=VerticalAlignment.Center});languageControls.Children.Add(language);Grid.SetColumn(languageControls,1);languageRow.Children.Add(languageControls);
        var workCard=Card();content.Children.Add(workCard);var workStack=new StackPanel();workCard.Child=workStack;
        var workTitle=TitleRow(2,"LightX2V workspace", "Workspace help");SetStateStyle(workspaceState);Grid.SetColumn(workspaceState,1);workTitle.Children.Add(workspaceState);workStack.Children.Add(workTitle);
        workStack.Children.Add(PathField("Workspace",vm.Settings.Workspace,false));
        var advanced=new Expander{Margin=new Thickness(0,8,0,0),FontSize=11};advanced.SetBinding(HeaderedContentControl.HeaderProperty,"T[Advanced configuration]");
        var advancedBody=new StackPanel{Margin=new Thickness(0,8,0,0)};advanced.Content=advancedBody;
        advancedBody.Children.Add(Label("LightX2V source",11));advancedBody.Children.Add(PathField("Repository",vm.Settings.Repository,false));advancedBody.Children.Add(Label("Model directory",11));advancedBody.Children.Add(PathField("Model",vm.Settings.Model,false));workStack.Children.Add(advanced);
        var pythonCard=Card();content.Children.Add(pythonCard);var pythonStack=new StackPanel();pythonCard.Child=pythonStack;
        var pythonTitle=TitleRow(3,"PyTorch environment","Python help");SetStateStyle(pythonState);Grid.SetColumn(pythonState,1);pythonTitle.Children.Add(pythonState);pythonStack.Children.Add(pythonTitle);
        pythonStack.Children.Add(PathField("Python",vm.Settings.Python,true));
        search.HorizontalAlignment=HorizontalAlignment.Left;search.BorderBrush=Line;search.Background=new SolidColorBrush(Color.FromRgb(245,245,245));search.Margin=new Thickness(0,7,0,0);search.Padding=new Thickness(10,6,10,6);var searchLabel=new StackPanel{Orientation=Orientation.Horizontal};searchLabel.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M16,9 A7,7 0 1 1 2,9 A7,7 0 1 1 16,9 M14,14 L21,21"),Width=13,Height=13,Stretch=Stretch.Uniform,Stroke=Ink,StrokeThickness=1.7,Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center});searchLabel.Children.Add(Label("Search local environment",11,FontWeights.SemiBold));search.Content=searchLabel;search.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,"T[Search local environment]");search.Click+=async(_,_)=>await SearchEnvironmentAsync();System.Windows.Automation.AutomationProperties.SetAutomationId(search,"SearchEnvironment");pythonStack.Children.Add(search);
        error.Margin=new Thickness(0,4,0,0);error.FontSize=11;error.Foreground=(Brush)Application.Current.FindResource("Ink");content.Children.Add(error);
        var footer=new Border{BorderBrush=Line,BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(22,14,22,16)};Grid.SetRow(footer,2);root.Children.Add(footer);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};footer.Child=buttons;
        var assistant=new Button{MinWidth=90,Margin=new Thickness(0,0,14,0)};assistant.SetBinding(ContentControl.ContentProperty,"T[Setup assistant]");assistant.Click+=(_,_)=>{Close();vm.Setup.Restart();vm.RequestSetup?.Invoke();};buttons.Children.Add(assistant);
        var close=new Button{MinWidth=64,Margin=new Thickness(0,0,10,0),Background=new SolidColorBrush(Color.FromRgb(245,245,245)),BorderBrush=Line,IsCancel=true};close.SetBinding(ContentControl.ContentProperty,"T[Close]");close.Click+=(_,_)=>Close();buttons.Children.Add(close);
        save.MinWidth=66;save.Style=(Style)Application.Current.FindResource("Primary");save.Padding=new Thickness(16,7,16,7);save.SetBinding(ContentControl.ContentProperty,"T[Save]");save.Click+=(_,_)=>Save();System.Windows.Automation.AutomationProperties.SetAutomationId(save,"SettingsSave");buttons.Children.Add(save);
        fields["Python"].TextChanged+=(_,_)=>{verified=false;selectedBackend="auto";UpdateStates();};
        fields["Workspace"].TextChanged+=(_,_)=>UpdateStates();
        UpdateStates();
    }
    static Brush Ink=>(Brush)Application.Current.FindResource("Ink");static Brush Muted=>(Brush)Application.Current.FindResource("Muted");static Brush Line=>(Brush)Application.Current.FindResource("Line");
    TextBlock Label(string key,double size,FontWeight? weight=null){var t=new TextBlock{FontSize=size,FontWeight=weight??FontWeights.Normal};t.SetBinding(TextBlock.TextProperty,$"T[{key}]");return t;}
    static Border Card()=>new(){BorderBrush=Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(12),Margin=new Thickness(0,0,0,10),Background=Brushes.White};
    Grid TitleRow(int number,string title,string? help=null)
    {
        var row=new Grid{MinHeight=28};row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var left=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};row.Children.Add(left);
        left.Children.Add(new Border{Width=26,Height=26,CornerRadius=new CornerRadius(13),Background=new SolidColorBrush(Color.FromRgb(238,238,238)),Child=new TextBlock{Text=number.ToString(),FontSize=13,FontWeight=FontWeights.SemiBold,Foreground=Muted,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}});
        var text=Label(title,15,FontWeights.SemiBold);text.Margin=new Thickness(9,0,0,0);text.VerticalAlignment=VerticalAlignment.Center;left.Children.Add(text);
        if(help!=null){var info=new TextBlock{Text="ⓘ",FontSize=13,Foreground=Muted,Margin=new Thickness(9,0,0,0),VerticalAlignment=VerticalAlignment.Center};info.SetBinding(ToolTipProperty,$"T[{help}]");left.Children.Add(info);}
        return row;
    }
    static void SetStateStyle(TextBlock state){state.Foreground=Muted;state.FontSize=11;state.VerticalAlignment=VerticalAlignment.Center;}
    Border PathField(string key,string value,bool python)
    {
        var field=new TextBox{Text=value,Padding=new Thickness(10,8,10,8),BorderBrush=Brushes.Transparent,Background=Brushes.Transparent,VerticalContentAlignment=VerticalAlignment.Center,FontSize=12};fields[key]=field;
        System.Windows.Automation.AutomationProperties.SetAutomationId(field,key+"Path");
        var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=new GridLength(36)});grid.Children.Add(field);
        var choose=new Button{Width=30,Height=30,Style=(Style)Application.Current.FindResource("IconButton"),VerticalAlignment=VerticalAlignment.Center,Content=new System.Windows.Shapes.Path{Data=Geometry.Parse("M2,5 H9 L11,8 H22 V21 H2 Z"),Width=16,Height=14,Stretch=Stretch.Uniform,Stroke=Muted,StrokeThickness=1.4}};Grid.SetColumn(choose,1);grid.Children.Add(choose);choose.SetBinding(ToolTipProperty,"T[Choose]");
        choose.Click+=(_,_)=>{if(python){var picker=new OpenFileDialog{Filter="Python executable|python.exe|Windows executable|*.exe"};if(picker.ShowDialog(this)==true)field.Text=picker.FileName;}else{var picker=new OpenFolderDialog();if(picker.ShowDialog(this)==true)field.Text=picker.FolderName;}};
        return new Border{CornerRadius=new CornerRadius(8),BorderBrush=Line,BorderThickness=new Thickness(1),Margin=new Thickness(0,7,0,0),Child=grid};
    }
    static string PythonExecutable(string value){value=value.Trim();if(Directory.Exists(value)){var direct=Path.Combine(value,"python.exe");return File.Exists(direct)?direct:Path.Combine(value,"Scripts","python.exe");}return value;}
    AppSettings Edited(){var value=vm.Settings.Clone();value.Workspace=fields["Workspace"].Text.Trim();value.Backend=selectedBackend;value.Python=PythonExecutable(fields["Python"].Text);value.Repository=fields["Repository"].Text.Trim();value.Model=fields["Model"].Text.Trim();return value;}
    void UpdateStates()
    {
        if(fields.Count<4)return;
        var work=Path.IsPathFullyQualified(fields["Workspace"].Text)&&Directory.Exists(fields["Workspace"].Text);
        workspaceState.Text=vm.T[work?"Configured":"Not configured"];
        pythonState.Text=vm.T[checking?"Checking environment":verified?"Check passed":"Not checked"];
        pythonState.Foreground=verified?new SolidColorBrush(Color.FromRgb(71,117,93)):Muted;
        configured.Text=vm.T["Configured"]+$" {(work?1:0)+(verified?1:0)+1} / 3";
        search.IsEnabled=!checking;save.IsEnabled=!checking;
    }
    public Task SearchEnvironmentAsync()
    {
        if(checking)return Task.CompletedTask;checking=true;error.Visibility=Visibility.Collapsed;UpdateStates();
        try
        {
            var edited=Edited();edited.ValidateWorkspace();
            var picker=new EnvironmentPickerWindow(edited,vm.T){Owner=this};
            if(picker.ShowDialog()==true&&picker.Result is {Ready:true} candidate)
            {fields["Python"].Text=candidate.Path;selectedBackend=candidate.Backend;verified=true;}
        }
        catch(Exception e){error.Text=e.Message;error.Visibility=Visibility.Visible;}
        finally{checking=false;UpdateStates();}
        return Task.CompletedTask;
    }
    void Save(){try{vm.ApplySettings(Edited());Close();}catch(Exception e){error.Text=e.Message;error.Visibility=Visibility.Visible;}}
}
