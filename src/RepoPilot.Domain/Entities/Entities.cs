using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Runs;

namespace RepoPilot.Domain.Entities;

/// <summary>Indexing lifecycle of a registered fixture.</summary>
public enum IndexingStatus
{
    NeverIndexed,
    Indexing,
    Indexed,
    Failed,
}

/// <summary>Whether a decision came from a person or the evaluation harness.</summary>
public enum ApprovalMode
{
    /// <summary>A person decided, through the review view.</summary>
    Interactive,

    /// <summary>
    /// The evaluation harness decided. Still a real recorded decision bound to
    /// the change hash, and still counted — but reported separately, so a
    /// programmatic approval is never presented as a human one (SC-001).
    /// </summary>
    Programmatic,
}

/// <summary>Approve or reject.</summary>
public enum ApprovalDecisionKind
{
    Approve,
    Reject,
}

/// <summary>Whether a proposal has been decided.</summary>
public enum ProposalDecisionStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>What a recorded run event describes.</summary>
public enum RunEventType
{
    StageChanged,
    StageTransitionRejected,
    ToolCall,
    PlanProduced,
    ProposalCreated,
    ApprovalRecorded,
    PatchApplied,
    TestsCompleted,
    RunFailed,
    RunEnded,
}

/// <summary>Outcome of a recorded action.</summary>
public enum RunEventStatus
{
    Succeeded,
    Failed,
}

/// <summary>Which mode an evaluation task result came from.</summary>
public enum EvaluationMode
{
    Baseline,
    ToolEnabled,
}

/// <summary>
/// A registered code repository the system is permitted to work against.
/// </summary>
public sealed class RepositoryFixture
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Stable key; must appear in the configured allowed set (FR-001).</summary>
    public required string Slug { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Path under the fixtures root. Read-only to every code path (FR-016a).</summary>
    public required string RootPath { get; set; }

    public IndexingStatus IndexingStatus { get; set; } = IndexingStatus.NeverIndexed;

    /// <summary>Null until the first successful atomic swap (FR-003a).</summary>
    public int? ActiveIndexVersion { get; set; }

    /// <summary>Pinned at first index; changing it forces a rebuild.</summary>
    public string? EmbeddingModelId { get; set; }

    public int? EmbeddingDimensions { get; set; }

    public DateTimeOffset? LastIndexedAt { get; set; }

    public int IncludedFileCount { get; set; }

    public int ExcludedFileCount { get; set; }

    /// <summary>Counts keyed by exclusion reason (FR-003, FR-003b).</summary>
    public Dictionary<string, int> ExclusionBreakdown { get; set; } = [];

    /// <summary>The committed <c>repopilot.fixture.json</c>, validated at registration.</summary>
    public required string TestConfigJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A searchable unit of repository content.</summary>
public sealed class IndexEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid RepositoryId { get; set; }

    /// <summary>Visible only while equal to the repository's active version.</summary>
    public int IndexVersion { get; set; }

    public required string RelativePath { get; set; }

    public int ChunkOrdinal { get; set; }

    public required string Content { get; set; }

    /// <summary>1-based, inclusive.</summary>
    public int StartLine { get; set; }

    /// <summary>1-based, inclusive.</summary>
    public int EndLine { get; set; }

    public string? Language { get; set; }

    public string[]? Symbols { get; set; }

    /// <summary>Stored as pgvector. Dimensions pinned per repository.</summary>
    public float[] Embedding { get; set; } = [];
}

