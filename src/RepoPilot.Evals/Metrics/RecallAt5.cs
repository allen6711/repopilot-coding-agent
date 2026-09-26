using RepoPilot.Evals.Tasks;
using RepoPilot.Infrastructure.Retrieval;

namespace RepoPilot.Evals.Metrics;

/// <summary>
/// Whether a task's relevant files were retrieved (FR-032, SC-005).
/// <para>
/// One implementation, shared by the full harness and by the retrieval-only mode.
/// Two copies of this would be two answers to the same question, and Principle V
/// makes the published figure the one that matters — a README quoting a number
/// that a second code path computes differently is exactly the failure the
/// principle is about.
/// </para>
/// </summary>
public static class RecallAt5
{
    /// <summary>
    /// How many retrieved results the criterion looks at. Fixed by SC-005 rather
    /// than configurable: a configurable "top five" is not a top five.
    /// </summary>
    public const int Depth = 5;

    /// <summary>
    /// Whether any file the task names as relevant appears in the results.
    /// </summary>
    /// <remarks>
    /// Separators are normalised on both sides, because a task definition is
    /// committed with forward slashes and a result's path comes from the host's
    /// filesystem. Comparison is case-insensitive for the same reason: a fixture
    /// indexed on a case-insensitive filesystem would otherwise miss.
    /// </remarks>
    public static bool IsHit(
        IEnumerable<RetrievalResult> results, IEnumerable<string> relevantFiles)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(relevantFiles);

        var paths = TopPaths(results).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return relevantFiles.Any(f => paths.Contains(Normalise(f)));
    }

    /// <summary>
    /// The distinct paths the criterion considered, in rank order — what a reader
    /// needs to see why a task missed.
    /// </summary>
    public static IReadOnlyList<string> TopPaths(IEnumerable<RetrievalResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return [.. results
            .Take(Depth)
            .Select(r => Normalise(r.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Runs the retrieval a task's description asks for and decides the hit.
    /// </summary>
    public static async Task<(bool Hit, IReadOnlyList<string> TopPaths)> MeasureAsync(
        HybridRetriever retriever,
        Guid repositoryId,
        EvaluationTaskDefinition task,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(task);

        // The task description verbatim, as the query. Anything added here would
        // make the measured figure a property of the harness rather than of the
        // retriever the product ships.
        var results = await retriever.SearchAsync(
            repositoryId, task.Description, Depth, documentationOnly: false, ct);

        return (IsHit(results, task.RelevantFiles), TopPaths(results));
    }

    private static string Normalise(string path) => path.Replace('\\', '/');
}
