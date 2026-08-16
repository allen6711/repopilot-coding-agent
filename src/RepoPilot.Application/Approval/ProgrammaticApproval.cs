using RepoPilot.Application.Ports;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;

namespace RepoPilot.Application.Approval;

/// <summary>
/// The evaluation harness's approval path (FR-015b).
/// <para>
/// It exists because an evaluation of thirty tasks cannot wait for thirty human
/// decisions, and it is written the way it is because the alternative — letting
/// the harness apply changes without a decision — would make SC-001's claim
/// ("100% of applied changes have a recorded approval") true only of interactive
/// runs, which is not what it says.
/// </para>
/// <para>
/// So this is not a bypass. It goes through <see cref="DecideProposalUseCase"/>
/// like every other decision, writes a real, immutable decision record bound to
/// the diff hash, and marks that record <see cref="ApprovalMode.Programmatic"/>
/// so no report can present it as a human one. The only thing it removes is the
/// person, and it removes them only where there was never going to be one.
/// </para>
/// </summary>
public sealed class ProgrammaticApproval(
    DecideProposalUseCase decide, IProposalStore proposals, IRunStore runs)
{
    /// <summary>
    /// The actor recorded on a programmatic decision.
    /// <para>
    /// A fixed, obviously non-human identity. Borrowing the operator's name would
    /// make the audit trail claim a person decided, and SC-015's requirement that
    /// every decision carry an actor is satisfied by an honest one, not by a
    /// plausible one.
    /// </para>
    /// </summary>
    public const string Actor = "evaluation-harness";

    /// <summary>
    /// Approves a proposal on behalf of an evaluation run.
    /// </summary>
    /// <param name="proposalId">The proposal to approve.</param>
    /// <param name="diffHash">
    /// The hash of the proposal as stored. Echoed back exactly as an interactive
    /// approver's would be, so the same mismatch check applies (FR-020a).
    /// </param>
    /// <exception cref="DecisionRefusedException">
    /// The run is interactive. Checked here as well as in the use case: this type
    /// is the one thing that could hand an interactive run an approval nobody
    /// made, so it refuses before it asks rather than relying on being refused.
    /// </exception>
    public async Task<ApprovalDecision> ApproveAsync(
        Guid proposalId, string diffHash, CancellationToken ct = default)
    {
        return await DecideAsync(proposalId, ApprovalDecisionKind.Approve, diffHash, ct);
    }

    /// <summary>
    /// Rejects a proposal on behalf of an evaluation run.
    /// <para>
    /// Present so that an evaluation condition which should not apply a change —
    /// a proposal the harness declines on a policy the evaluation is testing —
    /// still leaves a decision record rather than an undecided proposal. An
    /// undecided proposal at the end of an evaluation is indistinguishable from a
    /// crash.
    /// </para>
    /// </summary>
    public async Task<ApprovalDecision> RejectAsync(
        Guid proposalId, string diffHash, CancellationToken ct = default)
    {
        return await DecideAsync(proposalId, ApprovalDecisionKind.Reject, diffHash, ct);
    }

    private async Task<ApprovalDecision> DecideAsync(
        Guid proposalId,
        ApprovalDecisionKind decision,
        string diffHash,
        CancellationToken ct)
    {
        var run = await FindRunForProposalAsync(proposalId, ct);

        if (run?.EvaluationRunId is null)
        {
            throw new DecisionRefusedException(
                DecisionRefusalReason.ProgrammaticModeNotPermitted,
                "Programmatic approval is available only to evaluation runs (FR-015b).");
        }

        return await decide.DecideAsync(
            proposalId, decision, diffHash, Actor, ApprovalMode.Programmatic, ct);
    }

    private async Task<Domain.Entities.Run?> FindRunForProposalAsync(
        Guid proposalId, CancellationToken ct)
    {
        // Deliberately not a join through the proposal store: this needs the run,
        // and the run is what carries the evaluation binding. Resolving the run
        // id from the proposal and then reading the run keeps the check reading
        // against the same row the use case will read.
        var proposal = await proposals.FindAsync(proposalId, ct);

        return proposal is null ? null : await runs.FindAsync(proposal.RunId, ct);
    }
}
