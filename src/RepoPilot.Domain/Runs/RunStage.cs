namespace RepoPilot.Domain.Runs;

/// <summary>
/// The stages a run moves through (FR-008). Five of these are terminal.
/// <para>
/// Stage is decided by backend code and persisted before the next action.
/// It is never inferred from model output — that is the whole point of
/// Principle IV, and it is why this is an explicit enum rather than something
/// derived from the presence of related rows.
/// </para>
/// </summary>
public enum RunStage
{
    Created,
    Retrieving,
    Planning,
    Proposing,
    AwaitingApproval,
    Applying,
    Testing,

    // Terminal outcomes.
    Succeeded,
    Failed,
    Rejected,
    Cancelled,
    NoChange,
}

/// <summary>
/// What causes a stage change. A trigger is an event the backend observed, not
/// an instruction the model issued.
/// </summary>
public enum RunTrigger
{
    /// <summary>The queue picked the run up and gave it a slot.</summary>
    Start,

    /// <summary>Enough context was retrieved to plan against.</summary>
    ContextRetrieved,

    /// <summary>The task was too vague to locate relevant code (FR-008b).</summary>
    InsufficientContext,

    /// <summary>A short human-readable plan was produced (FR-010).</summary>
    PlanProduced,

    /// <summary>A change proposal was created and stored.</summary>
    ProposalCreated,

    /// <summary>The agent concluded nothing should change (FR-008b).</summary>
    NoModificationProposed,

    /// <summary>A human approved the current proposal.</summary>
    Approved,

    /// <summary>A human rejected the current proposal.</summary>
    Rejected,

    /// <summary>The approved change was written to the working copy.</summary>
    PatchApplied,

    /// <summary>The allow-listed test command succeeded.</summary>
    TestsPassed,

    /// <summary>Tests failed and a revision attempt remains (FR-012).</summary>
    TestsFailedRetryAvailable,

    /// <summary>Tests failed and the revision limit is reached (FR-012).</summary>
    TestsFailedRetriesExhausted,

    /// <summary>A human explicitly abandoned the run (FR-008a).</summary>
    Cancelled,

    /// <summary>An error ended the run at its current stage (FR-030).</summary>
    Failed,
}

/// <summary>
/// The terminal outcome of a run (FR-008). Exactly one is recorded.
/// </summary>
public enum TerminalOutcome
{
    Succeeded,
    Failed,
    Rejected,
    Cancelled,
    NoChange,
}

/// <summary>
/// The normative reason set (FR-008c). A reason outside this set cannot be
/// recorded — which is why this is an enum and the database column is a
/// PostgreSQL enum rather than free text.
/// </summary>
public enum OutcomeReason
{
    /// <summary>Tests passed; the run achieved what it set out to do.</summary>
    Completed,

    /// <summary>Tests still failing after the allowed revision attempts.</summary>
    RevisionLimitReached,

    /// <summary>The agent judged that no change was needed.</summary>
    DeliberateNoOp,

    /// <summary>The task was too vague to locate relevant code.</summary>
    InsufficientContext,

    /// <summary>A human rejected the proposal.</summary>
    ProposalRejected,

    /// <summary>A human explicitly abandoned the run.</summary>
    AbandonedByUser,

    /// <summary>The model provider could not be reached.</summary>
    ProviderUnavailable,

    /// <summary>No isolated execution environment was available.</summary>
    IsolatedEnvUnavailable,

    /// <summary>The time limit expired and the environment could not be stopped (FR-023b).</summary>
    IsolatedEnvNotTerminable,

    /// <summary>The service restarted; runs are not resumable (FR-030a).</summary>
    ServiceRestarted,
}
