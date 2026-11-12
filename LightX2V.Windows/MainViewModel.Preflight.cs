namespace LightX2V.Windows;
public sealed partial class MainViewModel
{
    public string ResourceWarning {get;private set;}="";
    internal string ResourceWarnings(EnvironmentCandidate candidate)
    {
        var parts=new List<string>();
        if(candidate.RamTotal>0&&candidate.RamTotal<48L*1024*1024*1024)parts.Add(T["BF16 models use substantial system memory. Close other applications and start with 512."]);
        if(candidate.GpuTotal>0)parts.Add($"{T["GPU memory"]}: {candidate.GpuTotal/1073741824.0:F1} GiB · {T["Available"]}: {candidate.GpuFree/1073741824.0:F1} GiB");
        return string.Join("\n",parts);
    }
}
