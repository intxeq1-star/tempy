using LanManagement.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LanManagement.Server.Data;

public sealed class ManagementDbContext(DbContextOptions<ManagementDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();
    public DbSet<DeviceGroupMembership> DeviceGroupMemberships => Set<DeviceGroupMembership>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<DevicePolicyAssignment> DevicePolicyAssignments => Set<DevicePolicyAssignment>();
    public DbSet<CommandOperation> CommandOperations => Set<CommandOperation>();
    public DbSet<CommandJob> CommandJobs => Set<CommandJob>();
    public DbSet<CommandResult> CommandResults => Set<CommandResult>();
    public DbSet<ApplicationCatalogItem> Applications => Set<ApplicationCatalogItem>();
    public DbSet<DeviceApplication> DeviceApplications => Set<DeviceApplication>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AgentVersionRecord> AgentVersions => Set<AgentVersionRecord>();
    public DbSet<ServerSetting> ServerSettings => Set<ServerSetting>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<ProcessedAgentMessage> ProcessedAgentMessages => Set<ProcessedAgentMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // SQLite cannot reliably translate DateTimeOffset ordering/comparison. Persist every
        // timestamp as UTC Unix milliseconds so heartbeat, retry, timeout, and audit predicates
        // remain server-side and correct under the single-server SQLite provider.
        var timestampConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        var nullableTimestampConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entityType => entityType.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset))
            {
                property.SetValueConverter(timestampConverter);
            }
            else if (property.ClrType == typeof(DateTimeOffset?))
            {
                property.SetValueConverter(nullableTimestampConverter);
            }
        }

        modelBuilder.Entity<Device>(entity =>
        {
            entity.Property(x => x.ConnectionState).HasConversion<string>();
            entity.Property(x => x.SyncState).HasConversion<string>();
            entity.HasIndex(x => x.Hostname);
            entity.HasIndex(x => x.ConnectionState);
            entity.HasIndex(x => x.LastSeenAt);
        });

        modelBuilder.Entity<DeviceGroup>(entity =>
        {
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<DeviceGroupMembership>(entity =>
        {
            entity.HasKey(x => new { x.DeviceId, x.DeviceGroupId });
            entity.HasOne(x => x.Device)
                .WithMany(x => x.GroupMemberships)
                .HasForeignKey(x => x.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.DeviceGroup)
                .WithMany(x => x.Members)
                .HasForeignKey(x => x.DeviceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Policy>(entity =>
        {
            entity.HasKey(x => x.PolicyVersion);
            entity.HasIndex(x => x.IsCurrent);
        });

        modelBuilder.Entity<DevicePolicyAssignment>(entity =>
        {
            entity.HasKey(x => new { x.DeviceId, x.PolicyVersion });
            entity.Property(x => x.Status).HasConversion<string>();
            entity.HasOne(x => x.Device)
                .WithMany(x => x.PolicyAssignments)
                .HasForeignKey(x => x.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Policy)
                .WithMany(x => x.DeviceAssignments)
                .HasForeignKey(x => x.PolicyVersion)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.Status, x.LastRequestedAt });
        });

        modelBuilder.Entity<CommandOperation>(entity =>
        {
            entity.Property(x => x.TargetKind).HasConversion<string>();
            entity.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<CommandJob>(entity =>
        {
            entity.Property(x => x.Status).HasConversion<string>();
            entity.HasOne(x => x.Operation)
                .WithMany(x => x.Jobs)
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.DeviceId, x.Status, x.NextAttemptAt });
            entity.HasIndex(x => new { x.OperationId, x.DeviceId }).IsUnique();
            entity.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<CommandResult>(entity =>
        {
            entity.Property(x => x.Status).HasConversion<string>();
            entity.HasOne(x => x.Command)
                .WithMany(x => x.Results)
                .HasForeignKey(x => x.CommandId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.MessageId).IsUnique();
            entity.HasIndex(x => x.CommandId);
        });

        modelBuilder.Entity<ApplicationCatalogItem>(entity =>
        {
            entity.HasIndex(x => x.PackageId).IsUnique();
        });

        modelBuilder.Entity<DeviceApplication>(entity =>
        {
            entity.HasIndex(x => new { x.DeviceId, x.PackageId }).IsUnique();
            entity.HasIndex(x => x.DeviceId);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasIndex(x => x.OccurredAt);
            entity.HasIndex(x => x.Actor);
        });

        modelBuilder.Entity<AgentVersionRecord>(entity =>
        {
            entity.HasIndex(x => x.Version);
        });

        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.Property(x => x.Role).HasConversion<string>();
            entity.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<ProcessedAgentMessage>(entity =>
        {
            entity.HasKey(x => new { x.DeviceId, x.MessageId });
            entity.HasIndex(x => x.ProcessedAt);
        });
    }
}
