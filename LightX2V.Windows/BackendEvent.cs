using System.Text.Json;

namespace LightX2V.Windows;

// Only the versioned NDJSON envelope is a protocol event. Dependencies may print
// ordinary JSON on either stream; retain it as text instead of invoking handlers.
internal static class BackendEvent
{
    internal static JsonElement ParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var value = document.RootElement;
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == JsonValueKind.Number && protocol.TryGetInt32(out var version) && version == 1 &&
                Text(value, "type") && Valid(value, value.GetProperty("type").GetString()!)) return value.Clone();
        }
        catch (JsonException) { }
        return JsonSerializer.SerializeToElement(new { protocol = 1, type = "log", message = line }, Files.Json);
    }

    static bool Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String;
    static bool Integer(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out _);
    static bool Number(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number) && double.IsFinite(number);
    static bool Duration(JsonElement value, string key)
    {
        if (!Number(value, key)) return false;
        var seconds = value.GetProperty(key).GetDouble();
        return seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds - 1;
    }
    static bool Optional(JsonElement value, string key, Func<JsonElement, string, bool> check) => !value.TryGetProperty(key, out _) || check(value, key);
    static bool Valid(JsonElement value, string type) => type switch
    {
        "log" or "error" => Text(value, "message"),
        "status" or "enhancement_phase" => Text(value, "phase"),
        "child_started" or "enhancement_child_started" => Integer(value, "pid"),
        "step" => Integer(value, "step") && Optional(value, "total", Integer),
        "effective_request" => Integer(value, "width") && Integer(value, "height"),
        "enhancement_prompt" or "enhancement_thinking" => Text(value, "delta"),
        "enhancement_result" => Text(value, "prompt") && Text(value, "whRatio") && Text(value, "ratioFollow") && Duration(value, "duration"),
        "done" => Text(value, "status") && value.GetProperty("status").GetString() is ("completed" or "failed" or "cancelled") && Optional(value, "seconds", Duration) && Optional(value, "message", Text),
        "ready" => Text(value, "device") && Text(value, "torch") && ValidReady(value),
        _ => false
    };

    static bool ValidReady(JsonElement value)
    {
        foreach (var key in new[] { "python", "backend" }) if (!Optional(value, key, Text)) return false;
        foreach (var key in new[] { "hip", "cuda" })
            if (value.TryGetProperty(key, out var runtime) && runtime.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
        foreach (var key in new[] { "gpuFree", "gpuTotal", "ramAvailable", "ramTotal" })
            if (value.TryGetProperty(key, out var number) && (number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out _))) return false;
        return !value.TryGetProperty("packages", out var packages) || packages.ValueKind == JsonValueKind.Object && packages.EnumerateObject().All(item => item.Value.ValueKind == JsonValueKind.String);
    }
}
