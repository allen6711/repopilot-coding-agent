using RepoPilot.Domain.Runs;

namespace RepoPilot.Application.Runs;

/// <summary>
/// Decides what follows a failed test run (FR-012, FR-013).
/// <para>
/// Separated from the orchestrator because it is the one part of the revision
/// loop that is a pure decision, and because getting it wrong is the difference
/// between a bounded loop and an unbounded one. Kept as a function of the
/// attempt count alone, so the bound cannot depend on anything the model
/// influenced.
/// </para>
/// </summary>
public static class RevisionPolicy
{
    /// <summary>
    /// The trigger to apply after tests fail.
    /// </summary>
    /// <param name="attemptsUsed">
    /// Revision attempts already made. The first proposal is attempt 0, so this
    /// is also the number of times the run has already been round the loop.
    /// </param>
    /// <param name="maxAttempts">The configured ceiling (FR-012).</param>
    public static RunTrigger AfterFailure(int attemptsUsed, int maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptsUsed);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAttempts);

        return attemptsUsed < maxAttempts
            ? RunTrigger.TestsFailedRetryAvailable
            : RunTrigger.TestsFailedRetriesExhausted;
    }

    /// <summary>
    /// Whether another attempt is permitted, for callers that need the answer
    /// without applying a transition.
    /// </summary>
    public static bool CanRevise(int attemptsUsed, int maxAttempts) =>
        AfterFailure(attemptsUsed, maxAttempts) == RunTrigger.TestsFailedRetryAvailable;

    /// <summary>
    /// How many proposals a run can produce in total: the original plus every
    /// permitted revision. Each of them needs its own approval (FR-013), so this
    /// is also the maximum number of approval decisions a single run can carry.
    /// </summary>
    public static int MaxProposalsPerRun(int maxAttempts) => maxAttempts + 1;
}
