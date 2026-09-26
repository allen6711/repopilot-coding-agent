using Microsoft.EntityFrameworkCore;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Persistence;

/// <summary>
/// FR-018 and SC-015, verified against the database rather than against a
/// service check.
/// <para>
/// The distinction matters. A service that queries for an existing decision and
/// then inserts has a race window between the two; a unique index does not. These
/// tests assert the guarantee lives in the schema, so no future code path — or
/// concurrent request — can get around it.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ApprovalConstraintTests(PostgresFixture postgres)
{
    private const string ValidHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static async Task<(Guid RunId, Guid ProposalId)> SeedAsync(RepoPilotDbContext db)
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
            RevisionAttempt = 0,
            EntriesJson = """[{"Path":"src/A.cs","Operation":"Modify","NewContent":"x"}]""",
            UnifiedDiff = "--- a/src/A.cs\n+++ b/src/A.cs\n",
            AffectedPaths = ["src/A.cs"],
            DiffHash = ValidHash,
        };
        db.ChangeProposals.Add(proposal);

        await db.SaveChangesAsync();
        return (run.Id, proposal.Id);
    }

    private static ApprovalDecision Decision(
        Guid runId,
        Guid proposalId,
        ApprovalDecisionKind kind = ApprovalDecisionKind.Approve,
        string decidedBy = "reviewer@example.test",
        string hash = ValidHash) =>
        new()
        {
            RunId = runId,
            ProposalId = proposalId,
            Decision = kind,
            DiffHash = hash,
            DecidedBy = decidedBy,
        };

    [RequiresDockerFact]
    public async Task ASecondDecisionOnTheSameProposal_IsRefusedByTheDatabase()
    {
        await using var db = postgres.CreateContext();
        var (runId, proposalId) = await SeedAsync(db);
        var store = new EfApprovalStore(db);

        await store.AddAsync(Decision(runId, proposalId));

        // The second decision is a rejection by a different actor — the case a
        // naive "already approved?" check would most easily miss.
        await Assert.ThrowsAsync<ProposalAlreadyDecidedException>(
            () => store.AddAsync(Decision(
                runId, proposalId, ApprovalDecisionKind.Reject, "someone.else@example.test")));

        Assert.Equal(1, await db.ApprovalDecisions.CountAsync(a => a.ProposalId == proposalId));
    }

    [RequiresDockerFact]
    public async Task ApproveThenApprove_IsAlsoRefused()
    {
        await using var db = postgres.CreateContext();
        var (runId, proposalId) = await SeedAsync(db);
        var store = new EfApprovalStore(db);

        await store.AddAsync(Decision(runId, proposalId));

        await Assert.ThrowsAsync<ProposalAlreadyDecidedException>(
            () => store.AddAsync(Decision(runId, proposalId)));
    }

    [RequiresDockerFact]
    public async Task ConcurrentDecisionsOnOneProposal_ProduceExactlyOneRecord()
    {
        // The race a service-level check cannot close: two requests both observe
        // "no decision yet", then both insert.
        await using var seedDb = postgres.CreateContext();
        var (runId, proposalId) = await SeedAsync(seedDb);

        var attempts = Enumerable.Range(0, 8).Select(async i =>
        {
            await using var db = postgres.CreateContext();
            try
            {
                await new EfApprovalStore(db).AddAsync(
                    Decision(runId, proposalId, decidedBy: $"reviewer-{i}@example.test"));
                return true;
            }
            catch (ProposalAlreadyDecidedException)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(succeeded => succeeded));
        Assert.Equal(1, await seedDb.ApprovalDecisions.CountAsync(a => a.ProposalId == proposalId));
    }

    [RequiresDockerFact]
    public async Task ADecisionWithNoActor_IsRefused()
    {
        // SC-015: every decision record carries an actor. An empty string
        // satisfies NOT NULL, so the check constraint is what actually enforces
        // it — authentication being out of scope makes the recorded actor the
        // only attribution there is, and a blank one is worthless.
        await using var db = postgres.CreateContext();
        var (runId, proposalId) = await SeedAsync(db);

        db.ApprovalDecisions.Add(Decision(runId, proposalId, decidedBy: "   "));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [RequiresDockerFact]
    public async Task ADecisionWithAMalformedHash_IsRefused()
    {
        // A hash that is not lower-case hex of a SHA-256 would compare unequal at
        // apply time and read as tampering rather than as the bug it is.
        await using var db = postgres.CreateContext();
        var (runId, proposalId) = await SeedAsync(db);

        db.ApprovalDecisions.Add(Decision(runId, proposalId, hash: "NOT-A-HASH"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [RequiresDockerFact]
    public async Task AProposalWithNoAffectedPaths_IsRefused()
    {
        // FR-011a and FR-008b: an empty proposal is a no-change outcome, never
        // something offered for approval.
        await using var db = postgres.CreateContext();
        var (runId, _) = await SeedAsync(db);

        db.ChangeProposals.Add(new ChangeProposal
        {
            RunId = runId,
            EntriesJson = "[]",
            UnifiedDiff = string.Empty,
            AffectedPaths = [],
            DiffHash = ValidHash,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [RequiresDockerFact]
    public async Task ARunClaimingATerminalOutcomeWithoutAReason_IsRefused()
    {
        // FR-008c and FR-030: a finished run always says why it finished. A
        // half-written update must not be able to leave one that claims to be
        // done with no reason and no end time.
        await using var db = postgres.CreateContext();
        var (runId, _) = await SeedAsync(db);

        var run = await db.Runs.FirstAsync(r => r.Id == runId);
        run.Stage = RunStage.Failed;
        run.TerminalOutcome = TerminalOutcome.Failed;
        run.OutcomeReason = null;
        run.EndedAt = null;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [RequiresDockerFact]
    public async Task ARevisionAttemptAboveTheLimit_IsRefused()
    {
        // FR-012 caps revisions at two.
        await using var db = postgres.CreateContext();
        var (runId, _) = await SeedAsync(db);

        var run = await db.Runs.FirstAsync(r => r.Id == runId);
        run.RevisionAttempt = 3;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [RequiresDockerFact]
    public async Task ATimedOutTestResult_CannotAlsoHavePassed()
    {
        // SC-009 counts clean finishes and forced terminations separately, which
        // is only meaningful if a row cannot claim to be both.
        await using var db = postgres.CreateContext();
        var (runId, _) = await SeedAsync(db);

        db.TestResults.Add(new TestResult
        {
            RunId = runId,
            CommandName = "unit",
            Passed = true,
            TimedOut = true,
            Output = "…",
            DurationMs = 300_000,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
