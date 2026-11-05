using System.ComponentModel;
using System.Text.Json;
using Microsoft.Win32;
using LightX2V.Windows;

// No windows, mouse input, browser launch or Explorer launch. Exercise the
// actual shared launcher with an injected OS dispatch and query native metadata.
var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
var passed=new List<string>();
void Check(bool value,string message){if(!value)throw new Exception(message);passed.Add(message);}
try
{
    var integrity=WindowsShell.CurrentIntegrity();
    if(args.Contains("--require-medium"))Check(integrity==0x2000,"Native test process runs at normal user integrity, without elevation");
    foreach(var repository in new[]{"Qwen/Qwen-Image-2.1-Turbo","lightx2v/Qwen-Image-2.1-PE-T2I-GGUF-Q8_0","lightx2v/Qwen-Image-2.1-PE-I2I-GGUF-Q8_0"})
    foreach(var modelScope in new[]{false,true})
    {
        var address=ModelPage.Address(repository,modelScope);
        var expected=(modelScope?"https://modelscope.cn/models/":"https://huggingface.co/")+repository;
        var invoked=0;
        WindowsShell.Launch(WindowsShell.WebRequest(address),0x2000,start=>
        {
            invoked++;
            Check(start.FileName==expected&&start.UseShellExecute&&start.Verb=="open"&&start.ArgumentList.Count==0,
                "HTTPS model page is dispatched intact to the default browser: "+expected);
        });
        Check(invoked==1,"Exactly one launch request for "+expected);
    }
    var fixture=Path.Combine(output,"输出 中文, 空格");Directory.CreateDirectory(fixture);
    var image=Path.Combine(fixture,"图像, 1.png");File.WriteAllText(image,"fixture");
    foreach(var path in new[]{fixture,image})
    {
        var request=WindowsShell.FolderRequest(path);
        Check(request.FileName==fixture&&request.UseShellExecute&&request.Verb=="open"&&request.ArgumentList.Count==0,
            "Folder dispatch preserves Unicode, spaces and commas: "+Path.GetFileName(path));
    }
    try{WindowsShell.FolderRequest(Path.Combine(output,"missing-folder"));throw new Exception("Missing path accepted");}
    catch(DirectoryNotFoundException){Check(true,"Missing output paths are rejected before shell dispatch");}
    foreach(var address in new[]{new Uri("file:///C:/Windows/system32/cmd.exe"),new Uri("ms-settings:defaultapps"),new Uri("relative",UriKind.Relative)})
    {
        try{WindowsShell.WebRequest(address);throw new Exception("Unsupported URI accepted");}
        catch(ArgumentException){Check(true,"Non-web URI is rejected: "+address);}
    }
    var page=WindowsShell.WebRequest(ModelPage.Address("Qwen/Qwen-Image-2.1-Turbo",true));
    foreach(var level in new[]{0,0x1000})
    {
        var invoked=false;
        try{WindowsShell.Launch(page,level,_=>invoked=true);throw new Exception("Restricted launch accepted");}
        catch(DesktopIntegrationRestrictedException){Check(!invoked,"Restricted integrity never attempts browser or Explorer dispatch: "+level);}
    }
    try{WindowsShell.Launch(page,0x2000,_=>throw new Win32Exception(1155));throw new Exception("Native error swallowed");}
    catch(Win32Exception e){Check(e.NativeErrorCode==1155,"Missing default handler errors propagate to the guide's status message");}
    using var choice=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
    var browser=choice?.GetValue("ProgId")?.ToString();
    Check(!string.IsNullOrWhiteSpace(browser),"Native Windows has an HTTPS default browser association");
    var result=new{passed,completedAt=DateTimeOffset.Now,integrity,defaultBrowser=browser,uiAutomationUsed=false,realBrowserLaunchTested=false};
    File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"PASS: {passed.Count} headless checks; native integrity={integrity}; browser={browser}");
    return 0;
}
catch(Exception e){File.WriteAllText(Path.Combine(output,"failure.txt"),e.ToString());Console.Error.WriteLine(e);return 1;}
