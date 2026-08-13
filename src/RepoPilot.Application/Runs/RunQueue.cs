using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RepoPilot.Application.Configuration;

namespace RepoPilot.Application.Runs;

/// <summary>
/// A slot held for the duration of one executing segment. Disposing returns it.
/// </summary>
public sealed class RunSlot : IDisposable
{
    private readonly SemaphoreSlim _limiter;
    private int _released;

    internal RunSlot(SemaphoreSlim limiter) => _limiter = limiter;

    /// <summary>
    /// Returns the slot. Safe to call more than once — the orchestrator releases
    /// explicitly on reaching <c>awaiting approval</c> and again through
    /// <c>using</c> on the way out, and the second call must not hand back a slot
    /// the queue never issued.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _limiter.Release();
        }
    }
}

/// <summary>
/// Admission control for run execution (FR-013a, FR-013b).
/// <para>
/// Two separate bounds, for two separate reasons. The channel bounds how many
/// runs may be <em>waiting</em>, so a flood is refused at submission rather than
/// accumulating in memory. The semaphore bounds how many may be <em>executing</em>,
/// so the machine is not oversubscribed.
/// </para>
/// <para>
/// The slot discipline is the load-bearing part: a run releases its slot on
/// entering <c>awaiting approval</c> and acquires a fresh one before
/// <c>applying</c>. Holding the slot across the approval gate would let a handful
/// of unanswered reviews stall every other run indefinitely — a human taking the
/// weekend to look at a diff would look exactly like a deadlock.
/// </para>
/// </summary>
public sealed class RunQueue : IDisposable
{
    private readonly Channel<Guid> _pending;
    private readonly SemaphoreSlim _limiter;

    public RunQueue(IOptions<RunConcurrencyOptions> options)
    {
        var concurrency = options.Value.MaxConcurrent;

        _limiter = new SemaphoreSlim(concurrency, concurrency);

        // Waiting capacity is deliberately far larger than execution capacity:
        // queueing is the expected behaviour above the limit, not an error
        // condition. FullMode.Wait means a submitter at the ceiling waits rather
        // than having its run dropped.
        _pending = Channel.CreateBounded<Guid>(new BoundedChannelOptions(concurrency * 64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>Executing runs the limiter still has room for.</summary>
    public int AvailableSlots => _limiter.CurrentCount;

    /// <summary>Submits a run for execution. Returns once it is queued.</summary>
    public ValueTask EnqueueAsync(Guid runId, CancellationToken ct = default) =>
        _pending.Writer.WriteAsync(runId, ct);

    /// <summary>Yields queued runs as they arrive, until the queue is completed.</summary>
    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct = default) =>
        _pending.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Waits for an execution slot.
    /// <para>
    /// Called twice per run that reaches approval: once to begin, and again
    /// after approval to apply. The second acquisition is a genuine wait — an
    /// approved run rejoins the contention rather than resuming by right —
    /// which is what keeps the executing count at or under the limit however
    /// approvals happen to land.
    /// </para>
    /// </summary>
    public async Task<RunSlot> AcquireAsync(CancellationToken ct = default)
    {
        await _limiter.WaitAsync(ct);
        return new RunSlot(_limiter);
    }

    /// <summary>
    /// Runs one segment while holding a slot, and returns the slot afterwards.
    /// <para>
    /// A segment, not a run. That is the whole distinction FR-013b turns on: the
    /// pre-approval segment ends when the run reaches <c>awaiting approval</c>,
    /// and the slot is returned there rather than being held across the human's
    /// decision. The post-approval segment calls this again and waits its turn
    /// like anything else.
    /// </para>
    /// </summary>
    public async Task<T> WithSlotAsync<T>(
        Func<CancellationToken, Task<T>> segment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        using var slot = await AcquireAsync(ct);
        return await segment(ct);
    }

    /// <summary>Stops accepting submissions. In-flight runs are unaffected.</summary>
    public void Complete() => _pending.Writer.TryComplete();

    public void Dispose()
    {
        Complete();
        _limiter.Dispose();
    }
}