/// <summary>One task execution against one repository.</summary>
public sealed class Run
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid RepositoryId { get; set; }

    public required string TaskDescription { get; set; }

    /// <summary>Set when started from the committed task set.</summary>
    public string? SeededTaskId { get; set; }

    public RunStage Stage { get; set; } = RunStage.Created;

    public TerminalOutcome? TerminalOutcome { get; set; }

    /// <summary>Drawn from the normative set; free text cannot be stored (FR-008c).</summary>
    public OutcomeReason? OutcomeReason { get; set; }

    /// <summary>The stage a non-success outcome occurred at (FR-030).</summary>
    public RunStage? FailureStage { get; set; }

    /// <summary>Capped at 2 (FR-012).</summary>
    public int RevisionAttempt { get; set; }

    /// <summary>False runs the retrieval-only baseline (FR-033).</summary>
    public bool ToolsEnabled { get; set; } = true;

    /// <summary>
    /// The allow-listed command this run is verified against, or null to use the
    /// fixture's default.
    /// <para>
    /// Set from the evaluation task's success command. It is stored on the run
    /// rather than resolved at test time so that the record says which command
    /// decided the run, and so a task definition changing later cannot rewrite
    /// what an earlier run was graded on (FR-031).
    /// </para>
    /// </summary>
    public string? VerifyCommandName { get; set; }

    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.Interactive;

    public Guid? EvaluationRunId { get; set; }

    /// <summary>The short human-readable plan, produced before any proposal (FR-010).</summary>
    public string? Plan { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>
/// A recorded action or stage change. This table alone must be enough to
/// reconstruct a completed run (FR-029, SC-008).
/// </summary>
public sealed class RunEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid RunId { get; set; }

    /// <summary>Monotonic per run; also the SSE frame id.</summary>
    public long Sequence { get; set; }

    public RunEventType EventType { get; set; }

    public string? ToolName { get; set; }

    /// <summary>Redacted and bounded; never full file contents (FR-027a).</summary>
    public string? ArgumentsSummary { get; set; }

    public RunEventStatus Status { get; set; } = RunEventStatus.Succeeded;

    public string? ErrorMessage { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedAt { get; set; }

    public int? DurationMs { get; set; }
}

/// <summary>A set of file modifications the agent proposes. Immutable once created.</summary>
public sealed class ChangeProposal
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid RunId { get; set; }

    public int RevisionAttempt { get; set; }

    /// <summary>Serialized <see cref="ProposalEntry"/> array.</summary>
    public required string EntriesJson { get; set; }

    /// <summary>Rendered for display only — never the hash input (FR-019a).</summary>
    public required string UnifiedDiff { get; set; }

    /// <summary>Denormalized for the review list (FR-011, SC-004).</summary>
    public string[] AffectedPaths { get; set; } = [];

    /// <summary>Canonical change-content hash (FR-019a).</summary>
    public required string DiffHash { get; set; }

    public ProposalDecisionStatus DecisionStatus { get; set; } = ProposalDecisionStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A decision on one proposal. Append-only: there is no update or delete path
/// (FR-019b).
/// </summary>
public sealed class ApprovalDecision
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid RunId { get; set; }

    /// <summary>Unique — the enforcement point for "at most one decision" (FR-018).</summary>
    public Guid ProposalId { get; set; }

    public ApprovalDecisionKind Decision { get; set; }

    /// <summary>Copied from the proposal at decision time (FR-020a).</summary>
    public required string DiffHash { get; set; }

    /// <summary>Attributable, not verified — authentication is out of scope (FR-015a).</summary>
    public required string DecidedBy { get; set; }

    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;

    public ApprovalMode Mode { get; set; } = ApprovalMode.Interactive;
}

/// <summary>A disposable copy of a fixture created for one run.</summary>
public sealed class WorkingCopy
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Unique — one working copy per run.</summary>
    public Guid RunId { get; set; }

    public required string AbsolutePath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when the directory is removed (FR-026a, SC-012).</summary>
    public DateTimeOffset? DestroyedAt { get; set; }
}

/// <summary>Outcome of one sandboxed execution.</summary>
public sealed class TestResult
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid RunId { get; set; }

    public int RevisionAttempt { get; set; }

    /// <summary>A name from the fixture allow-list — never a shell string (FR-022).</summary>
    public required string CommandName { get; set; }

    public bool Passed { get; set; }

    /// <summary>Null when terminated by timeout.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Redacted before storage, display, or model context (FR-025b).</summary>
    public required string Output { get; set; }

    public int DurationMs { get; set; }

    public bool TimedOut { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Metrics from one evaluation run over the committed task set.</summary>
public sealed class EvaluationRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedAt { get; set; }

    public int TaskCount { get; set; }

    public decimal? RecallAt5 { get; set; }

    public decimal? CompletionRateToolEnabled { get; set; }

    public decimal? CompletionRateBaseline { get; set; }

    /// <summary>Below 1.0 sets <see cref="Flagged"/> and fails the evaluation (FR-034).</summary>
    public decimal? ApprovalCoverage { get; set; }

    public decimal? ToolSuccessRate { get; set; }

    public decimal? AvgToolCallsPerCompletedTask { get; set; }

    public int? P50LatencyMs { get; set; }

    public int? P95LatencyMs { get; set; }

    public bool Flagged { get; set; }
}

/// <summary>One task's result within an evaluation run.</summary>
public sealed class EvaluationTaskResult
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid EvaluationRunId { get; set; }

    public required string TaskId { get; set; }

    public EvaluationMode Mode { get; set; }

    public Guid RunId { get; set; }

    public bool RelevantFileInTop5 { get; set; }

    public bool SuccessConditionMet { get; set; }

    public int ToolCallCount { get; set; }

    public int DurationMs { get; set; }
}
