using LanManagement.Server.Domain;
using LanManagement.Server.Options;
using LanManagement.Server.Security;
using LanManagement.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LanManagement.Server.Data;

/// <summary>
/// Creates the initial SQLite schema and the first desired-state policy. The model deliberately
/// uses EF Core provider abstractions so the context can later be configured for PostgreSQL or SQL Server.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(
        IDbContextFactory<ManagementDbContext> factory,
        IOptions<ServerOptions> serverOptions,
        IOptions<BootstrapAdminOptions> bootstrapAdmin,
        IPasswordService passwords,
        IClock clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        if (!await db.Policies.AnyAsync(cancellationToken))
        {
            var desired = DesiredPolicy.Default(serverOptions.Value.DefaultDnsServer);
            db.Policies.Add(new Policy
            {
                PolicyVersion = 1,
                IsCurrent = true,
                DesiredStateJson = DesiredPolicy.Serialize(desired),
                CreatedAt = clock.UtcNow,
                CreatedBy = "system",
                ChangeReason = "Initial policy"
            });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created initial desired policy version 1.");
        }

        // No default password exists. A first administrator is only seeded if the deployer supplied
        // both values through protected configuration or environment variables.
        var bootstrap = bootstrapAdmin.Value;
        if (!string.IsNullOrWhiteSpace(bootstrap.Username) &&
            !string.IsNullOrWhiteSpace(bootstrap.Password) &&
            !await db.AdminUsers.AnyAsync(cancellationToken))
        {
            db.AdminUsers.Add(new AdminUser
            {
                Username = bootstrap.Username.Trim(),
                PasswordHash = passwords.Hash(bootstrap.Password),
                Role = AdminRole.Administrator,
                IsEnabled = true,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
            db.AuditLogs.Add(new AuditLog
            {
                OccurredAt = clock.UtcNow,
                Actor = "system",
                Action = "BOOTSTRAP_ADMIN_CREATED",
                EntityType = "AdminUser",
                EntityId = bootstrap.Username.Trim(),
                DetailsJson = "{\"source\":\"protected_configuration\"}",
                Succeeded = true
            });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Bootstrap administrator {Username} was created. Remove the bootstrap password from the environment after setup.", bootstrap.Username.Trim());
        }
        else if (!await db.AdminUsers.AnyAsync(cancellationToken))
        {
            logger.LogWarning("No administrator exists. Set BootstrapAdmin__Username and BootstrapAdmin__Password securely before first production start.");
        }
    }
}

public sealed class DatabaseBootstrapHostedService(
    IDbContextFactory<ManagementDbContext> factory,
    IOptions<ServerOptions> serverOptions,
    IOptions<BootstrapAdminOptions> bootstrapAdmin,
    IPasswordService passwords,
    IClock clock,
    ILogger<DatabaseBootstrapHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => DatabaseInitializer.InitializeAsync(
        factory, serverOptions, bootstrapAdmin, passwords, clock, logger, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
