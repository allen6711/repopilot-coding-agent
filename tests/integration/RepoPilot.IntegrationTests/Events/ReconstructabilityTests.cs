using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RepoPilot.Application.Configuration;
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

namespace RepoPilot.IntegrationTests.Events;

/// <summary>
/// FR-029 and SC-008: a completed run is reconstructable from stored data alone.
/// <para>
/// The test deletes the working copy and builds every claim from the database,
/// with nothing in memory from the run. That is the situation an audit actually
/// happens in — weeks later, on a different machine, with the process that
/// produced the run long gone. A reconstruction that needed live state would be
/// a reconstruction nobody could ever perform.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReconstructabilityTests(PostgresFixture postgres) : IDisposable
{
    private readonly TempWorkspaceProvisioner _workspaces = new();

    public void Dispose() => _workspaces.Dispose();

    /// <summary>Drives a full run through the orchestrator against real PostgreSQL.</summary>
    private async Task<(Guid RunId, string WorkingCopyPath)> RunToCompletionAsync(
        RepoPilotDbContext db, bool testsPass)
    {
        var fixture = new RepositoryFixture
        {
            Slug = "reconstruct-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Reconstruction fixture",
            RootPath = Path.GetTempPath(),
            TestConfigJson =
                """{"slug":"reconstruct","commands":[{"name":"unit","argv":["true"],"purpose":"verify"}]}""",
        };

        var run = new Run
        {
            RepositoryId = fixture.Id,
            TaskDescription = "Guard the shipping address projection.",
        };

        db.Repositories.Add(fixture);
        db.Runs.Add(run);
        await db.SaveChangesAsync();

        var proposals = new EfProposalStore(db);
        var events = new RunEventRecorder(new EfRunEventStore(db), new InProcessRunEventPublisher());

        var orchestrator = new RunOrchestrator(
            new EfRunStore(db),
            new EfRepositoryFixtureStore(db),
            proposals,
            new EfApprovalStore(db),
            _workspaces,
            ScriptedAgent.ThatProposes(),
            // The same store the orchestrator reads from. A double writing
            // somewhere else would leave the orchestrator looping for a proposal
            // that exists only in the test's imagination.
            new ScriptedCapabilities(proposals, testsPass, events),
            events,
            Options.Create(new RunConcurrencyOptions { MaxRevisionAttempts = 0 }),
            Options.Create(new RetrievalOptions()),
            new ScriptedBaselineContext());

        await orchestrator.ExecuteUntilApprovalAsync(run.Id);

        var proposal = await proposals.FindCurrentForRunAsync(run.Id);

        await new DecideProposalUseCase(proposals, new EfApprovalStore(db), new EfRunStore(db), events)
            .DecideAsync(
                proposal!.Id,
                ApprovalDecisionKind.Approve,
                proposal.DiffHash,
                "reviewer@example.com");

        // Test results are written by the run_tests capability in production; the
        // scripted invoker does not persist them, so the one this run produced is
        // recorded here to stand in for it.
        await new EfTestResultStore(db).AddAsync(new TestResult
        {
            RunId = run.Id,
            CommandName = "unit",
            Passed = testsPass,
            ExitCode = testsPass ? 0 : 1,
            Output = testsPass ? "Passed!  - Failed: 0, Passed: 3" : "Failed!  - Failed: 1",
            DurationMs = 4_100,
        });

        await orchestrator.ApplyAndTestAsync(run.Id);

        return (run.Id, _workspaces.PathFor(run.Id));
    }

    [RequiresDockerFact]
    public async Task ACompletedRunIsFullyReconstructableFromStoredDataAlone()
    {
        Guid runId;
        string workingCopy;

        await using (var db = postgres.CreateContext())
        {
            (runId, workingCopy) = await RunToCompletionAsync(db, testsPass: true);
        }

        // Everything that was live is gone: the working copy, the orchestrator,
        // the context that produced the run.
        Assert.False(Directory.Exists(workingCopy));

        await using var audit = postgres.CreateContext();

        // 1. What the run was and how it ended -------------------------------
        var run = await audit.Runs.SingleAsync(r => r.Id == runId);

        Assert.Equal(TerminalOutcome.Succeeded, run.TerminalOutcome);
        Assert.Equal(OutcomeReason.Completed, run.OutcomeReason);
        Assert.False(string.IsNullOrWhiteSpace(run.Plan));

        // 2. The stage sequence it took ---------------------------------------
        var events = await audit.RunEvents
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();

        var stages = events
            .Where(e => e.EventType == RunEventType.StageChanged)
            .Select(e => TriggerOf(e))
            .OfType<string>()
            .ToList();

        Assert.Equal(
            [
                nameof(RunTrigger.Start),
                nameof(RunTrigger.ContextRetrieved),
                nameof(RunTrigger.PlanProduced),
                nameof(RunTrigger.ProposalCreated),
                nameof(RunTrigger.Approved),
                nameof(RunTrigger.PatchApplied),
                nameof(RunTrigger.TestsPassed),
            ],
            stages);

        // 3. The actions it took ----------------------------------------------
        var actions = events
            .Where(e => e.EventType == RunEventType.ToolCall)
            .Select(e => e.ToolName)
            .ToList();

        Assert.Contains("propose_patch", actions);
        Assert.Contains("apply_patch", actions);
        Assert.Contains("run_tests", actions);

        // 4. The diff that was approved ---------------------------------------
        var proposal = await audit.ChangeProposals.SingleAsync(p => p.RunId == runId);
        var approval = await audit.ApprovalDecisions.SingleAsync(a => a.RunId == runId);

        Assert.False(string.IsNullOrWhiteSpace(proposal.UnifiedDiff));
        Assert.Equal(proposal.DiffHash, approval.DiffHash);
        Assert.Equal("reviewer@example.com", approval.DecidedBy);

        // The hash still verifies against the stored entries, so a reader can
        // check that the diff was not altered after the fact rather than taking
        // the recorded hash on trust (FR-019a).
        var entries = JsonSerializer.Deserialize<List<ProposalEntry>>(proposal.EntriesJson)!;
        Assert.True(DiffHash.Matches(proposal.DiffHash, DiffHash.Compute(entries)));

        // 5. The test output ---------------------------------------------------
        var result = await audit.TestResults.SingleAsync(t => t.RunId == runId);

        Assert.True(result.Passed);
        Assert.Contains("Passed", result.Output, StringComparison.Ordinal);

        // 6. The record is complete and self-describing ------------------------
        Assert.Equal(
            Enumerable.Range(1, events.Count).Select(i => (long)i),
            events.Select(e => e.Sequence));

        Assert.Equal(RunEventType.RunEnded, events[^1].EventType);
    }

    [RequiresDockerFact]
    public async Task AFailedRunRecordsWhichStageItFailedAt()
    {
        Guid runId;

        await using (var db = postgres.CreateContext())
        {
            (runId, _) = await RunToCompletionAsync(db, testsPass: false);
        }

        await using var audit = postgres.CreateContext();

        var run = await audit.Runs.SingleAsync(r => r.Id == runId);

        Assert.Equal(TerminalOutcome.Failed, run.TerminalOutcome);
        Assert.Equal(OutcomeReason.RevisionLimitReached, run.OutcomeReason);

        // FR-030: which stage, not just that it failed. "Failed" alone leaves a
        // reader unable to tell a provider outage during planning from a test
        // failure during testing.
        Assert.Equal(RunStage.Testing, run.FailureStage);

        var failure = await audit.RunEvents
            .Where(e => e.RunId == runId && e.EventType == RunEventType.RunFailed)
            .SingleAsync();

        using var summary = JsonDocument.Parse(failure.ArgumentsSummary!);

        Assert.Equal("Testing", summary.RootElement.GetProperty("failureStage").GetString());
        Assert.Equal(
            nameof(OutcomeReason.RevisionLimitReached),
            summary.RootElement.GetProperty("outcomeReason").GetString());
    }

    [RequiresDockerFact]
    public async Task AnIllegalTransitionIsRecordedRatherThanSilentlyRefused()
    {
        await using var db = postgres.CreateContext();

        var fixture = new RepositoryFixture
        {
            Slug = "illegal-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Illegal transition fixture",
            RootPath = Path.GetTempPath(),
            TestConfigJson = """{"slug":"illegal","commands":[]}""",
        };

        // Already succeeded: nothing follows a terminal stage.
        var run = new Run
        {
            RepositoryId = fixture.Id,
            TaskDescription = "Already finished.",
            Stage = RunStage.Succeeded,
            TerminalOutcome = TerminalOutcome.Succeeded,
            // The schema requires a terminal run to carry a reason; a row
            // without one is refused by a check constraint, which is the
            // database enforcing FR-008c rather than trusting callers.
            OutcomeReason = OutcomeReason.Completed,
            EndedAt = DateTimeOffset.UtcNow,
        };

        db.Repositories.Add(fixture);
        db.Runs.Add(run);
        await db.SaveChangesAsync();

        var recorder = new RunEventRecorder(
            new EfRunEventStore(db), new InProcessRunEventPublisher());

        var orchestrator = new RunOrchestrator(
            new EfRunStore(db),
            new EfRepositoryFixtureStore(db),
            new EfProposalStore(db),
            new EfApprovalStore(db),
            _workspaces,
            ScriptedAgent.ThatProposes(),
            new ScriptedCapabilities(new EfProposalStore(db), testsPass: true, recorder),
            recorder,
            Options.Create(new RunConcurrencyOptions()),
            Options.Create(new RetrievalOptions()),
            new ScriptedBaselineContext());

        await Assert.ThrowsAsync<IllegalTransitionException>(
            () => orchestrator.RecordRejectionAsync(run.Id));

        await using var audit = postgres.CreateContext();

        var rejected = await audit.RunEvents
            .Where(e => e.RunId == run.Id && e.EventType == RunEventType.StageTransitionRejected)
            .SingleAsync();

        // FR-009: recorded, then thrown. Throwing alone would leave the attempt
        // visible only in a log the audit trail does not include.
        Assert.Equal(RunEventStatus.Failed, rejected.Status);

        using var summary = JsonDocument.Parse(rejected.ArgumentsSummary!);
        Assert.Equal("Succeeded", summary.RootElement.GetProperty("fromStage").GetString());
        Assert.Equal("Rejected", summary.RootElement.GetProperty("attemptedTrigger").GetString());
    }

    private static string? TriggerOf(RunEvent recorded)
    {
        if (recorded.ArgumentsSummary is null)
        {
            return null;
        }

        using var summary = JsonDocument.Parse(recorded.ArgumentsSummary);

        return summary.RootElement.TryGetProperty("trigger", out var trigger)
            ? trigger.GetString()
            : null;
    }
}
