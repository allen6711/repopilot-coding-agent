using System.Text.Json;
using System.Text.Json.Serialization;
using RepoPilot.Domain.Entities;
using RepoPilot.Evals.Metrics;

namespace RepoPilot.Evals.Reporting;

/// <summary>One task's line in the committed report.</summary>
/// <param name="TaskId">Which committed task.</param>
/// <param name="Mode">Which condition.</param>
/// <param name="RunId">The run this line describes, so it can be reconstructed.</param>
/// <param name="RelevantFileInTop5">Retrieval hit.</param>
/// <param name="SuccessConditionMet">Task completion.</param>
/// <param name="ToolCallCount">Recorded capability invocations.</param>
/// <param name="DurationMs">Wall-clock duration.</param>
public sealed record TaskReportLine(
    string TaskId,
    string Mode,
    Guid RunId,
    bool RelevantFileInTop5,
    bool SuccessConditionMet,
    int ToolCallCount,
    int DurationMs);

/// <summary>
/// The committed evaluation report.
/// </summary>
/// <param name="EvaluationId">Identifies the evaluation run these figures came from.</param>
/// <param name="StartedAt">When it began.</param>
/// <param name="EndedAt">When it finished.</param>
/// <param name="TaskSetSize">How many committed tasks were loaded.</param>
/// <param name="Metrics">The reported figures.</param>
/// <param name="ApprovalCoverageFlagged">Whether the release gate failed (SC-001).</param>
/// <param name="ApprovalCoverageDetail">What the gate found.</param>
/// <param name="Refusals">Workspace-confinement figures across the set (SC-010).</param>
/// <param name="Tasks">Per-task lines, so a figure can be traced to the runs behind it.</param>
public sealed record EvaluationReport(
    Guid EvaluationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int TaskSetSize,
    EvaluationMetrics Metrics,
    bool ApprovalCoverageFlagged,
    string ApprovalCoverageDetail,
    RefusalSummary Refusals,
    IReadOnlyList<TaskReportLine> Tasks);

/// <summary>
/// Writes the report to <c>evals/results/</c> (FR-032).
/// <para>
/// Committed rather than printed. Principle V requires published figures to be
/// measured ones, and a figure quoted in a README has to be traceable to the run
/// that produced it — so the report carries the per-task lines and the run ids,
/// not just the summary.
/// </para>
/// </summary>
public sealed class ReportWriter(string resultsDirectory)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Where reports are written.</summary>
    public string ResultsDirectory { get; } = Path.GetFullPath(resultsDirectory);

    /// <summary>
    /// Writes <paramref name="report"/> and returns the path it was written to.
    /// </summary>
    /// <param name="destination">
    /// An explicit file to write, overriding the generated name. Supplied by the
    /// CLI's <c>--output</c>; null uses <see cref="ResultsDirectory"/> and a
    /// generated name.
    /// </param>
    public async Task<string> WriteAsync(
        EvaluationReport report, string? destination = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        string path;

        if (string.IsNullOrWhiteSpace(destination))
        {
            Directory.CreateDirectory(ResultsDirectory);

            // Sortable, collision-free, and readable in a directory listing. The
            // evaluation id is in the name as well as the body so a report can be
            // matched to its stored run without opening it.
            path = Path.Combine(
                ResultsDirectory,
                $"{report.StartedAt.UtcDateTime:yyyyMMdd-HHmmss}-{report.EvaluationId:N}.json");
        }
        else
        {
            path = Path.GetFullPath(destination);

            // The caller named a file, so its directory is the caller's intent
            // too. Failing because a parent does not exist would be a worse
            // ending than creating it, after an evaluation that already ran.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Options), ct);

        return path;
    }

    /// <summary>Builds a report from an evaluation run and its measurements.</summary>
    public static EvaluationReport Build(
        EvaluationRun evaluation,
        int taskSetSize,
        EvaluationMetrics metrics,
        ApprovalCoverageVerdict verdict,
        RefusalSummary refusals,
        IReadOnlyList<TaskReportLine> tasks) =>
        new(
            evaluation.Id,
            evaluation.StartedAt,
            evaluation.EndedAt,
            taskSetSize,
            metrics,
            verdict.Flagged,
            verdict.Detail,
            refusals,

            // Ordered so two evaluations over an unchanged task set produce the
            // same file, which is what makes SC-007 a diff rather than a reading
            // exercise.
            [.. tasks.OrderBy(t => t.TaskId, StringComparer.Ordinal).ThenBy(t => t.Mode, StringComparer.Ordinal)]);
}
