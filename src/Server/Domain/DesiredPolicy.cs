using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanManagement.Server.Domain;

public sealed record DesiredPolicy(
    [property: JsonPropertyName("dns")] DnsDesiredPolicy Dns,
    [property: JsonPropertyName("chrome")] ChromeDesiredPolicy Chrome,
    [property: JsonPropertyName("edge")] EdgeDesiredPolicy Edge)
{
    public static DesiredPolicy Default(string dnsServer) => new(
        new DnsDesiredPolicy(true, dnsServer),
        new ChromeDesiredPolicy(false),
        new EdgeDesiredPolicy(false));

    public static string Serialize(DesiredPolicy policy) => JsonSerializer.Serialize(policy, PolicyJson.Options);

    public static DesiredPolicy Deserialize(string json) =>
        JsonSerializer.Deserialize<DesiredPolicy>(json, PolicyJson.Options)
        ?? throw new InvalidOperationException("The persisted desired policy is invalid.");
}

public sealed record DnsDesiredPolicy(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("server")] string Server);

public sealed record ChromeDesiredPolicy(
    [property: JsonPropertyName("incognito")] bool Incognito);

public sealed record EdgeDesiredPolicy(
    [property: JsonPropertyName("inprivate")] bool InPrivate);

public static class PolicyJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
