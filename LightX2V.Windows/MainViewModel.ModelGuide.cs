using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LightX2V.Windows;

public sealed partial class MainViewModel
{
    internal Popup? ActiveModelGuide {get;private set;}
    internal void ShowModelGuide(ModelCard card,FrameworkElement anchor)
    {
        if(ActiveModelGuide is {} previous)previous.IsOpen=false;
        var popup=CreateModelGuide(card,anchor);ActiveModelGuide=popup;
        void Unloaded(object sender,RoutedEventArgs e)=>popup.IsOpen=false;
        anchor.Unloaded+=Unloaded;
        popup.Closed+=(_,_)=>{anchor.Unloaded-=Unloaded;if(ReferenceEquals(ActiveModelGuide,popup))ActiveModelGuide=null;};
        popup.IsOpen=true;
    }

    public Popup CreateModelGuide(ModelCard card,FrameworkElement anchor)
    {
        var modelScope=UseModelScope||UseAutomaticDownloadSource&&card.ActiveDownloadSource=="modelScope";
        var address=ModelPage.Address(card.Repository,modelScope);
        var relative="./models/"+card.Repository;
        var command=modelScope?$"modelscope download --model '{card.Repository}' --local_dir '{relative}'":$"hf download {card.Repository} --local-dir '{relative}'";
        var popup=new Popup{PlacementTarget=anchor,Placement=PlacementMode.Custom,AllowsTransparency=true,StaysOpen=false,PopupAnimation=PopupAnimation.None};
        popup.CustomPopupPlacementCallback=(size,target,offset)=>[
            new(new Point(-size.Width+1,target.Height/2-size.Height/2),PopupPrimaryAxis.Vertical),
            new(new Point(target.Width-1,target.Height/2-size.Height/2),PopupPrimaryAxis.Vertical)];
        var body=new StackPanel{Margin=new Thickness(20),DataContext=this};
        body.Children.Add(new TextBlock{Text=card.Repository,FontSize=13,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        var help=SetupUi.Text("Model download command. Save anywhere; automatic downloads are saved in the workspace models folder.",11,false,true);
        help.Margin=new Thickness(0,14,0,14);body.Children.Add(help);
        var commandText=new TextBox{Text=command,IsReadOnly=true,TextWrapping=TextWrapping.NoWrap,FontSize=11,FontFamily=new("Cascadia Mono, Consolas"),BorderThickness=new Thickness(0),Background=Brushes.Transparent,Padding=new Thickness(12,4,12,4),VerticalContentAlignment=VerticalAlignment.Center,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};
        System.Windows.Automation.AutomationProperties.SetAutomationId(commandText,"ModelDownloadCommand");
        body.Children.Add(new Border{Height=52,CornerRadius=new CornerRadius(8),Background=new SolidColorBrush(Color.FromRgb(246,246,246)),Child=commandText});
        var row=new Grid{Margin=new Thickness(0,14,0,0)};row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});body.Children.Add(row);
        var link=new Button{Style=(Style)Application.Current.FindResource("IconButton"),Width=double.NaN,Height=28,HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(6,5,6,5),Content=new TextBlock{Text=T[modelScope?"Open ModelScope":"Open Hugging Face"],TextDecorations=TextDecorations.Underline,FontSize=11}};
        System.Windows.Automation.AutomationProperties.SetAutomationId(link,"OpenModelPage");
        link.Click+=(_,_)=>{try{WindowsShell.OpenWebPage(address);}catch(Exception error){Toast=ShellFailure(error,"Unable to open model page");}};row.Children.Add(link);
        var copy=new Button{Content=T["Copy command"],FontSize=11,FontWeight=FontWeights.SemiBold,Padding=new Thickness(10,5,10,5),Background=new SolidColorBrush(Color.FromRgb(246,246,246)),BorderBrush=SetupUi.Line,BorderThickness=new Thickness(1)};
        System.Windows.Automation.AutomationProperties.SetAutomationId(copy,"CopyModelDownloadCommand");
        copy.Click+=(_,_)=>{try{Clipboard.SetText(command);copy.Content=T["Copied"];}catch(System.Runtime.InteropServices.ExternalException error){Toast=error.Message;}};Grid.SetColumn(copy,1);row.Children.Add(copy);
        var face=new Border{Width=430,Background=new SolidColorBrush(Color.FromRgb(237,237,237)),BorderBrush=new SolidColorBrush(Color.FromRgb(180,180,180)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Child=body,Effect=new DropShadowEffect{BlurRadius=16,ShadowDepth=3,Opacity=.22}};
        face.Margin=new Thickness(12);
        var root=new Grid{Focusable=true,SnapsToDevicePixels=true};root.Children.Add(face);
        var arrow=new System.Windows.Shapes.Path{Data=Geometry.Parse("M0,0 L8,8 0,16 Z"),Fill=face.Background,Stroke=face.BorderBrush,StrokeThickness=1,Width=8,Height=16,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,5,0)};root.Children.Add(arrow);
        KeyEventHandler escape=(_,e)=>{if(e.Key==Key.Escape){popup.IsOpen=false;e.Handled=true;}};
        root.PreviewKeyDown+=escape;
        var host=Window.GetWindow(anchor);
        popup.Child=root;
        popup.Opened+=(_,_)=>
        {
            if(host!=null)host.PreviewKeyDown+=escape;
            var origin=root.PointToScreen(new Point());var target=anchor.PointToScreen(new Point(0,anchor.ActualHeight/2));
            var left=origin.X<target.X;var scale=VisualTreeHelper.GetDpi(root).DpiScaleY;
            var y=Math.Clamp((target.Y-origin.Y)/scale-8,24,Math.Max(24,root.ActualHeight-40));
            arrow.HorizontalAlignment=left?HorizontalAlignment.Right:HorizontalAlignment.Left;arrow.VerticalAlignment=VerticalAlignment.Top;
            arrow.Margin=left?new Thickness(0,y,5,0):new Thickness(5,y,0,0);arrow.RenderTransformOrigin=new Point(.5,.5);arrow.RenderTransform=new RotateTransform(left?0:180);
            root.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,()=>{if(popup.IsOpen)copy.Focus();});
        };
        popup.Closed+=(_,_)=>{if(host!=null)host.PreviewKeyDown-=escape;anchor.Focus();};
        return popup;
    }
}
