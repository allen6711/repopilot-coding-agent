using RepoPilot.Domain.Runs;

namespace RepoPilot.Application.Runs;

/// <summary>
/// The two ways a run legitimately ends without changing anything (FR-008b).
/// <para>
/// They are separate reasons because they call for opposite responses. A
/// deliberate no-op means the agent looked and concluded the code is already
/// correct; insufficient context means it could not find the code at all. A
/// single "no change" outcome would make those indistinguishable in the record,
/// and the second one is a retrieval problem worth acting on.
/// </para>
/// <para>
/// Neither produces a proposal. An empty proposal would be a thing a reviewer
/// could approve, and approving nothing is not a decision anyone should be asked
/// to make — so the run terminates instead of offering one.
/// </para>
/// </summary>
public static class NoChangeOutcome
{
    /// <summary>Which trigger ends the run, given where it is.</summary>
    /// <param name="reason">Why nothing is changing.</param>
    /// <exception cref="ArgumentOutOfRangeException">Not a no-change reason.</exception>
    public static RunTrigger TriggerFor(OutcomeReason reason) => reason switch
    {
        // Reached from Retrieving: the search found nothing to work with.
        OutcomeReason.InsufficientContext => RunTrigger.InsufficientContext,

        // Reached from Proposing: the agent had the context and chose not to act.
        OutcomeReason.DeliberateNoOp => RunTrigger.NoModificationProposed,

        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Not a reason a run ends with no change."),
    };

    /// <summary>
    /// Whether a stage may end in no change. Both entry points are stages that
    /// precede any approval, which is what makes "no change" always mean nothing
    /// was written rather than something being undone.
    /// </summary>
    public static bool IsReachableFrom(RunStage stage) =>
        stage is RunStage.Retrieving or RunStage.Proposing;

    /// <summary>
    /// The reason a stage's no-change exit carries.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The stage has no such exit.</exception>
    public static OutcomeReason ReasonFor(RunStage stage) => stage switch
    {
        RunStage.Retrieving => OutcomeReason.InsufficientContext,
        RunStage.Proposing => OutcomeReason.DeliberateNoOp,
        _ => throw new ArgumentOutOfRangeException(
            nameof(stage), stage, "This stage has no no-change exit."),
    };

    /// <summary>
    /// Whether what the agent produced counts as a proposal at all.
    /// <para>
    /// Called before a proposal is created. A model that emits an entry list with
    /// nothing in it, or entries whose content is unchanged, has not proposed a
    /// change — treating that as a proposal would put an empty diff in front of a
    /// reviewer and record an approval that authorised nothing.
    /// </para>
    /// </summary>
    public static bool IsEmptyProposal(int entryCount) => entryCount <= 0;
}
