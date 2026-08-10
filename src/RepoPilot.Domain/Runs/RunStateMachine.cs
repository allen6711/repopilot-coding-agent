using System.Collections.Frozen;

namespace RepoPilot.Domain.Runs;

/// <summary>
/// The run lifecycle, as an explicit transition table.
/// <para>
/// Principle IV requires transitions to be implemented in backend code and to
/// fail loudly rather than degrade. Two consequences shape this type: the table
/// is exhaustive and closed, so an unlisted pair is refused rather than assumed
/// harmless; and refusal throws <see cref="IllegalTransitionException"/> so a
/// caller cannot continue past it by ignoring a return value.
/// </para>
/// <para>
/// This type has no dependencies and touches no I/O, so the whole lifecycle is
/// unit-testable without a database, a container, or a model provider.
/// </para>
/// </summary>
public static class RunStateMachine
{
    /// <summary>Stages from which no further transition is possible.</summary>
    private static readonly FrozenSet<RunStage> TerminalStages = new[]
    {
        RunStage.Succeeded,
        RunStage.Failed,
        RunStage.Rejected,
        RunStage.Cancelled,
        RunStage.NoChange,
    }.ToFrozenSet();

    /// <summary>
    /// Triggers permitted from any non-terminal stage. Cancellation and failure
    /// are deliberately universal: a run must always be stoppable, and an error
    /// at any stage must be recordable rather than leaving the run wedged.
    /// </summary>
    private static readonly FrozenDictionary<RunTrigger, RunStage> UniversalTransitions =
        new Dictionary<RunTrigger, RunStage>
        {
            [RunTrigger.Cancelled] = RunStage.Cancelled,
            [RunTrigger.Failed] = RunStage.Failed,
        }.ToFrozenDictionary();

    /// <summary>The stage-specific transition table.</summary>
    private static readonly FrozenDictionary<(RunStage From, RunTrigger Trigger), RunStage> Table =
        new Dictionary<(RunStage, RunTrigger), RunStage>
        {
            [(RunStage.Created, RunTrigger.Start)] = RunStage.Retrieving,

            [(RunStage.Retrieving, RunTrigger.ContextRetrieved)] = RunStage.Planning,
            [(RunStage.Retrieving, RunTrigger.InsufficientContext)] = RunStage.NoChange,

            [(RunStage.Planning, RunTrigger.PlanProduced)] = RunStage.Proposing,

            // FR-010: a plan must precede a proposal, so ProposalCreated is not
            // reachable from Planning — the run has to pass through Proposing.
            [(RunStage.Proposing, RunTrigger.ProposalCreated)] = RunStage.AwaitingApproval,
            [(RunStage.Proposing, RunTrigger.NoModificationProposed)] = RunStage.NoChange,

            [(RunStage.AwaitingApproval, RunTrigger.Approved)] = RunStage.Applying,
            [(RunStage.AwaitingApproval, RunTrigger.Rejected)] = RunStage.Rejected,

            [(RunStage.Applying, RunTrigger.PatchApplied)] = RunStage.Testing,

            [(RunStage.Testing, RunTrigger.TestsPassed)] = RunStage.Succeeded,
            // A revision goes back to Proposing, and FR-013 makes the new
            // proposal require its own approval — it cannot re-enter Applying.
            [(RunStage.Testing, RunTrigger.TestsFailedRetryAvailable)] = RunStage.Proposing,
            [(RunStage.Testing, RunTrigger.TestsFailedRetriesExhausted)] = RunStage.Failed,
        }.ToFrozenDictionary();

    /// <summary>Whether no further transition is possible from <paramref name="stage"/>.</summary>
    public static bool IsTerminal(RunStage stage) => TerminalStages.Contains(stage);

    /// <summary>
    /// Maps a terminal stage to its recorded outcome.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stage"/> is not terminal.</exception>
    public static TerminalOutcome ToOutcome(RunStage stage) => stage switch
    {
        RunStage.Succeeded => TerminalOutcome.Succeeded,
        RunStage.Failed => TerminalOutcome.Failed,
        RunStage.Rejected => TerminalOutcome.Rejected,
        RunStage.Cancelled => TerminalOutcome.Cancelled,
        RunStage.NoChange => TerminalOutcome.NoChange,
        _ => throw new ArgumentOutOfRangeException(
            nameof(stage), stage, "Stage is not terminal and has no outcome."),
    };

    /// <summary>
    /// Attempts a transition without throwing.
    /// </summary>
    /// <returns><c>true</c> when the transition is permitted.</returns>
    public static bool TryTransition(RunStage from, RunTrigger trigger, out RunStage to)
    {
        if (IsTerminal(from))
        {
            // Nothing follows a terminal stage — including cancellation, which
            // is what makes "cancel an already-finished run" a refusal rather
            // than a silent no-op.
            to = from;
            return false;
        }

        if (Table.TryGetValue((from, trigger), out var next) ||
            UniversalTransitions.TryGetValue(trigger, out next))
        {
            to = next;
            return true;
        }

        to = from;
        return false;
    }

    /// <summary>
    /// Performs a transition, throwing when it is not permitted.
    /// </summary>
    /// <exception cref="IllegalTransitionException">
    /// The pair is not in the table. The caller must record the rejection rather
    /// than continue (FR-009).
    /// </exception>
    public static RunStage Transition(RunStage from, RunTrigger trigger)
    {
        if (!TryTransition(from, trigger, out var to))
        {
            throw new IllegalTransitionException(from, trigger);
        }

        return to;
    }

    /// <summary>
    /// Every permitted transition, for diagnostics and for asserting that the
    /// persisted history of a run is reachable.
    /// </summary>
    public static IReadOnlyCollection<(RunStage From, RunTrigger Trigger, RunStage To)> AllTransitions()
    {
        var all = new List<(RunStage, RunTrigger, RunStage)>(Table.Count + 16);

        foreach (var entry in Table)
        {
            all.Add((entry.Key.From, entry.Key.Trigger, entry.Value));
        }

        foreach (var stage in Enum.GetValues<RunStage>())
        {
            if (IsTerminal(stage))
            {
                continue;
            }

            foreach (var universal in UniversalTransitions)
            {
                all.Add((stage, universal.Key, universal.Value));
            }
        }

        return all;
    }
}

/// <summary>
/// Raised when a transition not present in the table is attempted. The handler
/// records a stage-transition-rejected event and stops; it does not continue
/// (FR-009).
/// </summary>
public sealed class IllegalTransitionException(RunStage from, RunTrigger trigger)
    : InvalidOperationException($"Transition from {from} on {trigger} is not permitted.")
{
    public RunStage From { get; } = from;

    public RunTrigger Trigger { get; } = trigger;
}
