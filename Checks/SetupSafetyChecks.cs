using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task SetupSafety()
    {
        var workspace=Path.Combine(root,"安装安全检查 空间");Directory.CreateDirectory(workspace);
        var progress=new Progress<SetupProgress>();var env=SetupInstaller.EnvironmentDirectory(workspace);Directory.CreateDirectory(env);
        var sentinel=Path.Combine(env,"user-file.txt");File.WriteAllText(sentinel,"user content");bool refused=false;
        try{await SetupInstaller.Install(workspace,false,[],progress,CancellationToken.None);}catch(IOException){refused=true;}
        Assert(refused&&File.ReadAllText(sentinel)=="user content","Automatic installation preserves an unowned nonempty environment directory");
        var source=await SetupInstaller.PrepareSource(workspace,CancellationToken.None);var infer=Path.Combine(source,"lightx2v","infer.py");
        var original=File.ReadAllBytes(infer);await File.AppendAllTextAsync(infer,"\n# modified\n");refused=false;
        try{await SetupInstaller.PrepareSource(workspace,CancellationToken.None);}catch(IOException){refused=true;}
        Assert(refused&&File.ReadAllText(infer).EndsWith("# modified\n"),"Changed managed inference source is detected and preserved for review");
        File.WriteAllBytes(infer,original);Assert(await SetupInstaller.PrepareSource(workspace,CancellationToken.None)==source,"An intact managed source bundle passes full file verification");
        var bytes=new byte[200000];new Random(42).NextBytes(bytes);var server=new TcpListener(IPAddress.Loopback,0);server.Start();
        var url="http://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port+"/package.whl";int range=-1,requests=0;
        var serving=Task.Run(async()=>{
            while(true)
            {
                using var client=await server.AcceptTcpClientAsync();using var stream=client.GetStream();using var input=new StreamReader(stream,Encoding.ASCII,false,4096,true);
                var line=await input.ReadLineAsync();if(line==null)continue;int offset=0;
                while(!string.IsNullOrEmpty(line=await input.ReadLineAsync()))if(line.StartsWith("Range: bytes=",StringComparison.OrdinalIgnoreCase))offset=int.Parse(line[13..].TrimEnd('-'));
                range=offset;Interlocked.Increment(ref requests);
                var header=$"HTTP/1.1 {(offset>0?"206 Partial Content":"200 OK")}\r\nContent-Length: {bytes.Length-offset}\r\n"+(offset>0?$"Content-Range: bytes {offset}-{bytes.Length-1}/{bytes.Length}\r\n":"")+"Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));await stream.WriteAsync(bytes.AsMemory(offset));
            }
        });
        try
        {
            var cache=Path.Combine(workspace,"cache");Directory.CreateDirectory(cache);var artifact=new SetupArtifact{Name="test",File="package.whl",Url=url,Size=bytes.Length,Sha256=Convert.ToHexString(SHA256.HashData(bytes))};
            File.WriteAllBytes(Path.Combine(cache,artifact.File)+".partial",bytes[..54321]);var result=await SetupInstaller.Download(artifact,cache,[],false,progress,CancellationToken.None);
            Assert(range==54321&&File.ReadAllBytes(result).SequenceEqual(bytes),"Environment downloads resume an existing byte range and verify the final SHA256");
            var previous=requests;await SetupInstaller.Download(artifact,cache,[],false,progress,CancellationToken.None);Assert(requests==previous,"A verified environment package is reused without another HTTP request");
            File.Delete(result);artifact.Sha256=new string('0',64);refused=false;
            try{await SetupInstaller.Download(artifact,cache,[],false,progress,CancellationToken.None);}catch(IOException){refused=true;}
            Assert(refused&&!File.Exists(result),"Invalid environment package hashes cannot become completed downloads");
        }
        finally{server.Stop();try{await serving;}catch(SocketException){}catch(ObjectDisposedException){}}
        var live=SetupManifest.Read().Packages.Where(p=>p.Mirror!=null).OrderBy(p=>p.Size).First();
        var livePath=await SetupInstaller.Download(live,Path.Combine(workspace,"live-mirror"),[],true,progress,CancellationToken.None);
        Assert(await SetupInstaller.Matches(livePath,live,CancellationToken.None),"Python package download with mirror preference and official fallback returns an authentic pinned package");
        var python=AppSettings.Detect().Python;var pidFile=Path.Combine(workspace,"child-pid.txt");var script=Path.Combine(workspace,"owned-child.py");
        File.WriteAllText(script,"import subprocess,sys,time,pathlib\np=subprocess.Popen([sys.executable,'-c','import time;time.sleep(120)'])\npathlib.Path(sys.argv[1]).write_text(str(p.pid))\ntime.sleep(120)\n");
        using var cancel=new CancellationTokenSource();var running=SetupInstaller.Run(python,[script,pidFile],workspace,Path.Combine(root,"cancel.log"),progress,cancel.Token);
        await Until(()=>File.Exists(pidFile),20);var child=int.Parse(File.ReadAllText(pidFile));cancel.Cancel();try{await running;}catch(OperationCanceledException){}
        await Until(()=>!Alive(child),15);Assert(!Alive(child),"Cancelling environment preparation terminates its owned child process tree inside the native Windows job");
    }
}
