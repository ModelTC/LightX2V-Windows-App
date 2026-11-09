using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task InstallerRegressionChecks()
    {
        var workspace = Path.Combine(root, "installer-regression");
        var source = await SetupInstaller.PrepareSource(workspace, CancellationToken.None);
        var manifest = SetupManifest.Read();
        using (var archive = ZipFile.OpenRead(Path.Combine(SetupManifest.Resources, manifest.Source.Archive)))
        {
            var entry = archive.Entries.First(entry => entry.Length > 0 && !entry.FullName.EndsWith('/'));
            var path = Path.Combine(source, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var original = await File.ReadAllBytesAsync(path);
            var modified = (byte[])original.Clone();
            modified[0] ^= 1;
            await File.WriteAllBytesAsync(path, modified);
            try
            {
                var refused = false;
                try { await SetupInstaller.PrepareSource(workspace, CancellationToken.None); }
                catch (IOException) { refused = true; }
                Check(refused && File.ReadAllBytes(path).SequenceEqual(modified),
                    "A same-size change to managed inference source is detected and preserved");
            }
            finally
            {
                await File.WriteAllBytesAsync(path, original);
            }
        }
        Check(await SetupInstaller.PrepareSource(workspace, CancellationToken.None) == source,
            "Restored managed inference source passes full bundled-file verification");

        var bytes = new byte[200000];
        new Random(42).NextBytes(bytes);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = cancellation.Token;
        var requests = 0;
        var lastRange = -1;
        var serving = ServePackagesAsync();
        try
        {
            var artifact = new SetupArtifact
            {
                Name = "fixture", File = "package.whl", Size = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/package.whl"
            };
            var cache = Path.Combine(workspace, "cache");
            Directory.CreateDirectory(cache);
            var target = Path.Combine(cache, artifact.File);
            const int prefixLength = 54321;
            await File.WriteAllBytesAsync(target + ".partial", bytes[..prefixLength], token);
            var progress = new Progress<SetupProgress>();
            var result = await SetupInstaller.Download(artifact, cache, [], false, progress, token);
            Check(Volatile.Read(ref lastRange) == prefixLength && File.ReadAllBytes(result).SequenceEqual(bytes),
                "Environment downloads resume the existing byte range and verify the completed bytes");

            var previousRequests = Volatile.Read(ref requests);
            Check(await SetupInstaller.Download(artifact, cache, [], false, progress, token) == result &&
                Volatile.Read(ref requests) == previousRequests,
                "A verified environment package is reused without another HTTP request");

            artifact.File = "invalid.whl";
            artifact.Sha256 = new string('0', 64);
            var refused = false;
            try { await SetupInstaller.Download(artifact, cache, [], false, progress, token); }
            catch (IOException) { refused = true; }
            Check(refused && !File.Exists(Path.Combine(cache, artifact.File)),
                "An environment package with an invalid SHA256 cannot become a completed download");
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
            try { await serving; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (SocketException) when (token.IsCancellationRequested) { }
        }

        async Task ServePackagesAsync()
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                await using var stream = client.GetStream();
                using var input = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                var request = await input.ReadLineAsync(token);
                if (request == null) continue;
                var offset = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await input.ReadLineAsync(token)))
                {
                    const string rangeHeader = "Range: bytes=";
                    if (line.StartsWith(rangeHeader, StringComparison.OrdinalIgnoreCase))
                        offset = int.Parse(line[rangeHeader.Length..].TrimEnd('-'));
                }
                Interlocked.Exchange(ref lastRange, offset);
                Interlocked.Increment(ref requests);
                var status = offset > 0 ? "206 Partial Content" : "200 OK";
                var header = $"HTTP/1.1 {status}\r\nContent-Length: {bytes.Length - offset}\r\n";
                if (offset > 0) header += $"Content-Range: bytes {offset}-{bytes.Length - 1}/{bytes.Length}\r\n";
                header += "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
                await stream.WriteAsync(bytes.AsMemory(offset), token);
            }
        }
    }
}
