using System.Text.Json;
using LanManagement.Server.Domain;
using LanManagement.Server.Protocol;
using LanManagement.Server.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Server.Tests;

public sealed class ReliabilityTests
{
    [Fact(DisplayName = "Test 1: one registered agent is ONLINE")]
    public async Task One_agent_connects_and_is_online()
    {
        await using var server = await TestHarness.CreateAsync();
        await server.RegisterAsync("PC-001");

        var dashboard = await server.Devices.GetDashboardAsync(CancellationToken.None);
        Assert.Equal(1, dashboard.Total);
        Assert.Equal(1, dashboard.Online);
        Assert.True(dashboard.Devices.Single().IsOnline);
    }

    [Fact(DisplayName = "Test 2: disconnected agent is OFFLINE")]
    public async Task Agent_disconnects_and_is_offline()
    {
        await using var server = await TestHarness.CreateAsync();
        await server.RegisterAsync("PC-001");
        await server.SetOfflineAsync("PC-001");

        var device = await server.DeviceAsync("PC-001");
        Assert.Equal(DeviceConnectionState.Offline, device.ConnectionState);
    }

    [Fact(DisplayName = "Test 3: reconnect changes OFFLINE to ONLINE")]
    public async Task Agent_reconnects_and_is_online()
    {
        await using var server = await TestHarness.CreateAsync();
        await server.RegisterAsync("PC-001");
        await server.SetOfflineAsync("PC-001");
        await server.RegisterAsync("PC-001");

        var device = await server.DeviceAsync("PC-001");
        Assert.Equal(DeviceConnectionState.Online, device.ConnectionState);
    }

    [Fact(DisplayName = "Test 4: one-PC command creates only one selected job")]
    public async Task One_pc_command_targets_only_selected_device()
    {
        await using var server = await TestHarness.CreateAsync();
        await server.RegisterAsync("PC-001");
        await server.RegisterAsync("PC-002");

        var operation = await server.Commands.CreateAsync(Command("GET_SYSTEM_INFO", TargetKind.Device, new[] { "PC-001" }), "admin", null, CancellationToken.None);
        var jobs = await server.JobsAsync();

        Assert.Single(jobs);
        Assert.Equal(operation.CommandIds.Single(), jobs.Single().CommandId);
        Assert.Equal("PC-001", jobs.Single().DeviceId);
    }

    [Fact(DisplayName = "Test 5: ALL-PC command creates one job for every device")]
    public async Task All_pc_command_creates_individual_jobs()
    {
        await using var server = await TestHarness.CreateAsync();
        await server.RegisterAsync("PC-001");
        await server.RegisterAsync("PC-002");
        await server.RegisterAsync("PC-003");

        var operation = await server.Commands.CreateAsync(Command("GET_SYSTEM_INFO", TargetKind.All), "admin", null, CancellationToken.None);
        var jobs = await server.JobsAsync();

        Assert.Equal(3, operation.TargetCount);
        Assert.Equal(3, jobs.Count);
        Assert.Equal(3, jobs.Select(x => x.CommandId).Distinct().Count());
        Assert.Equal(new[] { "PC-001", "PC-002", "PC-003" }, jobs.Select(x => x.DeviceId).ToArray());
    }

    [Fact(DisplayName = "Test 6: online jobs send while offline jobs remain PENDING")]
    public async Task Offline_devices_retain_pending_jobs()
    {
        await using var server = await TestHarness.CreateAsync();
        var first = await server.RegisterAsync("PC-001");
        var second = await server.RegisterAsync("PC-002");
        await server.RegisterAsync("PC-003");
        await server.SetOfflineAsync("PC-003");

        await server.Commands.CreateAsync(Command("GET_SYSTEM_INFO", TargetKind.All), "admin", null, CancellationToken.None);
        await server.CommandDispatcher.DispatchOnceAsync(CancellationToken.None);
        var jobs = await server.JobsAsync();

        Assert.Equal(CommandStatus.Sent, jobs.Single(x => x.DeviceId == "PC-001").Status);
        Assert.Equal(CommandStatus.Sent, jobs.Single(x => x.DeviceId == "PC-002").Status);
        Assert.Equal(CommandStatus.Pending, jobs.Single(x => x.DeviceId == "PC-003").Status);
        Assert.Single(first.Commands);
        Assert.Single(second.Commands);
    }

    [Fact(DisplayName = "Test 7: offline device reconnect automatically receives pending command")]
    public async Task Pending_command_delivers_when_offline_pc_boots()
    {
        await using var server = await TestHarness.CreateAsync();
        await server.RegisterAsync("PC-001");
        await server.SetOfflineAsync("PC-001");
        await server.Commands.CreateAsync(Command("GET_SYSTEM_INFO", TargetKind.All), "admin", null, CancellationToken.None);

        var reconnect = await server.RegisterAsync("PC-001");
        await server.CommandDispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Single(reconnect.Commands);
        Assert.Equal("PC-001", reconnect.Commands.Single().DeviceId);
        Assert.Equal(CommandStatus.Sent, (await server.JobsAsync()).Single().Status);
    }

