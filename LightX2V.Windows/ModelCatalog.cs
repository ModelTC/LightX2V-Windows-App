namespace LightX2V.Windows;

public sealed class ModelWeightComponent
{
    public string File { get; set; } = "";
    public string Index { get; set; } = "";
}

public sealed class ModelCatalogEntry
{
    public string Title { get; set; } = "";
    public string Repository { get; set; } = "";
    public string[] RequiredFiles { get; set; } = [];
    public string[] DownloadDirectories { get; set; } = [];
    public string[] DownloadFiles { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> JsonProperties { get; set; } = [];
    public double[] SampleSigmas { get; set; } = [];
    public Dictionary<string, ModelWeightComponent> WeightComponents { get; set; } = [];
    public string ModelFile { get; set; } = "";
    public string SystemPromptFile { get; set; } = "";
    public string[] ProjectorFiles { get; set; } = [];
    public Dictionary<string, long> ExpectedGgufSizes { get; set; } = [];
}

public static class ModelCatalog
{
    public sealed class CatalogData
    {
        public int SchemaVersion { get; set; }
        public string CompatibilityVersion { get; set; } = "";
        public long MinimumGgufBytes { get; set; }
        public Dictionary<string, ModelCatalogEntry> Models { get; set; } = [];
    }

    static readonly CatalogData data = Load();
    public static IReadOnlyDictionary<string, ModelCatalogEntry> Models => data.Models;
    public static string CompatibilityVersion => data.CompatibilityVersion;
    public static long MinimumGgufBytes => data.MinimumGgufBytes;
    public static ModelCatalogEntry Get(string key) => data.Models.TryGetValue(key, out var model)
        ? model : throw new ArgumentException("Unknown model resource.", nameof(key));

    static CatalogData Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "model-catalog.json");
        var catalog = Files.Read<CatalogData>(path) ?? throw new InvalidDataException("The model catalog is empty.");
        if (catalog.SchemaVersion != 1 || catalog.CompatibilityVersion.Length == 0 || catalog.MinimumGgufBytes <= 0)
            throw new InvalidDataException("Unsupported model catalog version.");
        foreach (var model in catalog.Models.Values)
        {
            if (model.Title.Length == 0 || model.Repository.Length == 0 || model.RequiredFiles.Length == 0)
                throw new InvalidDataException("The model catalog contains an incomplete model definition.");
        }
        return catalog;
    }
}
