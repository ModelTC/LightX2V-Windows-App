using System.Text.Json;

namespace LightX2V.Windows;

// The packaged layout belongs here; runtime assets still resolve beside the WPF binary.
public sealed record PackageLayout(string Root,string Manifest,string Executable,string EntryPoint)
{
    public static PackageLayout? Find(string application)
    {
        application=Path.GetFullPath(application).TrimEnd(Path.DirectorySeparatorChar);
        var manifest=Path.Combine(application,"build-manifest.json");
        if(!File.Exists(manifest))return null;
        using var json=JsonDocument.Parse(File.ReadAllText(manifest));
        var version=json.RootElement.TryGetProperty("layoutVersion",out var layout)?layout.GetInt32():1;
        if(version==1)return new(application,manifest,"LightX2V.Windows.exe","LightX2V.Windows.exe");
        if(version!=2||!Path.GetFileName(application).Equals("app",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsupported application package layout.");
        var root=Path.GetDirectoryName(application)!;
        if(json.RootElement.GetProperty("entryPoint").GetString()!="LightX2V.exe"||json.RootElement.GetProperty("appExecutable").GetString()!="app/LightX2V.Windows.exe")throw new InvalidDataException("Invalid application entry point.");
        return new(root,manifest,Path.Combine("app","LightX2V.Windows.exe"),"LightX2V.exe");
    }
}
