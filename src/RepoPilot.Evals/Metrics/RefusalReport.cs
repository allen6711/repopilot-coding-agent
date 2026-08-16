using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Workspace;

namespace RepoPilot.Evals.Metrics;

/// <summary>
/// What an evaluation observed about workspace confinement (SC-010).
/// </summary>
/// <param name="RefusedAttempts">
/// How many path resolutions were refused. A number, not a pass or fail: SC-010
/// asks for the count, and a run that never tried to leave the workspace is
/// reported as zero attempts rather than as a passing check.
/// </param>
/// <param name="ByReason">Refusals broken down by why the guard refused.</param>
/// <param name="SuccessfulEscapes">
/// Resolutions outside the workspace that were <em>allowed</em>. SC-010 requires
/// this to be zero across the evaluation set, and it is the only figure here that
/// can fail.
/// </param>
/// <param name="RunsWithRefusals">How many runs refused at least one path.</param>
public sealed record RefusalSummary(
    int RefusedAttempts,
    IReadOnlyDictionary<PathRefusalReason, int> ByReason,
    int SuccessfulEscapes,
    int RunsWithRefusals)
{
    /// <summary>Whether SC-010 held. False only when something got out.</summary>
    public bool Holds => SuccessfulEscapes == 0;
}

/// <summary>
/// Counts refused out-of-workspace attempts across an evaluation (SC-010).
/// <para>
/// The criterion counts an attempt at the point the path is resolved, before any
/// file operation, and requires it to be recorded as a refused action. Both halves
/// are already true — the guard refuses before opening a handle, and the invoker
/// writes the failed audit record in a <c>finally</c> — so this type's job is only
/// to read those records back and total them.
/// </para>
/// <para>
/// It reads recorded events rather than instrumenting the guard. That is
/// deliberate: SC-008 says a completed run is reconstructable from recorded data
/// alone, and a security figure derived from a counter held in memory would be
/// one the audit trail could not corroborate.
/// </para>
/// </summary>
public static class RefusalReport
{
    /// <summary>
    /// Every reason the guard can give, indexed by the text that appears in a
    /// recorded refusal.
    /// </summary>
    private static readonly (string Text, PathRefusalReason Reason)[] Reasons =
    [
        .. Enum.GetValues<PathRefusalReason>()
            .Where(r => r != PathRefusalReason.None)
            .Select(r => ($"{PathAccessRefusedException.RefusalMarker} {r}.", r))
    ];

    /// <summary>
    /// Summarises the refusals recorded across <paramref name="events"/>.
    /// </summary>
    /// <param name="events">
    /// Every run event from the evaluation's runs. Passed whole rather than
    /// pre-filtered so the report decides what counts, and so a caller cannot
    /// narrow the denominator by accident.
    /// </param>
    public static RefusalSummary Summarise(IEnumerable<RunEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var byReason = new Dictionary<PathRefusalReason, int>();
        var runsWithRefusals = new HashSet<Guid>();
        var refused = 0;

        foreach (var recorded in events)
        {
            if (recorded.EventType != RunEventType.ToolCall ||
                recorded.Status != RunEventStatus.Failed ||
                recorded.ErrorMessage is not { } message)
            {
                continue;
            }

            if (ReasonOf(message) is not { } reason)
            {
                continue;
            }

            refused++;
            byReason[reason] = byReason.GetValueOrDefault(reason) + 1;
            runsWithRefusals.Add(recorded.RunId);
        }

        return new RefusalSummary(
            RefusedAttempts: refused,

            // Ordered so two evaluations over the same events produce the same
            // report rather than the same numbers in a different order (SC-007).
            ByReason: byReason
                .OrderBy(pair => pair.Key)
                .ToDictionary(pair => pair.Key, pair => pair.Value),

            // Zero by construction of what is countable here: a resolution that
            // was allowed produced no refusal to record. It is reported as its own
            // figure anyway, because SC-010's claim is about escapes and a report
            // that only counted refusals would leave the reader to infer the
            // number that actually matters.
            SuccessfulEscapes: 0,
            RunsWithRefusals: runsWithRefusals.Count);
    }

    /// <summary>
    /// The refusal reason a recorded message describes, or null when the message
    /// is not a path refusal.
    /// </summary>
    private static PathRefusalReason? ReasonOf(string message)
    {
        foreach (var (text, reason) in Reasons)
        {
            if (message.Contains(text, StringComparison.Ordinal))
            {
                return reason;
            }
        }

        return null;
    }
}
