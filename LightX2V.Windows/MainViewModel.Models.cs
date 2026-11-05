using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace LightX2V.Windows;

public sealed class ModelCard : Observable
{
    readonly MainViewModel owner;
    string path="",status="";string? statusKey;bool ready,failed;double percent;
    public string Key {get;}
    public string Title {get;}
    public string Repository {get;}
    public string Path {get=>path;set{if(Set(ref path,value))owner.SetModelPath(this,value);}}
    internal void AssignPath(string value){path=value;Changed(nameof(Path));}
    public bool Ready {get=>ready;internal set{Set(ref ready,value);Changed(nameof(Indicator));}}
    public string Status {get=>statusKey==null?status:owner.T[statusKey];internal set{statusKey=null;status=value;Changed();}}
    internal void SetStatus(string key){statusKey=key;Changed(nameof(Status));}
    public string Indicator=>Ready?"✓":"○";
    public bool IsDownloading=>owner.DownloadingKey==Key;
    public bool Failed {get=>failed;internal set=>Set(ref failed,value);}
    public double Percent {get=>percent;internal set=>Set(ref percent,value);}
    public bool ShowProgress=>owner.DownloadingKey==Key||statusKey==null&&status.Length>0;
    public string DownloadLabel=>owner.T[IsDownloading?"Stop":Ready?"Files ready":Failed?"Retry download":"Download automatically"];
    public bool OptionalEnabled=>Key=="turbo"||owner.EnhancementEnabled||owner.IsDownloadingAll||owner.DownloadingKey==Key;
    public bool CanChoose=>owner.CanConfigureModels&&OptionalEnabled;
    public bool CanDownload=>(!Ready||IsDownloading)&&OptionalEnabled&&!owner.IsPreparingSetup&&!owner.IsRunning&&!owner.IsImporting&&!owner.IsChecking&&(!owner.IsDownloading||owner.DownloadingKey==Key);
    public Command ChooseCommand {get;}
    public Command DownloadCommand {get;}
    public Command GuideCommand {get;}
    public Command StatusCommand {get;}
    public ModelCard(MainViewModel owner,string key,string title,string repository)
    {
        this.owner=owner;Key=key;Title=title;Repository=repository;
        ChooseCommand=new(()=>{var dialog=new OpenFolderDialog{Title=owner.T["Choose"]+" "+Title};if(dialog.ShowDialog()==true){AssignPath(dialog.FolderName);owner.SetModelPath(this,dialog.FolderName);}},()=>CanChoose);
        DownloadCommand=new(()=>{if(owner.DownloadingKey==Key)_=owner.StopModelDownloadAsync();else _=owner.DownloadModelAsync(Key);},()=>CanDownload);
        GuideCommand=new(()=>owner.ShowModelGuide(this));
        StatusCommand=new(()=>owner.RecheckModel(this),()=>!owner.IsDownloading&&!owner.IsRunning);
    }
    internal void Refresh(){foreach(var n in new[]{nameof(DownloadLabel),nameof(IsDownloading),nameof(OptionalEnabled),nameof(CanChoose),nameof(CanDownload),nameof(Indicator),nameof(Status),nameof(ShowProgress)})Changed(n);}
}

