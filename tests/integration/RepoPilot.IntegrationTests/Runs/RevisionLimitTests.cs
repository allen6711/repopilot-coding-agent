using Microsoft.Extensions.Options;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Runs;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Capabilities;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using Xunit;

namespace RepoPilot.IntegrationTests.Runs;

/// <summary>
/// FR-012 and FR-013: the revision loop is bounded, and every attempt at it
/// needs its own approval.
/// </summary>
public sealed class RevisionLimitTests : IDisposable
{
    private const string FixtureConfig = """
        {
          "slug": "revision-fixture",
          "sandbox": { "image": "alpine:3", "workdir": "/workspace" },
          "commands": [{ "name": "unit", "argv": ["true"], "purpose": "verify" }]
        }
        """;

    private readonly TempWorkspaceProvisioner _workspaces = new();
    private readonly InMemoryRunStore _runs = new();
    private readonly InMemoryProposalStore _proposals = new();
    private readonly InMemoryApprovalStore _approvals = new();
    private readonly InMemoryRunEventStore _eventStore = new();
    private readonly RepositoryFixture _fixture;
    private readonly RunEventRecorder _events;

    public RevisionLimitTests()
    {
        _fixture = new RepositoryFixture
        {
            Slug = "revision-fixture",
            DisplayName = "Revision fixture",
            RootPath = _workspaces.PathFor(Guid.Empty),
            TestConfigJson = FixtureConfig,
        };

        _events = new RunEventRecorder(_eventStore, new NullRunEventPublisher());
    }

    private (RunOrchestrator Orchestrator, ScriptedCapabilities Capabilities) Build(
        bool testsPass, int maxRevisions = 2)
    {
        var capabilities = new ScriptedCapabilities(_proposals, testsPass);

        var orchestrator = new RunOrchestrator(
            _runs,
            new InMemoryFixtureStore(_fixture),
            _proposals,
            _approvals,
            _workspaces,
            ScriptedAgent.ThatProposes(),
            capabilities,
            _events,
            Options.Create(new RunConcurrencyOptions { MaxRevisionAttempts = maxRevisions }),
            Options.Create(new RetrievalOptions()));

        return (orchestrator, capabilities);
    }

    private Run SeedRun()
    {
        var run = new Run { RepositoryId = _fixture.Id, TaskDescription = "fix the null dereference" };
        _runs.Seed(run);
        return run;
    }

    private async Task ApproveCurrentAsync(Run run)
    {
        var proposal = await _proposals.FindCurrentForRunAsync(run.Id);
        Assert.NotNull(proposal);

        await new DecideProposalUseCase(_proposals, _approvals, _runs, _events)
            .DecideAsync(proposal!.Id, ApprovalDecisionKind.Approve, proposal.DiffHash, "reviewer@example.com");
    }

    [Fact]
    public async Task TheThirdFailureEndsTheRunAsFailed()
    {
        var (orchestrator, _) = Build(testsPass: false);
        var run = SeedRun();

        // Attempt 0: the original proposal.
        Assert.Equal(RunStage.AwaitingApproval, await orchestrator.ExecuteUntilApprovalAsync(run.Id));

        await ApproveCurrentAsync(run);
        Assert.Equal(RunStage.AwaitingApproval, await orchestrator.ApplyAndTestAsync(run.Id));

        // Attempt 1: the first revision.
        await ApproveCurrentAsync(run);
        Assert.Equal(RunStage.AwaitingApproval, await orchestrator.ApplyAndTestAsync(run.Id));

        // Attempt 2: the second and last revision. Its failure exhausts the
        // budget, so the run ends rather than proposing a fourth time.
        await ApproveCurrentAsync(run);
        Assert.Equal(RunStage.Failed, await orchestrator.ApplyAndTestAsync(run.Id));

        var final = await _runs.FindAsync(run.Id);
        Assert.Equal(TerminalOutcome.Failed, final!.TerminalOutcome);
        Assert.Equal(OutcomeReason.RevisionLimitReached, final.OutcomeReason);
        Assert.Equal(2, final.RevisionAttempt);
    }

