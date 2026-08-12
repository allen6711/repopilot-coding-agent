using RepoPilot.Domain.Entities;

namespace RepoPilot.Application.Ports;

/// <summary>
/// Delivers recorded events to anyone watching a run in progress.
/// <para>
/// Separate from <see cref="IRunEventStore"/> on purpose. Persistence is the
/// durable record that SC-008 reconstructs a run from; publication is the live
/// view that FR-028a must reach within two seconds. Keeping them apart is what
/// makes the ordering rule statable: persist first, then publish.
/// </para>
/// </summary>
public interface IRunEventPublisher
{
    /// <summary>
    /// Publishes an already-persisted event to current subscribers.
    /// </summary>
    void Publish(RunEvent runEvent);

    /// <summary>
    /// Streams events for a run as they are published.
    /// <para>
    /// Live events only. A subscriber that needs history reads it from the store
    /// first and then attaches — which is exactly how the SSE endpoint honours
    /// <c>Last-Event-ID</c>.
    /// </para>
    /// </summary>
    IAsyncEnumerable<RunEvent> SubscribeAsync(Guid runId, CancellationToken ct = default);
}
