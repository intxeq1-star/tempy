using System.Threading.Channels;

namespace LanManagement.Server.Services;

/// <summary>A coalescing wake-up signal. Periodic workers retain a timed sweep even if a signal is lost.</summary>
public interface IWorkSignal
{
    void Pulse();
    Task WaitAsync(TimeSpan maximumWait, CancellationToken cancellationToken);
}

public interface ICommandWorkSignal : IWorkSignal
{
}

public interface ISyncWorkSignal : IWorkSignal
{
}

public class WorkSignal : IWorkSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = false,
        SingleWriter = false
    });

    public void Pulse() => _channel.Writer.TryWrite(0);

    public async Task WaitAsync(TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var read = _channel.Reader.ReadAsync(readCancellation.Token).AsTask();
        var delay = Task.Delay(maximumWait, cancellationToken);
        var completed = await Task.WhenAny(read, delay);
        if (completed != read)
        {
            readCancellation.Cancel();
            try { await read; } catch (OperationCanceledException) { }
        }
        else
        {
            await read;
        }
    }
}

public sealed class CommandWorkSignal : WorkSignal, ICommandWorkSignal
{
}

public sealed class SyncWorkSignal : WorkSignal, ISyncWorkSignal
{
}
