using Microsoft.Extensions.Logging;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Indexing;

namespace RepoPilot.Application.UseCases;

/// <summary>Raised when a rebuild is already running for a repository.</summary>
public sealed class IndexRebuildInProgressException(string slug)
    : InvalidOperationException(
        $"An index rebuild is already running for '{slug}'. Rebuilds replace the whole index " +
        "atomically, so two at once would race to swap and one would silently win.")
{
    public string Slug { get; } = slug;
}

/// <summary>
/// Builds or rebuilds a repository's index and reports what it left out.
/// <para>
/// This owns the indexing <em>lifecycle</em> — claiming the repository, marking
/// failure, releasing it — while <see cref="IIndexBuilder"/> does the work.
/// They are separate because the interesting failure is not "the walk threw" but
/// "the walk threw and the repository is still marked as indexing", which would
/// make every later rebuild refuse.
/// </para>
/// </summary>
public sealed class IndexRepositoryUseCase(
    IRepositoryFixtureStore repositories,
    IIndexBuilder builder,
    ILogger<IndexRepositoryUseCase> logger)
{
    /// <summary>
    /// Rebuilds the index.
    /// </summary>
    /// <exception cref="IndexRebuildInProgressException">One is already running.</exception>
    public async Task<IndexingReport> RebuildAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var repository = await repositories.FindByIdAsync(repositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found.");

        if (repository.IndexingStatus == IndexingStatus.Indexing)
        {
            throw new IndexRebuildInProgressException(repository.Slug);
        }

        // Claimed before any work starts. A rebuild that marked itself running
        // only once it got going would leave a window where a second request
        // saw an idle repository and started a competing walk.
        repository.IndexingStatus = IndexingStatus.Indexing;
        await repositories.UpdateAsync(repository, ct);

        try
        {
            var report = await builder.RebuildAsync(repository, ct);

            logger.LogInformation(
                "Indexed {Slug}: {Included} included, {Excluded} excluded, version {Version}.",
                repository.Slug, report.IncludedFileCount, report.ExcludedFileCount,
                report.IndexVersion);

            return report;
        }
        catch (Exception)
        {
            // Released on the way out, and recorded as failed rather than reset
            // to never-indexed: an operator needs to see that a rebuild was
            // attempted and did not work. The previous index stays active
            // because the swap never happened (FR-003a).
            repository.IndexingStatus = IndexingStatus.Failed;
            await repositories.UpdateAsync(repository, CancellationToken.None);

            throw;
        }
    }

    /// <summary>
    /// What a repository's current index contains and excludes.
    /// <para>
    /// The breakdown is keyed by the normative reason set (FR-003b) with a zero
    /// entry for every reason that did not occur. Omitting the zeroes would make
    /// "no secret-bearing files were found" and "secret detection did not run"
    /// look identical to a reader.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, int> FullBreakdown(RepositoryFixture repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var breakdown = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var reason in Enum.GetValues<ExclusionReason>())
        {
            if (reason == ExclusionReason.None)
            {
                continue;
            }

            breakdown[reason.ToString()] = 0;
        }

        foreach (var (reason, count) in repository.ExclusionBreakdown)
        {
            // Reasons the policy reports that are not in the enum — currently
            // "Empty" — are carried through rather than dropped. The totals have
            // to add up to the files walked, and silently discarding a category
            // is how they stop adding up.
            breakdown[reason] = count;
        }

        return breakdown;
    }
}
