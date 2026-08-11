using Microsoft.EntityFrameworkCore;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Persistence;

/// <summary>
/// FR-019b: approval and rejection records are append-only and outlive their run.
/// <para>
/// An approval record is the evidence that a change was authorised. Editing one
/// after the fact would mean the audit trail describes a decision nobody made,
/// so correcting a mistake has to mean recording a new fact rather than
/// rewriting the old one.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuditImmutabilityTests(PostgresFixture postgres)
{
    private const string ValidHash =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static async Task<(Guid RunId, Guid ProposalId, Guid DecisionId)> SeedDecidedAsync(
        RepoPilotDbContext db)
    {
        var repository = new RepositoryFixture
        {
            Slug = "fixture-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Fixture",
            RootPath = "evals/fixtures/sample",
            TestConfigJson = """{"slug":"sample"}""",
        };
        db.Repositories.Add(repository);

        var run = new Run
        {
            RepositoryId = repository.Id,
            TaskDescription = "Fix the null guard.",
            Stage = RunStage.AwaitingApproval,
        };
        db.Runs.Add(run);

        var proposal = new ChangeProposal
        {
            RunId = run.Id,
            EntriesJson = """[{"Path":"src/A.cs"}]""",
            UnifiedDiff = "diff",
            AffectedPaths = ["src/A.cs"],
            DiffHash = ValidHash,
        };
        db.ChangeProposals.Add(proposal);

        var decision = new ApprovalDecision
        {
            RunId = run.Id,
            ProposalId = proposal.Id,
            Decision = ApprovalDecisionKind.Approve,
            DiffHash = ValidHash,
            DecidedBy = "reviewer@example.test",
        };
        db.ApprovalDecisions.Add(decision);

        await db.SaveChangesAsync();
        return (run.Id, proposal.Id, decision.Id);
    }

    [RequiresDockerFact]
    public async Task ADecisionCannotBeUpdated()
    {
        await using var db = postgres.CreateContext();
        var (_, _, decisionId) = await SeedDecidedAsync(db);

        var decision = await db.ApprovalDecisions.FirstAsync(a => a.Id == decisionId);
        decision.Decision = ApprovalDecisionKind.Reject;

        var ex = await Assert.ThrowsAsync<AuditRecordImmutableException>(
            () => db.SaveChangesAsync());

        Assert.Equal(nameof(ApprovalDecision), ex.EntityType);
        Assert.Equal("update", ex.Operation);
    }

    [RequiresDockerFact]
    public async Task ADecisionCannotBeDeleted()
    {
        await using var db = postgres.CreateContext();
        var (_, _, decisionId) = await SeedDecidedAsync(db);

        var decision = await db.ApprovalDecisions.FirstAsync(a => a.Id == decisionId);
        db.ApprovalDecisions.Remove(decision);

        var ex = await Assert.ThrowsAsync<AuditRecordImmutableException>(
            () => db.SaveChangesAsync());

        Assert.Equal("delete", ex.Operation);
    }

    [RequiresDockerFact]
    public async Task TheActorOnADecisionCannotBeRewritten()
    {
        // Attribution is the whole value of the record while authentication is
        // out of scope; a rewritable actor field would make it worthless.
        await using var db = postgres.CreateContext();
        var (_, _, decisionId) = await SeedDecidedAsync(db);

        var decision = await db.ApprovalDecisions.FirstAsync(a => a.Id == decisionId);
        decision.DecidedBy = "someone.else@example.test";

        await Assert.ThrowsAsync<AuditRecordImmutableException>(() => db.SaveChangesAsync());
    }

    [RequiresDockerFact]
    public async Task ARecordedEventCannotBeUpdatedOrDeleted()
    {
        // SC-008 reconstructs a completed run from recorded events alone, which
        // only holds if the events cannot be edited afterwards.
        await using var db = postgres.CreateContext();
        var (runId, _, _) = await SeedDecidedAsync(db);

        var recorded = new RunEvent
        {
            RunId = runId,
            Sequence = 1,
            EventType = RunEventType.StageChanged,
            Status = RunEventStatus.Succeeded,
        };
        db.RunEvents.Add(recorded);
        await db.SaveChangesAsync();

        recorded.Status = RunEventStatus.Failed;
        await Assert.ThrowsAsync<AuditRecordImmutableException>(() => db.SaveChangesAsync());

        db.Entry(recorded).State = EntityState.Unchanged;
        db.RunEvents.Remove(recorded);
        await Assert.ThrowsAsync<AuditRecordImmutableException>(() => db.SaveChangesAsync());
    }

    [RequiresDockerFact]
    public async Task DecisionsOutliveTheirProposal()
    {
        // The foreign key from a decision to its proposal is Restrict, not
        // Cascade: deleting a proposal must not be able to take the evidence
        // that it was approved with it.
        await using var db = postgres.CreateContext();
        var (_, proposalId, _) = await SeedDecidedAsync(db);

        var proposal = await db.ChangeProposals.FirstAsync(p => p.Id == proposalId);
        db.ChangeProposals.Remove(proposal);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [RequiresDockerFact]
    public async Task InsertingANewDecisionRecordIsStillPermitted()
    {
        // Append-only means append is allowed. A guard that blocked inserts too
        // would be trivially "correct" and useless.
        await using var db = postgres.CreateContext();
        var (runId, _, _) = await SeedDecidedAsync(db);

        var secondProposal = new ChangeProposal
        {
            RunId = runId,
            RevisionAttempt = 1,
            EntriesJson = """[{"Path":"src/B.cs"}]""",
            UnifiedDiff = "diff",
            AffectedPaths = ["src/B.cs"],
            DiffHash = ValidHash,
        };
        db.ChangeProposals.Add(secondProposal);
        await db.SaveChangesAsync();

        db.ApprovalDecisions.Add(new ApprovalDecision
        {
            RunId = runId,
            ProposalId = secondProposal.Id,
            Decision = ApprovalDecisionKind.Approve,
            DiffHash = ValidHash,
            DecidedBy = "reviewer@example.test",
        });

        await db.SaveChangesAsync();
        Assert.Equal(2, await db.ApprovalDecisions.CountAsync(a => a.RunId == runId));
    }
}
