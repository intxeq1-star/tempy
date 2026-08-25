using System.Text.Json;
using System.Text.Json.Serialization;
using LanManagement.Server.Domain;

namespace LanManagement.Server.Api;

public sealed record LoginRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password);

public sealed record CreateAdminRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("role")] string Role);

public sealed record CreateCommandApiRequest(
    [property: JsonPropertyName("command_type")] string CommandType,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("target")] CommandTargetApiRequest Target,
    [property: JsonPropertyName("display_name")] string? DisplayName);

public sealed record CommandTargetApiRequest(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("device_ids")] IReadOnlyList<string>? DeviceIds,
    [property: JsonPropertyName("group_id")] Guid? GroupId);

public sealed record PolicyUpdateRequest(
    [property: JsonPropertyName("dns_enabled")] bool DnsEnabled,
    [property: JsonPropertyName("dns_server")] string DnsServer,
    [property: JsonPropertyName("chrome_incognito")] bool ChromeIncognito,
    [property: JsonPropertyName("edge_inprivate")] bool EdgeInPrivate,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record GroupRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("device_ids")] IReadOnlyList<string>? DeviceIds);

public sealed record ApplicationRequest(
    [property: JsonPropertyName("package_id")] string PackageId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("desired_version")] string? DesiredVersion,
    [property: JsonPropertyName("install_arguments")] JsonElement? InstallArguments,
    [property: JsonPropertyName("is_enabled")] bool IsEnabled = true);

public sealed record ProvisionDeviceRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("hostname")] string? Hostname,
    [property: JsonPropertyName("agent_token")] string AgentToken);
