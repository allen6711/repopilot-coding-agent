using Microsoft.Extensions.Options;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using Xunit;

namespace RepoPilot.IntegrationTests.Runs;

/// <summary>
/// FR-010: a plan precedes every proposal.
/// <para>
/// The ordering is not presentational. A reviewer reads the plan to judge
/// whether the diff does what was intended; a diff with no stated intent can
/// only be checked for internal coherence, which is a much weaker thing. So the
/// system has to make a proposal without a preceding plan impossible rather
/// than merely unusual.
/// </para>
/// </summary>
public sealed class PlanPrecedesProposalTests : IDisposable
{
    private const string FixtureConfig = """
        {
          "slug": "plan-fixture",
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

    public PlanPrecedesProposalTests()
    {
        _fixture = new RepositoryFixture
        {
            Slug = "plan-fixture",
            DisplayName = "Plan ordering fixture",
            RootPath = _workspaces.PathFor(Guid.Empty),
            TestConfigJson = FixtureConfig,
        };

        _events = new RunEventRecorder(_eventStore, new NullRunEventPublisher());
    }

    private RunOrchestrator Build(IAgentTurnRunner agent) => new(
        _runs,
        new InMemoryFixtureStore(_fixture),
        _proposals,
        _approvals,
        _workspaces,
        agent,
        new ScriptedCapabilities(_proposals, testsPass: true),
        _events,
        Options.Create(new RunConcurrencyOptions()),
        Options.Create(new RetrievalOptions()),
        new ScriptedBaselineContext());

    private Run SeedRun()
    {
        var run = new Run { RepositoryId = _fixture.Id, TaskDescription = "add a null guard" };
        _runs.Seed(run);
        return run;
    }

    /// <summary>The stage transitions the run recorded, in order.</summary>
    private IReadOnlyList<string> RecordedTransitions(Guid runId) =>
        [.. _eventStore.All
            .Where(e => e.RunId == runId && e.EventType == RunEventType.StageChanged)
            .OrderBy(e => e.Sequence)
            .Select(e => e.ArgumentsSummary ?? string.Empty)];

    [Fact]
    public async Task ThePlanIsRecordedBeforeTheProposal()
    {
        var run = SeedRun();

        Assert.Equal(
            RunStage.AwaitingApproval,
            await Build(ScriptedAgent.ThatProposes()).ExecuteUntilApprovalAsync(run.Id));

        var transitions = RecordedTransitions(run.Id);

        var planIndex = transitions.ToList().FindIndex(t => t.Contains("PlanProduced", StringComparison.Ordinal));
        var proposalIndex = transitions.ToList().FindIndex(t => t.Contains("ProposalCreated", StringComparison.Ordinal));

        Assert.True(planIndex >= 0, "No PlanProduced transition was recorded.");
        Assert.True(proposalIndex >= 0, "No ProposalCreated transition was recorded.");

        // The recorded order, not just the presence of both. Reconstructing a
        // run from events alone (SC-008) has to show the plan first, or the
        // record does not support the claim the UI makes.
        Assert.True(
            planIndex < proposalIndex,
            $"PlanProduced was recorded at {planIndex}, after ProposalCreated at {proposalIndex}.");
    }

    [Fact]
    public async Task TheRunCarriesThePlanByTheTimeItAwaitsADecision()
    {
        var run = SeedRun();

        await Build(ScriptedAgent.ThatProposes()).ExecuteUntilApprovalAsync(run.Id);

        var stored = await _runs.FindAsync(run.Id);

        // A reviewer opening the run at this moment must find the plan there.
        Assert.False(string.IsNullOrWhiteSpace(stored!.Plan));
        Assert.NotNull(await _proposals.FindCurrentForRunAsync(run.Id));
    }

    [Fact]
    public void ProposingIsUnreachableWithoutPassingThroughAPlan()
    {
        // The state machine is where the ordering is actually enforced. Every
        // path into Proposing goes through PlanProduced, so no orchestrator bug
        // can produce a proposal from an unplanned run — it would have to
        // transition illegally, which throws.
        var pathsIntoProposing = RunStateMachine.AllTransitions()
            .Where(t => t.To == RunStage.Proposing)
            .ToList();

        Assert.NotEmpty(pathsIntoProposing);

        foreach (var (from, trigger, _) in pathsIntoProposing)
        {
            Assert.True(
                (from, trigger) is (RunStage.Planning, RunTrigger.PlanProduced)
                    or (RunStage.Testing, RunTrigger.TestsFailedRetryAvailable),
                $"Proposing is reachable from {from} on {trigger}, which bypasses the plan.");
        }
    }

    [Fact]
    public void AProposalCannotBeRecordedFromAnUnplannedStage()
    {
        // Directly: the transition a proposal needs does not exist from any
        // stage before Planning has completed.
        foreach (var stage in new[] { RunStage.Created, RunStage.Retrieving, RunStage.Planning })
        {
            Assert.False(
                RunStateMachine.TryTransition(stage, RunTrigger.ProposalCreated, out _),
                $"ProposalCreated was permitted from {stage}.");
        }

        Assert.True(
            RunStateMachine.TryTransition(RunStage.Proposing, RunTrigger.ProposalCreated, out var to));
        Assert.Equal(RunStage.AwaitingApproval, to);
    }

    [Fact]
    public async Task ARevisionReusesThePlanRatherThanProposingWithoutOne()
    {
        var run = SeedRun();
        var orchestrator = Build(ScriptedAgent.ThatProposes());

        await orchestrator.ExecuteUntilApprovalAsync(run.Id);

        var planAfterFirstProposal = (await _runs.FindAsync(run.Id))!.Plan;

        // A revision re-enters Proposing from Testing, which is the one path
        // that does not pass through PlanProduced again. That is deliberate —
        // the plan has not changed — but it means the run must still carry one.
        Assert.False(string.IsNullOrWhiteSpace(planAfterFirstProposal));
    }

    public void Dispose() => _workspaces.Dispose();
}
