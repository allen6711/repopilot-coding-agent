using Microsoft.Extensions.Logging;
using RepoPilot.Application.Ports;
using RepoPilot.Evals.Metrics;
using RepoPilot.Evals.Reporting;
using RepoPilot.Evals.Tasks;
using RepoPilot.Infrastructure.Retrieval;

namespace RepoPilot.Evals.Modes;

/// <summary>
/// Measures Recall@5 over the committed task set and nothing else (FR-032,
/// SC-005).
/// <para>
/// This exists because of what the retrieval half of an evaluation needs, which
/// is less than the whole. Embeddings come from
/// <c>DeterministicEmbeddingAdapter</c>, so retrieval is a function of committed
/// fixture content alone — no model provider, no credential, no token spend, no
/// sandbox, no Docker daemon. Recall@5 was nonetheless obtainable only by running
/// the full harness, which calls a chat provider once per task per condition, so
/// the one figure the committed fixtures already determine could not be produced
/// without a credential. The README said "Not measured" for a number that was
/// sitting in the repository.
/// </para>
/// <para>
/// It deliberately records no <c>EvaluationRun</c> and creates no runs. Nothing
/// here measures the agent — there is no proposal, no approval, and no applied
/// change — and storing this beside a real evaluation would invite a reader to
/// compare rows that measured different things. The report it writes says
/// retrieval in its name and carries only retrieval figures.
/// </para>
/// </summary>
public sealed class RetrievalOnlyEvaluation(
    EvaluationTaskLoader tasks,
    IRepositoryFixtureStore repositories,
    HybridRetriever retriever,
    ReportWriter reports,
    ILogger<RetrievalOnlyEvaluation> logger)
{
    /// <summary>
    /// Measures every committed task and writes the report.
    /// </summary>
    /// <param name="reportPath">
    /// An explicit file, or null for a generated name under the results
    /// directory.
    /// </param>
    /// <returns>The report, and the path it was written to.</returns>
    /// <exception cref="EvaluationRefusedException">
    /// The committed task set is empty, or a task names a fixture with no active
    /// index.
    /// </exception>
    public async Task<(RetrievalReport Report, string Path)> MeasureAsync(
        string? reportPath = null, CancellationToken ct = default)
    {
        var definitions = await tasks.LoadAsync(ct);

        if (definitions.Count == 0)
        {
            throw new EvaluationRefusedException(
                $"No committed tasks were found under {tasks.TasksDirectory}.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var lines = new List<RetrievalTaskLine>(definitions.Count);

        foreach (var task in definitions)
        {
            ct.ThrowIfCancellationRequested();

            var fixture = await repositories.FindBySlugAsync(task.RepositorySlug, ct)
                ?? throw new EvaluationRefusedException(
                    $"Task '{task.Id}' names fixture '{task.RepositorySlug}', which is not " +
                    "registered.");

            // Refused rather than indexed here, for the same reason the full
            // harness refuses: SC-007 requires a repeat measurement over
            // unchanged fixtures to reproduce the figure, and a mode that built
            // an index would be measuring a different index each time.
            if (fixture.ActiveIndexVersion is null || fixture.IncludedFileCount == 0)
            {
                throw new EvaluationRefusedException(
                    $"Fixture '{fixture.Slug}' has no active index. Index it before measuring: " +
                    "this never builds one, so that a repeat reads the same vectors (SC-007).");
            }

            var (hit, topPaths) = await RecallAt5.MeasureAsync(retriever, fixture.Id, task, ct);

            lines.Add(new RetrievalTaskLine(
                task.Id, fixture.Slug, hit, task.RelevantFiles, topPaths));
        }

        var hits = lines.Count(l => l.RelevantFileInTop5);

        var report = new RetrievalReport(
            MeasuredAt: startedAt,
            TaskSetSize: definitions.Count,
            RecallDepth: RecallAt5.Depth,
            RelevantFileInTopFive: hits,

            // Rounded at four places, which is finer than any figure this is
            // quoted at and coarse enough that two equal measurements serialise
            // to equal text.
            RecallAt5: Math.Round((decimal)hits / definitions.Count, 4),
            Tasks: [.. lines.OrderBy(l => l.TaskId, StringComparer.Ordinal)]);

        var path = await reports.WriteAsync(report, reportPath, ct);

        logger.LogInformation(
            "Recall@{Depth} {Recall} over {Tasks} committed tasks ({Hits} hits). Written to {Path}.",
            RecallAt5.Depth,
            report.RecallAt5,
            report.TaskSetSize,
            hits,
            path);

        return (report, path);
    }
}
