using Microsoft.Extensions.Options;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Runs;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using Xunit;

namespace RepoPilot.IntegrationTests.Runs;

/// <summary>
/// FR-008a and FR-008b: the ways a run ends without a change being written.
/// </summary>
public sealed class NoChangeAndCancellationTests : IDisposable
{
    private const string FixtureConfig = """
        {
          "slug": "no-change-fixture",
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

    public NoChangeAndCancellationTests()
    {
        _fixture = new RepositoryFixture
        {
            Slug = "no-change-fixture",
            DisplayName = "No-change fixture",
            RootPath = _workspaces.PathFor(Guid.Empty),
            TestConfigJson = FixtureConfig,
        };

        _events = new RunEventRecorder(_eventStore, new NullRunEventPublisher());
    }

    private RunOrchestrator Build(ScriptedAgent agent) => new(
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
        var run = new Run { RepositoryId = _fixture.Id, TaskDescription = "make it better somehow" };
        _runs.Seed(run);
        return run;
    }

    [Fact]
    public async Task DecliningAfterLookingIsRecordedAsADeliberateNoOp()
    {
        var run = SeedRun();

        Assert.Equal(
            RunStage.NoChange,
            await Build(ScriptedAgent.ThatDeclinesAfterLooking()).ExecuteUntilApprovalAsync(run.Id));

        var final = await _runs.FindAsync(run.Id);
        Assert.Equal(TerminalOutcome.NoChange, final!.TerminalOutcome);
        Assert.Equal(OutcomeReason.DeliberateNoOp, final.OutcomeReason);
    }

    [Fact]
    public async Task NeverFindingAnythingIsRecordedAsInsufficientContext()
    {
        var run = SeedRun();

        Assert.Equal(
            RunStage.NoChange,
            await Build(ScriptedAgent.ThatCannotFindAnything()).ExecuteUntilApprovalAsync(run.Id));

        var final = await _runs.FindAsync(run.Id);
        Assert.Equal(TerminalOutcome.NoChange, final!.TerminalOutcome);

        // The distinction is the point. Both ended with nothing written, but only
        // this one is a retrieval problem worth investigating — collapsing them
        // into one reason would hide that.
        Assert.Equal(OutcomeReason.InsufficientContext, final.OutcomeReason);
    }

    [Fact]
    public async Task NoChangeOffersNothingForAHumanToApprove()
    {
        var run = SeedRun();

        await Build(ScriptedAgent.ThatDeclinesAfterLooking()).ExecuteUntilApprovalAsync(run.Id);

        // An empty proposal would be a thing a reviewer could approve, and
        // approving nothing is not a decision anyone should be asked to make.
        Assert.Null(await _proposals.FindCurrentForRunAsync(run.Id));
        Assert.Empty(_approvals.All);
        Assert.Contains(run.Id, _workspaces.Destroyed);
    }

    [Fact]
    public async Task ARunCanBeCancelledWhileAwaitingApproval()
    {
        var run = SeedRun();
        await Build(ScriptedAgent.ThatProposes()).ExecuteUntilApprovalAsync(run.Id);
        Assert.Equal(RunStage.AwaitingApproval, run.Stage);

        var cancelled = await Cancel().CancelAsync(run.Id, "reviewer@example.com");

        // The stage a run is most likely to sit in forever is exactly the one it
        // must be escapable from.
        Assert.Equal(TerminalOutcome.Cancelled, cancelled.TerminalOutcome);
        Assert.Equal(OutcomeReason.AbandonedByUser, cancelled.OutcomeReason);
        Assert.Contains(run.Id, _workspaces.Destroyed);
    }

    [Theory]
    [InlineData(RunStage.Created)]
    [InlineData(RunStage.Retrieving)]
    [InlineData(RunStage.Planning)]
    [InlineData(RunStage.Proposing)]
    [InlineData(RunStage.AwaitingApproval)]
    [InlineData(RunStage.Applying)]
    [InlineData(RunStage.Testing)]
    public async Task CancellationIsAvailableFromEveryNonTerminalStage(RunStage stage)
    {
        var run = SeedRun();
        run.Stage = stage;

        var cancelled = await Cancel().CancelAsync(run.Id, "reviewer@example.com");

        Assert.Equal(RunStage.Cancelled, cancelled.Stage);
    }

    [Fact]
    public async Task CancellationRequiresAnActor()
    {
        var run = SeedRun();

        var refusal = await Assert.ThrowsAsync<CancellationRefusedException>(
            () => Cancel().CancelAsync(run.Id, "   "));

        Assert.Equal(CancellationRefusalReason.ActorMissing, refusal.Reason);

        // Refused before anything changed: the run is still live and still
        // cancellable by someone who identifies themselves.
        Assert.Equal(RunStage.Created, (await _runs.FindAsync(run.Id))!.Stage);
    }

    [Fact]
    public async Task AFinishedRunCannotBeCancelledAfterTheFact()
    {
        var run = SeedRun();
        await Build(ScriptedAgent.ThatDeclinesAfterLooking()).ExecuteUntilApprovalAsync(run.Id);

        var refusal = await Assert.ThrowsAsync<CancellationRefusedException>(
            () => Cancel().CancelAsync(run.Id, "reviewer@example.com"));

        Assert.Equal(CancellationRefusalReason.AlreadyTerminal, refusal.Reason);

        // The recorded outcome stands. Letting a late cancellation overwrite it
        // would make the run history editable after the fact.
        Assert.Equal(TerminalOutcome.NoChange, (await _runs.FindAsync(run.Id))!.TerminalOutcome);
    }

    private CancelRunUseCase Cancel() => new(_runs, _workspaces, _events);

    public void Dispose() => _workspaces.Dispose();
}
