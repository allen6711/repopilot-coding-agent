using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;

namespace RepoPilot.Application.Ports;

/// <summary>Registered repository fixtures.</summary>
public interface IRepositoryFixtureStore
{
    Task<RepositoryFixture?> FindBySlugAsync(string slug, CancellationToken ct = default);

    Task<RepositoryFixture?> FindByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryFixture>> ListAsync(CancellationToken ct = default);

    Task AddAsync(RepositoryFixture fixture, CancellationToken ct = default);

    Task UpdateAsync(RepositoryFixture fixture, CancellationToken ct = default);
}

/// <summary>Runs and their stage transitions.</summary>
public interface IRunStore
{
    Task<Run?> FindAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Run>> ListAsync(
        Guid? repositoryId = null,
        RunStage? stage = null,
        CancellationToken ct = default);

    Task AddAsync(Run run, CancellationToken ct = default);

    Task UpdateAsync(Run run, CancellationToken ct = default);

    /// <summary>
    /// Runs left non-terminal by a previous process. Startup fails these and
    /// sweeps their resources (FR-030a).
    /// </summary>
    Task<IReadOnlyList<Run>> ListNonTerminalAsync(CancellationToken ct = default);
}

/// <summary>
/// Recorded run events. Append-only (FR-019b), and written before publication so
/// a live subscriber can never see an event a later replay would miss.
/// </summary>
public interface IRunEventStore
{
    /// <summary>
    /// Appends an event, assigning the next sequence for the run.
    /// </summary>
    /// <returns>The persisted event, with its sequence.</returns>
    Task<RunEvent> AppendAsync(RunEvent runEvent, CancellationToken ct = default);

    /// <summary>Events for a run in sequence order, optionally after a cursor.</summary>
    Task<IReadOnlyList<RunEvent>> ListAsync(
        Guid runId,
        long afterSequence = 0,
        CancellationToken ct = default);
}

/// <summary>Change proposals and the decisions made on them.</summary>
public interface IProposalStore
{
    Task<ChangeProposal?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>The most recent proposal for a run, or null when none exists.</summary>
    Task<ChangeProposal?> FindCurrentForRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Every proposal a run produced, oldest first.
    /// <para>
    /// A superseded proposal stays readable. Each one was separately approved,
    /// and the record of what a reviewer authorised has to outlive the revision
    /// that replaced it (FR-019b).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ChangeProposal>> ListForRunAsync(Guid runId, CancellationToken ct = default);

    Task AddAsync(ChangeProposal proposal, CancellationToken ct = default);

    Task UpdateStatusAsync(
        Guid proposalId,
        ProposalDecisionStatus status,
        CancellationToken ct = default);
}

/// <summary>
/// Approval decisions. There is no update or delete method, and that absence is
/// deliberate: the interface should not offer an operation the audit trail must
/// never permit (FR-019b).
/// </summary>
public interface IApprovalStore
{
    /// <summary>
    /// Records a decision.
    /// </summary>
    /// <exception cref="ProposalAlreadyDecidedException">
    /// The proposal already has a decision (FR-018). The uniqueness is enforced
    /// by the database, so this cannot be lost to a race.
    /// </exception>
    Task AddAsync(ApprovalDecision decision, CancellationToken ct = default);

    Task<ApprovalDecision?> FindByProposalAsync(Guid proposalId, CancellationToken ct = default);
}

/// <summary>Raised when a second decision is attempted on one proposal.</summary>
public sealed class ProposalAlreadyDecidedException(Guid proposalId)
    : InvalidOperationException(
        $"Proposal {proposalId} has already been decided. A change is applied at most once (FR-018).")
{
    public Guid ProposalId { get; } = proposalId;
}

/// <summary>Disposable working copies.</summary>
public interface IWorkingCopyStore
{
    Task<WorkingCopy?> FindByRunAsync(Guid runId, CancellationToken ct = default);

    Task AddAsync(WorkingCopy copy, CancellationToken ct = default);

    Task MarkDestroyedAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Copies recorded as still present. Startup compares this against what is
    /// on disk to find orphans (SC-012).
    /// </summary>
    Task<IReadOnlyList<WorkingCopy>> ListLiveAsync(CancellationToken ct = default);
}

/// <summary>Sandboxed test results.</summary>
public interface ITestResultStore
{
    Task AddAsync(TestResult result, CancellationToken ct = default);

    Task<IReadOnlyList<TestResult>> ListForRunAsync(Guid runId, CancellationToken ct = default);
}
