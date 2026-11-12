using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
namespace LightX2V.Windows;

public sealed class DriverSetupCard : Border,IDisposable
{
    readonly DriverSetupModel model;readonly bool showReadyDetails;readonly TextBlock title,status,message,error,progressLabel,steps;readonly Button install,recheck,page,stop,reset;readonly StackPanel help;readonly ProgressBar progress;readonly TextBlock device;
    public DriverSetupCard(Window window,DriverSetupModel model,bool autoCheck=true,string headingKey="1 · Prepare your graphics card",bool showReadyDetails=true)
    {
        this.model=model;this.showReadyDetails=showReadyDetails;DataContext=model;VerticalAlignment=VerticalAlignment.Top;Background=Brushes.White;BorderBrush=SetupUi.Line;BorderThickness=new Thickness(1);CornerRadius=new CornerRadius(13);Padding=new Thickness(18);Margin=new Thickness(0,0,0,14);
        System.Windows.Automation.AutomationProperties.SetAutomationId(this,"DriverSetupCard");
        var body=new StackPanel();Child=body;var heading=new Grid();heading.ColumnDefinitions.Add(new());heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});body.Children.Add(heading);
        title=SetupUi.Text(headingKey,15,true);heading.Children.Add(title);status=new TextBlock{FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,0,0)};Grid.SetColumn(status,1);heading.Children.Add(status);
        message=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=SetupUi.Muted,Margin=new Thickness(0,10,0,0)};body.Children.Add(message);
        device=new TextBlock{FontSize=11,Foreground=SetupUi.Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,0)};device.SetBinding(TextBlock.TextProperty,"DeviceName");body.Children.Add(device);
        help=new StackPanel{Margin=new Thickness(0,12,0,0)};body.Children.Add(help);
        var explanation=SetupUi.Text("Graphics software (also called a driver) lets Windows use your graphics card. The official assistant can install or update it for you.",11,false,true);help.Children.Add(explanation);
        steps=new TextBlock{FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,10),LineHeight=22};System.Windows.Automation.AutomationProperties.SetAutomationId(steps,"DriverInstructions");help.Children.Add(steps);
        var actions=new WrapPanel();help.Children.Add(actions);
        install=SetupUi.Button(window,"Download and open the official assistant",true,"DriverDownload");install.Margin=new Thickness(0,0,8,8);install.Click+=async(_,_)=>await model.DownloadAndOpenAsync();actions.Children.Add(install);
        recheck=SetupUi.Button(window,"I have finished installing · Check again",false,"DriverRecheck");recheck.Margin=new Thickness(0,0,8,8);recheck.Click+=async(_,_)=>await model.CheckAsync(true);actions.Children.Add(recheck);
        progressLabel=new TextBlock{FontSize=11,Foreground=SetupUi.Muted,Margin=new Thickness(0,4,0,5)};help.Children.Add(progressLabel);progress=new ProgressBar{Height=3,Maximum=100,Foreground=SetupUi.Ink,Background=SetupUi.Line};help.Children.Add(progress);
        error=new TextBlock{FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(164,81,69)),Margin=new Thickness(0,8,0,0)};body.Children.Add(error);
        page=SetupUi.Button(window,"Open the official download page",false,"DriverOfficialPage");page.Margin=new Thickness(0,0,8,8);page.Click+=(_,_)=>model.OpenOfficialPage();actions.Children.Add(page);
        var copy=SetupUi.Button(window,"Copy official link",false,"DriverCopyLink");copy.Margin=new Thickness(0,0,8,8);copy.Click+=(_,_)=>{try{Clipboard.SetText(DriverAssistant.Read(model.Backend).LocalizedPage);model.LinkCopied();}catch(Exception e){error.Text=e.Message;error.Visibility=Visibility.Visible;}};actions.Children.Add(copy);
        stop=SetupUi.Button(window,"Stop downloading",false,"DriverStop");stop.Margin=new Thickness(0,0,0,8);stop.Click+=async(_,_)=>await model.CancelAsync();actions.Children.Add(stop);
        reset=SetupUi.Button(window,"I closed the official installer · Start again",false,"DriverResetWaiting");reset.Margin=new Thickness(0,0,8,8);reset.Click+=async(_,_)=>{model.ResetWaiting();await model.CheckAsync(true);};actions.Children.Add(reset);
        model.PropertyChanged+=Changed;Loaded+=async(_,_)=>{if(autoCheck&&!model.Checked&&!model.Busy)await model.CheckAsync();};Refresh();
    }
    void Changed(object? sender,PropertyChangedEventArgs args)=>Refresh();
    void Refresh()
    {
        status.Visibility=model.CanContinue&&!showReadyDetails?Visibility.Collapsed:Visibility.Visible;device.Visibility=model.CanContinue&&!showReadyDetails?Visibility.Collapsed:Visibility.Visible;status.Text=model.Status;status.Foreground=model.CanContinue?SetupUi.Green:SetupUi.Muted;message.Text=model.CanContinue&&!showReadyDetails?model.T["Graphics software is installed"]:model.Message;error.Text=model.Error;error.Visibility=string.IsNullOrEmpty(model.Error)?Visibility.Collapsed:Visibility.Visible;
        help.Visibility=model.CanContinue?Visibility.Collapsed:Visibility.Visible;
        steps.Text=model.T[model.Backend=="cuda"?"1. Click the download button below.\n2. If Windows asks for permission, check that the publisher is NVIDIA Corporation, then choose Yes.\n3. In NVIDIA App, open Drivers, then Download and Install. Keep the default options.\n4. When installation is finished, return here and check again.":"1. Click the download button below.\n2. If Windows asks for permission, check that the publisher is Advanced Micro Devices (AMD), then choose Yes.\n3. Follow the AMD window to install the recommended graphics software. Keep the default options.\n4. When installation is finished, return here and check again."];
        steps.Visibility=model.Pending||model.Assessment?.CanInstall==true?Visibility.Visible:Visibility.Collapsed;if(model.Pending)steps.Text=string.Format(model.T["Finish installing in the {0} window first. If it asks for a restart, save your files and restart. Reopen LightX2V, then check again."],model.Vendor);
        install.Visibility=model.CanDownload?Visibility.Visible:Visibility.Collapsed;install.IsEnabled=model.CanDownload;recheck.IsEnabled=!model.Busy;page.IsEnabled=!model.Busy;reset.Visibility=model.Pending?Visibility.Visible:Visibility.Collapsed;reset.IsEnabled=!model.Busy;stop.Visibility=model.Busy?Visibility.Visible:Visibility.Collapsed;
        progress.Visibility=model.Busy?Visibility.Visible:Visibility.Collapsed;progress.IsIndeterminate=model.Busy&&model.Percent<=0;progress.Value=model.Percent;progressLabel.Text=model.Busy?model.Message+(model.Percent>0?$" · {model.Percent:F0}%":""):"";
    }
    public void Dispose()=>model.PropertyChanged-=Changed;
}
