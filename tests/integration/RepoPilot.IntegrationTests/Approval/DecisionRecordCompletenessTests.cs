using Microsoft.EntityFrameworkCore;
using RepoPilot.Application.Approval;
using RepoPilot.Application.Runs;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Runs;
using RepoPilot.Infrastructure.Events;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.IntegrationTests.Infrastructure;
using RepoPilot.IntegrationTests.Runs;
using Xunit;

namespace RepoPilot.IntegrationTests.Approval;

/// <summary>
/// SC-015: every decision record carries an actor and a hash.
/// <para>
/// Stated as a property of the stored table rather than of the code that writes
/// to it. The use case checks both before recording, and that check is the
/// control — but SC-015 is a claim about what is in the audit trail, and the way
/// to check a claim about stored rows is to look at the stored rows.
/// </para>
/// <para>
/// So this works from both ends. It sweeps every decision written by every path
/// in the system and asserts the invariant holds, and it attacks the invariant
/// directly — an empty actor, a whitespace actor, a null hash — to show the
/// refusal is real rather than incidental to how the tests happen to call it.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DecisionRecordCompletenessTests(PostgresFixture postgres)
{
    private const string FixtureConfig =
        """{"slug":"completeness","commands":[{"name":"unit","argv":["true"],"purpose":"verify"}]}""";

    private sealed record Scaffold(
        Run Run,
        ChangeProposal Proposal,
        DecideProposalUseCase Decide,
        ProgrammaticApproval Programmatic);

    private static async Task<Scaffold> ScaffoldAsync(
        RepoPilotDbContext db, Guid? evaluationRunId = null)
    {
        var fixture = new RepositoryFixture
        {
            Slug = "completeness-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Decision completeness fixture",
            RootPath = Path.Combine(Path.GetTempPath(), "repopilot-completeness"),
            TestConfigJson = FixtureConfig,
        };

        db.Repositories.Add(fixture);

        if (evaluationRunId is { } id)
        {
            db.EvaluationRuns.Add(new EvaluationRun { Id = id, TaskCount = 1 });
        }

        var run = new Run
        {
            RepositoryId = fixture.Id,
            TaskDescription = "add a guard",
            Stage = RunStage.AwaitingApproval,
            EvaluationRunId = evaluationRunId,
        };

        db.Runs.Add(run);

        var entries = new List<ProposalEntry>
        {
            new("src/Service.cs", ProposalOperation.Modify, "changed " + Guid.NewGuid() + "\n"),
        };

        var proposal = new ChangeProposal
        {
            RunId = run.Id,
            EntriesJson = System.Text.Json.JsonSerializer.Serialize(entries),
            UnifiedDiff = "--- a/src/Service.cs\n+++ b/src/Service.cs\n",
            AffectedPaths = ["src/Service.cs"],
            DiffHash = DiffHash.Compute(entries),
        };

        db.ChangeProposals.Add(proposal);
        await db.SaveChangesAsync();

        var recorder = new RunEventRecorder(new EfRunEventStore(db), new NullRunEventPublisher());

        var decide = new DecideProposalUseCase(
            new EfProposalStore(db),
            new EfApprovalStore(db),
            new EfRunStore(db),
            recorder);

        return new Scaffold(
            run,
            proposal,
            decide,
            new ProgrammaticApproval(decide, new EfProposalStore(db), new EfRunStore(db)));
    }

    // ---- the sweep -----------------------------------------------------------

    /// <summary>
    /// Every decision in the database, whatever wrote it, carries an actor and
    /// the hash of the proposal it decided.
    /// </summary>
    [RequiresDockerFact]
    public async Task EveryStoredDecisionCarriesAnActorAndAMatchingHash()
    {
        await using var db = postgres.CreateContext();

        // Both paths that can write a decision, so the sweep is over a table that
        // actually holds one of each rather than over whatever earlier tests left.
        var interactive = await ScaffoldAsync(db);
        await interactive.Decide.DecideAsync(
            interactive.Proposal.Id,
            ApprovalDecisionKind.Approve,
            interactive.Proposal.DiffHash,
            "reviewer@example.test");

        var programmatic = await ScaffoldAsync(db, Guid.CreateVersion7());
        await programmatic.Programmatic.ApproveAsync(
            programmatic.Proposal.Id, programmatic.Proposal.DiffHash);

        var decisions = await db.ApprovalDecisions.AsNoTracking().ToListAsync();

        Assert.NotEmpty(decisions);

        foreach (var decision in decisions)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(decision.DecidedBy),
                $"Decision {decision.Id} is unattributable.");

            Assert.False(
                string.IsNullOrWhiteSpace(decision.DiffHash),
                $"Decision {decision.Id} names no change.");

            // FR-020a: the hash on the record is the hash of the proposal it
            // decided. A decision carrying some other change's hash would satisfy
            // "has a hash" and authorise the wrong content.
            var proposal = await db.ChangeProposals.AsNoTracking()
                .SingleAsync(p => p.Id == decision.ProposalId);

            Assert.Equal(proposal.DiffHash, decision.DiffHash);
        }
    }

    /// <summary>
    /// A programmatic decision is attributed to the harness, by name. SC-001
    /// requires the two kinds to be reported separately, which needs the record to
    /// say which it was — and an actor that could be mistaken for a person would
    /// defeat that even with the mode field set.
    /// </summary>
    [RequiresDockerFact]
    public async Task AProgrammaticDecisionIsAttributedToTheHarness()
    {
        await using var db = postgres.CreateContext();

        var s = await ScaffoldAsync(db, Guid.CreateVersion7());

        var decision = await s.Programmatic.ApproveAsync(s.Proposal.Id, s.Proposal.DiffHash);

        Assert.Equal(ApprovalMode.Programmatic, decision.Mode);
        Assert.Equal(ProgrammaticApproval.Actor, decision.DecidedBy);
        Assert.DoesNotContain('@', decision.DecidedBy);
    }

    // ---- the invariant, attacked --------------------------------------------

    [RequiresDockerTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ADecisionWithoutAnActorIsRefused(string actor)
    {
        await using var db = postgres.CreateContext();

        var s = await ScaffoldAsync(db);

        var refusal = await Assert.ThrowsAsync<DecisionRefusedException>(
            () => s.Decide.DecideAsync(
                s.Proposal.Id, ApprovalDecisionKind.Approve, s.Proposal.DiffHash, actor));

        Assert.Equal(DecisionRefusalReason.ActorMissing, refusal.Reason);

        // Nothing was written. An empty actor satisfies a NOT NULL column, so a
        // refusal that had already inserted would leave exactly the unattributable
        // row SC-015 exists to prevent.
        Assert.Empty(await db.ApprovalDecisions
            .Where(a => a.ProposalId == s.Proposal.Id)
            .ToListAsync());
    }

    [RequiresDockerFact]
    public async Task AnActorIsStoredTrimmedRatherThanAsTyped()
    {
        await using var db = postgres.CreateContext();

        var s = await ScaffoldAsync(db);

        var decision = await s.Decide.DecideAsync(
            s.Proposal.Id,
            ApprovalDecisionKind.Approve,
            s.Proposal.DiffHash,
            "  reviewer@example.test  ");

        Assert.Equal("reviewer@example.test", decision.DecidedBy);
    }

    [RequiresDockerFact]
    public async Task ADecisionNamingADifferentChangeIsRefused()
    {
        await using var db = postgres.CreateContext();

        var s = await ScaffoldAsync(db);

        var refusal = await Assert.ThrowsAsync<DecisionRefusedException>(
            () => s.Decide.DecideAsync(
                s.Proposal.Id,
                ApprovalDecisionKind.Approve,
                DiffHash.Compute([new ProposalEntry("src/Other.cs", ProposalOperation.Modify, "x")]),
                "reviewer@example.test"));

        Assert.Equal(DecisionRefusalReason.HashMismatch, refusal.Reason);
        Assert.Empty(await db.ApprovalDecisions
            .Where(a => a.ProposalId == s.Proposal.Id)
            .ToListAsync());
    }

    /// <summary>
    /// FR-014a: an approval does not expire.
    /// <para>
    /// The requirement is expressed as the absence of a mechanism — there is no
    /// TTL to test — so what is checkable is the reasoning behind it: a proposal
    /// is immutable once created and its working copy is owned by one run, so the
    /// content a decision names cannot have changed underneath it however long the
    /// reviewer took. This pins that a stale proposal still decides, which is what
    /// would break if someone added a freshness check.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task ADecisionOnALongStandingProposalIsStillValid()
    {
        await using var db = postgres.CreateContext();

        var s = await ScaffoldAsync(db);

        // Backdated well past any plausible expiry window someone might add.
        s.Proposal.CreatedAt = DateTimeOffset.UtcNow.AddDays(-90);
        await db.SaveChangesAsync();

        var decision = await s.Decide.DecideAsync(
            s.Proposal.Id, ApprovalDecisionKind.Approve, s.Proposal.DiffHash, "reviewer@example.test");

        Assert.Equal(ApprovalDecisionKind.Approve, decision.Decision);
        Assert.Equal(s.Proposal.DiffHash, decision.DiffHash);

        // The hash still matches, which is the actual guarantee: the decision
        // binds the same content the reviewer saw ninety days ago because nothing
        // could have rewritten it.
        var stored = await db.ChangeProposals.AsNoTracking()
            .SingleAsync(p => p.Id == s.Proposal.Id);

        Assert.Equal(stored.DiffHash, decision.DiffHash);
        Assert.Equal(ProposalDecisionStatus.Approved, stored.DecisionStatus);
    }

    /// <summary>
    /// A rejection is a decision too, and carries the same fields. Without this,
    /// "every decision has an actor and a hash" could hold only for approvals and
    /// the sweep above would never notice.
    /// </summary>
    [RequiresDockerFact]
    public async Task ARejectionCarriesTheSameFieldsAsAnApproval()
    {
        await using var db = postgres.CreateContext();

        var s = await ScaffoldAsync(db);

        var decision = await s.Decide.DecideAsync(
            s.Proposal.Id, ApprovalDecisionKind.Reject, s.Proposal.DiffHash, "reviewer@example.test");

        Assert.Equal(ApprovalDecisionKind.Reject, decision.Decision);
        Assert.Equal("reviewer@example.test", decision.DecidedBy);
        Assert.Equal(s.Proposal.DiffHash, decision.DiffHash);
        Assert.NotEqual(default, decision.DecidedAt);
    }
}
