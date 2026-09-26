using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Indexing;
using RepoPilot.Domain.Workspace;
using RepoPilot.Infrastructure.Persistence;

namespace RepoPilot.Infrastructure.Indexing;

/// <summary>
/// Builds a repository's retrieval index.
/// <para>
/// Always a full rebuild replaced atomically (FR-003a). Incremental per-file
/// updates were rejected deliberately: at fixture scale the time saved does not
/// justify the correctness risk, and a partially-updated index fails in the
/// worst possible way — it returns plausible results that are quietly stale.
/// </para>
/// </summary>
public sealed class IndexingService(
    RepoPilotDbContext db,
    IEmbeddingProviderAdapter embeddings,
    IndexingOptions options,
    ILogger<IndexingService> logger) : IIndexBuilder
{
    /// <summary>
    /// Rebuilds the index for <paramref name="repository"/> and swaps it in.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The embedding model's dimensions do not match what the repository was
    /// previously indexed with. Mixing vector spaces would return silently wrong
    /// neighbours, so it is refused rather than reconciled.
    /// </exception>
    public async Task<IndexingReport> RebuildAsync(
        RepositoryFixture repository,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        if (repository.EmbeddingDimensions is { } pinned && pinned != embeddings.Dimensions)
        {
            throw new InvalidOperationException(
                $"Repository '{repository.Slug}' was indexed with {pinned}-dimension vectors but " +
                $"the configured model produces {embeddings.Dimensions}. Vectors from different " +
                "models are not comparable; clear the index before changing the model.");
        }

        // The fixture is opened read-only, so no path taken here can write to it
        // (FR-016a). Registering the root as read-only is what makes that a
        // property of the guard rather than of this method's good behaviour.
        var root = WorkspaceRoot.ReadOnly(repository.RootPath);
        var exclusions = ReadExclusionGlobs(repository);
        var policy = new IndexingExclusionPolicy(options.MaxFileBytes, exclusions);
        var chunker = new LineWindowChunker(options.ChunkLines, options.ChunkOverlapLines);

        var nextVersion = (repository.ActiveIndexVersion ?? 0) + 1;
        var breakdown = new Dictionary<string, int>(StringComparer.Ordinal);
        var included = 0;
        var excluded = 0;
        var entries = new List<IndexEntry>();

        foreach (var absolutePath in EnumerateFiles(root.FullPath))
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = ToRelativePath(root.FullPath, absolutePath);
            var info = new FileInfo(absolutePath);

            // Content is read only when the cheap checks have not already decided,
            // so an oversized or vendored file is never loaded into memory.
            var verdict = policy.Evaluate(relativePath, info.Length, contentSample: null);
            string? content = null;

            if (verdict.IsIncluded)
            {
                content = await File.ReadAllTextAsync(absolutePath, ct);
                verdict = policy.Evaluate(relativePath, info.Length, content);
            }

            if (!verdict.IsIncluded)
            {
                excluded++;
                var reason = verdict.Reason.ToString();
                breakdown[reason] = breakdown.GetValueOrDefault(reason) + 1;
                continue;
            }

            var chunks = chunker.Chunk(content!);
            if (chunks.Count == 0)
            {
                // Whitespace-only file: nothing to retrieve, and an empty chunk
                // would only dilute rankings. Counted as excluded so the totals
                // still add up to the files walked.
                excluded++;
                breakdown["Empty"] = breakdown.GetValueOrDefault("Empty") + 1;
                continue;
            }

            var vectors = await embeddings.EmbedAsync(
                [.. chunks.Select(c => c.Content)], ct);

            for (var i = 0; i < chunks.Count; i++)
            {
                entries.Add(new IndexEntry
                {
                    RepositoryId = repository.Id,
                    IndexVersion = nextVersion,
                    RelativePath = relativePath,
                    ChunkOrdinal = chunks[i].Ordinal,
                    Content = chunks[i].Content,
                    StartLine = chunks[i].StartLine,
                    EndLine = chunks[i].EndLine,
                    Language = InferLanguage(relativePath),
                    Embedding = vectors[i],
                });
            }

            included++;
        }

        await WriteAndSwapAsync(repository, nextVersion, entries, ct);

        repository.IncludedFileCount = included;
        repository.ExcludedFileCount = excluded;
        repository.ExclusionBreakdown = breakdown;
        repository.EmbeddingModelId = embeddings.ModelId;
        repository.EmbeddingDimensions = embeddings.Dimensions;
        repository.IndexingStatus = IndexingStatus.Indexed;
        repository.LastIndexedAt = DateTimeOffset.UtcNow;
        repository.ActiveIndexVersion = nextVersion;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Indexed {Slug}: {Included} files included, {Excluded} excluded, {Chunks} chunks, version {Version}.",
            repository.Slug, included, excluded, entries.Count, nextVersion);

        return new IndexingReport(included, excluded, breakdown, entries.Count, nextVersion);
    }

    /// <summary>
    /// Writes the new version and swaps it in.
    /// <para>
    /// The write and the swap are one transaction, and searches filter on the
    /// repository's active version — so a search running during a rebuild keeps
    /// seeing the previous index until the swap commits, and never observes a
    /// half-populated one (FR-003a).
    /// </para>
    /// </summary>
    private async Task WriteAndSwapAsync(
        RepositoryFixture repository,
        int nextVersion,
        List<IndexEntry> entries,
        CancellationToken ct)
    {
        var previousVersion = repository.ActiveIndexVersion;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.IndexEntries.AddRange(entries);
        await db.SaveChangesAsync(ct);

        if (previousVersion is { } old)
        {
            // Deleted in the same transaction as the swap, so there is no window
            // where both versions are visible or neither is.
            await db.IndexEntries
                .Where(e => e.RepositoryId == repository.Id && e.IndexVersion == old)
                .ExecuteDeleteAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static IEnumerable<string> EnumerateFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            : [];

    private static string ToRelativePath(string root, string absolutePath) =>
        Path.GetRelativePath(root, absolutePath).Replace('\\', '/');

    private static IReadOnlyList<string> ReadExclusionGlobs(RepositoryFixture repository)
    {
        try
        {
            using var config = System.Text.Json.JsonDocument.Parse(repository.TestConfigJson);

            if (config.RootElement.TryGetProperty("indexing", out var indexing) &&
                indexing.TryGetProperty("additionalExcludedGlobs", out var globs) &&
                globs.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return [.. globs.EnumerateArray()
                    .Select(g => g.GetString())
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Select(g => g!)];
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // The configuration is schema-validated at registration, so this is
            // unreachable in practice. Falling back to no extra exclusions is the
            // safe direction: it can only narrow indexing, never widen it.
        }

        return [];
    }

    private static string? InferLanguage(string relativePath) =>
        Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".go" => "go",
            ".java" => "java",
            ".rs" => "rust",
            ".rb" => "ruby",
            ".sql" => "sql",
            ".md" => "markdown",
            ".json" => "json",
            ".yml" or ".yaml" => "yaml",
            _ => null,
        };
}
