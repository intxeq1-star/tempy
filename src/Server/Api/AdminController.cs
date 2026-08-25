using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using LanManagement.Server.Protocol;
using LanManagement.Server.Security;
using LanManagement.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Api;

[Route("api/admin")]
[Authorize(Roles = "Administrator")]
public sealed class AdminController(
    IAdminUserService users,
    IDbContextFactory<ManagementDbContext> contextFactory,
    IAgentTokenHasher tokens,
    IClock clock,
    IDashboardEventBus events) : ApiControllerBase
{
    [HttpGet("users")]
    public async Task<ActionResult<object>> Users(CancellationToken cancellationToken)
    {
        var usersList = await users.ListAsync(cancellationToken);
        return Ok(usersList.Select(x => new
        {
            user_id = x.AdminUserId,
            x.Username,
            role = x.Role.ToString(),
            x.IsEnabled,
            x.CreatedAt,
            x.LastLoginAt
        }));
    }

    [HttpPost("users")]
    public async Task<ActionResult<object>> CreateUser([FromBody] CreateAdminRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "A user request is required." });
        }
        if (!Enum.TryParse<AdminRole>(request.Role, true, out var role))
        {
            return BadRequest(new { error = "Role must be Viewer, Operator, or Administrator." });
        }
        try
        {
            var user = await users.CreateAsync(request.Username, request.Password, role, Actor, RemoteIp, cancellationToken);
            return Created($"/api/admin/users/{user.AdminUserId}", new { user_id = user.AdminUserId, user.Username, role = user.Role.ToString() });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    [HttpPost("devices/provision")]
    public async Task<ActionResult<object>> ProvisionDevice([FromBody] ProvisionDeviceRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !ProtocolParser.IsValidDeviceId(request.DeviceId) || string.IsNullOrWhiteSpace(request.AgentToken) || request.AgentToken.Length < 24)
        {
            return BadRequest(new { error = "device_id is invalid or the per-device agent token is too short." });
        }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Devices.AnyAsync(x => x.DeviceId == request.DeviceId, cancellationToken))
        {
            return Conflict(new { error = "A device with that ID already exists. Use a new provisioning token only through an explicit rotation workflow." });
        }
        var now = clock.UtcNow;
        var device = new Device
        {
            DeviceId = request.DeviceId,
            Hostname = string.IsNullOrWhiteSpace(request.Hostname) ? request.DeviceId : request.Hostname.Trim()[..Math.Min(request.Hostname.Trim().Length, 255)],
            AgentTokenHash = tokens.Hash(request.AgentToken),
            ConnectionState = DeviceConnectionState.Offline,
            SyncState = DeviceSyncState.Unknown,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Devices.Add(device);
        db.AuditLogs.Add(AuditLogFactory.Create(now, Actor, "DEVICE_PROVISIONED", "Device", device.DeviceId, new { device_id = device.DeviceId }, RemoteIp));
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(new DashboardEvent("device-provisioned", now, device.DeviceId), cancellationToken);
        // The supplied token is intentionally never echoed or persisted in plaintext.
        return Created($"/api/devices/{device.DeviceId}", new { device_id = device.DeviceId, device.Hostname, status = "OFFLINE" });
    }
}
