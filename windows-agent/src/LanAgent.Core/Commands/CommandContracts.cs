using System.Text.Json;

namespace LanAgent.Core.Commands;

/// <summary>A management command received from the server (PROTOCOL_CONTRACT §5.12).</summary>
public sealed class CommandDefinition
{
    public required string CommandId { get; init; }
    public required string CommandType { get; init; }
    public required int Attempt { get; init; }
    public required string CreatedAt { get; init; }
    public JsonElement? Payload { get; init; }
    public int? TimeoutSeconds { get; init; }
    public bool RequiresResult { get; init; } = true;
}

/// <summary>Execution context handed to a handler.</summary>
public sealed class CommandContext
{
    public required CommandDefinition Command { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    public string CommandId => Command.CommandId;
    public string CommandType => Command.CommandType;
    public JsonElement? Payload => Command.Payload;

    public string? PayloadString(string name)
    {
        var el = Payload;
        if (el is not { ValueKind: JsonValueKind.Object }) return null;
        if (el.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    public int PayloadInt(string name, int defaultValue)
    {
        var el = Payload;
        if (el is not { ValueKind: JsonValueKind.Object }) return defaultValue;
        if (el.Value.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
        }
        return defaultValue;
    }

    public bool PayloadBool(string name, bool defaultValue)
    {
        var el = Payload;
        if (el is not { ValueKind: JsonValueKind.Object }) return defaultValue;
        if (el.Value.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }
}

/// <summary>Handler outcome. Error != null or ExitCode != 0 → command FAILED.</summary>
public sealed record HandlerResult(int ExitCode, object? Result, string? Error)
{
    public static HandlerResult Ok(object? result, int exitCode = 0) => new(exitCode, result, null);
    public static HandlerResult Fail(string error, object? result = null, int exitCode = 1) => new(exitCode, result, error);
}

/// <summary>One command type = one handler (PROTOCOL_CONTRACT §10).</summary>
public interface ICommandHandler
{
    string CommandType { get; }
    Task<HandlerResult> ExecuteAsync(CommandContext context);
}

/// <summary>Sends protocol messages best-effort; false means "not delivered now" (durability is layered on top).</summary>
public delegate Task<bool> SendProtocolMessage(Dictionary<string, object?> message);
