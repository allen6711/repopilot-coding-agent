using RepoPilot.Domain.Entities;

namespace RepoPilot.Application.Ports;

/// <summary>
/// What one index rebuild produced.
/// </summary>
/// <param name="IncludedFileCount">Files indexed.</param>
/// <param name="ExcludedFileCount">Files left out.</param>
/// <param name="ExclusionBreakdown">
/// Counts keyed by normative reason (FR-003b). Reporting a total without the
/// breakdown would tell an operator that files are missing without telling them
/// whether that is correct — a vendored directory and a secret-bearing file are
/// both "excluded", and only one of them is worth investigating.
/// </param>
/// <param name="ChunkCount">Total chunks written.</param>
/// <param name="IndexVersion">The version now active.</param>
public sealed record IndexingReport(
    int IncludedFileCount,
    int ExcludedFileCount,
    IReadOnlyDictionary<string, int> ExclusionBreakdown,
    int ChunkCount,
    int IndexVersion);

/// <summary>
/// Builds a repository's retrieval index.
/// <para>
/// Declared here so the use case that owns the indexing lifecycle — status
/// transitions, refusal of a concurrent rebuild, failure recording — does not
/// have to know about the database or the embedding provider.
/// </para>
/// </summary>
public interface IIndexBuilder
{
    /// <summary>
    /// Rebuilds the index and swaps it in atomically (FR-003a).
    /// </summary>
    Task<IndexingReport> RebuildAsync(RepositoryFixture repository, CancellationToken ct = default);
}
