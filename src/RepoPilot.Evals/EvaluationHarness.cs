using Microsoft.Extensions.Logging;
using RepoPilot.Application.Approval;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Evals.Metrics;
using RepoPilot.Evals.Modes;
using RepoPilot.Evals.Reporting;
using RepoPilot.Evals.Tasks;
using RepoPilot.Infrastructure.Retrieval;

namespace RepoPilot.Evals;

/// <summary>
/// Raised when an evaluation cannot run against the system as it stands.
/// </summary>
public sealed class EvaluationRefusedException(string message) : Exception(message);

/// <summary>
/// Runs the committed task set and reports what happened (FR-031 to FR-035).
/// <para>
/// Every task goes through <see cref="RunOrchestrator"/> — the same type an
/// interactive run goes through, taking the same stage transitions, gated by the
/// same approval check (FR-034). A separate, faster evaluation path would be
/// measuring something other than the system, and the resulting numbers would
/// describe the harness.
/// </para>
/// <para>
/// The only thing the harness substitutes is the person at the approval gate, and
/// it substitutes them with <see cref="ProgrammaticApproval"/>, which still writes
/// a real decision record bound to the diff hash and marks it as programmatic
/// (FR-015b, SC-001).
/// </para>
/// </summary>
public sealed class EvaluationHarness(
    EvaluationTaskLoader tasks,
    IRepositoryFixtureStore repositories,
    IRunStore runs,
    IProposalStore proposals,
    IApprovalStore approvals,
    IRunEventStore events,
    ITestResultStore testResults,
    IEvaluationStore evaluations,
    RunOrchestrator orchestrator,
    ProgrammaticApproval approval,
    FixtureBaselineProbe baseline,
    HybridRetriever retriever,
    ReportWriter reports,
    ILogger<EvaluationHarness> logger)
{
    /// <summary>
    /// Approvals a run may need before it ends. A revision produces a fresh
    /// proposal needing its own approval (FR-013), and the revision limit is two,
    /// so three is the most any run can legitimately ask for.
    /// </summary>
    private const int MaxApprovalsPerRun = 3;

    /// <summary>
    /// Creates and persists the evaluation record, before any task runs.
    /// <para>
    /// Separate from <see cref="ExecuteAsync"/> so a caller that returns before
    /// the evaluation finishes — the HTTP endpoint — can hand back an id that is
    /// already readable. Returning an id whose row appears minutes later would
    /// make a poll indistinguishable from a lost evaluation.
    /// </para>
    /// </summary>
    /// <exception cref="EvaluationRefusedException">The committed task set is empty.</exception>
    public async Task<EvaluationRun> BeginAsync(CancellationToken ct = default)
    {
        var definitions = await tasks.LoadAsync(ct);

        if (definitions.Count == 0)
        {
            throw new EvaluationRefusedException(
                $"No committed tasks were found under {tasks.TasksDirectory}.");
        }

        var evaluation = new EvaluationRun { TaskCount = definitions.Count };
        await evaluations.AddAsync(evaluation, ct);

        return evaluation;
    }

    /// <summary>
    /// Runs every committed task in both conditions and writes the report.
    /// </summary>
    /// <returns>The evaluation run, with its metrics.</returns>
    /// <param name="reportPath">
    /// An explicit file for the report, or null for a generated name under the
    /// configured results directory.
    /// </param>
    public async Task<EvaluationRun> RunAsync(
        string? reportPath = null, CancellationToken ct = default) =>
        await ExecuteAsync(await BeginAsync(ct), reportPath, ct);

    /// <summary>
    /// Runs the committed task set against an evaluation record already created
    /// by <see cref="BeginAsync"/>.
    /// </summary>
    public async Task<EvaluationRun> ExecuteAsync(
        EvaluationRun evaluation, string? reportPath = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        var definitions = await tasks.LoadAsync(ct);

        var measurements = new List<TaskMeasurement>(definitions.Count * 2);
        var lines = new List<TaskReportLine>(definitions.Count * 2);

        // Kept for the refusal report. Every recorded event from every run in the
        // evaluation, because SC-010's count is across the set rather than per
        // run and a per-run total could not distinguish one run refusing thirty
        // paths from thirty runs refusing one.
        var recorded = new List<RunEvent>();

        foreach (var task in definitions)
        {
            var fixture = await RequireIndexedFixtureAsync(task, ct);

            // Measured once per task, not once per condition. The query is the
            // task description in both conditions, and the index does not change
            // between them, so a second measurement could only differ by noise.
            var (retrievalHit, _) = await RecallAt5.MeasureAsync(retriever, fixture.Id, task, ct);

            var baselineFailed = await baseline.FailsBeforeChangeAsync(
                fixture, task.BaselineTestCommand, ct);

            foreach (var mode in BaselineMode.Conditions)
            {
                ct.ThrowIfCancellationRequested();

                var (measurement, line) = await RunOneAsync(
                    task, fixture, evaluation.Id, mode, retrievalHit, baselineFailed, recorded, ct);

                measurements.Add(measurement);
                lines.Add(line);

                await evaluations.AddResultAsync(
                    new EvaluationTaskResult
                    {
                        EvaluationRunId = evaluation.Id,
                        TaskId = measurement.TaskId,
                        Mode = measurement.Mode,
                        RunId = line.RunId,
                        RelevantFileInTop5 = measurement.RelevantFileInTop5,
                        SuccessConditionMet = measurement.SuccessConditionMet,
                        ToolCallCount = measurement.ToolCallCount,
                        DurationMs = measurement.DurationMs,
                    },
                    ct);
            }
        }

        var decisions = await CountDecisionsAsync(lines, ct);

        var metrics = MetricsCalculator.Calculate(
            measurements, decisions.Interactive, decisions.Programmatic);

        var verdict = ApprovalCoverageGate.Judge(
            metrics.ApprovalCoverage, measurements.Count(m => m.AppliedChange));

        var refusals = RefusalReport.Summarise(recorded);

        evaluation.EndedAt = DateTimeOffset.UtcNow;
        evaluation.RecallAt5 = metrics.RecallAt5;
        evaluation.CompletionRateToolEnabled = metrics.CompletionRateToolEnabled;
        evaluation.CompletionRateBaseline = metrics.CompletionRateBaseline;
        evaluation.ApprovalCoverage = metrics.ApprovalCoverage;
        evaluation.InteractiveApprovals = metrics.InteractiveApprovals;
        evaluation.ProgrammaticApprovals = metrics.ProgrammaticApprovals;
        evaluation.ToolSuccessRate = metrics.ToolSuccessRate;
        evaluation.AvgToolCallsPerCompletedTask = metrics.AvgToolCallsPerCompletedTask;
        evaluation.P50LatencyMs = metrics.P50LatencyMs;
        evaluation.P95LatencyMs = metrics.P95LatencyMs;
        evaluation.Flagged = verdict.Flagged;

        await evaluations.UpdateAsync(evaluation, ct);

        var path = await reports.WriteAsync(
            ReportWriter.Build(evaluation, definitions.Count, metrics, verdict, refusals, lines),
            reportPath,
            ct);

        logger.LogInformation("Evaluation {Id} written to {Path}.", evaluation.Id, path);

        if (verdict.Flagged)
        {
            logger.LogError("Evaluation {Id} is flagged: {Detail}", evaluation.Id, verdict.Detail);
        }

        logger.LogInformation(
            "Workspace confinement: {Refused} refused attempts across {Runs} runs, " +
            "{Escapes} successful escapes (SC-010).",
            refusals.RefusedAttempts, refusals.RunsWithRefusals, refusals.SuccessfulEscapes);

        return evaluation;
    }

    /// <summary>
    /// Runs one task in one condition, from creation to a terminal outcome.
    /// </summary>
    private async Task<(TaskMeasurement Measurement, TaskReportLine Line)> RunOneAsync(
        EvaluationTaskDefinition task,
        RepositoryFixture fixture,
        Guid evaluationId,
        EvaluationMode mode,
        bool retrievalHit,
        bool baselineFailed,
        List<RunEvent> recorded,
        CancellationToken ct)
    {
        var run = BaselineMode.RunFor(task, fixture.Id, evaluationId, mode);
        await runs.AddAsync(run, ct);

        try
        {
            var stage = await orchestrator.ExecuteUntilApprovalAsync(run.Id, ct);

            for (var approvals = 0;
                 stage == RunStage.AwaitingApproval && approvals < MaxApprovalsPerRun;
                 approvals++)
            {
                stage = await ApproveAndContinueAsync(run.Id, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One task failing is a result, not an evaluation failure. The
            // orchestrator has already recorded why the run failed; losing the
            // other twenty-nine tasks to it would turn a measurable outcome into
            // no measurement at all.
            logger.LogWarning(
                ex, "Task {Task} ({Mode}) ended in failure.", task.Id, mode);
        }

        return await MeasureAsync(task, run.Id, mode, retrievalHit, baselineFailed, recorded, ct);
    }

    /// <summary>
    /// Approves the run's current proposal and lets it apply and test.
    /// </summary>
    private async Task<RunStage> ApproveAndContinueAsync(Guid runId, CancellationToken ct)
    {
        var proposal = await proposals.FindCurrentForRunAsync(runId, ct)
            ?? throw new EvaluationRefusedException(
                $"Run {runId} is awaiting approval with no proposal to approve.");

        // The stored hash is echoed back, exactly as a reviewer's would be. Not a
        // formality: it is what makes the recorded decision bind to the content
        // that gets written (FR-020a), and it is the only thing separating this
        // from an unapproved write.
        await approval.ApproveAsync(proposal.Id, proposal.DiffHash, ct);

        return await orchestrator.ApplyAndTestAsync(runId, ct);
    }

    /// <summary>
    /// Reads back what a finished run recorded, and decides the task.
    /// <para>
    /// Everything here comes from stored rows rather than from what the run
    /// returned in memory. SC-008 says a completed run is reconstructable from
    /// recorded data alone; measuring from that data is how the harness holds
    /// itself to the same claim.
    /// </para>
    /// </summary>
    private async Task<(TaskMeasurement, TaskReportLine)> MeasureAsync(
        EvaluationTaskDefinition task,
        Guid runId,
        EvaluationMode mode,
        bool retrievalHit,
        bool baselineFailed,
        List<RunEvent> recorded,
        CancellationToken ct)
    {
        var run = await runs.FindAsync(runId, ct)
            ?? throw new EvaluationRefusedException($"Run {runId} disappeared mid-evaluation.");

        var runEvents = await events.ListAsync(runId, 0, ct);
        recorded.AddRange(runEvents);

        var toolCalls = runEvents.Where(e => e.EventType == RunEventType.ToolCall).ToList();

        var applyEvents = runEvents.Count(e =>
            e.ToolName == "apply_patch" && e.Status == RunEventStatus.Succeeded);

        var results = await testResults.ListForRunAsync(runId, ct);
        var last = results.Count == 0 ? null : results[^1];

        var met = last is not null &&
            task.SuccessCondition.IsMet(last.Passed, baselineFailed, last.Output);

        var duration = run.StartedAt is null || run.EndedAt is null
            ? 0
            : (int)(run.EndedAt.Value - run.StartedAt.Value).TotalMilliseconds;

        var measurement = new TaskMeasurement(
            TaskId: task.Id,
            Mode: mode,
            RelevantFileInTop5: retrievalHit,
            SuccessConditionMet: met,
            ToolCallCount: toolCalls.Count,
            ToolCallFailures: toolCalls.Count(e => e.Status == RunEventStatus.Failed),
            AppliedChange: applyEvents > 0,
            HadApprovalRecord: await EveryAppliedChangeWasApprovedAsync(runId, applyEvents, ct),
            DurationMs: duration);

        var line = new TaskReportLine(
            task.Id,
            mode.ToString(),
            runId,
            retrievalHit,
            met,
            toolCalls.Count,
            duration);

        return (measurement, line);
    }

    /// <summary>
    /// Whether every change this run wrote is covered by a matching decision
    /// (SC-001).
    /// <para>
    /// Checked against the stored records rather than inferred from the
    /// orchestrator having refused to write without one. The orchestrator's check
    /// is the control; this is the measurement of whether the control held, and a
    /// measurement that assumes its own answer measures nothing.
    /// </para>
    /// </summary>
    private async Task<bool> EveryAppliedChangeWasApprovedAsync(
        Guid runId, int applyEvents, CancellationToken ct)
    {
        if (applyEvents == 0)
        {
            return false;
        }

        var covered = 0;

        foreach (var proposal in await proposals.ListForRunAsync(runId, ct))
        {
            var decision = await approvals.FindByProposalAsync(proposal.Id, ct);

            if (decision is { Decision: ApprovalDecisionKind.Approve } &&
                string.Equals(decision.DiffHash, proposal.DiffHash, StringComparison.Ordinal))
            {
                covered++;
            }
        }

        return covered >= applyEvents;
    }

    /// <summary>
    /// The fixture a task names, refusing anything that would make the evaluation
    /// unrepeatable.
    /// <para>
    /// It never indexes. SC-007 requires a repeated evaluation over unchanged
    /// fixtures to reproduce identical retrieval metrics, and an evaluation that
    /// rebuilt an index would be comparing two different indexes — so a fixture
    /// that is not already indexed is refused rather than quietly prepared.
    /// </para>
    /// </summary>
    private async Task<RepositoryFixture> RequireIndexedFixtureAsync(
        EvaluationTaskDefinition task, CancellationToken ct)
    {
        var fixture = await repositories.FindBySlugAsync(task.RepositorySlug, ct)
            ?? throw new EvaluationRefusedException(
                $"Task '{task.Id}' names fixture '{task.RepositorySlug}', which is not registered.");

        if (fixture.ActiveIndexVersion is null || fixture.IncludedFileCount == 0)
        {
            throw new EvaluationRefusedException(
                $"Fixture '{fixture.Slug}' has no active index. Index it before evaluating: an " +
                "evaluation never builds one, so that a repeat run reads the same vectors (SC-007).");
        }

        return fixture;
    }

    private async Task<(int Interactive, int Programmatic)> CountDecisionsAsync(
        IReadOnlyList<TaskReportLine> lines, CancellationToken ct)
    {
        var interactive = 0;
        var programmatic = 0;

        foreach (var line in lines)
        {
            foreach (var proposal in await proposals.ListForRunAsync(line.RunId, ct))
            {
                var decision = await approvals.FindByProposalAsync(proposal.Id, ct);

                if (decision is null)
                {
                    continue;
                }

                // Counted separately and never summed into one "approved" figure.
                // SC-001 is explicit that a programmatic decision must never be
                // presented as a human one.
                if (decision.Mode == ApprovalMode.Interactive)
                {
                    interactive++;
                }
                else
                {
                    programmatic++;
                }
            }
        }

        return (interactive, programmatic);
    }
}
