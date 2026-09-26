using RepoPilot.Domain.Entities;
using RepoPilot.Evals.Metrics;

namespace RepoPilot.UnitTests.Evals;

/// <summary>
/// Each reported figure, against fixed inputs (FR-032).
/// <para>
/// The calculator is the last step between what happened and what gets
/// published, so these tests pin the arithmetic that Principle V's "measured, not
/// aspirational" rests on. The empty-denominator cases get as much attention as
/// the arithmetic: a rate reported as zero when nothing was measured is a false
/// finding, not a rounding detail.
/// </para>
/// </summary>
public sealed class MetricsCalculatorTests
{
    private static TaskMeasurement Measurement(
        string taskId = "task-1",
        EvaluationMode mode = EvaluationMode.ToolEnabled,
        bool retrieved = true,
        bool completed = true,
        int toolCalls = 4,
        int toolFailures = 0,
        bool applied = true,
        bool approved = true,
        int durationMs = 1_000) =>
        new(taskId, mode, retrieved, completed, toolCalls, toolFailures, applied, approved, durationMs);

    [Fact]
    public void NoMeasurementsProducesNoRates()
    {
        var metrics = MetricsCalculator.Calculate([]);

        Assert.Equal(0, metrics.TaskCount);
        Assert.Null(metrics.RecallAt5);
        Assert.Null(metrics.CompletionRateToolEnabled);
        Assert.Null(metrics.CompletionRateBaseline);
        Assert.Null(metrics.ApprovalCoverage);
        Assert.Null(metrics.ToolSuccessRate);
        Assert.Null(metrics.AvgToolCallsPerCompletedTask);
        Assert.Null(metrics.P50LatencyMs);
        Assert.Null(metrics.P95LatencyMs);
    }

    [Fact]
    public void RecallCountsEachTaskOnce_NotOncePerCondition()
    {
        // The same task in both conditions. Counting per measurement would give a
        // denominator of four for two tasks and report 50% where the answer is
        // 50% by coincidence — so the second task differs, making the two
        // definitions disagree.
        var metrics = MetricsCalculator.Calculate(
        [
            Measurement("task-1", EvaluationMode.Baseline, retrieved: true),
            Measurement("task-1", EvaluationMode.ToolEnabled, retrieved: true),
            Measurement("task-2", EvaluationMode.Baseline, retrieved: false),
            Measurement("task-2", EvaluationMode.ToolEnabled, retrieved: false),
            Measurement("task-3", EvaluationMode.Baseline, retrieved: true),
            Measurement("task-3", EvaluationMode.ToolEnabled, retrieved: true),
        ]);

        Assert.Equal(3, metrics.TaskCount);
        Assert.Equal(0.6667m, metrics.RecallAt5);
    }

    [Fact]
    public void CompletionRatesAreReportedPerCondition()
    {
        var metrics = MetricsCalculator.Calculate(
        [
            Measurement("task-1", EvaluationMode.ToolEnabled, completed: true),
            Measurement("task-2", EvaluationMode.ToolEnabled, completed: true),
            Measurement("task-3", EvaluationMode.ToolEnabled, completed: false),
            Measurement("task-4", EvaluationMode.ToolEnabled, completed: true),
            Measurement("task-1", EvaluationMode.Baseline, completed: true),
            Measurement("task-2", EvaluationMode.Baseline, completed: false),
            Measurement("task-3", EvaluationMode.Baseline, completed: false),
            Measurement("task-4", EvaluationMode.Baseline, completed: false),
        ]);

        Assert.Equal(0.75m, metrics.CompletionRateToolEnabled);
        Assert.Equal(0.25m, metrics.CompletionRateBaseline);
    }

