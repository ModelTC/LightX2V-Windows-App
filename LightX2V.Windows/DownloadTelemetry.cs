using System.Diagnostics;
namespace LightX2V.Windows;

// Only received network bytes enter the rate window; cached/resumed bytes only reduce remaining work.
public sealed class DownloadRate
{
    readonly Stopwatch watch=Stopwatch.StartNew();
    readonly Queue<(double Time,long Bytes)> samples=new();
    public double Observe(long received)
    {
        lock(samples)
        {
            var now=watch.Elapsed.TotalSeconds;
            if(samples.Count>0&&(received<samples.Last().Bytes||now-samples.Last().Time>5))samples.Clear();
            samples.Enqueue((now,received));
            while(samples.Count>2&&samples.ElementAt(1).Time<=now-5)samples.Dequeue();
            var first=samples.Peek();return now-first.Time>=.3?Math.Max(0,received-first.Bytes)/(now-first.Time):0;
        }
    }
}
public static class DownloadDisplay
{
    public static string Remaining(L10n t,double? seconds)
    {
        if(seconds is not double value||!double.IsFinite(value)||value<0)return t["Estimating remaining time…"];
        if(value<60)return t["Less than a minute remaining"];
        var minutes=Math.Ceiling(value/60);
        return minutes<60?string.Format(t["About {0} min remaining"],minutes):string.Format(t["About {0} h {1} min remaining"],Math.Floor(minutes/60),minutes%60);
    }
    public static string Format(L10n t,string stage,double rate,double? eta)=>stage=="Downloading environment"?
        (rate>0?$"{rate/1048576:F1} MB/s · ":"")+Remaining(t,eta):"";
    public static string Detail(L10n t,SetupProgress progress)=>progress.Detail+
        (progress.Stage=="Downloading environment"?"\n"+Format(t,progress.Stage,progress.BytesPerSecond,progress.EtaSeconds):"");
}
