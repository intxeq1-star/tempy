using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using LanManagement.Server.Protocol;
using LanManagement.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Api;

[Route("api/groups")]
public sealed class GroupsController(
    IDbContextFactory<ManagementDbContext> contextFactory,
    IClock clock,
    IDashboardEventBus events) : ApiControllerBase
{
    [HttpGet]
    [Authorize(Roles = "Viewer,Operator,Administrator")]
    public async Task<ActionResult<object>> List(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var groups = await db.DeviceGroups.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var counts = await db.DeviceGroupMemberships.GroupBy(x => x.DeviceGroupId).Select(x => new { GroupId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.GroupId, x => x.Count, cancellationToken);
        return Ok(groups.Select(x => new
        {
            group_id = x.DeviceGroupId,
            x.Name,
            x.Description,
            member_count = counts.GetValueOrDefault(x.DeviceGroupId),
            x.CreatedAt
        }));
    }

    [HttpPost]
    [Authorize(Roles = "Operator,Administrator")]
    public async Task<ActionResult<object>> Create([FromBody] GroupRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
        {
            return BadRequest(new { error = "Group name must contain 1-128 characters." });
        }
        var deviceIds = request.DeviceIds?.Where(ProtocolParser.IsValidDeviceId).Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.DeviceGroups.AnyAsync(x => x.Name == request.Name.Trim(), cancellationToken))
        {
            return Conflict(new { error = "A group with that name already exists." });
        }
        var known = await db.Devices.Where(x => deviceIds.Contains(x.DeviceId)).Select(x => x.DeviceId).ToListAsync(cancellationToken);
        if (known.Count != deviceIds.Length)
        {
            return BadRequest(new { error = "One or more selected device IDs are unknown." });
        }
        var now = clock.UtcNow;
        var group = new DeviceGroup
        {
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()[..Math.Min(request.Description.Trim().Length, 512)],
            CreatedAt = now,
            UpdatedAt = now
        };
        db.DeviceGroups.Add(group);
        foreach (var id in known)
        {
            db.DeviceGroupMemberships.Add(new DeviceGroupMembership { DeviceGroupId = group.DeviceGroupId, DeviceId = id, AddedAt = now });
        }
        db.AuditLogs.Add(AuditLogFactory.Create(now, Actor, "GROUP_CREATED", "DeviceGroup", group.DeviceGroupId.ToString(), new
        {
            group.Name,
            members = known
        }, RemoteIp));
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(new DashboardEvent("group-created", now), cancellationToken);
        return Created($"/api/groups/{group.DeviceGroupId}", new { group_id = group.DeviceGroupId, group.Name, group.Description, member_count = known.Count });
    }

    [HttpPut("{groupId:guid}")]
    [Authorize(Roles = "Operator,Administrator")]
    public async Task<IActionResult> Replace(Guid groupId, [FromBody] GroupRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
        {
            return BadRequest(new { error = "Group name must contain 1-128 characters." });
        }
        var ids = request.DeviceIds?.Where(ProtocolParser.IsValidDeviceId).Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.DeviceGroups.SingleOrDefaultAsync(x => x.DeviceGroupId == groupId, cancellationToken);
        if (group is null)
        {
            return NotFound();
        }
        if (await db.DeviceGroups.AnyAsync(x => x.DeviceGroupId != groupId && x.Name == request.Name.Trim(), cancellationToken))
        {
            return Conflict(new { error = "A group with that name already exists." });
        }
        var known = await db.Devices.Where(x => ids.Contains(x.DeviceId)).Select(x => x.DeviceId).ToListAsync(cancellationToken);
        if (known.Count != ids.Length)
        {
            return BadRequest(new { error = "One or more selected device IDs are unknown." });
        }
        var existing = await db.DeviceGroupMemberships.Where(x => x.DeviceGroupId == groupId).ToListAsync(cancellationToken);
        db.DeviceGroupMemberships.RemoveRange(existing);
        var now = clock.UtcNow;
        group.Name = request.Name.Trim();
        group.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()[..Math.Min(request.Description.Trim().Length, 512)];
        group.UpdatedAt = now;
        foreach (var id in known)
        {
            db.DeviceGroupMemberships.Add(new DeviceGroupMembership { DeviceGroupId = groupId, DeviceId = id, AddedAt = now });
        }
        db.AuditLogs.Add(AuditLogFactory.Create(now, Actor, "GROUP_UPDATED", "DeviceGroup", groupId.ToString(), new { group.Name, members = known }, RemoteIp));
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(new DashboardEvent("group-updated", now), cancellationToken);
        return NoContent();
    }

    [HttpDelete("{groupId:guid}")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Delete(Guid groupId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.DeviceGroups.SingleOrDefaultAsync(x => x.DeviceGroupId == groupId, cancellationToken);
        if (group is null)
        {
            return NotFound();
        }
        db.DeviceGroups.Remove(group);
        db.AuditLogs.Add(AuditLogFactory.Create(clock.UtcNow, Actor, "GROUP_DELETED", "DeviceGroup", groupId.ToString(), new { group.Name }, RemoteIp));
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(new DashboardEvent("group-deleted", clock.UtcNow), cancellationToken);
        return NoContent();
    }
}
