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
/// Programmatic approval is available to evaluation runs and to nothing else
/// (FR-015b).
/// <para>
/// The harness's approval path is the one place a change can be applied without a
/// person deciding. If it were reachable from an interactive run, SC-001's
/// separate reporting of interactive and programmatic approvals would be
/// decoration: a run started by a reviewer could be approved by nobody and
/// counted in the wrong column.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProgrammaticModeScopeTests(PostgresFixture postgres)
{
    private const string FixtureConfig =
        """{"slug":"programmatic","commands":[{"name":"unit","argv":["true"],"purpose":"verify"}]}""";

    private sealed record Scaffold(ChangeProposal Proposal, ProgrammaticApproval Approval);

    private async Task<Scaffold> ScaffoldAsync(RepoPilotDbContext db, Guid? evaluationRunId)
    {
        var fixture = new RepositoryFixture
        {
            Slug = "programmatic-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Programmatic scope fixture",
            RootPath = Path.Combine(Path.GetTempPath(), "repopilot-programmatic"),
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
            ApprovalMode = evaluationRunId is null
                ? ApprovalMode.Interactive
                : ApprovalMode.Programmatic,
        };

        db.Runs.Add(run);

        var entries = new List<ProposalEntry>
        {
            new("src/Service.cs", ProposalOperation.Modify, "changed\n"),
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
            proposal,
            new ProgrammaticApproval(decide, new EfProposalStore(db), new EfRunStore(db)));
    }

    [RequiresDockerFact]
    public async Task ProgrammaticApprovalIsRefusedOnAnInteractiveRun()
    {
        await using var db = postgres.CreateContext();

        var scaffold = await ScaffoldAsync(db, evaluationRunId: null);

        var refusal = await Assert.ThrowsAsync<DecisionRefusedException>(
            () => scaffold.Approval.ApproveAsync(
                scaffold.Proposal.Id, scaffold.Proposal.DiffHash));

        Assert.Equal(DecisionRefusalReason.ProgrammaticModeNotPermitted, refusal.Reason);

        // The refusal wrote nothing. A refusal that still left a decision record
        // would have unlocked the write it was refusing.
        Assert.Null(await new EfApprovalStore(db).FindByProposalAsync(scaffold.Proposal.Id));
        Assert.Equal(
            ProposalDecisionStatus.Pending,
            (await new EfProposalStore(db).FindAsync(scaffold.Proposal.Id))!.DecisionStatus);
    }

    [RequiresDockerFact]
    public async Task ProgrammaticApprovalOnAnEvaluationRunWritesARealDecision()
    {
        await using var db = postgres.CreateContext();

        var scaffold = await ScaffoldAsync(db, evaluationRunId: Guid.CreateVersion7());

        var decision = await scaffold.Approval.ApproveAsync(
            scaffold.Proposal.Id, scaffold.Proposal.DiffHash);

        // Everything an interactive decision carries, so the record is auditable
        // on the same terms — bound to the exact content, and attributed.
        Assert.Equal(ApprovalMode.Programmatic, decision.Mode);
        Assert.Equal(scaffold.Proposal.DiffHash, decision.DiffHash);
        Assert.Equal(ProgrammaticApproval.Actor, decision.DecidedBy);
        Assert.NotEqual(string.Empty, decision.DecidedBy);

        var stored = await new EfApprovalStore(db).FindByProposalAsync(scaffold.Proposal.Id);

        Assert.NotNull(stored);
        Assert.Equal(ApprovalMode.Programmatic, stored!.Mode);
    }

    [RequiresDockerFact]
    public async Task ProgrammaticApprovalStillRefusesAHashThatDoesNotMatch()
    {
        await using var db = postgres.CreateContext();

        var scaffold = await ScaffoldAsync(db, evaluationRunId: Guid.CreateVersion7());

        // FR-020a applies to the harness exactly as it does to a person. Being
        // automated is not a reason to approve content other than the stored one.
        var refusal = await Assert.ThrowsAsync<DecisionRefusedException>(
            () => scaffold.Approval.ApproveAsync(
                scaffold.Proposal.Id, "0000000000000000000000000000000000000000000000000000000000000000"));

        Assert.Equal(DecisionRefusalReason.HashMismatch, refusal.Reason);
    }

    [RequiresDockerFact]
    public async Task ProgrammaticApprovalIsRefusedForAProposalThatDoesNotExist()
    {
        await using var db = postgres.CreateContext();

        var scaffold = await ScaffoldAsync(db, evaluationRunId: Guid.CreateVersion7());

        var refusal = await Assert.ThrowsAsync<DecisionRefusedException>(
            () => scaffold.Approval.ApproveAsync(Guid.CreateVersion7(), scaffold.Proposal.DiffHash));

        // Reported as an unavailable mode rather than a missing proposal: the
        // harness could not establish that this proposal belongs to an evaluation
        // run, and that is the check that matters here.
        Assert.Equal(DecisionRefusalReason.ProgrammaticModeNotPermitted, refusal.Reason);
    }
}
