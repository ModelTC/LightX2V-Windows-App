using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace LightX2V.Windows;

public partial class MainWindow
{
    bool composing;
    void InitializeComposer()
    {
        Composer.AllowDrop=true;
        Composer.PreviewDragOver+=(_,e)=>{e.Effects=ViewModel.CanAddImages&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};
        Composer.PreviewDrop+=async(_,e)=>{e.Handled=true;if(e.Data.GetData(DataFormats.FileDrop) is string[] paths)await ViewModel.AddImagePathsAsync(paths);};
        DataObject.AddPastingHandler(PromptEditor,async(_,e)=>
        {
            if(e.DataObject.GetDataPresent(DataFormats.FileDrop)&&e.DataObject.GetData(DataFormats.FileDrop) is string[] paths)
            {e.CancelCommand();await ViewModel.AddImagePathsAsync(paths);}
            else if(e.DataObject.GetDataPresent(DataFormats.Bitmap)&&e.DataObject.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {e.CancelCommand();await PasteBitmapAsync(bitmap);}
        });
        TextCompositionManager.AddPreviewTextInputStartHandler(PromptEditor,(_,_)=>composing=true);
        TextCompositionManager.AddPreviewTextInputHandler(PromptEditor,(_,_)=>composing=false);
        PromptEditor.PreviewKeyUp+=(_,e)=>{if(e.Key==Key.Escape)composing=false;};
        PromptEditor.LostKeyboardFocus+=(_,_)=>composing=false;
        PromptEditor.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key!=Key.Return||Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))return;
            // IME composition uses ImeProcessed; its candidate-confirming Enter must not submit.
            if(composing||e.ImeProcessedKey==Key.Return)return;
            e.Handled=true;if(!e.IsRepeat&&ViewModel.SubmitCommand.CanExecute(null))ViewModel.SubmitCommand.Execute(null);
        };
    }
    async Task PasteBitmapAsync(BitmapSource image)
    {
        if(!ViewModel.CanAddImages)return;
        string? path=null;
        try
        {
            if((long)image.PixelWidth*image.PixelHeight>InputImagePolicy.MaximumPixels)throw new InvalidDataException(ViewModel.T["image_too_large"]);
            var folder=Path.Combine(ViewModel.Settings.Workspace,"tmp");Directory.CreateDirectory(folder);path=Path.Combine(folder,"clipboard-"+Guid.NewGuid().ToString("N")+".png");
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(path))encoder.Save(stream);
            await ViewModel.AddImagePathsAsync([path]);
        }
        catch(Exception error){ViewModel.ReportImportError(error.Message);}
        finally{if(path!=null)try{File.Delete(path);}catch(IOException error){System.Diagnostics.Trace.WriteLine(error);}}
    }
    Point? imageDragOrigin;
    Image? imageDragSource;
    void ImageDragStart(object sender,MouseButtonEventArgs e)
    {
        imageDragOrigin=e.GetPosition(this);imageDragSource=sender as Image;
    }
    void ImageDragMove(object sender,MouseEventArgs e)
    {
        if(e.LeftButton!=MouseButtonState.Pressed){imageDragOrigin=null;imageDragSource=null;return;}
        if(sender is not Image image||image!=imageDragSource||imageDragOrigin is not {} origin)return;
        var position=e.GetPosition(this);
        if(Math.Abs(position.X-origin.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(position.Y-origin.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        var path=image.DataContext switch{InputImage reference=>reference.Path,Generation generation when generation.Completed=>generation.Request.Output,_=>null};
        imageDragOrigin=null;imageDragSource=null;
        if(path!=null&&File.Exists(path))DragDrop.DoDragDrop(image,new DataObject(DataFormats.FileDrop,new[]{path}),DragDropEffects.Copy);
    }
    void ChooseModel(object sender,RoutedEventArgs e)
    {
        if(sender is not Button button)return;
        var menu=new ContextMenu();var family=new MenuItem{Header="Qwen-Image-2.1-Turbo"};
        var standard=new MenuItem{Header=ViewModel.T["Standard"],IsCheckable=true,IsChecked=ViewModel.HasSelectedModel};
        standard.Click+=(_,_)=>{ViewModel.SelectStandardModel();};family.Items.Add(standard);menu.Items.Add(family);button.ContextMenu=menu;menu.PlacementTarget=button;menu.IsOpen=true;
    }
}
