using RepoPilot.Application.Ports;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;

namespace RepoPilot.Application.UseCases;

/// <summary>Why a cancellation was refused.</summary>
public enum CancellationRefusalReason
{
    RunNotFound,
    ActorMissing,
    AlreadyTerminal,
}

/// <summary>Raised when a run cannot be cancelled.</summary>
public sealed class CancellationRefusedException(CancellationRefusalReason reason, string detail)
    : InvalidOperationException(detail)
{
    public CancellationRefusalReason Reason { get; } = reason;
}

/// <summary>
/// Ends a run at the user's request (FR-008a).
/// <para>
/// Cancellation is a universal transition in the state machine, so it is
/// available from every non-terminal stage — a run stuck awaiting an approval
/// nobody will give must be stoppable, and so must one mid-retrieval. What it is
/// deliberately <em>not</em> available from is a terminal stage: cancelling a
/// finished run would overwrite a recorded outcome, and the audit trail is
/// append-only for the same reason approvals are (FR-019b).
/// </para>
/// <para>
/// It requires an actor for the same reason an approval does. "The run was
/// abandoned" with nobody attached is not an account of what happened.
/// </para>
/// </summary>
public sealed class CancelRunUseCase(
    IRunStore runs,
    IWorkspaceProvisioner workspaces,
    RunEventRecorder events)
{
    /// <summary>
    /// Cancels a run.
    /// </summary>
    /// <param name="runId">The run to cancel.</param>
    /// <param name="cancelledBy">Actor identity. Attributable, not verified.</param>
    /// <exception cref="CancellationRefusedException">The run cannot be cancelled.</exception>
    public async Task<Run> CancelAsync(
        Guid runId, string cancelledBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cancelledBy))
        {
            throw new CancellationRefusedException(
                CancellationRefusalReason.ActorMissing,
                "A cancellation must record who requested it (FR-015a).");
        }

        var run = await runs.FindAsync(runId, ct)
            ?? throw new CancellationRefusedException(
                CancellationRefusalReason.RunNotFound, $"Run {runId} does not exist.");

        if (RunStateMachine.IsTerminal(run.Stage))
        {
            throw new CancellationRefusedException(
                CancellationRefusalReason.AlreadyTerminal,
                $"Run {runId} already ended as {run.TerminalOutcome}. A recorded outcome is not " +
                "replaced by a later cancellation.");
        }

        var from = run.Stage;

        run.Stage = RunStateMachine.Transition(from, RunTrigger.Cancelled);
        run.TerminalOutcome = TerminalOutcome.Cancelled;
        run.OutcomeReason = OutcomeReason.AbandonedByUser;
        run.EndedAt = DateTimeOffset.UtcNow;

        await runs.UpdateAsync(run, ct);

        await events.RecordAsync(
            new RunEvent
            {
                RunId = runId,
                EventType = RunEventType.StageChanged,
                ArgumentsSummary =
                    $$"""{"fromStage":"{{from}}","trigger":"Cancelled","toStage":"Cancelled","by":"{{cancelledBy.Trim()}}"}""",
                Status = RunEventStatus.Succeeded,
            },
            ct);

        // Always the last event of a run, cancellation included. A watching
        // client treats stream closure without it as a transport drop and
        // reconnects — so a cancelled run that omitted it would leave every
        // viewer reconnecting forever to a run that had already ended.
        await events.RecordAsync(
            new RunEvent
            {
                RunId = runId,
                EventType = RunEventType.RunEnded,
                ArgumentsSummary =
                    $$"""{"terminalOutcome":"Cancelled","outcomeReason":"AbandonedByUser"}""",
                Status = RunEventStatus.Succeeded,
            },
            ct);

        // FR-026a: the working copy goes at every terminal outcome, cancellation
        // included. A cancelled run that left its directory behind would be the
        // most likely source of orphans, since it is the one path a human takes
        // deliberately.
        await workspaces.DestroyAsync(runId, ct);

        return run;
    }
}
