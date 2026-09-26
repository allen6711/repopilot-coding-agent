using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;

namespace RepoPilot.Infrastructure.Events;

/// <summary>
/// In-process fan-out of run events to live subscribers.
/// <para>
/// In-process because runs are not resumable across restarts anyway (a spec
/// assumption), so a distributed bus would buy durability the rest of the design
/// does not claim. Anything a subscriber missed is recoverable from the store,
/// which is the durable record.
/// </para>
/// </summary>
public sealed class InProcessRunEventPublisher : IRunEventPublisher, IDisposable
{
    private readonly ConcurrentDictionary<Guid, SubscriberSet> _byRun = new();

    /// <inheritdoc />
    public void Publish(RunEvent runEvent)
    {
        ArgumentNullException.ThrowIfNull(runEvent);

        if (_byRun.TryGetValue(runEvent.RunId, out var subscribers))
        {
            subscribers.Publish(runEvent);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RunEvent> SubscribeAsync(
        Guid runId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var subscribers = _byRun.GetOrAdd(runId, _ => new SubscriberSet());
        var channel = subscribers.Add();

        try
        {
            await foreach (var runEvent in channel.Reader.ReadAllAsync(ct))
            {
                yield return runEvent;
            }
        }
        finally
        {
            // Removing the run's entry once its last subscriber leaves keeps a
            // long-lived process from accumulating an entry per run ever started.
            if (subscribers.Remove(channel) == 0)
            {
                _byRun.TryRemove(runId, out _);
            }
        }
    }

    public void Dispose()
    {
        foreach (var subscribers in _byRun.Values)
        {
            subscribers.CompleteAll();
        }

        _byRun.Clear();
    }

    private sealed class SubscriberSet
    {
        private readonly Lock _gate = new();
        private readonly List<Channel<RunEvent>> _channels = [];

        public Channel<RunEvent> Add()
        {
            // Unbounded, because dropping an event would put a live subscriber
            // permanently out of step with the durable record. A slow reader
            // costs memory for the length of one run, which is bounded.
            var channel = Channel.CreateUnbounded<RunEvent>(
                new UnboundedChannelOptions { SingleReader = true });

            lock (_gate)
            {
                _channels.Add(channel);
            }

            return channel;
        }

        public int Remove(Channel<RunEvent> channel)
        {
            lock (_gate)
            {
                _channels.Remove(channel);
                channel.Writer.TryComplete();
                return _channels.Count;
            }
        }

        public void Publish(RunEvent runEvent)
        {
            lock (_gate)
            {
                foreach (var channel in _channels)
                {
                    channel.Writer.TryWrite(runEvent);
                }
            }
        }

        public void CompleteAll()
        {
            lock (_gate)
            {
                foreach (var channel in _channels)
                {
                    channel.Writer.TryComplete();
                }

                _channels.Clear();
            }
        }
    }
}
