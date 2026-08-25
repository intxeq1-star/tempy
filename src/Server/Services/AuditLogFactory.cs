using System.Text.Json;
using LanManagement.Server.Domain;

namespace LanManagement.Server.Services;

public static class AuditLogFactory
{
    public static AuditLog Create(
        DateTimeOffset at,
        string actor,
        string action,
        string entityType,
        string? entityId,
        object details,
        string? remoteIp = null,
        bool succeeded = true) => new()
        {
            OccurredAt = at,
            Actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            DetailsJson = JsonSerializer.Serialize(details),
            RemoteIp = remoteIp,
            Succeeded = succeeded
        };
}
