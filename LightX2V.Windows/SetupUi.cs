using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Shape=System.Windows.Shapes.Path;

namespace LightX2V.Windows;
internal static class SetupUi
{
    public static Brush Ink=>(Brush)Application.Current.FindResource("Ink");
    public static Brush Muted=>(Brush)Application.Current.FindResource("Muted");
    public static Brush Line=>(Brush)Application.Current.FindResource("Line");
    public static Brush Subtle {get;}=new SolidColorBrush(Color.FromRgb(248,248,248));
    public static Brush Green {get;}=new SolidColorBrush(Color.FromRgb(71,117,93));
    public static void Resources(Window window)=>window.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/LightX2V.Windows;component/Resources/SetupTheme.xaml",UriKind.Relative)});
    public static TextBlock Text(string key,double size=12,bool bold=false,bool muted=false)
    {
        var value=new TextBlock{FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Foreground=muted?Muted:Ink};value.SetBinding(TextBlock.TextProperty,$"T[{key.Replace("^","^^").Replace(",","^,").Replace("]","^]")}]");return value;
    }
    public static Border Card(UIElement child,Thickness? margin=null)=>new(){Background=Brushes.White,BorderBrush=Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(13),Padding=new Thickness(18),Margin=margin??new Thickness(0,0,0,12),Child=child};
    public static Button Button(Window window,string key,bool primary=false,string? id=null)
    {
        var button=new Button{Style=(Style)window.FindResource(primary?"Primary":"SetupSecondary"),MinHeight=34};button.SetBinding(ContentControl.ContentProperty,$"T[{key.Replace("^","^^").Replace(",","^,").Replace("]","^]")}]");if(id!=null)System.Windows.Automation.AutomationProperties.SetAutomationId(button,id);return button;
    }
    public static Shape Icon(string data,double size=16)=>new(){Data=Geometry.Parse(data),Width=size,Height=size,Stretch=Stretch.Uniform,Stroke=Muted,StrokeThickness=1.4,VerticalAlignment=VerticalAlignment.Center};
    public const string Folder="M2,5 H9 L11,8 H22 V21 H2 Z M2,9 H22";
    public const string Search="M16,9 A7,7 0 1 1 2,9 A7,7 0 1 1 16,9 M14,14 L21,21";
    public static Border Logo()=>new(){Width=36,Height=36,Background=Brushes.Black,CornerRadius=new CornerRadius(10),Child=new Image{Source=(ImageSource)Application.Current.FindResource("BrandBolt"),Width=17,Height=24}};
    public static Border Footer(UIElement child)=>new(){Background=Brushes.White,BorderBrush=Line,BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(24,14,24,16),Child=child};
    public static ComboBox BackendPicker(string path)
    {
        var picker=new ComboBox{MinWidth=190,Height=32,Style=(Style)Application.Current.FindResource("LanguagePicker"),ItemsSource=new[]{"AMD ROCm","NVIDIA CUDA"}};
        picker.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty,new Binding(path){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,Converter=new BackendIndexConverter()});System.Windows.Automation.AutomationProperties.SetAutomationId(picker,"BackendPicker");return picker;
    }
    public static Border SourcePicker(Window window,Action<bool> changed,bool mirror)
    {
        var grid=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};
        foreach(var (name,isMirror) in new[]{("pypi.org",false),("pypi.tsinghua",true)})
        {
            var button=new RadioButton{Content=name,Tag=isMirror,Style=(Style)window.FindResource("SetupSegment"),GroupName="InstallSource"+grid.GetHashCode(),IsChecked=mirror==isMirror};button.Checked+=(_,_)=>changed(isMirror);grid.Children.Add(button);
        }
        return new Border{CornerRadius=new CornerRadius(7),BorderBrush=Line,BorderThickness=new Thickness(1),Background=Brushes.White,Padding=new Thickness(2),Child=grid};
    }
    public static void RefreshSource(Border picker,bool mirror)
    {
        foreach(var radio in ((Panel)picker.Child).Children.OfType<RadioButton>())radio.IsChecked=Equals(radio.Tag,mirror);
    }

}
