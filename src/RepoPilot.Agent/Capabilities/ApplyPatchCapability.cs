using System.Text.Json;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Runs;
using RepoPilot.Infrastructure.Workspace;

namespace RepoPilot.Agent.Capabilities;

/// <summary>
/// Why an apply was refused.
/// </summary>
public enum ApplyRefusalReason
{
    ApprovalRequired,
    DiffHashMismatch,
    IllegalStage,
    ProposalNotFound,
}

/// <summary>
/// Raised when an apply is refused. Distinct from a write failure: this means
/// the write was never attempted.
/// </summary>
public sealed class ApplyRefusedException(ApplyRefusalReason reason, string detail)
    : InvalidOperationException(detail)
{
    public ApplyRefusalReason Reason { get; } = reason;
}

/// <summary>
/// Applies an approved change to the run's working copy (permission class:
/// write with approval).
/// <para>
/// This is the only code path in the system that writes to a repository. Its
/// preconditions run in a fixed order, and every one of them is fatal:
/// </para>
/// <list type="number">
///   <item>An approval decision exists for this proposal, and it is an approval
///   rather than a rejection (FR-014).</item>
///   <item>The change-content hash recomputed from the stored proposal equals
///   the hash the approver decided on (FR-020).</item>
///   <item>The run is in the applying stage — reached only through an approval,
///   as the state machine's only inbound transition (Principle IV).</item>
///   <item>Every path resolves inside the working copy, checked again at write
///   time rather than trusted from validation (FR-016, FR-024).</item>
/// </list>
/// <para>
/// The order matters for what a refusal reports. Checking the hash before the
/// approval would describe an unapproved change as "tampered with", which sends
/// a reader looking for an attack rather than a missing decision.
/// </para>
/// </summary>
public sealed class ApplyPatchCapability(
    IProposalStore proposals,
    IApprovalStore approvals,
    IRunStore runs,
    WorkingCopyManager workingCopies) : ICapability
{
    public string Name => "apply_patch";

    public async Task<CapabilityResult> InvokeAsync(
        CapabilityContext context, string argumentsJson, CancellationToken ct = default)
    {
        var args = Arguments.Parse(argumentsJson);
        var proposalId = Guid.Parse(Arguments.RequireString(args, "proposal_id"));

        var proposal = await proposals.FindAsync(proposalId, ct)
            ?? throw new ApplyRefusedException(
                ApplyRefusalReason.ProposalNotFound, $"Proposal {proposalId} does not exist.");

        // 1. An approval must exist, and be an approval.
        var decision = await approvals.FindByProposalAsync(proposalId, ct);
        if (decision is null)
        {
            throw new ApplyRefusedException(
                ApplyRefusalReason.ApprovalRequired,
                $"Proposal {proposalId} has no recorded decision. No file is modified until a " +
                "human approval for that specific change has been recorded (FR-014).");
        }

        if (decision.Decision != ApprovalDecisionKind.Approve)
        {
            throw new ApplyRefusedException(
                ApplyRefusalReason.ApprovalRequired,
                $"Proposal {proposalId} was rejected. A rejected proposal leaves the workspace " +
                "unchanged (FR-017).");
        }

        // 2. The content decided on must be the content about to be written.
        var entries = JsonSerializer.Deserialize<List<ProposalEntry>>(proposal.EntriesJson)
            ?? throw new InvalidOperationException($"Proposal {proposalId} has unreadable entries.");

        var recomputed = DiffHash.Compute(entries);

        if (!DiffHash.Matches(decision.DiffHash, recomputed))
        {
            throw new ApplyRefusedException(
                ApplyRefusalReason.DiffHashMismatch,
                $"Proposal {proposalId} hashes to {recomputed} but was approved as " +
                $"{decision.DiffHash}. The content differs from what was shown to the approver " +
                "(FR-020).");
        }

        // 3. The stage is reachable only through an approval.
        var run = await runs.FindAsync(context.RunId, ct)
            ?? throw new InvalidOperationException($"Run {context.RunId} not found.");

        if (run.Stage != RunStage.Applying)
        {
            throw new ApplyRefusedException(
                ApplyRefusalReason.IllegalStage,
                $"Run {context.RunId} is in {run.Stage}, not Applying.");
        }

        // 4. Paths are re-checked at write time. Validation happened when the
        // proposal was created, possibly long before; the guard is cheap and the
        // consequence of trusting a stale check is a write outside the workspace.
        workingCopies.Apply(context.Workspace, entries);

        var applied = JsonSerializer.Serialize(new
        {
            applied_paths = proposal.AffectedPaths,
            applied_at = DateTimeOffset.UtcNow,
        });

        // Charged as zero: this output never enters model context, and charging
        // it would let a large proposal consume the retrieval budget.
        return new CapabilityResult(
            applied, 0, $$"""{"proposal_id":"{{proposalId}}","files":{{entries.Count}}}""");
    }
}
