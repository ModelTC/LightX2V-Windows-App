namespace LightX2V.Windows;

public static class InputImagePolicy
{
    sealed class Limits
    {
        public int MaximumCount {get;set;}
        public long MaximumFileBytes {get;set;}
        public long MaximumPixels {get;set;}
        public int MaximumDimension {get;set;}
    }
    static readonly Limits limits=Files.Read<Limits>(Path.Combine(AppContext.BaseDirectory,"Resources","image-input.json"))
        ?? throw new InvalidDataException("Image input policy is missing.");
    public static int MaximumCount=>limits.MaximumCount;
    public static long MaximumFileBytes=>limits.MaximumFileBytes;
    public static long MaximumPixels=>limits.MaximumPixels;
}
