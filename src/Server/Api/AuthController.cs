using System.Security.Claims;
using LanManagement.Server.Data;
using LanManagement.Server.Security;
using LanManagement.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Api;

[Route("api/auth")]
public sealed class AuthController(
    IAdminUserService users,
    IDbContextFactory<ManagementDbContext> contextFactory,
    IAntiforgery antiforgery,
    IClock clock) : ControllerBase
{
    [HttpGet("csrf")]
    [AllowAnonymous]
    public ActionResult<object> GetCsrfToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { token = tokens.RequestToken });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Invalid login request." });
        }
        var user = await users.AuthenticateAsync(request.Username, request.Password, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var actor = user?.Username ?? request.Username?.Trim() ?? "unknown";
        db.AuditLogs.Add(AuditLogFactory.Create(clock.UtcNow, actor, user is null ? "LOGIN_FAILED" : "LOGIN_SUCCEEDED", "Authentication", actor,
            new { succeeded = user is not null }, HttpContext.Connection.RemoteIpAddress?.ToString(), user is not null));
        await db.SaveChangesAsync(cancellationToken);

        if (user is null)
        {
            return Unauthorized(new { error = "Invalid username or password." });
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.AdminUserId.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        return Ok(new { username = user.Username, role = user.Role.ToString() });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<object> Me() => Ok(new
    {
        username = User.Identity?.Name,
        role = User.FindFirst(ClaimTypes.Role)?.Value
    });
}
