using System.Text.Json;

namespace LanManagement.Server.Domain;

public static class PolicyEvaluator
{
    /// <summary>Only reports a device as synced when the reported actual state proves all desired values.</summary>
    public static bool Matches(DesiredPolicy desired, string? actualStateJson)
    {
        if (string.IsNullOrWhiteSpace(actualStateJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(actualStateJson);
            var root = document.RootElement;
            var dnsEnabled = ReadBool(root, "dns", "enabled");
            var chromeIncognito = ReadBool(root, "chrome", "incognito");
            var edgeInPrivate = ReadBool(root, "edge", "inprivate");
            return dnsEnabled.HasValue && dnsEnabled.Value == desired.Dns.Enabled &&
                   string.Equals(ReadString(root, "dns", "server"), desired.Dns.Server, StringComparison.OrdinalIgnoreCase) &&
                   chromeIncognito.HasValue && chromeIncognito.Value == desired.Chrome.Incognito &&
                   edgeInPrivate.HasValue && edgeInPrivate.Value == desired.Edge.InPrivate;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool? ReadBool(JsonElement root, string section, string field)
    {
        if (root.TryGetProperty(section, out var sectionElement) &&
            sectionElement.ValueKind == JsonValueKind.Object &&
            sectionElement.TryGetProperty(field, out var fieldElement) &&
            (fieldElement.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return fieldElement.GetBoolean();
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string section, string field)
    {
        if (root.TryGetProperty(section, out var sectionElement) &&
            sectionElement.ValueKind == JsonValueKind.Object &&
            sectionElement.TryGetProperty(field, out var fieldElement) &&
            fieldElement.ValueKind == JsonValueKind.String)
        {
            return fieldElement.GetString();
        }

        return null;
    }
}
