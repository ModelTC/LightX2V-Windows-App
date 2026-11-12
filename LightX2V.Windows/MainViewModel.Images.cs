using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;

namespace LightX2V.Windows;

public sealed partial class MainViewModel
{
    internal void ReportImportError(string message)=>Toast=message;
    public ObservableCollection<InputImage> InputImages {get;}=[];
    bool importing,disposed;int pendingImports;
    readonly SemaphoreSlim importQueue=new(1,1);
    Process? importer;
    public bool IsImporting{get=>importing;private set{Set(ref importing,value);RefreshStatus();Changed(nameof(CanAddImages));}}
    public bool HasInputImages=>InputImages.Count>0;
    public bool CanAddImages=>!disposed&&File.Exists(Settings.Python)&&!IsPreparingSetup&&!IsBaseModelDownloading;
    public Command AddImagesCommand{get;private set;}=null!;
    public ParameterCommand RemoveImageCommand{get;private set;}=null!;
    public ParameterCommand ReferenceGenerationCommand{get;private set;}=null!;
    void InitializeImages()
    {
        AddImagesCommand=new(()=>_=ChooseImagesAsync(),()=>CanAddImages);
        ReferenceGenerationCommand=new(p=>_=ReferenceGenerationAsync(p as Generation));
        RemoveImageCommand=new(p=>{if(CanAddImages&&p is InputImage image)InputImages.Remove(image);});
        InputImages.CollectionChanged+=(_,_)=>{Changed(nameof(HasInputImages));Changed(nameof(SizeText));Changed(nameof(CanGenerate));CommandManager.InvalidateRequerySuggested();};
    }
    async Task ChooseImagesAsync()
    {
        if(!CanAddImages)return;
        var dialog=new OpenFileDialog{Title=T["Add reference images"],Filter="Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff",Multiselect=true};
        if(dialog.ShowDialog()==true)await AddImagePathsAsync(dialog.FileNames,true);
    }
    public async Task ReferenceGenerationAsync(Generation? generation)
    {
        if(disposed||generation?.Completed!=true||!CanAddImages)return;
        await AddImagePathsAsync([generation.Request.Output]);
    }
    public async Task AddImagePathsAsync(IEnumerable<string> paths,bool deduplicate=false)
    {
        var selected=paths.ToArray();if(selected.Length==0||!CanAddImages)return;
        pendingImports++;IsImporting=true;
        await importQueue.WaitAsync();
        if(!CanAddImages){pendingImports--;IsImporting=pendingImports>0;importQueue.Release();return;}
        var settings=Settings.Clone();IsImporting=true;Toast="";
        var batch=new List<InputImage>();var created=new List<string>();
        var hashes=new HashSet<string>();
        try
        {
            if(deduplicate)foreach(var existing in InputImages)
                hashes.Add(await Task.Run(()=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(existing.Path)))));
            foreach(var path in selected)
            {
                var output=Path.Combine(settings.Workspace,"imports",Guid.NewGuid().ToString("N")+".png");
                created.Add(output);
                var start=new ProcessStartInfo(settings.Python){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8};
                foreach(var a in new[]{"-X","utf8",Path.Combine(AppContext.BaseDirectory,"Backend","import_image.py"),Path.GetFullPath(path),output})start.ArgumentList.Add(a);
                var temp=Path.Combine(settings.Workspace,"tmp");Directory.CreateDirectory(temp);start.Environment["TEMP"]=temp;start.Environment["TMP"]=temp;
                using var process=new Process{StartInfo=start};importer=process;process.Start();
                var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
                var response=await stdout;_ = await stderr;importer=null;if(disposed)return;
                using var json=JsonDocument.Parse(response);var data=json.RootElement;
                if(process.ExitCode!=0)throw new InvalidOperationException(T[data.GetProperty("code").GetString()??"invalid_image"]);
                if(deduplicate)
                {
                    var digest=await Task.Run(()=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(output))));
                    if(!hashes.Add(digest)){File.Delete(output);continue;}
                }
                if(InputImages.Count+batch.Count>=InputImagePolicy.MaximumCount)throw new InvalidOperationException(string.Format(T["You can add up to {0} images."],InputImagePolicy.MaximumCount));
                batch.Add(new(){Path=output,Name=data.GetProperty("name").GetString()!,Width=data.GetProperty("width").GetInt32(),Height=data.GetProperty("height").GetInt32()});
            }
            if(!disposed){foreach(var image in batch)InputImages.Add(image);created.Clear();}
        }
        catch(Exception e){if(!disposed)Toast=e.Message;}
        finally
        {
            foreach(var path in created)foreach(var candidate in new[]{path,Path.ChangeExtension(path,".tmp")})
                try{File.Delete(candidate);}catch(IOException error){Trace.WriteLine(error);}catch(UnauthorizedAccessException error){Trace.WriteLine(error);}
            importer=null;pendingImports--;IsImporting=pendingImports>0;importQueue.Release();
        }
    }
    (int Width,int Height) CurrentDimensions()
    {
        if(Ratio!="Adaptive"||InputImages.Count==0)return Presets.Dimensions(Tier,Ratio);
        var image=InputImages[^1];return Presets.AdaptiveDimensions(Tier,image.Width,image.Height);
    }
    bool ValidDimensions{get{var (w,h)=CurrentDimensions();return w>=256&&h>=256&&w<=2752&&h<=2752&&(long)w*h<=4400000;}}
    List<InputImage> SnapshotImages(string directory)
    {
        var result=new List<InputImage>();
        foreach(var image in InputImages)
        {
            var folder=Path.Combine(directory,"inputs");Directory.CreateDirectory(folder);
            var path=Path.Combine(folder,$"{result.Count+1:D2}.png");File.Copy(image.Path,path,false);
            result.Add(new(){Id=image.Id,Name=image.Name,Path=path,Width=image.Width,Height=image.Height});
        }
        return result;
    }
    void DisposeImages(){disposed=true;if(importer is {HasExited:false})importer.Kill(true);}
}
