using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace LightX2V.Windows;

public sealed class InverseVisibility : IValueConverter { public object Convert(object v, Type t, object p, CultureInfo c) => v is bool b ? (b ? Visibility.Collapsed : Visibility.Visible) : v is int n ? (n == 0 ? Visibility.Visible : Visibility.Collapsed) : Visibility.Collapsed; public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing; }
public sealed class InspectorWidth : IValueConverter { public object Convert(object v, Type t, object p, CultureInfo c) => new GridLength(v is true ? 320 : 0); public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing; }
public sealed class EqualConverter : IMultiValueConverter { public object Convert(object[] v, Type t, object p, CultureInfo c) => v.Length == 2 && Equals(v[0], v[1]); public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException(); }
public sealed class LocalizedConverter : IMultiValueConverter { public object Convert(object[] v, Type t, object p, CultureInfo c) => L10n.Current[v[0]?.ToString() ?? ""]; public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException(); }
public sealed class RatioIconConverter : IValueConverter { public object Convert(object v, Type t, object p, CultureInfo c) { var parts = (v?.ToString() ?? "1:1").Split(':'); double r = parts.Length == 2 ? double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture) : 1; return p?.ToString() == "height" ? 22 / Math.Max(1, r) : 22 * Math.Min(1, r); } public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing; }
public static class ImageLoader { public static BitmapImage Load(string path, int maximum = 1800) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; if (maximum > 0) image.DecodePixelWidth = maximum; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image; } }
public sealed class ImageConverter : IValueConverter { public object? Convert(object v, Type t, object p, CultureInfo c) { if (v is not string path || !File.Exists(path)) return null; try { return ImageLoader.Load(path,p?.ToString()=="thumbnail"?160:1800); } catch { return null; } } public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing; }

public sealed class TierChoice : IValueConverter { public object Convert(object v, Type t, object p, CultureInfo c) => System.Convert.ToInt32(v) == System.Convert.ToInt32(p); public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is true ? System.Convert.ToInt32(p) : Binding.DoNothing; }

public sealed class NonEmptyVisibility : IValueConverter {public object Convert(object v,Type t,object p,CultureInfo c)=>string.IsNullOrEmpty(v?.ToString())?Visibility.Collapsed:Visibility.Visible;public object ConvertBack(object v,Type t,object p,CultureInfo c)=>Binding.DoNothing;}
public sealed class PreviewWidth : IMultiValueConverter {
    public object Convert(object[] v,Type t,object p,CultureInfo c){if(v.Length<4||v.Any(x=>x==DependencyProperty.UnsetValue))return 280.0;var available=Math.Max(1,Math.Min(620,System.Convert.ToDouble(v[0])-56));var height=Math.Clamp(System.Convert.ToDouble(v[1])-449,280,500);return Math.Min(available,Math.Max(220,height*System.Convert.ToDouble(v[2])/Math.Max(1,System.Convert.ToDouble(v[3]))));}
    public object[] ConvertBack(object v,Type[] t,object p,CultureInfo c)=>throw new NotSupportedException();
}

public static class StepProgress { public static int[] Steps {get;}=Enumerable.Range(1,8).ToArray(); }
public sealed class StepFill : IMultiValueConverter {
    static readonly Brush Past=BrushesCollection("#242424"), Current=BrushesCollection("#858585"), Future=BrushesCollection("#E5E5E5");
    static Brush BrushesCollection(string color){var b=(SolidColorBrush)new BrushConverter().ConvertFromString(color)!;b.Freeze();return b;}
    public object Convert(object[] v,Type t,object p,CultureInfo c)=>v.Length==2&&v[0] is int index&&v[1] is int step?(index<step?Past:index==step?Current:Future):Future;
    public object[] ConvertBack(object v,Type[] t,object p,CultureInfo c)=>throw new NotSupportedException();
}

public sealed class HasItemsVisibility : IValueConverter {public object Convert(object v,Type t,object p,CultureInfo c)=>v is int n&&n>0?Visibility.Visible:Visibility.Collapsed;public object ConvertBack(object v,Type t,object p,CultureInfo c)=>Binding.DoNothing;}

public sealed class CudaVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>Equals(value,"cuda")?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
    public object ConvertBack(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>throw new NotSupportedException();
}

public sealed class BackendIndexConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>Equals(value,"cuda")?1:0;
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Equals(value,1)?"cuda":"amd_rocm";
}

public sealed class ReferenceIndexConverter : IValueConverter {public object Convert(object value,Type target,object parameter,CultureInfo culture)=>value is int index?(index+1).ToString():"";public object ConvertBack(object value,Type target,object parameter,CultureInfo culture)=>Binding.DoNothing;}

public sealed class TurnSpacingConverter : IMultiValueConverter
{
    public object Convert(object[] values,Type target,object parameter,CultureInfo culture)=>new Thickness(0,0,0,values.Length==2&&values[0] is int index&&values[1] is int count&&index<count-1?36:0);
    public object[] ConvertBack(object value,Type[] targets,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
