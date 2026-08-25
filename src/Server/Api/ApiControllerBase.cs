using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LanManagement.Server.Api;

[ApiController]
[Authorize]
public abstract class ApiControllerBase : ControllerBase
{
    protected string Actor => User.Identity?.Name ?? "unknown";
    protected string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();
}