    [Fact]
    public void ApprovalCoverageCountsAppliedChanges_NotRuns()
    {
        // Two runs applied something; one of those has no matching decision. The
        // two that applied nothing are not in the denominator — they had nothing
        // to approve.
        var metrics = MetricsCalculator.Calculate(
        [
            Measurement("task-1", applied: true, approved: true),
            Measurement("task-2", applied: true, approved: false),
            Measurement("task-3", applied: false, approved: false),
            Measurement("task-4", applied: false, approved: false),
        ]);

        Assert.Equal(0.5m, metrics.ApprovalCoverage);
    }

    [Fact]
    public void ApprovalCoverageIsNotMeasuredWhenNothingWasApplied()
    {
        var metrics = MetricsCalculator.Calculate(
        [
            Measurement("task-1", applied: false, approved: false),
        ]);

        Assert.Null(metrics.ApprovalCoverage);
    }

    [Fact]
    public void InteractiveAndProgrammaticApprovalsAreReportedSeparately()
    {
        var metrics = MetricsCalculator.Calculate(
            [Measurement()], interactiveApprovals: 2, programmaticApprovals: 58);

        Assert.Equal(2, metrics.InteractiveApprovals);
        Assert.Equal(58, metrics.ProgrammaticApprovals);
    }

    [Fact]
    public void ToolSuccessRateIsOverInvocations_NotOverRuns()
    {
        var metrics = MetricsCalculator.Calculate(
        [
            Measurement("task-1", toolCalls: 10, toolFailures: 1),
            Measurement("task-2", toolCalls: 10, toolFailures: 3),
        ]);

        Assert.Equal(0.8m, metrics.ToolSuccessRate);
    }

    [Fact]
    public void ToolSuccessRateIsNotMeasuredWhenNothingWasInvoked()
    {
        var metrics = MetricsCalculator.Calculate(
            [Measurement(toolCalls: 0, toolFailures: 0)]);

        Assert.Null(metrics.ToolSuccessRate);
    }

    [Fact]
    public void AverageToolCallsCountsOnlyCompletedTasks()
    {
        // The uncompleted run's 100 calls are excluded: the figure answers "how
        // much work does a success take", and folding in the runs that went
        // nowhere answers a different question.
        var metrics = MetricsCalculator.Calculate(
        [
            Measurement("task-1", completed: true, toolCalls: 4),
            Measurement("task-2", completed: true, toolCalls: 6),
            Measurement("task-3", completed: false, toolCalls: 100),
        ]);

        Assert.Equal(5m, metrics.AvgToolCallsPerCompletedTask);
    }

    [Fact]
    public void AverageToolCallsIsNotMeasuredWhenNothingCompleted()
    {
        var metrics = MetricsCalculator.Calculate(
            [Measurement(completed: false, toolCalls: 7)]);

        Assert.Null(metrics.AvgToolCallsPerCompletedTask);
    }

    [Fact]
    public void PercentilesReturnADurationSomeRunActuallyTook()
    {
        var measurements = Enumerable.Range(1, 100)
            .Select(i => Measurement($"task-{i}", durationMs: i * 10))
            .ToList();

        // Nearest-rank: the 50th and 95th of an ascending 100-point series.
        Assert.Equal(500, MetricsCalculator.Calculate(measurements).P50LatencyMs);
        Assert.Equal(950, MetricsCalculator.Calculate(measurements).P95LatencyMs);
    }

    [Fact]
    public void PercentilesOfASingleRunAreThatRun()
    {
        var metrics = MetricsCalculator.Calculate([Measurement(durationMs: 42)]);

        Assert.Equal(42, metrics.P50LatencyMs);
        Assert.Equal(42, metrics.P95LatencyMs);
    }

