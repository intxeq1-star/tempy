using OMClientAgent.Core.Models;

namespace OMClientAgent.Core;

public sealed class OmEvents
{
    public event Func<Task>? CommunicationEstablished;
    public event Func<Job, Task>? JobReceived;
    public event Func<AppPolicy, Task>? PolicyReceived;
    public event Func<Task>? SyncRequested;
    public event Func<AgentUpdateMessage, Task>? AgentUpdate;
    public event Action<ConnectionState>? ConnectionStateChanged;

    public Task RaiseCommunicationEstablished() => CommunicationEstablished?.InvokeAll() ?? Task.CompletedTask;
    public Task RaiseJobReceived(Job job) => JobReceived?.InvokeAll(job) ?? Task.CompletedTask;
    public Task RaisePolicyReceived(AppPolicy policy) => PolicyReceived?.InvokeAll(policy) ?? Task.CompletedTask;
    public Task RaiseSyncRequested() => SyncRequested?.InvokeAll() ?? Task.CompletedTask;
    public Task RaiseAgentUpdate(AgentUpdateMessage msg) => AgentUpdate?.InvokeAll(msg) ?? Task.CompletedTask;
    public void RaiseConnectionStateChanged(ConnectionState state) => ConnectionStateChanged?.Invoke(state);
}

internal static class EventExtensions
{
    public static async Task InvokeAll(this Func<Task>? handlers)
    {
        if (handlers is null) return;
        foreach (Func<Task> d in handlers.GetInvocationList())
        {
            try { await d().ConfigureAwait(false); } catch { }
        }
    }

    public static async Task InvokeAll<T>(this Func<T, Task>? handlers, T arg)
    {
        if (handlers is null) return;
        foreach (Func<T, Task> d in handlers.GetInvocationList())
        {
            try { await d(arg).ConfigureAwait(false); } catch { }
        }
    }
}
