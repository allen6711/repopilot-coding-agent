using RepoPilot.Application.Ports;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Proposals;

namespace RepoPilot.Application.UseCases;

/// <summary>Why a decision was refused.</summary>
public enum DecisionRefusalReason
{
    ProposalNotFound,
    HashMismatch,
    ActorMissing,
    ProgrammaticModeNotPermitted,
}

/// <summary>Raised when a decision cannot be recorded.</summary>
public sealed class DecisionRefusedException(DecisionRefusalReason reason, string detail)
    : InvalidOperationException(detail)
{
    public DecisionRefusalReason Reason { get; } = reason;
}

/// <summary>
/// Records a human decision on a proposal (FR-015, FR-018, FR-019, FR-020a).
/// <para>
/// This is the only thing in the system that can unlock a write. Everything it
/// checks is therefore checked before the record exists, not after: a decision
/// that was written and then found invalid would already have unlocked the
/// write it was supposed to gate.
/// </para>
/// </summary>
public sealed class DecideProposalUseCase(
    IProposalStore proposals,
    IApprovalStore approvals,
    IRunStore runs,
    RunEventRecorder events)
{
    /// <summary>
    /// Records an approval or rejection.
    /// </summary>
    /// <param name="proposalId">The proposal being decided.</param>
    /// <param name="decision">Approve or reject.</param>
    /// <param name="echoedDiffHash">
    /// The hash shown to the approver, echoed back. This is what makes FR-020a
    /// checkable: it proves the decision was made against the content now
    /// stored, rather than against something the reviewer saw earlier.
    /// </param>
    /// <param name="decidedBy">Actor identity. Attributable, not verified.</param>
    /// <param name="mode">Interactive, or programmatic for evaluation runs only.</param>
    /// <exception cref="DecisionRefusedException">The decision is not acceptable.</exception>
    /// <exception cref="ProposalAlreadyDecidedException">
    /// A decision already exists. Enforced by a unique index, so this cannot be
    /// lost to a race (FR-018).
    /// </exception>
    public async Task<ApprovalDecision> DecideAsync(
        Guid proposalId,
        ApprovalDecisionKind decision,
        string echoedDiffHash,
        string decidedBy,
        ApprovalMode mode = ApprovalMode.Interactive,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decidedBy))
        {
            // SC-015. An empty actor satisfies a NOT NULL column while leaving
            // the decision unattributable, which is precisely the failure the
            // criterion exists to prevent.
            throw new DecisionRefusedException(
                DecisionRefusalReason.ActorMissing,
                "A decision must record who made it (FR-015a).");
        }

        var proposal = await proposals.FindAsync(proposalId, ct)
            ?? throw new DecisionRefusedException(
                DecisionRefusalReason.ProposalNotFound, $"Proposal {proposalId} does not exist.");

        var run = await runs.FindAsync(proposal.RunId, ct)
            ?? throw new InvalidOperationException($"Run {proposal.RunId} not found.");

        if (mode == ApprovalMode.Programmatic && run.EvaluationRunId is null)
        {
            // FR-015b. Without this, the harness's approval path would be
            // available to interactive runs and SC-001's separation of
            // interactive from programmatic approvals would be meaningless.
            throw new DecisionRefusedException(
                DecisionRefusalReason.ProgrammaticModeNotPermitted,
                "Programmatic approval is available only to evaluation runs (FR-015b).");
        }

        if (!DiffHash.Matches(proposal.DiffHash, echoedDiffHash))
        {
            throw new DecisionRefusedException(
                DecisionRefusalReason.HashMismatch,
                $"The decision names hash {echoedDiffHash} but the proposal is {proposal.DiffHash}. " +
                "The reviewer decided on different content from what is stored (FR-020a).");
        }

        var record = new ApprovalDecision
        {
            RunId = proposal.RunId,
            ProposalId = proposalId,
            Decision = decision,
            DiffHash = proposal.DiffHash,
            DecidedBy = decidedBy.Trim(),
            Mode = mode,
        };

        // The unique index refuses a second decision. Recording before updating
        // the proposal's status means a duplicate attempt fails without having
        // changed anything.
        await approvals.AddAsync(record, ct);

        await proposals.UpdateStatusAsync(
            proposalId,
            decision == ApprovalDecisionKind.Approve
                ? ProposalDecisionStatus.Approved
                : ProposalDecisionStatus.Rejected,
            ct);

        await events.RecordAsync(
            new RunEvent
            {
                RunId = proposal.RunId,
                EventType = RunEventType.ApprovalRecorded,
                ArgumentsSummary =
                    $$"""{"proposal_id":"{{proposalId}}","decision":"{{decision}}","by":"{{record.DecidedBy}}","mode":"{{mode}}"}""",
                Status = RunEventStatus.Succeeded,
            },
            ct);

        return record;
    }
}
