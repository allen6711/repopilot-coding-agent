using Microsoft.EntityFrameworkCore;
using Npgsql;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;

namespace RepoPilot.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="IRepositoryFixtureStore" />
public sealed class EfRepositoryFixtureStore(RepoPilotDbContext db) : IRepositoryFixtureStore
{
    public Task<RepositoryFixture?> FindBySlugAsync(string slug, CancellationToken ct = default) =>
        db.Repositories.FirstOrDefaultAsync(r => r.Slug == slug, ct);

    public Task<RepositoryFixture?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<RepositoryFixture>> ListAsync(CancellationToken ct = default) =>
        await db.Repositories.OrderBy(r => r.Slug).ToListAsync(ct);

    public async Task AddAsync(RepositoryFixture fixture, CancellationToken ct = default)
    {
        db.Repositories.Add(fixture);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(RepositoryFixture fixture, CancellationToken ct = default)
    {
        db.Repositories.Update(fixture);
        await db.SaveChangesAsync(ct);
    }
}

/// <inheritdoc cref="IRunStore" />
public sealed class EfRunStore(RepoPilotDbContext db) : IRunStore
{
    private static readonly RunStage[] TerminalStages =
    [
        RunStage.Succeeded, RunStage.Failed, RunStage.Rejected,
        RunStage.Cancelled, RunStage.NoChange,
    ];

    public Task<Run?> FindAsync(Guid id, CancellationToken ct = default) =>
        db.Runs.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<Run>> ListAsync(
        Guid? repositoryId = null,
        RunStage? stage = null,
        CancellationToken ct = default)
    {
        var query = db.Runs.AsQueryable();

        if (repositoryId is not null)
        {
            query = query.Where(r => r.RepositoryId == repositoryId);
        }

        if (stage is not null)
        {
            query = query.Where(r => r.Stage == stage);
        }

        return await query.OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
    }

    public async Task AddAsync(Run run, CancellationToken ct = default)
    {
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Run run, CancellationToken ct = default)
    {
        db.Runs.Update(run);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Run>> ListNonTerminalAsync(CancellationToken ct = default) =>
        await db.Runs.Where(r => !TerminalStages.Contains(r.Stage)).ToListAsync(ct);
}

/// <inheritdoc cref="IRunEventStore" />
public sealed class EfRunEventStore(RepoPilotDbContext db) : IRunEventStore
{
    public async Task<RunEvent> AppendAsync(RunEvent runEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runEvent);

        // The sequence is the SSE frame id and the replay cursor, so it must be
        // gap-free per run.
        //
        // Read-the-max-then-insert with optimistic retry was tried first and is
        // not good enough: under concurrent capability invocations the retries
        // are exhausted and the append fails outright. A transaction-scoped
        // advisory lock keyed on the run serialises appends for that run only —
        // different runs never contend — and the lock releases on commit or
        // rollback, so a crash mid-append cannot strand it.
        //
        // The unique index on (RunId, Sequence) stays as the backstop: it is
        // what guarantees correctness if this code path is ever bypassed.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtext({0}))",
            [runEvent.RunId.ToString()],
            ct);

        var current = await db.RunEvents
            .Where(e => e.RunId == runEvent.RunId)
            .MaxAsync(e => (long?)e.Sequence, ct) ?? 0;

        runEvent.Sequence = current + 1;
        db.RunEvents.Add(runEvent);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return runEvent;
    }

    public async Task<IReadOnlyList<RunEvent>> ListAsync(
        Guid runId,
        long afterSequence = 0,
        CancellationToken ct = default) =>
        await db.RunEvents
            .Where(e => e.RunId == runId && e.Sequence > afterSequence)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

    internal static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

/// <inheritdoc cref="IProposalStore" />
public sealed class EfProposalStore(RepoPilotDbContext db) : IProposalStore
{
    public Task<ChangeProposal?> FindAsync(Guid id, CancellationToken ct = default) =>
        db.ChangeProposals.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<ChangeProposal?> FindCurrentForRunAsync(Guid runId, CancellationToken ct = default) =>
        db.ChangeProposals
            .Where(p => p.RunId == runId)
            .OrderByDescending(p => p.RevisionAttempt)
            .ThenByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ChangeProposal>> ListForRunAsync(
        Guid runId, CancellationToken ct = default) =>
        await db.ChangeProposals
            .Where(p => p.RunId == runId)
            .OrderBy(p => p.RevisionAttempt)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(ChangeProposal proposal, CancellationToken ct = default)
    {
        db.ChangeProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateStatusAsync(
        Guid proposalId,
        ProposalDecisionStatus status,
        CancellationToken ct = default)
    {
        var proposal = await db.ChangeProposals.FirstOrDefaultAsync(p => p.Id == proposalId, ct)
            ?? throw new InvalidOperationException($"Proposal {proposalId} not found.");

        proposal.DecisionStatus = status;
        await db.SaveChangesAsync(ct);
    }
}

/// <inheritdoc cref="IApprovalStore" />
public sealed class EfApprovalStore(RepoPilotDbContext db) : IApprovalStore
{
    public async Task AddAsync(ApprovalDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        db.ApprovalDecisions.Add(decision);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (EfRunEventStore.IsUniqueViolation(ex))
        {
            // FR-018. Translating the database's refusal into a domain exception
            // keeps the guarantee where it belongs — in the schema — while still
            // giving callers something meaningful to catch.
            db.Entry(decision).State = EntityState.Detached;
            throw new ProposalAlreadyDecidedException(decision.ProposalId);
        }
    }

    public Task<ApprovalDecision?> FindByProposalAsync(Guid proposalId, CancellationToken ct = default) =>
        db.ApprovalDecisions.FirstOrDefaultAsync(a => a.ProposalId == proposalId, ct);
}

/// <inheritdoc cref="IWorkingCopyStore" />
public sealed class EfWorkingCopyStore(RepoPilotDbContext db) : IWorkingCopyStore
{
    public Task<WorkingCopy?> FindByRunAsync(Guid runId, CancellationToken ct = default) =>
        db.WorkingCopies.FirstOrDefaultAsync(w => w.RunId == runId, ct);

    public async Task AddAsync(WorkingCopy copy, CancellationToken ct = default)
    {
        db.WorkingCopies.Add(copy);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkDestroyedAsync(Guid runId, CancellationToken ct = default)
    {
        var copy = await db.WorkingCopies.FirstOrDefaultAsync(w => w.RunId == runId, ct);
        if (copy is null)
        {
            return;
        }

        copy.DestroyedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WorkingCopy>> ListLiveAsync(CancellationToken ct = default) =>
        await db.WorkingCopies.Where(w => w.DestroyedAt == null).ToListAsync(ct);
}

/// <inheritdoc cref="ITestResultStore" />
public sealed class EfTestResultStore(RepoPilotDbContext db) : ITestResultStore
{
    public async Task AddAsync(TestResult result, CancellationToken ct = default)
    {
        db.TestResults.Add(result);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TestResult>> ListForRunAsync(
        Guid runId,
        CancellationToken ct = default) =>
        await db.TestResults
            .Where(t => t.RunId == runId)
            .OrderBy(t => t.RevisionAttempt)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);
}
