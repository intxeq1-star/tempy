using LanManagement.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Api;

[Route("api/audit")]
[Authorize(Roles = "Administrator")]
public sealed class AuditController(IDbContextFactory<ManagementDbContext> contextFactory) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<object>> List([FromQuery] int take = 200, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 1000);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var logs = await db.AuditLogs.AsNoTracking().OrderByDescending(x => x.OccurredAt).Take(take).ToListAsync(cancellationToken);
        return Ok(logs.Select(x => new
        {
            audit_log_id = x.AuditLogId,
            occurred_at = x.OccurredAt,
            x.Actor,
            x.Action,
            entity_type = x.EntityType,
            entity_id = x.EntityId,
            details = x.DetailsJson,
            remote_ip = x.RemoteIp,
            x.Succeeded
        }));
    }
}
