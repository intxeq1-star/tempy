using LanManagement.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LanManagement.Server.Api;

[Route("api")]
[Authorize(Roles = "Viewer,Operator,Administrator")]
public sealed class DashboardController(IDeviceService devices) : ApiControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardSnapshot>> GetDashboard(CancellationToken cancellationToken) =>
        Ok(await devices.GetDashboardAsync(cancellationToken));

    [HttpGet("devices")]
    public async Task<ActionResult<IReadOnlyList<DashboardDevice>>> GetDevices(CancellationToken cancellationToken) =>
        Ok((await devices.GetDashboardAsync(cancellationToken)).Devices);

    [HttpGet("devices/{deviceId}")]
    public async Task<ActionResult<DeviceDetail>> GetDevice(string deviceId, CancellationToken cancellationToken)
    {
        var detail = await devices.GetDetailAsync(deviceId, cancellationToken);
        return detail is null ? NotFound() : Ok(detail);
    }
}
