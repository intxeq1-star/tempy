using LanManagement.Server.Domain;
using LanManagement.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LanManagement.Server.Api;

[Route("api/policy")]
public sealed class PolicyController(IPolicyService policies) : ApiControllerBase
{
    [HttpGet]
    [Authorize(Roles = "Viewer,Operator,Administrator")]
    public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
    {
        var policy = await policies.GetCurrentAsync(cancellationToken);
        return Ok(new
        {
            policy_version = policy.PolicyVersion,
            desired_policy = DesiredPolicy.Deserialize(policy.DesiredStateJson),
            created_at = policy.CreatedAt,
            created_by = policy.CreatedBy,
            reason = policy.ChangeReason
        });
    }

    [HttpPut]
    [Authorize(Roles = "Operator,Administrator")]
    public async Task<ActionResult<object>> Update([FromBody] PolicyUpdateRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "A policy request is required." });
        }
        try
        {
            var desired = new DesiredPolicy(new DnsDesiredPolicy(request.DnsEnabled, request.DnsServer),
                new ChromeDesiredPolicy(request.ChromeIncognito), new EdgeDesiredPolicy(request.EdgeInPrivate));
            var policy = await policies.UpdateAsync(desired, Actor, RemoteIp, request.Reason ?? "Administrator changed desired policy", cancellationToken);
            return Ok(new { policy_version = policy.PolicyVersion, desired_policy = desired });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPost("sync-all")]
    [Authorize(Roles = "Operator,Administrator")]
    public async Task<ActionResult<object>> SyncAll(CancellationToken cancellationToken)
    {
        var policy = await policies.SyncAllAsync(Actor, RemoteIp, cancellationToken);
        return Accepted(new { policy_version = policy.PolicyVersion, message = "A durable policy assignment was created for every managed device." });
    }

    [HttpPost("devices/{deviceId}/sync")]
    [Authorize(Roles = "Operator,Administrator")]
    public async Task<IActionResult> SyncOne(string deviceId, CancellationToken cancellationToken)
    {
        await policies.QueueDeviceAsync(deviceId, "ADMINISTRATOR_REQUESTED_SYNC", cancellationToken);
        return Accepted();
    }
}
