using System.Collections.Concurrent;
using System.Text.Json;
using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using LanManagement.Server.Options;
using LanManagement.Server.Protocol;
using LanManagement.Server.Security;
using LanManagement.Server.Services;
using LanManagement.Server.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Server.Tests;

internal sealed class TestHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly bool _deleteOnDispose;
    public string DatabasePath { get; }
    public IDeviceService Devices => _provider.GetRequiredService<IDeviceService>();
    public ICommandService Commands => _provider.GetRequiredService<ICommandService>();
    public IPolicyService Policies => _provider.GetRequiredService<IPolicyService>();
    public IAgentConnectionManager Connections => _provider.GetRequiredService<IAgentConnectionManager>();
    public IDbContextFactory<ManagementDbContext> ContextFactory => _provider.GetRequiredService<IDbContextFactory<ManagementDbContext>>();
    public CommandDispatcherService CommandDispatcher => _provider.GetRequiredService<CommandDispatcherService>();
    public SyncDispatcherService SyncDispatcher => _provider.GetRequiredService<SyncDispatcherService>();

    private TestHarness(ServiceProvider provider, string path, bool deleteOnDispose)
    {
        _provider = provider;
        DatabasePath = path;
        _deleteOnDispose = deleteOnDispose;
    }

    public static async Task<TestHarness> CreateAsync(string? databasePath = null, bool recover = false)
    {
        var path = databasePath ?? Path.Combine(Path.GetTempPath(), $"tempy-tests-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddOptions<ServerOptions>().Configure(options =>
        {
            options.HeartbeatTimeoutSeconds = 45;
            options.InitialRetrySeconds = 1;
            options.MaximumRetrySeconds = 5;
            options.MaximumDeliveryAttempts = 10;
            options.DefaultDnsServer = "192.168.1.100";
            options.CommandExecutionTimeoutMinutes = 120;
        });
        services.AddOptions<SecurityOptions>().Configure(options =>
        {
            options.AllowNewDeviceEnrollment = true;
            options.AgentEnrollmentToken = "test-enrollment-token-that-is-long-enough";
        });
        services.AddOptions<BootstrapAdminOptions>();
        services.AddDbContextFactory<ManagementDbContext>(options => options.UseSqlite($"Data Source={path};Foreign Keys=True;Default Timeout=30"));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordService, Pbkdf2PasswordService>();
        services.AddSingleton<IAgentTokenHasher, AgentTokenHasher>();
        services.AddSingleton<IDashboardEventBus, DashboardEventBus>();
        services.AddSingleton<ICommandWorkSignal, CommandWorkSignal>();
        services.AddSingleton<ISyncWorkSignal, SyncWorkSignal>();
        services.AddSingleton<IAgentConnectionManager, AgentConnectionManager>();
        services.AddSingleton<ICommandService, CommandService>();
        services.AddSingleton<IPolicyService, PolicyService>();
        services.AddSingleton<IDeviceService, DeviceService>();
        services.AddSingleton<CommandDispatcherService>();
        services.AddSingleton<SyncDispatcherService>();
        var provider = services.BuildServiceProvider();
        var harness = new TestHarness(provider, path, databasePath is null);
        await DatabaseInitializer.InitializeAsync(harness.ContextFactory,
            provider.GetRequiredService<IOptions<ServerOptions>>(),
            provider.GetRequiredService<IOptions<BootstrapAdminOptions>>(),
            provider.GetRequiredService<IPasswordService>(), provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<ILogger<TestHarness>>(), CancellationToken.None);
        if (recover)
        {
            await harness.Commands.RecoverAfterServerStartAsync(CancellationToken.None);
        }
        return harness;
    }

    public async Task<TestAgentConnection> RegisterAsync(string deviceId, string? hostname = null)
    {
        var registration = new RegisterDeviceMessage
        {
            Type = ProtocolConstants.RegisterDevice,
            MessageId = Guid.NewGuid(),
            SentAt = DateTimeOffset.UtcNow,
            DeviceId = deviceId,
            ProtocolVersion = ProtocolConstants.Version,
            Hostname = hostname ?? deviceId,
            AgentVersion = "1.0.0-test",
            OsVersion = "Windows test",
            LocalIp = "192.168.1.10",
            AuthToken = "test-enrollment-token-that-is-long-enough",
            ReportedPolicyVersion = 0
        };
        await Devices.RegisterAsync(registration, "192.168.1.10", CancellationToken.None);
        var connection = new TestAgentConnection(deviceId);
        await Connections.BindAsync(deviceId, connection, CancellationToken.None);
        await Policies.QueueDeviceAsync(deviceId, "TEST_REGISTER", CancellationToken.None);
        return connection;
    }

    public async Task SetOfflineAsync(string deviceId) => await Devices.MarkOfflineAsync(deviceId, "TEST_DISCONNECT", CancellationToken.None);

    public async Task<IReadOnlyList<CommandJob>> JobsAsync()
    {
        await using var db = await ContextFactory.CreateDbContextAsync();
        return await db.CommandJobs.AsNoTracking().OrderBy(x => x.DeviceId).ToListAsync();
    }

    public async Task<Device> DeviceAsync(string deviceId)
    {
        await using var db = await ContextFactory.CreateDbContextAsync();
        return await db.Devices.AsNoTracking().SingleAsync(x => x.DeviceId == deviceId);
    }

    public async Task<int> AssignmentStatusCountAsync(string deviceId, DevicePolicyStatus status)
    {
        await using var db = await ContextFactory.CreateDbContextAsync();
        return await db.DevicePolicyAssignments.CountAsync(x => x.DeviceId == deviceId && x.Status == status);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        if (_deleteOnDispose)
        {
            foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
            {
                try { File.Delete(DatabasePath + suffix); } catch { }
            }
        }
    }
}

internal sealed class TestAgentConnection : IAgentConnection
{
    private string? _deviceId;
    public ConcurrentQueue<object> Sent { get; } = new();
    public Guid SessionId { get; } = Guid.NewGuid();
    public string? DeviceId => _deviceId;
    public string RemoteIp => "192.168.1.10";
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public bool IsAuthenticated => _deviceId is not null;
    public bool IsOpen { get; private set; } = true;

    public TestAgentConnection(string deviceId) => _deviceId = deviceId;
    public void BindDevice(string deviceId) => _deviceId = deviceId;
    public Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        if (!IsOpen) throw new IOException("Test connection is closed.");
        Sent.Enqueue(message!);
        return Task.CompletedTask;
    }
    public Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string description, CancellationToken cancellationToken)
    {
        IsOpen = false;
        return Task.CompletedTask;
    }

    public IReadOnlyList<OutboundCommandMessage> Commands => Sent.OfType<OutboundCommandMessage>().ToList();
    public IReadOnlyList<ServerSyncRequestMessage> SyncRequests => Sent.OfType<ServerSyncRequestMessage>().ToList();
}
