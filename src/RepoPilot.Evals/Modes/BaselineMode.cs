using RepoPilot.Domain.Entities;
using RepoPilot.Evals.Tasks;

namespace RepoPilot.Evals.Modes;

/// <summary>
/// Configures the two conditions an evaluation compares (FR-033).
/// <para>
/// Both conditions run the same tasks through the same orchestrator. The only
/// difference is what the agent is offered: the tool-enabled condition gets the
/// full model-surface capability set, and the baseline gets its retrieved context
/// up front and <c>propose_patch</c> alone.
/// </para>
/// <para>
/// The difference is expressed as a field on the run rather than as a flag the
/// harness passes down a separate code path, so that the record of a baseline run
/// says it was one. SC-006 compares the two completion rates; a reader who cannot
/// tell from the stored run which condition produced it cannot check the
/// comparison.
/// </para>
/// </summary>
public static class BaselineMode
{
    /// <summary>
    /// Builds the run for one task in one condition.
    /// </summary>
    /// <param name="task">The committed task definition.</param>
    /// <param name="repositoryId">The fixture the task names.</param>
    /// <param name="evaluationRunId">
    /// Binds the run to this evaluation. It is also what makes programmatic
    /// approval available to the run at all (FR-015b), so an evaluation run that
    /// failed to set it would be refused at the approval gate rather than
    /// silently approved.
    /// </param>
    /// <param name="mode">Which condition this run is.</param>
    public static Run RunFor(
        EvaluationTaskDefinition task,
        Guid repositoryId,
        Guid evaluationRunId,
        EvaluationMode mode) => new()
        {
            RepositoryId = repositoryId,

            // Verbatim. Adding an instruction here — "be careful", "the tests
            // matter" — would make the measured figure a property of the harness's
            // prompt rather than of the committed task set.
            TaskDescription = task.Description,
            SeededTaskId = task.Id,
            VerifyCommandName = task.SuccessTestCommand,
            ToolsEnabled = mode == EvaluationMode.ToolEnabled,
            ApprovalMode = ApprovalMode.Programmatic,
            EvaluationRunId = evaluationRunId,
        };

    /// <summary>The conditions every task is run under, in execution order.</summary>
    public static IReadOnlyList<EvaluationMode> Conditions { get; } =
    [
        EvaluationMode.Baseline,
        EvaluationMode.ToolEnabled,
    ];
}
