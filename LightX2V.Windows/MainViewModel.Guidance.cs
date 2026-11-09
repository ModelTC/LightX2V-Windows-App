using System.ComponentModel;

namespace LightX2V.Windows;

public sealed partial class MainViewModel
{
    Command? downloadEnhancementCommand,disableEnhancementCommand;
    ModelCard CurrentEnhancementCard=>HasInputImages?I2ICard:T2ICard;
    public bool NeedsEnhancementDownload=>EnhancementEnabled&&!CurrentEnhancementCard.Ready&&!IsGenerationBusy;
    public string CanGenerateReason
    {
        get
        {
            if(!WorkspaceAvailable)return T["Creation history could not be opened. Check workspace permissions or choose another workspace in Settings."];
            if(IsGenerationBusy)return "";
            if(Setup?.Driver.Pending==true)return T["Finish the graphics software step in setup, then check again."];
            if(NeedsInitialSetup)return T["Finish setting up this computer before creating an image."];
            if(IsPreparingSetup)return T["Wait for setup to finish preparing this computer."];
            if(IsBaseModelDownloading)return T["Wait for the base model download to finish."];
            if(!TurboCard.Ready)return T["Prepare the base image model in the right sidebar."];
            if(IsImporting)return T["Wait for the reference images to finish importing."];
            if(NeedsEnhancementDownload)return T[HasInputImages?
                "Download reference-image prompt enhancement, or use the base model to continue.":
                "Download text-to-image prompt enhancement, or use the base model to continue."];
            if(!EnhancementReady)return T["Prompt enhancement is unavailable. Use the base model to continue."];
            if(!ValidDimensions)return T["Choose a supported image size and aspect ratio."];
            var length=Prompt.Trim().Length;
            if(length==0)return T["Describe the image you want to create."];
            if(length>20000)return T["Shorten the prompt to 20,000 characters or fewer."];
            return "";
        }
    }
    public Command DownloadEnhancementCommand=>downloadEnhancementCommand??=new(
        ()=>_=DownloadModelAsync(CurrentEnhancementCard.Key),
        ()=>NeedsEnhancementDownload&&CurrentEnhancementCard.CanDownload&&!CurrentEnhancementCard.IsDownloading);
    public Command DisableEnhancementCommand=>disableEnhancementCommand??=new(
        ()=>EnhancementEnabled=false,()=>EnhancementEnabled&&CanToggleEnhancement);
    public bool ShowEnhancementActions=>EnhancementEnabled&&!EnhancementReady&&!IsGenerationBusy;
    void InitializeGenerationGuidance()=>PropertyChanged+=GenerationGuidanceChanged;
    void GenerationGuidanceChanged(object? sender,PropertyChangedEventArgs args)
    {
        if(args.PropertyName is not (nameof(CanGenerate) or nameof(WorkspaceAvailable)))return;
        Changed(nameof(CanGenerateReason));
        Changed(nameof(NeedsEnhancementDownload));
        Changed(nameof(ShowEnhancementActions));
    }
}