    [Fact(DisplayName = "Test 8: server restart preserves pending command")]
    public async Task Server_restart_preserves_and_recovers_pending_commands()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tempy-restart-{Guid.NewGuid():N}.db");
        try
        {
            await using (var first = await TestHarness.CreateAsync(path))
            {
                await first.RegisterAsync("PC-001");
                await first.Commands.CreateAsync(Command("GET_SYSTEM_INFO", TargetKind.All), "admin", null, CancellationToken.None);
            }
            await using var restarted = await TestHarness.CreateAsync(path, recover: true);
            var jobs = await restarted.JobsAsync();
            Assert.Single(jobs);
            Assert.Equal(CommandStatus.Pending, jobs.Single().Status);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-shm", "-wal" }) try { File.Delete(path + suffix); } catch { }
        }
    }

    [Fact(DisplayName = "Test 9: network interruption redelivers same job after reconnect")]
    public async Task Network_interruption_reconnects_without_creating_a_second_job()
    {
        await using var server = await TestHarness.CreateAsync();
        var firstConnection = await server.RegisterAsync("PC-001");
        await server.Commands.CreateAsync(Command("GET_SYSTEM_INFO", TargetKind.All), "admin", null, CancellationToken.None);
        await server.CommandDispatcher.DispatchOnceAsync(CancellationToken.None);
        var originalId = firstConnection.Commands.Single().CommandId;

        await server.SetOfflineAsync("PC-001");
        var reconnected = await server.RegisterAsync("PC-001");
        await server.CommandDispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Single(await server.JobsAsync());
        Assert.Equal(originalId, reconnected.Commands.Single().CommandId);
    }

    [Fact(DisplayName = "Test 10: duplicate delivery has one durable command and idempotent result")]
    public async Task Duplicate_delivery_is_idempotent_by_command_id()
    {
        await using var server = await TestHarness.CreateAsync();
        var connection = await server.RegisterAsync("PC-001");
        await server.Commands.CreateAsync(Command("GET_SYSTEM_INFO", TargetKind.All), "admin", null, CancellationToken.None);
        await server.CommandDispatcher.DispatchOnceAsync(CancellationToken.None);
        var commandId = connection.Commands.Single().CommandId;

        await server.SetOfflineAsync("PC-001");
        var secondConnection = await server.RegisterAsync("PC-001");
        await server.CommandDispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal(commandId, secondConnection.Commands.Single().CommandId);
        Assert.Single(await server.JobsAsync());

        var simulatedAgentLedger = new IdempotentAgentLedger();
        simulatedAgentLedger.Accept(firstConnection.Commands.Single());
        simulatedAgentLedger.Accept(secondConnection.Commands.Single());
        Assert.Equal(1, simulatedAgentLedger.ExecutionCount);

        var result = Result("PC-001", commandId, Guid.NewGuid());
        Assert.True((await server.Commands.RecordResultAsync(result, CancellationToken.None)).Accepted);
        Assert.True((await server.Commands.RecordResultAsync(Result("PC-001", commandId, Guid.NewGuid()), CancellationToken.None)).Accepted);
        await using var db = await server.ContextFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.CommandResults.CountAsync(x => x.CommandId == commandId));
        Assert.Equal(CommandStatus.Success, await db.CommandJobs.Where(x => x.CommandId == commandId).Select(x => x.Status).SingleAsync());
    }

    [Fact(DisplayName = "Test 11: policy change increments desired policy version")]
    public async Task Policy_change_increments_version()
    {
        await using var server = await TestHarness.CreateAsync();
        var before = await server.Policies.GetCurrentAsync(CancellationToken.None);
        var after = await server.Policies.UpdateAsync(new DesiredPolicy(
            new DnsDesiredPolicy(true, "192.168.1.100"), new ChromeDesiredPolicy(false), new EdgeDesiredPolicy(false)),
            "admin", null, "Test policy update", CancellationToken.None);

        Assert.Equal(before.PolicyVersion + 1, after.PolicyVersion);
    }

    [Fact(DisplayName = "Test 12: SYNC ALL synchronizes online then offline after reconnect")]
    public async Task Sync_all_leaves_offline_assignment_and_syncs_after_reconnect()
    {
        await using var server = await TestHarness.CreateAsync();
        var online = await server.RegisterAsync("PC-001");
        await server.RegisterAsync("PC-002");
        await server.SetOfflineAsync("PC-002");

        var policy = await server.Policies.SyncAllAsync("admin", null, CancellationToken.None);
        await server.SyncDispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Single(online.SyncRequests);
        Assert.Equal(policy.PolicyVersion, online.SyncRequests.Single().PolicyVersion);
        Assert.Equal(1, await server.AssignmentStatusCountAsync("PC-002", DevicePolicyStatus.Pending));

        var reconnect = await server.RegisterAsync("PC-002");
        await server.SyncDispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Single(reconnect.SyncRequests);
        Assert.Equal(policy.PolicyVersion, reconnect.SyncRequests.Single().PolicyVersion);
    }

    private static CreateCommandRequest Command(string type, TargetKind targetKind, IReadOnlyCollection<string>? devices = null) =>
        new(type, ProtocolJson.ToElement("{}"), new CommandTarget(targetKind, devices));

    private static CommandResultMessage Result(string deviceId, Guid commandId, Guid messageId) => new()
    {
        Type = ProtocolConstants.CommandResult,
        MessageId = messageId,
        SentAt = DateTimeOffset.UtcNow,
        DeviceId = deviceId,
        CommandId = commandId,
        Status = "SUCCESS",
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        ExitCode = 0,
        Output = "completed"
    };
}

/// <summary>Protocol-level stand-in for the persistent command-id ledger every compatible Agent must maintain.</summary>
internal sealed class IdempotentAgentLedger
{
    private readonly HashSet<Guid> _completedOrRunning = new();
    public int ExecutionCount { get; private set; }

    public void Accept(OutboundCommandMessage command)
    {
        if (_completedOrRunning.Add(command.CommandId))
        {
            ExecutionCount++;
        }
    }
}