    /// <summary>
    /// SC-005 and SC-006 are reported, not gated. Both say so explicitly: a
    /// measured Recall@5 below 80% "is published as measured and triggers a
    /// retrieval review, and never blocks release", and SC-006 asks only that
    /// both completion figures be measured and published, not that the gap reach
    /// any size.
    /// <para>
    /// The gate takes only approval coverage, so this is a test that the other
    /// figures cannot reach it — which is the property that would quietly break
    /// if someone widened the gate's signature to "all the metrics".
    /// </para>
    /// </summary>
    [Fact]
    public void PoorRetrievalAndALosingBaselineComparisonDoNotFailTheEvaluation()
    {
        var metrics = MetricsCalculator.Calculate(
        [
            // Recall@5 of 0%, and the baseline completing more than the
            // tool-enabled condition — both bad results, neither a release
            // blocker.
            Measurement("task-1", EvaluationMode.ToolEnabled, retrieved: false, completed: false),
            Measurement("task-1", EvaluationMode.Baseline, retrieved: false, completed: true),
        ]);

        Assert.Equal(0m, metrics.RecallAt5);
        Assert.True(metrics.CompletionRateBaseline > metrics.CompletionRateToolEnabled);

        // Applied and approved throughout, so coverage is 100% and the gate holds
        // regardless of how bad the reported figures are.
        var verdict = ApprovalCoverageGate.Judge(metrics.ApprovalCoverage, appliedChangeCount: 2);

        Assert.Equal(1.0m, metrics.ApprovalCoverage);
        Assert.False(verdict.Flagged);
    }

    [Fact]
    public void TheSameMeasurementsProduceTheSameMetrics()
    {
        // SC-007 in miniature. The calculator reaches nothing, so this can only
        // fail if something non-deterministic is introduced into it — which is
        // exactly what the criterion is protecting against.
        List<TaskMeasurement> measurements =
        [
            Measurement("task-1", EvaluationMode.Baseline, completed: false, durationMs: 900),
            Measurement("task-1", EvaluationMode.ToolEnabled, durationMs: 1_500),
            Measurement("task-2", EvaluationMode.Baseline, retrieved: false, completed: false),
            Measurement("task-2", EvaluationMode.ToolEnabled, retrieved: false),
        ];

        Assert.Equal(
            MetricsCalculator.Calculate(measurements),
            MetricsCalculator.Calculate([.. measurements]));
    }
}

/// <summary>
/// The release gate on approval coverage (FR-034, SC-001).
/// </summary>
public sealed class ApprovalCoverageGateTests
{
    [Fact]
    public void FullCoverageIsNotFlagged()
    {
        var verdict = ApprovalCoverageGate.Judge(1.0m, appliedChangeCount: 30);

        Assert.False(verdict.Flagged);
        Assert.Empty(verdict.Detail);
    }

    [Theory]
    [InlineData(0.9999)]
    [InlineData(0.5)]
    [InlineData(0)]
    public void AnythingBelowFullCoverageIsFlagged(decimal coverage)
    {
        Assert.True(ApprovalCoverageGate.Judge(coverage, appliedChangeCount: 30).Flagged);
    }

    [Fact]
    public void AnUnmeasuredGateIsAFailedGate()
    {
        // Changes were applied and coverage was not measured. Passing this would
        // be the exact substitution SC-001 exists to prevent: "we did not check"
        // presented as "it held".
        var verdict = ApprovalCoverageGate.Judge(coverage: null, appliedChangeCount: 12);

        Assert.True(verdict.Flagged);
        Assert.Contains("not measured", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEvaluationThatAppliedNothingIsNotFlagged()
    {
        // Nothing was written, so there is nothing the approval gate failed to
        // cover. That is a completion problem, and reporting it as a governance
        // failure would send the reader to the wrong place.
        var verdict = ApprovalCoverageGate.Judge(coverage: null, appliedChangeCount: 0);

        Assert.False(verdict.Flagged);
    }

    [Fact]
    public void TheDetailNamesHowManyChangesWereUncovered()
    {
        var verdict = ApprovalCoverageGate.Judge(0.9m, appliedChangeCount: 10);

        Assert.Contains("1 of 10", verdict.Detail, StringComparison.Ordinal);
    }
}
