using LanManagement.Server.Domain;
using LanManagement.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LanManagement.Server.Api;

[Route("api/commands")]
public sealed class CommandsController(ICommandService commands) : ApiControllerBase
{
    [HttpPost]
    [Authorize(Roles = "Operator,Administrator")]
    public async Task<ActionResult<CreateCommandResult>> Create([FromBody] CreateCommandApiRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request is null || request.Target is null || !TryMapTarget(request.Target, out var target, out var error))
            {
                return BadRequest(new { error = error ?? "Invalid command target." });
            }
            var result = await commands.CreateAsync(new CreateCommandRequest(request.CommandType, request.Payload, target!, request.DisplayName), Actor, RemoteIp, cancellationToken);
            return CreatedAtAction(nameof(GetOperation), new { operationId = result.OperationId }, result);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpGet("operations")]
    [Authorize(Roles = "Viewer,Operator,Administrator")]
    public async Task<ActionResult<IReadOnlyList<CommandOperationProgress>>> GetOperations([FromQuery] int take = 20, CancellationToken cancellationToken = default) =>
        Ok(await commands.GetRecentOperationsAsync(take, cancellationToken));

    [HttpGet("operations/{operationId:guid}")]
    [Authorize(Roles = "Viewer,Operator,Administrator")]
    public async Task<ActionResult<CommandOperationProgress>> GetOperation(Guid operationId, CancellationToken cancellationToken)
    {
        var operation = await commands.GetOperationAsync(operationId, cancellationToken);
        return operation is null ? NotFound() : Ok(operation);
    }

    [HttpPost("{commandId:guid}/cancel")]
    [Authorize(Roles = "Operator,Administrator")]
    public async Task<IActionResult> Cancel(Guid commandId, CancellationToken cancellationToken)
    {
        return await commands.CancelAsync(commandId, Actor, RemoteIp, cancellationToken) ? NoContent() : Conflict(new { error = "Only PENDING or SENT commands can be cancelled." });
    }

    private static bool TryMapTarget(CommandTargetApiRequest request, out CommandTarget? target, out string? error)
    {
        target = null;
        error = null;
        if (!Enum.TryParse<TargetKind>(request.Kind, true, out var kind))
        {
            error = "Target kind must be DEVICE, DEVICES, GROUP, or ALL.";
            return false;
        }
        if (kind == TargetKind.Device && request.DeviceIds?.Count != 1)
        {
            error = "A one-PC command requires exactly one device ID.";
            return false;
        }
        if (kind == TargetKind.Group && !request.GroupId.HasValue)
        {
            error = "A group target requires group_id.";
            return false;
        }
        if ((kind is TargetKind.Device or TargetKind.Devices) && (request.DeviceIds is null || request.DeviceIds.Count == 0))
        {
            error = "Device target requires device_ids.";
            return false;
        }
        target = new CommandTarget(kind, request.DeviceIds, request.GroupId);
        return true;
    }
}
