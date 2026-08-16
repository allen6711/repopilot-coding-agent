using RepoPilot.Domain.Entities;

namespace RepoPilot.Evals.Metrics;

/// <summary>
/// One task's raw measurements, as the harness observed them.
/// </summary>
/// <param name="TaskId">Which committed task.</param>
/// <param name="Mode">Which condition produced this result.</param>
/// <param name="RelevantFileInTop5">
/// Whether a file the task names as relevant appeared in the top five retrieved
/// results (FR-032).
/// </param>
/// <param name="SuccessConditionMet">Whether the task's own condition was satisfied.</param>
/// <param name="ToolCallCount">Recorded capability invocations for the run.</param>
/// <param name="ToolCallFailures">How many of those were recorded as failed.</param>
/// <param name="AppliedChange">
/// Whether anything was written to the working copy. Approval coverage counts
/// applied changes, not runs — a run that proposed nothing had nothing to approve
/// and neither passes nor fails the check.
/// </param>
/// <param name="HadApprovalRecord">
/// Whether a decision record exists whose hash matches the applied change.
/// </param>
/// <param name="DurationMs">Wall-clock duration of the run.</param>
public sealed record TaskMeasurement(
    string TaskId,
    EvaluationMode Mode,
    bool RelevantFileInTop5,
    bool SuccessConditionMet,
    int ToolCallCount,
    int ToolCallFailures,
    bool AppliedChange,
    bool HadApprovalRecord,
    int DurationMs);

/// <summary>
/// The figures an evaluation reports (FR-032, FR-033, FR-034).
/// </summary>
/// <param name="TaskCount">Distinct tasks measured.</param>
/// <param name="RecallAt5">
/// Share of tasks where a relevant file was in the top five (SC-005). Measured
/// once per task rather than per mode: it is a property of the index and the
/// query, and both conditions issue the same query.
/// </param>
/// <param name="CompletionRateToolEnabled">Share of tool-enabled tasks completed.</param>
/// <param name="CompletionRateBaseline">Share of retrieval-only tasks completed.</param>
/// <param name="ApprovalCoverage">
/// Share of applied changes carrying a matching decision record (SC-001). Null
/// when nothing was applied — reporting 100% for an evaluation that wrote nothing
/// would claim a check that never ran.
/// </param>
/// <param name="InteractiveApprovals">Decisions a person made.</param>
/// <param name="ProgrammaticApprovals">
/// Decisions the harness made. Reported separately so a programmatic approval is
/// never presented as a human one (SC-001).
/// </param>
/// <param name="ToolSuccessRate">Share of capability invocations that succeeded.</param>
/// <param name="AvgToolCallsPerCompletedTask">Tool calls per completed task.</param>
/// <param name="P50LatencyMs">Median run duration.</param>
/// <param name="P95LatencyMs">95th percentile run duration.</param>
public sealed record EvaluationMetrics(
    int TaskCount,
    decimal? RecallAt5,
    decimal? CompletionRateToolEnabled,
    decimal? CompletionRateBaseline,
    decimal? ApprovalCoverage,
    int InteractiveApprovals,
    int ProgrammaticApprovals,
    decimal? ToolSuccessRate,
    decimal? AvgToolCallsPerCompletedTask,
    int? P50LatencyMs,
    int? P95LatencyMs);

/// <summary>
/// Turns per-task measurements into the reported figures.
/// <para>
/// Pure and total: it takes measurements and returns numbers, reaching nothing.
/// That is what makes SC-007 checkable — two evaluations over unchanged fixtures
/// producing the same measurements must produce the same metrics, and a
/// calculator that read a clock or a database could not promise that.
/// </para>
/// <para>
/// Every rate is null rather than zero when its denominator is empty. A reported
/// 0% and "there was nothing to measure" are different findings, and collapsing
/// them is how an evaluation comes to look worse, or better, than it was.
/// </para>
/// </summary>
public static class MetricsCalculator
{
    /// <summary>Computes the reported figures.</summary>
    /// <param name="measurements">Every task result across both modes.</param>
    /// <param name="interactiveApprovals">Human decisions recorded during the evaluation.</param>
    /// <param name="programmaticApprovals">Harness decisions recorded during the evaluation.</param>
    public static EvaluationMetrics Calculate(
        IReadOnlyList<TaskMeasurement> measurements,
        int interactiveApprovals = 0,
        int programmaticApprovals = 0)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        var toolEnabled = measurements
            .Where(m => m.Mode == EvaluationMode.ToolEnabled).ToList();
        var baseline = measurements
            .Where(m => m.Mode == EvaluationMode.Baseline).ToList();

        // Retrieval is measured once per task. Counting it per mode would double
        // every task that ran in both conditions and report a rate over a
        // denominator no one asked about.
        var byTask = measurements
            .GroupBy(m => m.TaskId, StringComparer.Ordinal)
            .Select(g => g.Any(m => m.RelevantFileInTop5))
            .ToList();

        var applied = measurements.Where(m => m.AppliedChange).ToList();
        var completed = measurements.Where(m => m.SuccessConditionMet).ToList();

        var totalToolCalls = measurements.Sum(m => m.ToolCallCount);
        var totalFailures = measurements.Sum(m => m.ToolCallFailures);

        var durations = measurements
            .Select(m => m.DurationMs)
            .OrderBy(d => d)
            .ToList();

        return new EvaluationMetrics(
            TaskCount: measurements
                .Select(m => m.TaskId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            RecallAt5: Share(byTask.Count(hit => hit), byTask.Count),
            CompletionRateToolEnabled: Share(
                toolEnabled.Count(m => m.SuccessConditionMet), toolEnabled.Count),
            CompletionRateBaseline: Share(
                baseline.Count(m => m.SuccessConditionMet), baseline.Count),
            ApprovalCoverage: Share(applied.Count(m => m.HadApprovalRecord), applied.Count),
            InteractiveApprovals: interactiveApprovals,
            ProgrammaticApprovals: programmaticApprovals,
            ToolSuccessRate: Share(totalToolCalls - totalFailures, totalToolCalls),
            AvgToolCallsPerCompletedTask: completed.Count == 0
                ? null
                : Round((decimal)completed.Sum(m => m.ToolCallCount) / completed.Count),
            P50LatencyMs: Percentile(durations, 0.50),
            P95LatencyMs: Percentile(durations, 0.95));
    }

    /// <summary>
    /// A rate, or null when nothing was measured.
    /// </summary>
    private static decimal? Share(int numerator, int denominator) =>
        denominator == 0 ? null : Round((decimal)numerator / denominator);

    /// <summary>
    /// Rounded to four places, away from zero.
    /// <para>
    /// Fixed here rather than at the point of display so that two evaluations
    /// producing the same measurements produce byte-identical reports, which is
    /// what SC-007's "identical" is compared with.
    /// </para>
    /// </summary>
    private static decimal Round(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The nearest-rank percentile of an ascending series.
    /// <para>
    /// Nearest-rank rather than an interpolating definition: it always returns a
    /// value that actually occurred, so a reported p95 latency is a duration some
    /// run really took rather than one between two runs.
    /// </para>
    /// </summary>
    private static int? Percentile(IReadOnlyList<int> ascending, double percentile)
    {
        if (ascending.Count == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(percentile * ascending.Count);

        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }
}
