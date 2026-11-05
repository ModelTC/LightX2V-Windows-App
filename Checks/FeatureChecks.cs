using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task FeaturesRun(string fixture,bool uiOnly=false,bool twoK=false)
    {
        var state=Files.Read<WorkspaceState>(fixture)!;
        var reference=state.Generations.First(g=>g.Completed).Request.Output;
        var settings=AppSettings.Detect();settings.Workspace=Path.Combine(root,"workspace");
        var prefs=Path.Combine(root,"preferences");Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});
        using var vm=new MainViewModel(prefs);var window=new MainWindow(vm);window.Show();await Until(()=>vm.Ready&&!vm.IsChecking,60);
        if(twoK)
        {
            vm.Prompt="2K preset check";
            var radio=(RadioButton)window.FindName("Resolution2K");
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();
            Assert(vm.Tier==2&&radio.IsChecked==true&&radio.IsEnabled,"2K selector updates the bound image tier");
            foreach(var ratio in Presets.Ratios){vm.Ratio=ratio;var (w,h)=Presets.Dimensions(2,ratio);Assert(vm.CanGenerate&&vm.SizeText==$"{w} × {h} px","2K preset available: "+ratio);}
            var referenceImage=new InputImage{Path=reference,Name="adaptive check",Width=1500,Height=1000};vm.InputImages.Add(referenceImage);vm.Ratio="Adaptive";
            var dimensions=Presets.AdaptiveDimensions(2,1500,1000);Assert(vm.CanGenerate&&vm.SizeText==$"{dimensions.Width} × {dimensions.Height} px"&&dimensions.Width>2000,"2K adaptive reference dimensions use the 2048 area");
            vm.Tier=0;vm.Ratio="1:1";vm.ReuseCommand.Execute(new Generation{Request=new(){Width=dimensions.Width,Height=dimensions.Height,Prompt="Reuse adaptive 2K",InputImages=[referenceImage]}});
            Assert(vm.Tier==2&&vm.Ratio=="Adaptive"&&vm.SizeText==$"{dimensions.Width} × {dimensions.Height} px","Reuse restores non-preset adaptive 2K dimensions");
            vm.ReuseCommand.Execute(new Generation{Request=new(){Width=2048,Height=2048,Prompt="Reuse square 2K",InputImages=[referenceImage]}});
            Assert(vm.Tier==2&&vm.Ratio=="1:1"&&vm.SizeText=="2048 × 2048 px","Square 2K reuse stays square with a non-square reference");
            vm.InputImages.Clear();vm.Ratio="1:1";vm.Prompt="";Shot(window,"2k-selector");
        }
        await Task.Delay(200);Shot(window,"welcome");
        var examples=new[]{("ExampleWonder","capybara"),("ExampleLight","cafe"),("ExampleNature","glass house")};
        foreach(var (id,text) in examples){var b=Descendants<Button>(window).First(b=>AutomationProperties.GetAutomationId(b)==id);Invoke(b);await Until(()=>vm.Prompt.Contains(text));}
        Assert(true,"All three welcome cards insert their real example prompts");vm.Prompt="";
        window.Width=1100;window.Height=720;await Task.Delay(150);Fit(window,WorkArea(window),"welcome-minimum");Shot(window,"welcome-minimum");
        window.WindowState=WindowState.Maximized;await Task.Delay(150);
        ((Expander)window.FindName("ModelSection")).IsExpanded=false;await Task.Delay(100);await CheckHeaderHover(window);Shot(window,"header-after-pointer-leave");
        Invoke((Button)window.FindName("SettingsButton"));await Until(()=>Application.Current.Windows.OfType<SettingsWindow>().Any());
        var dialog=Application.Current.Windows.OfType<SettingsWindow>().Single();await Task.Delay(150);Shot(dialog,"settings");
        Assert(Descendants<TextBlock>(dialog).Any(t=>t.Text==vm.T["Check passed"]),"Settings search validates the native ROCm environment");
        var language=Descendants<ComboBox>(dialog).Single();var languagePeer=new ComboBoxAutomationPeer(language);var dropdown=(IExpandCollapseProvider)languagePeer.GetPattern(PatternInterface.ExpandCollapse)!;dropdown.Expand();await Until(()=>language.IsDropDownOpen);Assert(language.Items.Count==2,"Language picker opens both supported languages");dropdown.Collapse();language.SelectedIndex=1;await Task.Delay(150);Shot(dialog,"settings-en");language.SelectedIndex=0;
        var workspace=Descendants<TextBox>(dialog).First(t=>AutomationProperties.GetAutomationId(t)=="WorkspacePath");
        var previous=vm.Settings.Workspace;workspace.Text="";var save=Descendants<Button>(dialog).First(b=>AutomationProperties.GetAutomationId(b)=="SettingsSave");Invoke(save);await Task.Delay(100);
        Assert(dialog.IsVisible&&vm.Settings.Workspace==previous,"Invalid settings are rejected without changing the active workspace");
        workspace.Text=previous;Invoke(save);await Until(()=>!dialog.IsVisible&&vm.Ready&&!vm.IsChecking,60);
        vm.NewCreation();await Task.Delay(100);
        var plus=(Button)window.FindName("AddImagesButton");Assert(plus.IsVisible&&plus.IsEnabled&&plus.Command==vm.AddImagesCommand,"Composer plus control is connected to the image picker");
        var original=Path.Combine(root,"参考,图片.png");File.Copy(reference,original);
        await vm.AddImagePathsAsync([original]);Assert(vm.InputImages.Count==1&&File.Exists(vm.InputImages[0].Path),"Unicode and comma image names import to immutable PNG files");
        var input=vm.InputImages[0];var thumbnail=ImageLoader.Load(input.Path,160);Assert(thumbnail.PixelWidth>0,"Imported thumbnail decodes correctly");
        await Task.Delay(150);Shot(window,"attachment");Fit(window,WorkArea(window),"composer-with-image");
        vm.RemoveImageCommand.Execute(input);Assert(!vm.HasInputImages,"Reference thumbnail can be removed");
        if(uiOnly){window.Close();return;}
        if(twoK)
        {
            vm.Tier=2;vm.Ratio="1:1";vm.ExampleCommand.Execute("wonder");var pending=vm.GenerateAsync();
            await Until(()=>vm.Turns.Count>0&&vm.Turns[^1].BackendPid>0,30);var cancelled=vm.Turns[^1];
            using(var invocation=JsonDocument.Parse(File.ReadAllText(Path.Combine(cancelled.Directory,"invocation.json"))))Assert(cancelled.Request.Task=="t2i"&&cancelled.Request.Width==2048&&invocation.RootElement.GetProperty("profile").GetString()=="qwen-2k","Text-only 2K submission selects the accepted backend profile");
            await vm.StopAsync();await pending;Assert(cancelled.Status=="cancelled"&&!Alive(cancelled.BackendPid),"2K cancellation terminates its owned inference process");vm.NewCreation();
        }
        await vm.AddImagePathsAsync([original]);File.Delete(original);
        vm.Prompt="Keep the cafe interior and the window exactly as in the reference image. Add a bright red ceramic cup on the table.";
        vm.Tier=twoK?2:1;vm.Ratio="1:1";vm.RandomSeed=false;vm.Seed="42";
        await Until(()=>((Button)window.FindName("GenerateButton")).IsEnabled);Invoke((Button)window.FindName("GenerateButton"));
        await Until(()=>vm.IsRunning&&vm.Turns.Count>0&&vm.Turns[^1].BackendPid>0,30);var edited=vm.Turns[^1];
        Assert(edited.Request.Task=="i2i"&&edited.Request.InputImages.Count==1&&File.Exists(edited.Request.InputImages[0].Path),"Image request snapshots references and selects the real i2i backend");
        Assert(!vm.HasInputImages&&!plus.IsEnabled,"Submitted references leave the draft and picker is disabled while running");
        await Until(()=>edited.Step>=3||!vm.IsRunning,twoK?600:240);Shot(window,"image-generation");
        await Until(()=>!vm.IsRunning,twoK?1800:300);Assert(edited.Completed,$"Real {(twoK?"2K":"1K")} image editing completes: "+edited.Error);
        var result=ImageLoader.Load(edited.Request.Output,0);int outputSize=twoK?2048:1024;Assert(result.PixelWidth==outputSize&&result.PixelHeight==outputSize,$"Real image edit produces a valid {outputSize} PNG");
        if(twoK){using var invocation=JsonDocument.Parse(File.ReadAllText(Path.Combine(edited.Directory,"invocation.json")));using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(edited.Directory,"config.json")));Assert(invocation.RootElement.GetProperty("profile").GetString()=="qwen-2k"&&invocation.RootElement.GetProperty("environment").GetProperty("TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL").GetString()=="1"&&config.RootElement.GetProperty("use_tiling_vae").GetBoolean()&&config.RootElement.GetProperty("cpu_offload").GetBoolean(),"2K job freezes the accepted memory profile and attention environment");}
        Assert(!Alive(edited.BackendPid),"Image-edit inference process exits after completion");
        using(var invocation=JsonDocument.Parse(File.ReadAllText(Path.Combine(edited.Directory,"invocation.json")))){var argv=invocation.RootElement.GetProperty("argv").EnumerateArray().Select(v=>v.GetString()).ToArray();Assert(argv.Contains("i2i")&&argv.Contains("inputs/01.png"),"CLI uses saved relative image paths without comma ambiguity");}
        await Task.Delay(150);Shot(window,"image-edit-completed");
        vm.ReuseCommand.Execute(edited);Assert(vm.InputImages.Count==1&&vm.Prompt==edited.Request.Prompt&&vm.Tier==(twoK?2:1),"Reuse restores the reference image, prompt and output tier");
        vm.NewCreation();vm.Tier=0;vm.Ratio="4:3";vm.ExampleCommand.Execute("light");await vm.GenerateAsync();var generated=vm.Turns[^1];
        Assert(generated.Completed&&generated.Request.Task=="t2i"&&generated.Request.InputImages.Count==0,"Text-only generation still completes after the image-input change");
        if(twoK){using var invocation=JsonDocument.Parse(File.ReadAllText(Path.Combine(generated.Directory,"invocation.json")));using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(generated.Directory,"config.json")));Assert(invocation.RootElement.GetProperty("profile").GetString()=="standard"&&!invocation.RootElement.GetProperty("environment").TryGetProperty("TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL",out _)&&!config.RootElement.TryGetProperty("use_tiling_vae",out _),"Small jobs retain the original configuration after a 2K job");}
        await Task.Delay(100);Shot(window,"text-generation-completed");
        Files.Write(Path.Combine(root,"features.json"),new{imageEdit=new{edited.Status,edited.Seconds,edited.Request,edited.Directory},textGeneration=new{generated.Status,generated.Seconds,generated.Request,generated.Directory}});
        window.Close();
    }
    static async Task SettingsPreview()
    {
        var settings=AppSettings.Detect();settings.Workspace=Path.Combine(root,"workspace");var prefs=Path.Combine(root,"preferences");Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});
        using var vm=new MainViewModel(prefs);await Until(()=>vm.Ready&&!vm.IsChecking,120);var dialog=new SettingsWindow(vm);dialog.Show();await Task.Delay(150);Shot(dialog,"settings");Assert(Descendants<TextBlock>(dialog).Any(t=>t.Text==vm.T["Check passed"]),"Final settings preview is connected to the native environment");dialog.Close();
    }
    static void Invoke(Button button){var peer=new ButtonAutomationPeer(button);((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();}
    static async Task CheckHeaderHover(MainWindow window)
    {
        var section=(Expander)window.FindName("ParametersSection");section.IsExpanded=true;await Task.Delay(100);
        var header=(ToggleButton)section.Template.FindName("SectionHeader",section);
        var face=(Border)header.Template.FindName("HeaderFace",header);
        GetCursorPos(out var previous);
        try
        {
            window.Activate();var point=header.PointToScreen(new Point(header.ActualWidth/2,header.ActualHeight/2));
            SetCursorPos((int)point.X,(int)point.Y);await Until(()=>header.IsMouseOver);
            mouse_event(2,0,0,0,0);mouse_event(4,0,0,0,0);await Task.Delay(100);
            var composer=(FrameworkElement)window.FindName("Composer");point=composer.PointToScreen(new Point(20,20));SetCursorPos((int)point.X,(int)point.Y);
            await Until(()=>!header.IsMouseOver);
            Assert(header.IsKeyboardFocused&&face.Background is SolidColorBrush brush&&brush.Color.A==0,"Clicked section returns to transparent background after the pointer leaves while focus remains");
            section.IsExpanded=true;
        }
        finally{SetCursorPos(previous.X,previous.Y);}
    }
    [StructLayout(LayoutKind.Sequential)]struct CursorPoint{public int X,Y;}
    [DllImport("user32.dll")]static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")]static extern void mouse_event(uint flags,uint x,uint y,uint data,nuint extra);
}