    [Fact]
    public async Task EachRevisionRequiresItsOwnApproval()
    {
        var (orchestrator, capabilities) = Build(testsPass: false);
        var run = SeedRun();

        await orchestrator.ExecuteUntilApprovalAsync(run.Id);
        await ApproveCurrentAsync(run);
        await orchestrator.ApplyAndTestAsync(run.Id);

        // A revision proposal now exists and has not been decided. Applying it
        // without an approval must be refused — the earlier approval was bound
        // to different content and cannot carry over (FR-013, FR-019a).
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.ApplyAndTestAsync(run.Id));

        Assert.Contains("no recorded approval", refusal.Message);

        // One apply, from the one approval. The refusal happened before the
        // second write, not after it.
        Assert.Equal(1, capabilities.ApplyCount);
    }

    [Fact]
    public async Task EveryAppliedChangeHasADistinctApprovalBoundToIt()
    {
        var (orchestrator, capabilities) = Build(testsPass: false);
        var run = SeedRun();

        await orchestrator.ExecuteUntilApprovalAsync(run.Id);
        await ApproveCurrentAsync(run);
        await orchestrator.ApplyAndTestAsync(run.Id);
        await ApproveCurrentAsync(run);
        await orchestrator.ApplyAndTestAsync(run.Id);
        await ApproveCurrentAsync(run);
        await orchestrator.ApplyAndTestAsync(run.Id);

        // Three writes, three approvals, three distinct proposals — and each
        // approval names the hash of the proposal it decided.
        Assert.Equal(3, capabilities.ApplyCount);
        Assert.Equal(3, _approvals.All.Count);
        Assert.Equal(3, _approvals.All.Select(a => a.ProposalId).Distinct().Count());

        foreach (var decision in _approvals.All)
        {
            var proposal = await _proposals.FindAsync(decision.ProposalId);
            Assert.Equal(proposal!.DiffHash, decision.DiffHash);
        }
    }

    [Fact]
    public async Task PassingTestsEndTheRunWithoutUsingTheRevisionBudget()
    {
        var (orchestrator, capabilities) = Build(testsPass: true);
        var run = SeedRun();

        await orchestrator.ExecuteUntilApprovalAsync(run.Id);
        await ApproveCurrentAsync(run);

        Assert.Equal(RunStage.Succeeded, await orchestrator.ApplyAndTestAsync(run.Id));

        var final = await _runs.FindAsync(run.Id);
        Assert.Equal(OutcomeReason.Completed, final!.OutcomeReason);
        Assert.Equal(0, final.RevisionAttempt);
        Assert.Equal(1, capabilities.ApplyCount);
    }

    [Fact]
    public async Task ApplyAndTestAreInvokedOnlyByTheOrchestrator()
    {
        var (orchestrator, capabilities) = Build(testsPass: true);
        var run = SeedRun();

        await orchestrator.ExecuteUntilApprovalAsync(run.Id);
        await ApproveCurrentAsync(run);
        await orchestrator.ApplyAndTestAsync(run.Id);

        // Principle III: the write and the sandbox never appear on the model's
        // surface, so no prompt can reach them however it is phrased.
        foreach (var (name, surface) in capabilities.Invocations)
        {
            var expected = name is "apply_patch" or "run_tests"
                ? InvocationSurface.Orchestrator
                : InvocationSurface.Model;

            Assert.Equal(expected, surface);
        }
    }

    [Fact]
    public async Task ZeroRevisionsMeansTheFirstFailureIsFinal()
    {
        var (orchestrator, _) = Build(testsPass: false, maxRevisions: 0);
        var run = SeedRun();

        await orchestrator.ExecuteUntilApprovalAsync(run.Id);
        await ApproveCurrentAsync(run);

        Assert.Equal(RunStage.Failed, await orchestrator.ApplyAndTestAsync(run.Id));

        var final = await _runs.FindAsync(run.Id);
        Assert.Equal(OutcomeReason.RevisionLimitReached, final!.OutcomeReason);
    }

    [Fact]
    public async Task AFinishedRunReleasesItsWorkingCopy()
    {
        var (orchestrator, _) = Build(testsPass: true);
        var run = SeedRun();

        await orchestrator.ExecuteUntilApprovalAsync(run.Id);
        await ApproveCurrentAsync(run);
        await orchestrator.ApplyAndTestAsync(run.Id);

        // FR-026a. The diff and the events outlive the run; the directory does not.
        Assert.Contains(run.Id, _workspaces.Destroyed);
        Assert.False(Directory.Exists(_workspaces.PathFor(run.Id)));
    }

    public void Dispose() => _workspaces.Dispose();
}
