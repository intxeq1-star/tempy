using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using LanManagement.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Api;

[Route("api/applications")]
public sealed class ApplicationsController(
    IDbContextFactory<ManagementDbContext> contextFactory,
    ICommandService commands,
    IClock clock,
    IDashboardEventBus events) : ApiControllerBase
{
    [HttpGet]
    [Authorize(Roles = "Viewer,Operator,Administrator")]
    public async Task<ActionResult<IReadOnlyList<ApplicationCatalogItem>>> List(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return Ok(await db.Applications.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(cancellationToken));
    }

    [HttpPost]
    [Authorize(Roles = "Operator,Administrator")]
    public async Task<ActionResult<object>> Create([FromBody] ApplicationRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request, out var error))
        {
            return BadRequest(new { error });
        }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var packageId = request.PackageId.Trim();
        if (await db.Applications.AnyAsync(x => x.PackageId == packageId, cancellationToken))
        {
            return Conflict(new { error = "An application with that package_id already exists." });
        }
        var now = clock.UtcNow;
        var app = new ApplicationCatalogItem
        {
            PackageId = packageId,
            DisplayName = request.DisplayName.Trim(),
            Source = string.IsNullOrWhiteSpace(request.Source) ? "winget" : request.Source.Trim(),
            DesiredVersion = TrimOrNull(request.DesiredVersion, 128),
            InstallArgumentsJson = ToJsonOrNull(request.InstallArguments),
            IsEnabled = request.IsEnabled,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Applications.Add(app);
        db.AuditLogs.Add(AuditLogFactory.Create(now, Actor, "APPLICATION_CATALOG_CREATED", "Application", app.ApplicationId.ToString(), new { app.PackageId, app.DisplayName, app.Source }, RemoteIp));
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(new DashboardEvent("application-created", now), cancellationToken);
        return Created($"/api/applications/{app.ApplicationId}", app);
    }

    [HttpPut("{applicationId:guid}")]
    [Authorize(Roles = "Operator,Administrator")]
    public async Task<IActionResult> Update(Guid applicationId, [FromBody] ApplicationRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request, out var error))
        {
            return BadRequest(new { error });
        }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var app = await db.Applications.SingleOrDefaultAsync(x => x.ApplicationId == applicationId, cancellationToken);
        if (app is null)
        {
            return NotFound();
        }
        var packageId = request.PackageId.Trim();
        if (await db.Applications.AnyAsync(x => x.ApplicationId != applicationId && x.PackageId == packageId, cancellationToken))
        {
            return Conflict(new { error = "An application with that package_id already exists." });
        }
        app.PackageId = packageId;
        app.DisplayName = request.DisplayName.Trim();
        app.Source = string.IsNullOrWhiteSpace(request.Source) ? "winget" : request.Source.Trim();
        app.DesiredVersion = TrimOrNull(request.DesiredVersion, 128);
        app.InstallArgumentsJson = ToJsonOrNull(request.InstallArguments);
        app.IsEnabled = request.IsEnabled;
        app.UpdatedAt = clock.UtcNow;
        db.AuditLogs.Add(AuditLogFactory.Create(clock.UtcNow, Actor, "APPLICATION_CATALOG_UPDATED", "Application", app.ApplicationId.ToString(), new { app.PackageId, app.DisplayName, app.Source }, RemoteIp));
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(new DashboardEvent("application-updated", clock.UtcNow), cancellationToken);
        return NoContent();
    }

    [HttpPost("{applicationId:guid}/deploy/{action}")]
    [Authorize(Roles = "Operator,Administrator")]
    public async Task<ActionResult<CreateCommandResult>> Deploy(Guid applicationId, string action, [FromBody] CommandTargetApiRequest target, CancellationToken cancellationToken)
    {
        if (!TryMapAction(action, out var commandType) || !TryTarget(target, out var commandTarget, out var error))
        {
            return BadRequest(new { error = error ?? "Action must be INSTALL, UNINSTALL, UPDATE, or CHECK." });
        }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var app = await db.Applications.AsNoTracking().SingleOrDefaultAsync(x => x.ApplicationId == applicationId && x.IsEnabled, cancellationToken);
        if (app is null)
        {
            return NotFound(new { error = "Enabled application was not found." });
        }
        var payload = LanManagement.Server.Protocol.ProtocolJson.ToElement(System.Text.Json.JsonSerializer.Serialize(new
        {
            package_id = app.PackageId,
            source = app.Source,
            desired_version = app.DesiredVersion,
            install_arguments = string.IsNullOrWhiteSpace(app.InstallArgumentsJson) ? null : LanManagement.Server.Protocol.ProtocolJson.ToElement(app.InstallArgumentsJson)
        }));
        try
        {
            var result = await commands.CreateAsync(new CreateCommandRequest(commandType!, payload, commandTarget!, $"{action.ToUpperInvariant()} {app.DisplayName}"), Actor, RemoteIp, cancellationToken);
            return Accepted(result);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    private static bool IsValid(ApplicationRequest? request, out string? error)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PackageId) || request.PackageId.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 256 ||
            (!string.IsNullOrWhiteSpace(request.Source) && request.Source.Trim().Length > 64))
        {
            error = "Package ID, display name, and source values are invalid.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryMapAction(string action, out string? commandType)
    {
        commandType = action.ToUpperInvariant() switch
        {
            "INSTALL" => CommandTypes.InstallApp,
            "UNINSTALL" => CommandTypes.UninstallApp,
            "UPDATE" => CommandTypes.UpdateApp,
            "CHECK" => CommandTypes.CheckApp,
            _ => null
        };
        return commandType is not null;
    }

    private static bool TryTarget(CommandTargetApiRequest? request, out CommandTarget? target, out string? error)
    {
        target = null;
        error = null;
        if (request is null || !Enum.TryParse<TargetKind>(request.Kind, true, out var kind))
        {
            error = "Target kind is invalid.";
            return false;
        }
        if (kind == TargetKind.Group && !request.GroupId.HasValue)
        {
            error = "Group target requires group_id.";
            return false;
        }
        if (kind == TargetKind.Device && request.DeviceIds?.Count != 1)
        {
            error = "One-PC deployment requires exactly one device ID.";
            return false;
        }
        if ((kind == TargetKind.Device || kind == TargetKind.Devices) && request.DeviceIds is not { Count: > 0 })
        {
            error = "Selected devices are required.";
            return false;
        }
        target = new CommandTarget(kind, request.DeviceIds, request.GroupId);
        return true;
    }

    private static string? TrimOrNull(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static string? ToJsonOrNull(System.Text.Json.JsonElement? value) => !value.HasValue || value.Value.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined ? null : value.Value.GetRawText();
}
