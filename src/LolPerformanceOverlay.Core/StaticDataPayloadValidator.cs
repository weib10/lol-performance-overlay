using System.Text.Json;

namespace LolPerformanceOverlay.Core;

public static class StaticDataPayloadValidator
{
    public static void RequireDataObject(string json, string description)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.EnumerateObject().Any())
        {
            throw new InvalidDataException($"Static data {description} has no data object.");
        }
    }
}
