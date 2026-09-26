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

/// <summary>One task's line in a retrieval-only report.</summary>
/// <param name="TaskId">Which committed task.</param>
/// <param name="RepositorySlug">The fixture it was measured against.</param>
/// <param name="RelevantFileInTop5">Whether a relevant file was in the top five.</param>
/// <param name="RelevantFiles">What the committed task names as relevant.</param>
/// <param name="TopPaths">
/// The paths the criterion considered, in rank order. Present so a miss can be
/// read rather than re-derived: a reader seeing which five files came back
/// instead can tell a retrieval problem from a task whose relevant files are
/// wrong.
/// </param>
public sealed record RetrievalTaskLine(
    string TaskId,
    string RepositorySlug,
    bool RelevantFileInTop5,
    IReadOnlyList<string> RelevantFiles,
    IReadOnlyList<string> TopPaths);

/// <summary>
/// A retrieval-only measurement (FR-032, SC-005).
/// </summary>
/// <remarks>
/// Separate from <see cref="EvaluationReport"/> and deliberately not a subset of
/// it. This measures the retriever; that measures the agent. A single shape
/// carrying both with the agent's fields left null would put a reader one missing
/// value away from reading a retrieval measurement as a completion rate of zero.
/// </remarks>
/// <param name="MeasuredAt">When the measurement ran.</param>
/// <param name="TaskSetSize">How many committed tasks were loaded.</param>
/// <param name="RecallDepth">The depth the criterion is stated at.</param>
/// <param name="RelevantFileInTopFive">How many tasks hit.</param>
/// <param name="RecallAt5">The reported share (SC-005).</param>
/// <param name="Tasks">Per-task lines, ordered so a repeat produces the same file.</param>
public sealed record RetrievalReport(
    DateTimeOffset MeasuredAt,
    int TaskSetSize,
    int RecallDepth,
    int RelevantFileInTopFive,
    decimal RecallAt5,
    IReadOnlyList<RetrievalTaskLine> Tasks);

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
    public Task<string> WriteAsync(
        EvaluationReport report, string? destination = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Sortable, collision-free, and readable in a directory listing. The
        // evaluation id is in the name as well as the body so a report can be
        // matched to its stored run without opening it.
        return WriteJsonAsync(
            report,
            destination,
            $"{report.StartedAt.UtcDateTime:yyyyMMdd-HHmmss}-{report.EvaluationId:N}.json",
            ct);
    }

    /// <summary>
    /// Writes a retrieval-only report and returns the path it was written to.
    /// </summary>
    /// <remarks>
    /// The name says <c>retrieval</c> rather than carrying an id, because there is
    /// no evaluation run to match it to — and because a directory holding both
    /// kinds should say which is which without either being opened.
    /// </remarks>
    public Task<string> WriteAsync(
        RetrievalReport report, string? destination = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        return WriteJsonAsync(
            report,
            destination,
            $"{report.MeasuredAt.UtcDateTime:yyyyMMdd-HHmmss}-retrieval.json",
            ct);
    }

    private async Task<string> WriteJsonAsync<T>(
        T report, string? destination, string generatedName, CancellationToken ct)
    {
        string path;

        if (string.IsNullOrWhiteSpace(destination))
        {
            Directory.CreateDirectory(ResultsDirectory);
            path = Path.Combine(ResultsDirectory, generatedName);
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
