using System.Text.Json;

namespace LightX2V.Windows;

public static class LegacyWorkspaceImporter
{
    public static AppSettings? TryRead(string root, Action<string>? report = null)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var baseline = Files.Read<Dictionary<string, JsonElement>>(Path.Combine(root, "baseline-root.json"))
                ?? throw new InvalidDataException("Legacy settings are empty.");
            var models = baseline.TryGetValue("model_workspace", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : root;
            if (string.IsNullOrWhiteSpace(models) || !Path.IsPathFullyQualified(models))
                throw new InvalidDataException("Legacy model path is invalid.");
            return new()
            {
                Workspace = Path.Combine(root, "output", "app-workspace"),
                Python = Path.Combine(root, "python312", "python.exe"),
                Repository = Path.Combine(root, "src"),
                Model = Path.Combine(models, "models", "Qwen-Image-2.1-Turbo"),
                T2IDirectory = Path.Combine(root, "output", "app-models", "lightx2v", "Qwen-Image-2.1-PE-T2I-GGUF-Q8_0"),
                I2IDirectory = Path.Combine(root, "output", "app-models", "lightx2v", "Qwen-Image-2.1-PE-I2I-GGUF-Q8_0")
            };
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Trace.WriteLine($"Legacy workspace import failed: {error}");
            report?.Invoke("Legacy workspace settings could not be read. Please configure your workspace again.");
            return null;
        }
    }
}
