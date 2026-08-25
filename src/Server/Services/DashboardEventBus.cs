using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace LanManagement.Server.Services;

public sealed record DashboardEvent(string Kind, DateTimeOffset OccurredAt, string? DeviceId = null, Guid? OperationId = null);

public interface IDashboardEventBus
{
    DashboardSubscription Subscribe();
    Task PublishAsync(DashboardEvent dashboardEvent, CancellationToken cancellationToken = default);
}

public sealed class DashboardSubscription : IAsyncDisposable
{
    private readonly Guid _id;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers;
    internal ChannelReader<string> Reader { get; }

    internal DashboardSubscription(Guid id, ChannelReader<string> reader, ConcurrentDictionary<Guid, Channel<string>> subscribers)
    {
        _id = id;
        Reader = reader;
        _subscribers = subscribers;
    }

    public ValueTask DisposeAsync()
    {
        if (_subscribers.TryRemove(_id, out var channel))
        {
            channel.Writer.TryComplete();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Internal event bus feeding same-origin Server-Sent Events for instant dashboard refreshes.</summary>
public sealed class DashboardEventBus : IDashboardEventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public DashboardSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[id] = channel;
        return new DashboardSubscription(id, channel.Reader, _subscribers);
    }

    public Task PublishAsync(DashboardEvent dashboardEvent, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(dashboardEvent);
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(json);
        }
        return Task.CompletedTask;
    }
}
