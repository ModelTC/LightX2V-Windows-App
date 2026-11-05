using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task EnhancementRun(string mode,string? fixture=null,string? environmentPreferences=null)
    {
        RecordBackendHashes();
        var settings=environmentPreferences==null?AppSettings.Detect():Files.Read<Preferences>(environmentPreferences)!.Settings.Clone();settings.Workspace=Path.Combine(root,"workspace");
        var preferences=Path.Combine(root,"preferences");Files.Write(Path.Combine(preferences,"preferences.json"),new Preferences{Settings=settings});
        using var vm=new MainViewModel(preferences);var window=new MainWindow(vm);window.Show();
        await Until(()=>vm.Ready&&!vm.IsChecking,60);vm.ModelExpanded=true;
        Assert(vm.ModelCards.Count==3&&vm.T2ICard.Title=="PE-T2I"&&vm.I2ICard.Title=="PE-I2I","All three model preparation cards are present");
        var toggle=(CheckBox)window.FindName("EnhancementToggle");
        ((IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!).Toggle();
        Assert(vm.EnhancementEnabled,"Prompt enhancement switch updates the actual setting");
        vm.UseModelScope=true;
        using(var restored=new MainViewModel(preferences))Assert(restored.EnhancementEnabled&&restored.UseModelScope,"Enhancement switch and download source persist");
        vm.UseHuggingFace=true;Assert(vm.UseHuggingFace&&!vm.UseModelScope,"Hugging Face source remains selectable");vm.UseModelScope=true;
        var good=vm.T2ICard.Path;vm.T2ICard.Path=Path.Combine(root,"missing-model");vm.Prompt="A blue teapot";
        Assert(!vm.CanGenerate,"An enabled but incomplete enhancer blocks generation");vm.EnhancementEnabled=false;Assert(vm.CanGenerate,"Turning enhancement off retains normal image generation");
        vm.T2ICard.Path=good;vm.EnhancementEnabled=true;
        if(mode=="t2i")
        {
            Assert(vm.T2ICard.Ready,"Real PE-T2I GGUF and system prompt are available");
            vm.Tier=0;vm.Ratio="1:1";vm.Prompt="A simple blue teapot";
            var pending=vm.GenerateAsync();await Until(()=>vm.Turns.Count>0&&(vm.Turns[^1].EnhancementPid>0||!vm.IsRunning),60);var cancelled=vm.Turns[^1];
            Assert(cancelled.EnhancementPid>0,"Native PE process starts: "+cancelled.Error);
            await Until(()=>cancelled.EnhancementThinking.Length>0||!vm.IsRunning,900);Assert(vm.IsRunning,"Cancel exercises the loaded GPU enhancer");
            await vm.StopAsync();await pending;
            Assert(cancelled.Status=="cancelled"&&!Alive(cancelled.EnhancementPid)&&cancelled.BackendPid==0,"Cancelling enhancement stops llama.cpp before image inference starts");
            vm.NewCreation();vm.EnhancementEnabled=true;vm.Tier=0;vm.Ratio="Adaptive";vm.RandomSeed=false;vm.Seed="42";
            vm.Prompt="A blue ceramic teapot on a wooden table beside a sunny window. Wide 16:9 composition.";
        }
        else
        {
            Assert(vm.T2ICard.Ready&&vm.I2ICard.Ready,"Both real enhancement models and the vision projector are available");
            await CheckDownloads(vm);
            var previous=Files.Read<Generation>(fixture!)!;
            await vm.AddImagePathsAsync([previous.Request.Output]);Assert(vm.HasInputImages,"Reference image imports before PE-I2I selection");
            vm.Tier=2;vm.Ratio="1:1";vm.RandomSeed=false;vm.Seed="42";
            vm.Prompt="Keep the blue teapot and the scene in <image1>. Add a bright red apple next to the teapot. Keep the other objects unchanged.";
        }
        await Task.Delay(150);Shot(window,"models-ready");
        var task=vm.GenerateAsync();await Until(()=>vm.Turns.Count>0&&vm.IsRunning,15);var generated=vm.Turns[^1];
        await Until(()=>generated.EnhancementThinking.Length>0||!vm.IsRunning,900);
        Assert(vm.IsRunning&&generated.EnhancementThinking.Length>0,"Real model reasoning streams into the conversation");Shot(window,"enhancement-thinking");
        await Until(()=>generated.BackendPid>0||!vm.IsRunning,1200);
        Assert(generated.EnhancementCompleted&&generated.EnhancedPrompt.Length>0&&generated.EnhancedPrompt!=generated.Request.Prompt,"A validated enhanced prompt hands off to image generation");
        Assert(!Alive(generated.EnhancementPid)&&generated.BackendPid>0,"Enhancer exits before the image model is launched");
        Assert(!generated.ShowThinking,"Reasoning collapses when the enhanced prompt begins");generated.ShowThinking=true;
        if(mode=="t2i")Assert(generated.Request.Resolution==512&&generated.Request.Width>generated.Request.Height,"Adaptive text enhancement applies the returned landscape framing");
        else Assert(generated.Request.Width==2048&&generated.Request.Height==2048,"The fixed user-selected 2K canvas takes precedence over model framing");
        Shot(window,"enhanced-prompt");
        await Until(()=>!vm.IsRunning,1800);await task;
        Assert(generated.Completed,"Enhanced "+mode+" image generation completes: "+generated.Error);
        var bitmap=ImageLoader.Load(generated.Request.Output,0);
        Assert(bitmap.PixelWidth==generated.Request.Width&&bitmap.PixelHeight==generated.Request.Height,"Enhanced output decodes at the effective dimensions");
        Assert(generated.ShowThinking,"A manually reopened reasoning panel remains open after generation");
        Assert(File.Exists(Path.Combine(generated.Directory,"enhancement","result.json"))&&File.Exists(Path.Combine(generated.Directory,"effective_request.json")),"Original inputs, enhancement result and effective inference request are saved");
        using(var effective=JsonDocument.Parse(File.ReadAllText(Path.Combine(generated.Directory,"effective_request.json"))))Assert(effective.RootElement.GetProperty("prompt").GetString()==generated.EnhancedPrompt,"The actual image request uses the validated rewritten prompt");
        vm.CopyEnhancedCommand.Execute(generated);Assert(Clipboard.GetText()==generated.EnhancedPrompt,"Enhanced prompt can be copied from the conversation");
        vm.ReuseCommand.Execute(generated);Assert(vm.Prompt==generated.Request.Prompt&&vm.EnhancementEnabled&&vm.Tier==(mode=="t2i"?0:2)&&vm.Ratio==(mode=="t2i"?"Adaptive":"1:1"),"Reuse restores original user intent and enhancement selection");
        Shot(window,"completed");Files.Write(Path.Combine(root,"result.json"),generated);
        using(var restored=new MainViewModel(preferences)){restored.Selected=restored.Recent.First(g=>g.Id==generated.Id);var saved=restored.Turns.Single(g=>g.Id==generated.Id);Assert(saved.EnhancementCompleted&&saved.EnhancedPrompt==generated.EnhancedPrompt&&saved.EnhancementThinking==generated.EnhancementThinking,"Completed enhancement and reasoning restore from history");}
        window.Close();
    }

    static void RecordBackendHashes()=>Files.Write(Path.Combine(root,"backend-sha256.json"),Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory,"Backend"),"*.py").ToDictionary(p=>Path.GetFileName(p)!,p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()));

    static async Task EnhancementUi(string fixture)
    {
        RecordBackendHashes();var settings=AppSettings.Detect();settings.Workspace=Path.Combine(root,"workspace");settings.EnhancementEnabled=true;settings.DownloadSource="modelScope";
        var preferences=Path.Combine(root,"preferences");Files.Write(Path.Combine(preferences,"preferences.json"),new Preferences{Settings=settings});
        using var vm=new MainViewModel(preferences);var window=new MainWindow(vm);window.Show();await Until(()=>vm.Ready&&!vm.IsChecking,60);
        Assert(vm.ModelCards.All(c=>c.Ready),"All three real model cards are ready");
        Shot(window,"models-ready");window.Width=1100;window.Height=720;await Task.Delay(150);Fit(window,WorkArea(window),"pe-minimum");Shot(window,"minimum");
        window.WindowState=WindowState.Maximized;await Task.Delay(150);Fit(window,WorkArea(window),"pe-maximized");
        var models=(Expander)window.FindName("ModelSection");var parameters=(Expander)window.FindName("ParametersSection");ToggleHeader(models);ToggleHeader(parameters);await Task.Delay(100);Shot(window,"collapsed");ToggleHeader(models);
        Assert(vm.ModelExpanded&&!vm.ParametersExpanded,"Model cards fold independently of generation parameters");
        vm.SetLanguage("en");await Task.Delay(100);Assert(vm.T2ICard.Status=="Model files ready","Model readiness text follows language changes");Shot(window,"english");vm.SetLanguage("zh");
        var guideCall=window.Dispatcher.InvokeAsync(()=>vm.T2ICard.GuideCommand.Execute(null),System.Windows.Threading.DispatcherPriority.Background);
        await Until(()=>Application.Current.Windows.OfType<Window>().Any(w=>w!=window&&w.Title==vm.T["Download guide"]));
        var guide=Application.Current.Windows.OfType<Window>().Single(w=>w!=window&&w.Title==vm.T["Download guide"]);
        Descendants<Expander>(guide).Single(e=>System.Windows.Automation.AutomationProperties.GetAutomationId(e)=="ManualModelDownload").IsExpanded=true;await Task.Delay(100);
        var command=Descendants<TextBox>(guide).Single(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="ModelDownloadCommand").Text;Assert(command.StartsWith("modelscope download --model lightx2v/")&&command.Contains("--local_dir"),"Download guide uses the selected source and real destination");
        Invoke(Descendants<Button>(guide).First(b=>b.Content?.ToString()==vm.T["Copy command"]));await Until(()=>Clipboard.GetText()==command);Assert(Clipboard.GetText()==command,"Guide command copies intact");Shot(guide,"download-guide");guide.Close();await guideCall;
        var dialog=new SettingsWindow(vm){Owner=window};dialog.Show();await Task.Delay(100);Invoke(Descendants<Button>(dialog).First(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="SettingsSave"));await Until(()=>!dialog.IsVisible&&vm.Ready&&!vm.IsChecking,60);
        Assert(vm.EnhancementEnabled&&vm.UseModelScope&&vm.T2ICard.Ready&&vm.I2ICard.Ready,"Saving general settings preserves enhancement models and source");
        await CheckDownloads(vm);
        var invalid=new InferenceRequest{Repository=settings.Repository,Model=settings.Model,Config=Path.Combine(AppContext.BaseDirectory,"Resources","qwen_image_21_turbo.json"),Output=Path.Combine(root,"failed-job","image.png"),Prompt="A teapot",PromptEnhancement=new(){RuntimeDirectory=Path.Combine(AppContext.BaseDirectory,"Runtime","llama-b11564-vulkan"),T2IModel=Path.Combine(root,"missing.gguf"),T2ISystemPrompt=Path.Combine(vm.T2ICard.Path,"system_prompt.txt")}};
        var invalidPath=Path.Combine(root,"invalid-request.json");Files.Write(invalidPath,invalid);bool failed=false,started=false;
        using(var bridge=new BridgeClient()){var code=await bridge.RunAsync(settings.Python,"run",invalidPath,Path.Combine(settings.Workspace,"tmp"),e=>{var kind=e.GetProperty("type").GetString();if(kind=="child_started")started=true;if(kind=="done"&&e.GetProperty("status").GetString()=="failed")failed=true;});Assert(code!=0&&failed&&!started,"Enhancement resource failure never silently starts raw image inference");}
        vm.EnhancementEnabled=false;vm.Tier=0;vm.Ratio="4:3";vm.Prompt="A blue ceramic teapot on a wooden table in soft window light.";await vm.GenerateAsync();var plain=vm.Turns[^1];
        Assert(plain.Completed&&!plain.HasEnhancement&&!Directory.Exists(Path.Combine(plain.Directory,"enhancement")),"Turning enhancement off completes ordinary native image generation");Files.Write(Path.Combine(root,"plain-result.json"),plain);Shot(window,"enhancement-off");
        window.Close();
    }

    static async Task CheckDownloads(MainViewModel vm)
    {
        foreach(var card in new[]{vm.T2ICard,vm.I2ICard})
        {
            var original=card.Path;var target=Path.Combine(root,"download-models",card.Key);Directory.CreateDirectory(target);
            foreach(var source in Directory.EnumerateFiles(original,"*.gguf"))
            {
                var destination=Path.Combine(target,Path.GetFileName(source));
                if(!CreateHardLink(destination,source,IntPtr.Zero))throw new System.ComponentModel.Win32Exception();
            }
            card.Path=target;Assert(!card.Ready,"Missing system prompt is detected before download: "+card.Title);
            await vm.DownloadModelAsync(card.Key);
            Assert(card.Ready&&File.Exists(Path.Combine(target,"system_prompt.txt")),"ModelScope download completes and verifies real model files: "+card.Title+": "+card.Status);
        }
        vm.DownloadAllCommand.Execute(null);await Task.Delay(100);Assert(!vm.IsDownloading,"Download all skips the already complete model repositories");
        foreach(var card in new[]{vm.T2ICard,vm.I2ICard}){var directory=card.Path;File.Delete(Path.Combine(directory,"system_prompt.txt"));card.Path=Path.Combine(root,"temporarily-missing");card.Path=directory;}
        vm.EnhancementEnabled=false;vm.DownloadAllCommand.Execute(null);await Until(()=>!vm.IsDownloading,180);
        Assert(vm.T2ICard.Ready&&vm.I2ICard.Ready&&!vm.EnhancementEnabled,"Download all includes both missing PE resources while enhancement is off");vm.EnhancementEnabled=true;
    }
    [DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateHardLink(string name,string existing,IntPtr attributes);
}
