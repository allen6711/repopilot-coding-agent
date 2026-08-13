using System.Text.Json;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Runs;

namespace RepoPilot.Api.Contracts;

/// <summary>
/// The wire shapes from <c>contracts/rest-api.yaml</c>.
/// <para>
/// Written out rather than serialized from the entities directly. Two reasons:
/// the contract's enum values are snake_case while the domain's are PascalCase,
/// and returning entities would make every field addition an unannounced API
/// change. The mapping being explicit is what makes the contract test able to
/// fail when the two drift.
/// </para>
/// </summary>
public static class WireNames
{
    /// <summary>Maps a stage to its contract value.</summary>
    public static string Stage(RunStage stage) => stage switch
    {
        RunStage.Created => "created",
        RunStage.Retrieving => "retrieving",
        RunStage.Planning => "planning",
        RunStage.Proposing => "proposing",
        RunStage.AwaitingApproval => "awaiting_approval",
        RunStage.Applying => "applying",
        RunStage.Testing => "testing",
        RunStage.Succeeded => "succeeded",
        RunStage.Failed => "failed",
        RunStage.Rejected => "rejected",
        RunStage.Cancelled => "cancelled",
        RunStage.NoChange => "no_change",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
    };

    /// <summary>Maps a terminal outcome to its contract value.</summary>
    public static string? Outcome(TerminalOutcome? outcome) => outcome switch
    {
        null => null,
        TerminalOutcome.Succeeded => "succeeded",
        TerminalOutcome.Failed => "failed",
        TerminalOutcome.Rejected => "rejected",
        TerminalOutcome.Cancelled => "cancelled",
        TerminalOutcome.NoChange => "no_change",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    /// <summary>Maps an outcome reason to its contract value.</summary>
    public static string? Reason(OutcomeReason? reason) => reason switch
    {
        null => null,
        OutcomeReason.Completed => "completed",
        OutcomeReason.RevisionLimitReached => "revision_limit_reached",
        OutcomeReason.DeliberateNoOp => "deliberate_no_op",
        OutcomeReason.InsufficientContext => "insufficient_context",
        OutcomeReason.ProposalRejected => "proposal_rejected",
        OutcomeReason.AbandonedByUser => "abandoned_by_user",
        OutcomeReason.ProviderUnavailable => "provider_unavailable",
        OutcomeReason.IsolatedEnvUnavailable => "isolated_env_unavailable",
        OutcomeReason.IsolatedEnvNotTerminable => "isolated_env_not_terminable",
        OutcomeReason.ServiceRestarted => "service_restarted",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>Parses a contract stage value, for query filtering.</summary>
    public static RunStage? ParseStage(string? value) => value switch
    {
        null or "" => null,
        "created" => RunStage.Created,
        "retrieving" => RunStage.Retrieving,
        "planning" => RunStage.Planning,
        "proposing" => RunStage.Proposing,
        "awaiting_approval" => RunStage.AwaitingApproval,
        "applying" => RunStage.Applying,
        "testing" => RunStage.Testing,
        "succeeded" => RunStage.Succeeded,
        "failed" => RunStage.Failed,
        "rejected" => RunStage.Rejected,
        "cancelled" => RunStage.Cancelled,
        "no_change" => RunStage.NoChange,
        _ => throw new FormatException($"'{value}' is not a run stage."),
    };
}

/// <summary>A run, as the contract's <c>Run</c> schema.</summary>
public sealed record RunDto(
    Guid Id,
    Guid RepositoryId,
    string TaskDescription,
    string Stage,
    string? TerminalOutcome,
    string? OutcomeReason,
    string? FailureStage,
    int RevisionAttempt,
    bool ToolsEnabled,
    string? Plan,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt)
{
    public static RunDto From(Run run) => new(
        run.Id,
        run.RepositoryId,
        run.TaskDescription,
        WireNames.Stage(run.Stage),
        WireNames.Outcome(run.TerminalOutcome),
        WireNames.Reason(run.OutcomeReason),
        run.FailureStage is null ? null : WireNames.Stage(run.FailureStage.Value),
        run.RevisionAttempt,
        run.ToolsEnabled,
        run.Plan,
        run.CreatedAt,
        run.StartedAt,
        run.EndedAt);
}

/// <summary>One file in a proposal. Content is deliberately absent — the diff carries it.</summary>
public sealed record ProposalEntryDto(string Path, string Operation);

/// <summary>A proposal, as the contract's <c>ChangeProposal</c> schema.</summary>
public sealed record ChangeProposalDto(
    Guid Id,
    Guid RunId,
    int RevisionAttempt,
    IReadOnlyList<string> AffectedPaths,
    IReadOnlyList<ProposalEntryDto> Entries,
    string UnifiedDiff,
    string DiffHash,
    string DecisionStatus,
    DateTimeOffset CreatedAt)
{
    public static ChangeProposalDto From(ChangeProposal proposal)
    {
        var entries = JsonSerializer.Deserialize<List<ProposalEntry>>(proposal.EntriesJson) ?? [];

        return new ChangeProposalDto(
            proposal.Id,
            proposal.RunId,
            proposal.RevisionAttempt,
            proposal.AffectedPaths,
            [.. entries.Select(e => new ProposalEntryDto(
                e.Path,
                e.Operation == ProposalOperation.Create ? "create" : "modify"))],
            proposal.UnifiedDiff,
            proposal.DiffHash,
            proposal.DecisionStatus switch
            {
                ProposalDecisionStatus.Approved => "approved",
                ProposalDecisionStatus.Rejected => "rejected",
                _ => "pending",
            },
            proposal.CreatedAt);
    }
}

/// <summary>A decision, as the contract's <c>ApprovalDecision</c> schema.</summary>
public sealed record ApprovalDecisionDto(
    Guid Id,
    Guid RunId,
    Guid ProposalId,
    string Decision,
    string DiffHash,
    string DecidedBy,
    DateTimeOffset DecidedAt,
    string Mode)
{
    public static ApprovalDecisionDto From(ApprovalDecision decision) => new(
        decision.Id,
        decision.RunId,
        decision.ProposalId,
        decision.Decision == ApprovalDecisionKind.Approve ? "approve" : "reject",
        decision.DiffHash,
        decision.DecidedBy,
        decision.DecidedAt,
        decision.Mode == ApprovalMode.Programmatic ? "programmatic" : "interactive");
}

/// <summary>A test result, as the contract's <c>TestResult</c> schema.</summary>
public sealed record TestResultDto(
    Guid Id,
    Guid RunId,
    int RevisionAttempt,
    string CommandName,
    bool Passed,
    int? ExitCode,
    string Output,
    int DurationMs,
    bool TimedOut)
{
    public static TestResultDto From(TestResult result) => new(
        result.Id,
        result.RunId,
        result.RevisionAttempt,
        result.CommandName,
        result.Passed,
        result.ExitCode,
        result.Output,
        result.DurationMs,
        result.TimedOut);
}

/// <summary>The body of <c>POST /api/runs</c>.</summary>
public sealed record CreateRunRequest(
    Guid RepositoryId,
    string? TaskDescription,
    string? SeededTaskId,
    bool? ToolsEnabled);

/// <summary>The body of <c>POST /api/runs/{runId}/approval</c>.</summary>
public sealed record DecideProposalRequest(Guid ProposalId, string Decision, string DiffHash);
