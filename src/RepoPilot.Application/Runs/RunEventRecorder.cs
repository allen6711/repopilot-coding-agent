using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;

namespace RepoPilot.Application.Runs;

/// <summary>
/// Records a run event: persists it, then publishes it.
/// <para>
/// The ordering is the whole point of this type existing rather than callers
/// doing both. Persist-then-publish means a live subscriber can never see an
/// event that a later replay would miss, so the same table serves both the
/// two-second live view (FR-028a) and reconstruction from recorded data alone
/// (FR-029, SC-008). Publishing first would make the reverse possible, and the
/// gap would only show up as a reconnecting client missing an event.
/// </para>
/// </summary>
public sealed class RunEventRecorder(IRunEventStore store, IRunEventPublisher publisher)
{
    /// <summary>
    /// Persists and publishes an event.
    /// </summary>
    /// <returns>The persisted event, carrying its assigned sequence.</returns>
    public async Task<RunEvent> RecordAsync(RunEvent runEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runEvent);

        var persisted = await store.AppendAsync(runEvent, ct);

        // Publication failures must not undo the record. The durable copy is
        // already committed and a subscriber can recover from it, so an
        // in-process delivery problem is not worth losing the audit trail over.
        try
        {
            publisher.Publish(persisted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Intentionally swallowed — see above. The event is recorded.
        }

        return persisted;
    }
}
