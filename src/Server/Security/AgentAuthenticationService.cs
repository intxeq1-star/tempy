using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using LanManagement.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LanManagement.Server.Security;

public sealed record AgentAuthenticationResult(bool Succeeded, bool CanEnroll, string? FailureCode = null);

/// <summary>
/// Verifies a per-device token when one is provisioned. A deployment may explicitly enable a
/// one-time/shared enrollment token for new machines; it is intentionally disabled by default.
/// </summary>
public sealed class AgentAuthenticationService(
    IDbContextFactory<ManagementDbContext> contextFactory,
    IOptions<SecurityOptions> securityOptions,
    IAgentTokenHasher tokenHasher) : IAgentAuthenticationService
{
    public async Task<AgentAuthenticationResult> AuthenticateAsync(string deviceId, string authToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authToken))
        {
            return new(false, false, "AUTH_REQUIRED");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        var options = securityOptions.Value;

        if (existing is { IsDisabled: true })
        {
            return new(false, false, "DEVICE_DISABLED");
        }

        if (existing?.AgentTokenHash is { Length: > 0 } deviceHash)
        {
            return tokenHasher.Verify(authToken, deviceHash)
                ? new(true, false)
                : new(false, false, "AUTH_FAILED");
        }

        var enrollmentTokenIsValid = !string.IsNullOrWhiteSpace(options.AgentEnrollmentToken) &&
                                     tokenHasher.FixedTimeEquals(authToken, options.AgentEnrollmentToken);

        if (existing is null)
        {
            return options.AllowNewDeviceEnrollment && enrollmentTokenIsValid
                ? new(true, true)
                : new(false, false, "DEVICE_NOT_PROVISIONED");
        }

        return options.AllowEnrollmentTokenForExistingDevices && enrollmentTokenIsValid
            ? new(true, false)
            : new(false, false, "AUTH_FAILED");
    }
}

public interface IAgentAuthenticationService
{
    Task<AgentAuthenticationResult> AuthenticateAsync(string deviceId, string authToken, CancellationToken cancellationToken);
}