public sealed partial class MainViewModel
{
    public ModelCard TurboCard {get;private set;}=null!;
    public ModelCard T2ICard {get;private set;}=null!;
    public ModelCard I2ICard {get;private set;}=null!;
    public IReadOnlyList<ModelCard> ModelCards=>[TurboCard,T2ICard,I2ICard];
    public bool EnhancementEnabled {get=>Settings.EnhancementEnabled;set{if(Settings.EnhancementEnabled==value)return;Settings.EnhancementEnabled=value;SavePreferences();Changed();ModelStateChanged();}}
    public bool UseHuggingFace {get=>Settings.DownloadSource!="modelScope";set{if(value)SetDownloadSource("huggingFace");}}
    public bool UseModelScope {get=>Settings.DownloadSource=="modelScope";set{if(value)SetDownloadSource("modelScope");}}
    public bool IsDownloading=>downloadCancellation!=null;
    public bool IsDownloadingAll=>downloadingAll;
    public bool CanConfigureModels=>!IsPreparingSetup&&!IsRunning&&!IsChecking&&!IsImporting&&!IsDownloading;
    public string DownloadAllLabel=>T[IsDownloading?"Stop downloads":"Download all models"];
    public string? DownloadingKey {get;private set;}
    public Command DownloadAllCommand {get;private set;}=null!;
    public ParameterCommand CopyEnhancedCommand {get;private set;}=null!;
    readonly IModelDownloader modelDownloader;CancellationTokenSource? downloadCancellation;Task<bool>? modelDownloadOperation;bool downloadingAll,modelsDisposed;
    public bool RequiredModelsReady=>ModelCards.Where(c=>c.Key=="turbo"||EnhancementEnabled).All(c=>c.Ready);
    public string RequiredDownloadLabel=>T[IsDownloading?"Stop downloads":RequiredModelsReady?"All required files are ready":ModelCards.Any(c=>c.Failed)?"Retry automatic download":"Download required files automatically"];
    public string ModelDownloadSummary=>DownloadingKey is {} key?ModelCards.Single(c=>c.Key==key).Title+" · "+ModelCards.Single(c=>c.Key==key).Status:RequiredModelsReady?T["All required files are ready"]:ModelCards.FirstOrDefault(c=>c.Failed)?.Status??T["Click automatic download. LightX2V will save and check the files for you; no commands are needed."];
    public Command RequiredModelsDownloadCommand {get;private set;}=null!;
    static string RuntimeDirectory=>Path.Combine(AppContext.BaseDirectory,"Runtime","llama-b11564-vulkan");
    string DefaultModelPath(string repository)
    {
        return Path.Combine(Path.Combine(Settings.Workspace,"models"),repository.Replace('/',Path.DirectorySeparatorChar));
    }
    void InitializeModels()
    {
        TurboCard=new(this,"turbo","Qwen-Image-2.1-Turbo","Qwen/Qwen-Image-2.1-Turbo");
        T2ICard=new(this,"t2i","PE-T2I","lightx2v/Qwen-Image-2.1-PE-T2I-GGUF-Q8_0");
        I2ICard=new(this,"i2i","PE-I2I","lightx2v/Qwen-Image-2.1-PE-I2I-GGUF-Q8_0");
        RequiredModelsDownloadCommand=new(()=>{if(IsDownloading)_=StopModelDownloadAsync();else _=Setup.DownloadModelsAsync();},()=>IsDownloading||CanConfigureModels&&!RequiredModelsReady);
        DownloadAllCommand=new(()=>{if(IsDownloading)_=StopModelDownloadAsync();else _=DownloadAllModelsAsync();},()=>!IsPreparingSetup&&!IsRunning&&!IsImporting&&!IsChecking);
        CopyEnhancedCommand=new(p=>{if(p is Generation g&&g.EnhancedPrompt.Length>0){Clipboard.SetText(g.EnhancedPrompt);Toast=T["Copied"];}});
        ReloadModelPaths();
    }
    void ReloadModelPaths()
    {
        if(Settings.T2IDirectory.Length==0)Settings.T2IDirectory=DefaultModelPath(T2ICard.Repository);
        if(Settings.I2IDirectory.Length==0)Settings.I2IDirectory=DefaultModelPath(I2ICard.Repository);
        TurboCard.AssignPath(Settings.Model);T2ICard.AssignPath(Settings.T2IDirectory);I2ICard.AssignPath(Settings.I2IDirectory);
        foreach(var card in ModelCards)RefreshModel(card);
        Changed(nameof(EnhancementEnabled));Changed(nameof(UseHuggingFace));Changed(nameof(UseModelScope));ModelStateChanged();
    }
    internal void SetModelPath(ModelCard card,string value)
    {
        value=value.Trim();
        if(card.Key=="turbo"){Settings.Model=value;Changed(nameof(Settings));}
        else if(card.Key=="t2i")Settings.T2IDirectory=value;else Settings.I2IDirectory=value;
        SavePreferences();RefreshModel(card);ModelStateChanged();
    }
    void SetDownloadSource(string source){Settings.DownloadSource=source;SavePreferences();Changed(nameof(UseHuggingFace));Changed(nameof(UseModelScope));}
    static bool Gguf(string path)
    {
        if(!ModelValidation.Gguf(path))return false;
        using var stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        Span<byte> magic=stackalloc byte[4];return stream.Read(magic)==4&&magic.SequenceEqual("GGUF"u8);
    }
    static (string Model,string System,string Projector) EnhancementFiles(ModelCard card)
    {
        var name="Qwen-Image-2.1-PE-"+card.Key.ToUpperInvariant();
        var model=Path.Combine(card.Path,name+"-Q8_0.gguf");var system=Path.Combine(card.Path,"system_prompt.txt");
        var projector=card.Key=="i2i"?Path.Combine(card.Path,"mmproj-"+name+"-BF16.gguf"):"";
        if(card.Key=="i2i"&&!File.Exists(projector))projector=Path.Combine(card.Path,"mmproj-"+name+"-F16.gguf");
        if(!Path.IsPathFullyQualified(card.Path)||!Gguf(model)||!File.Exists(system)||new FileInfo(system).Length==0||card.Key=="i2i"&&!Gguf(projector))
            throw new InvalidOperationException("Choose a complete "+card.Title+" model directory.");
        return(model,system,projector);
    }
    void RefreshModel(ModelCard card)
    {
        try
        {
            if(card.Key=="turbo")card.Ready=ModelValidation.Ready(card.Path,"turbo");
            else{EnhancementFiles(card);card.Ready=true;}
            card.SetStatus(card.Ready?"Model files ready":"Missing model files");
        }
        catch(Exception){card.Ready=false;card.SetStatus("Missing model files");}
    }
    internal void RecheckModel(ModelCard card){RefreshModel(card);ModelStateChanged();}
    bool EnhancementReady=>!EnhancementEnabled||((HasInputImages?I2ICard:T2ICard).Ready&&File.Exists(Path.Combine(RuntimeDirectory,"llama-completion.exe"))&&File.Exists(Path.Combine(RuntimeDirectory,"llama-mtmd-cli.exe")));
    PromptEnhancementSettings SnapshotEnhancement()
    {
        var card=HasInputImages?I2ICard:T2ICard;var files=EnhancementFiles(card);
        return new(){RuntimeDirectory=RuntimeDirectory,T2IModel=card.Key=="t2i"?files.Model:"",T2ISystemPrompt=card.Key=="t2i"?files.System:"",I2IModel=card.Key=="i2i"?files.Model:"",I2ISystemPrompt=card.Key=="i2i"?files.System:"",VisionProjector=files.Projector};
    }
    void ModelStateChanged()
    {
        if(TurboCard==null)return;
        foreach(var card in ModelCards)card.Refresh();
        foreach(var name in new[]{nameof(IsDownloading),nameof(CanConfigureModels),nameof(DownloadAllLabel),nameof(IsDownloadingAll),nameof(RequiredModelsReady),nameof(RequiredDownloadLabel),nameof(ModelDownloadSummary),nameof(CanGenerate),nameof(CanAddImages)})Changed(name);
        CommandManager.InvalidateRequerySuggested();
    }
    internal void ShowModelGuide(ModelCard card)=>CreateModelGuide(card).ShowDialog();
    public Window CreateModelGuide(ModelCard card)
    {
        var window=new Window{Title=T["Download guide"],DataContext=this,Owner=Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(),Width=620,Height=510,MinWidth=480,MinHeight=360,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=System.Windows.Media.Brushes.White};SetupUi.Resources(window);
        var body=new StackPanel{Margin=new Thickness(20)};
        body.Children.Add(new TextBlock{Text=card.Repository,FontSize=16,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        var help=SetupUi.Text("Click automatic download. LightX2V will save and check the files for you; no commands are needed.",12,false,true);help.Margin=new Thickness(0,12,0,12);body.Children.Add(help);
        var automatic=SetupUi.Button(window,"Download automatically",true,"GuideAutomaticDownload");automatic.Command=card.DownloadCommand;automatic.SetBinding(ContentControl.ContentProperty,new System.Windows.Data.Binding("DownloadLabel"){Source=card});automatic.HorizontalAlignment=HorizontalAlignment.Left;body.Children.Add(automatic);
        var state=new TextBlock{FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=SetupUi.Muted,Margin=new Thickness(0,10,0,0)};state.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("Status"){Source=card});body.Children.Add(state);
        var address=ModelPage.Address(card.Repository,UseModelScope);var path=(card.Path.Length==0?DefaultModelPath(card.Repository):card.Path).Replace("'","''");
        var command=UseModelScope?$"modelscope download --model {card.Repository} --local_dir '{path}'":$"hf download {card.Repository} --local-dir '{path}'";
        var manual=new StackPanel();var commandBox=new TextBox{Text=command,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MinHeight=70,Padding=new Thickness(10),FontFamily=new("Consolas"),Margin=new Thickness(0,10,0,0)};
        System.Windows.Automation.AutomationProperties.SetAutomationId(commandBox,"ModelDownloadCommand");manual.Children.Add(commandBox);
        var copy=SetupUi.Button(window,"Copy command",false,"CopyModelDownloadCommand");copy.Margin=new Thickness(0,10,0,0);copy.HorizontalAlignment=HorizontalAlignment.Left;copy.Click+=(_,_)=>{try{Clipboard.SetText(command);state.Text=T["Copied"];}catch(Exception e){state.Text=e.Message;}};manual.Children.Add(copy);
        var advanced=new Expander{Style=(Style)window.FindResource("SetupLog"),Content=manual,Margin=new Thickness(0,16,0,12)};advanced.SetBinding(HeaderedContentControl.HeaderProperty,"T[Manual download commands (optional)]");System.Windows.Automation.AutomationProperties.SetAutomationId(advanced,"ManualModelDownload");body.Children.Add(advanced);
        var link=new TextBox{Text=address.AbsoluteUri,IsReadOnly=true,Padding=new Thickness(8),TextWrapping=TextWrapping.Wrap};System.Windows.Automation.AutomationProperties.SetAutomationId(link,"ModelPageAddress");body.Children.Add(link);
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Visibility=Visibility.Collapsed};System.Windows.Automation.AutomationProperties.SetAutomationId(status,"ModelPageStatus");body.Children.Add(status);
        void Notice(string message){status.Text=message;status.Visibility=Visibility.Visible;}
        var buttons=new WrapPanel{Margin=new Thickness(0,12,0,0)};
        var copyLink=SetupUi.Button(window,"Copy link",false);copyLink.Margin=new Thickness(0,0,8,0);copyLink.Click+=(_,_)=>{try{Clipboard.SetText(address.AbsoluteUri);Notice(T["Link copied"]);}catch(Exception e){Notice(e.Message);}};buttons.Children.Add(copyLink);
        var open=SetupUi.Button(window,"Open model page",false,"OpenModelPage");open.Click+=(_,_)=>{try{WindowsShell.OpenWebPage(address);Notice(T["Requested browser to open model page"]);}catch(Exception e){Notice(ShellFailure(e,"Unable to open model page"));}};buttons.Children.Add(open);body.Children.Add(buttons);
        var root=new Grid();root.Children.Add(new ScrollViewer{HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=body});window.Content=root;return window;
    }
    public Task DownloadModelAsync(string key)
    {
        var card=ModelCards.Single(c=>c.Key==key);
        return card.OptionalEnabled?StartModelDownloads([card],false):Task.CompletedTask;
    }
    Task DownloadAllModelsAsync()=>StartModelDownloads(ModelCards.ToArray(),true);
    internal Task<bool> StartRequiredModelDownloads()=>StartModelDownloads(ModelCards.Where(c=>c.Key=="turbo"||EnhancementEnabled).ToArray(),true);
    Task<bool> StartModelDownloads(ModelCard[] cards,bool all)
    {
        if(!CanConfigureModels)return Task.FromResult(false);
        cards=cards.Where(c=>!c.Ready).ToArray();
        if(cards.Length==0){Toast=T["All models ready"];return Task.FromResult(true);}
        downloadingAll=all;var source=new CancellationTokenSource();downloadCancellation=source;ModelStateChanged();
        return modelDownloadOperation=RunModelDownloads(cards,source);
    }
    async Task<bool> RunModelDownloads(ModelCard[] cards,CancellationTokenSource source)
    {
        try
        {
            foreach(var card in cards)
            {
                if(source.IsCancellationRequested||!await DownloadOneAsync(card,source.Token))return false;
            }
            return !source.IsCancellationRequested;
        }
        finally
        {
            downloadCancellation=null;DownloadingKey=null;downloadingAll=false;source.Dispose();ModelStateChanged();
            if(cards.Any(c=>c.Key=="turbo")&&TurboCard.Ready&&!source.IsCancellationRequested&&!Ready&&!modelsDisposed)await CheckAsync();
        }
    }
    async Task<bool> DownloadOneAsync(ModelCard card,CancellationToken token)
    {
        var completed=false;
        try
        {
            if(card.Path.Length==0)card.Path=DefaultModelPath(card.Repository);
            DownloadingKey=card.Key;card.Ready=false;card.Failed=false;card.Percent=0;card.SetStatus("Preparing download");ModelStateChanged();
            var progress=new Progress<ModelDownloadProgress>(value=>
            {
                if(modelsDisposed||token.IsCancellationRequested||DownloadingKey!=card.Key)return;
                card.Percent=value.Percent;
                card.Status=value.Phase=="checking"?T["Checking model files"]+" · "+value.File:$"{value.Percent:F1}% · {value.BytesPerSecond/1048576:F1} MB/s";
                ModelStateChanged();
            });
            var result=await modelDownloader.DownloadAsync(new(card.Key,Settings.DownloadSource,card.Path,Settings.Python,Settings.Workspace),progress,token);
            if(token.IsCancellationRequested||result.Cancelled){card.SetStatus("Download cancelled");return false;}
            if(!result.Completed)throw new IOException(result.Message.Length>0?result.Message:T["The download did not finish. Please try again."]);
            RefreshModel(card);completed=card.Ready;
            if(!completed)throw new IOException(T["The downloaded files are incomplete. Retry the automatic download."]);
            card.Percent=100;return true;
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){card.SetStatus("Download cancelled");return false;}
        catch(Exception e){card.Failed=true;card.Status=T["Download failed"]+": "+e.Message;return false;}
        finally
        {
            if(!completed){var message=card.Status;RefreshModel(card);card.Status=message;Toast=message;}
            DownloadingKey=null;ModelStateChanged();
        }
    }
    public async Task StopModelDownloadAsync()
    {
        var source=downloadCancellation;if(source==null)return;source.Cancel();
        if(modelDownloadOperation is {} pending)await pending;
    }
    void DisposeModels(){modelsDisposed=true;downloadCancellation?.Cancel();}
}
