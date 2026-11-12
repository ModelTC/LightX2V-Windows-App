using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
namespace LightX2V.Windows;

// Source selection is limited to the two pinned manifest URLs. It never resolves package versions.
static class SetupDownloadSources
{
    static readonly HttpClient client=new(){Timeout=Timeout.InfiniteTimeSpan};
    static readonly ConcurrentDictionary<string,(string Host,DateTime Time)> choices=new();
    public static async Task<string[]> Order(SetupArtifact artifact,CancellationToken token)
    {
        var urls=new[]{artifact.Url,artifact.Mirror}.Where(x=>!string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct().ToArray();
        if(urls.Length<2)return urls;
        var key=string.Join("|",urls.Select(u=>new Uri(u).GetLeftPart(UriPartial.Authority)));
        if(choices.TryGetValue(key,out var choice)&&DateTime.UtcNow-choice.Time<TimeSpan.FromMinutes(10))
            return urls.OrderBy(u=>new Uri(u).GetLeftPart(UriPartial.Authority)==choice.Host?0:1).ToArray();
        async Task<(string Url,double Speed)> Probe(string url)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                var watch=Stopwatch.StartNew();using var request=new HttpRequestMessage(HttpMethod.Get,url);
                request.Headers.Range=new RangeHeaderValue(0,65535);
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                response.EnsureSuccessStatusCode();await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer=new byte[65536];var received=0;
                while(received<buffer.Length){var n=await stream.ReadAsync(buffer.AsMemory(received),timeout.Token);if(n==0)break;received+=n;}
                return (url,received/Math.Max(.001,watch.Elapsed.TotalSeconds));
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(HttpRequestException){return(url,0);}
            catch(OperationCanceledException){return(url,0);}
            catch(IOException){return(url,0);}
        }
        var results=await Task.WhenAll(urls.Select(Probe));token.ThrowIfCancellationRequested();
        var ordered=results.OrderByDescending(r=>r.Speed).Select(r=>r.Url).ToArray();
        if(results.Any(r=>r.Speed>0))choices[key]=(new Uri(ordered[0]).GetLeftPart(UriPartial.Authority),DateTime.UtcNow);
        return ordered;
    }
}
