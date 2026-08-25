using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using LanManagement.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Security;

public sealed class AdminUserService(
    IDbContextFactory<ManagementDbContext> contextFactory,
    IPasswordService passwordService,
    IClock clock) : IAdminUserService
{
    public async Task<AdminUser?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.AdminUsers.SingleOrDefaultAsync(x => x.Username == username.Trim(), cancellationToken);
        if (user is null || !user.IsEnabled || !passwordService.Verify(password, user.PasswordHash))
        {
            return null;
        }

        user.LastLoginAt = clock.UtcNow;
        user.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<AdminUser> CreateAsync(string username, string password, AdminRole role, string actor, string? remoteIp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 128)
        {
            throw new ArgumentException("Username must contain 1-128 characters.", nameof(username));
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 14)
        {
            throw new ArgumentException("Password must contain at least 14 characters.", nameof(password));
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var normalized = username.Trim();
        if (await db.AdminUsers.AnyAsync(x => x.Username == normalized, cancellationToken))
        {
            throw new InvalidOperationException("An administrator with that username already exists.");
        }

        var now = clock.UtcNow;
        var user = new AdminUser
        {
            Username = normalized,
            PasswordHash = passwordService.Hash(password),
            Role = role,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AdminUsers.Add(user);
        db.AuditLogs.Add(AuditLogFactory.Create(now, actor, "ADMIN_USER_CREATED", "AdminUser", user.Username,
            new { username = user.Username, role = user.Role.ToString() }, remoteIp));
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AdminUsers.AsNoTracking().OrderBy(x => x.Username).ToListAsync(cancellationToken);
    }
}

public interface IAdminUserService
{
    Task<AdminUser?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken);
    Task<AdminUser> CreateAsync(string username, string password, AdminRole role, string actor, string? remoteIp, CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken);
}
