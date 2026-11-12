using System.Windows;
using System.Windows.Controls;

namespace LightX2V.Windows;

public sealed class UninstallWindow : Window
{
    public UninstallWindow(MainViewModel vm)
    {
        DataContext=vm;Title=vm.T["Uninstall"];Width=640;Height=Math.Min(620,SystemParameters.WorkArea.Height);WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=SetupUi.Subtle;SetupUi.Resources(this);
        var root=new DockPanel{Margin=new Thickness(24)};Content=root;
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var close=SetupUi.Button(this,"Close");close.Click+=(_,_)=>Close();footer.Children.Add(close);
        var remove=SetupUi.Button(this,"Delete selected items…",true,"ConfirmUninstall");remove.Margin=new Thickness(10,0,0,0);footer.Children.Add(remove);
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};root.Children.Add(scroll);var body=new StackPanel();scroll.Content=body;
        body.Children.Add(SetupUi.Text("Choose what to remove",18,true));body.Children.Add(SetupUi.Text("Select the files to delete. Unchecked items will be kept.",12,false,true));
        var error=SetupUi.Text("",11);body.Children.Add(error);var groups=new Dictionary<string,CheckBox>();UninstallPlan? plan=null;
        try
        {
            plan=AppMaintenance.Discover(vm.Settings,vm.PreferencesDirectory,AppContext.BaseDirectory);
            foreach(var (group,title) in new[]{("models","Remove model weights"),("workspace","Remove workspace data"),("application","Remove app and settings")})
            {
                var items=plan.Items.Where(i=>i.Group==group).ToList();var box=new CheckBox{IsChecked=items.Count>0&&items.All(i=>!i.External),IsEnabled=items.Count>0,Margin=new Thickness(0,14,0,0)};groups[group]=box;
                var content=new StackPanel();content.Children.Add(SetupUi.Text(title,13,true));
                foreach(var item in items){content.Children.Add(new TextBlock{Text=item.Path,FontSize=10,TextWrapping=TextWrapping.Wrap,Foreground=SetupUi.Muted,Margin=new Thickness(0,5,0,0)});if(item.External)content.Children.Add(SetupUi.Text("Includes configured external folders.",10));
                    if(item.Files is {} files)
                    {
                        content.Children.Add(SetupUi.Text("Only listed application files are removed. Other files in this folder are kept.",10));
                        var included=new Expander{Margin=new Thickness(0,5,0,0),Content=new TextBox{Text=string.Join(Environment.NewLine,files.Select(file=>Path.Combine(item.Path,file))),IsReadOnly=true,TextWrapping=TextWrapping.NoWrap,MaxHeight=150,FontSize=10,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto}};included.SetBinding(HeaderedContentControl.HeaderProperty,"T[Show included application files]");content.Children.Add(included);
                    }}
                box.Content=content;body.Children.Add(SetupUi.Card(box));
            }
        }
        catch(Exception ex){error.Text=vm.T[ex.Message];remove.IsEnabled=false;}
        bool working=false;Closing+=(_,e)=>{if(working)e.Cancel=true;};
        remove.Click+=async(_,_)=>
        {
            if(plan==null||working||vm.IsPreparingSetup||vm.IsGenerationBusy||vm.IsDownloading||vm.IsImporting||vm.IsChecking)return;
            try
            {
                var selected=AppMaintenance.Select(plan,groups.Where(g=>g.Value.IsChecked==true).Select(g=>g.Key).ToHashSet());
                if(selected.Items.Count==0){error.Text=vm.T["Choose what to remove first."];return;}
                var confirmation=vm.T["The selected files will be permanently deleted after LightX2V closes."]+"\n\n"+string.Join("\n",selected.Items.Select(i=>i.Path));
                if(MessageBox.Show(this,confirmation,vm.T["Uninstall"],MessageBoxButton.OKCancel,MessageBoxImage.Warning)!=MessageBoxResult.OK)return;
                working=true;remove.IsEnabled=close.IsEnabled=false;foreach(var box in groups.Values)box.IsEnabled=false;
                error.Text=vm.T["Preparing"];
                await AppMaintenance.LaunchAsync(selected);working=false;Application.Current.Shutdown();
            }
            catch(Exception ex){working=false;error.Text=vm.T[ex.Message];remove.IsEnabled=close.IsEnabled=true;foreach(var box in groups.Values)box.IsEnabled=true;}
        };
    }
}
