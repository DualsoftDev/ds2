using System.Text.Json;

namespace AasxEditor.Services;

public enum ValueEditorMode
{
    Text,
    Json
}

public static class JsonValueDetector
{
    public static bool LooksLikeJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length < 2) return false;
        var first = trimmed[0];
        if (first != '{' && first != '[') return false;
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static string TryFormat(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return value; }
    }

    public static bool IsValid(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { using var _ = JsonDocument.Parse(value); return true; }
        catch (JsonException) { return false; }
    }
}
