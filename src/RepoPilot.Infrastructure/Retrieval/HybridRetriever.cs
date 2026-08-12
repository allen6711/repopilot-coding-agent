using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Infrastructure.Persistence;

namespace RepoPilot.Infrastructure.Retrieval;

/// <summary>One retrieval result (FR-004).</summary>
/// <param name="ChunkId">Identifies the chunk.</param>
/// <param name="RelativePath">Repository-relative path.</param>
/// <param name="Content">The chunk's text.</param>
/// <param name="StartLine">1-based, inclusive.</param>
/// <param name="EndLine">1-based, inclusive.</param>
/// <param name="Score">Fused relevance score; higher is better.</param>
/// <param name="Language">Inferred language, when known.</param>
public sealed record RetrievalResult(
    Guid ChunkId,
    string RelativePath,
    string Content,
    int StartLine,
    int EndLine,
    double Score,
    string? Language);

/// <summary>
/// Hybrid retrieval over the active index (FR-005).
/// <para>
/// Both arms always run. The lexical arm finds an identifier a developer already
/// knows the name of; the vector arm finds code described rather than named.
/// Which one a query needs is not knowable in advance, and picking wrong is
/// invisible — the search returns something, just not the right thing.
/// </para>
/// <para>
/// Results are fused with Reciprocal Rank Fusion rather than by blending scores.
/// Cosine distance and <c>ts_rank</c> are not on comparable scales, so any
/// weighted blend needs a normalisation and a weight, both of which are tuning
/// knobs with no principled default. RRF uses only rank position, so it needs
/// neither.
/// </para>
/// </summary>
public sealed class HybridRetriever(
    RepoPilotDbContext db,
    IEmbeddingProviderAdapter embeddings,
    RetrievalOptions options)
{
    /// <summary>
    /// Searches a repository's active index.
    /// </summary>
    /// <param name="repositoryId">Repository to search.</param>
    /// <param name="query">Free text or an exact identifier.</param>
    /// <param name="limit">Maximum results to return.</param>
    /// <param name="documentationOnly">
    /// Restrict to documentation entries, for the docs capability.
    /// </param>
    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        Guid repositoryId,
        string query,
        int? limit = null,
        bool documentationOnly = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var take = limit ?? options.DefaultResultLimit;

        var activeVersion = await db.Repositories
            .Where(r => r.Id == repositoryId)
            .Select(r => r.ActiveIndexVersion)
            .FirstOrDefaultAsync(ct);

        if (activeVersion is null)
        {
            // Never indexed, or indexed but never swapped in. Returning nothing
            // is correct; a run against it is refused higher up.
            return [];
        }

        var vectors = await embeddings.EmbedAsync([query], ct);
        var queryVector = new Vector(vectors[0]);

        // Each arm retrieves a deeper candidate set than the final limit, so a
        // result ranked modestly by one arm can still surface when the other
        // ranks it highly — which is the entire point of fusing.
        var candidateDepth = Math.Max(take * 5, 50);

        var sql = $"""
            WITH lexical AS (
                SELECT "Id",
                       ROW_NUMBER() OVER (
                           ORDER BY ts_rank("ContentSearchVector", plainto_tsquery('simple', @query)) DESC,
                                    similarity("Content", @query) DESC
                       ) AS rank
                FROM index_entries
                WHERE "RepositoryId" = @repositoryId
                  AND "IndexVersion" = @version
                  {(documentationOnly ? "AND \"Language\" = 'markdown'" : string.Empty)}
                  AND (
                        "ContentSearchVector" @@ plainto_tsquery('simple', @query)
                        OR "Content" ILIKE @likeQuery
                      )
                LIMIT @depth
            ),
            semantic AS (
                SELECT "Id",
                       ROW_NUMBER() OVER (ORDER BY "Embedding" <=> @embedding) AS rank
                FROM index_entries
                WHERE "RepositoryId" = @repositoryId
                  AND "IndexVersion" = @version
                  {(documentationOnly ? "AND \"Language\" = 'markdown'" : string.Empty)}
                ORDER BY "Embedding" <=> @embedding
                LIMIT @depth
            )
            SELECT e."Id", e."RelativePath", e."Content", e."StartLine", e."EndLine", e."Language",
                   COALESCE(1.0 / (@k + l.rank), 0.0) + COALESCE(1.0 / (@k + s.rank), 0.0) AS score
            FROM index_entries e
            LEFT JOIN lexical l ON l."Id" = e."Id"
            LEFT JOIN semantic s ON s."Id" = e."Id"
            WHERE l."Id" IS NOT NULL OR s."Id" IS NOT NULL
            ORDER BY score DESC
            LIMIT @take;
            """;

        var results = new List<RetrievalResult>(take);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("query", query);
        command.Parameters.AddWithValue("likeQuery", $"%{EscapeLike(query)}%");
        command.Parameters.AddWithValue("repositoryId", repositoryId);
        command.Parameters.AddWithValue("version", activeVersion.Value);
        command.Parameters.AddWithValue("embedding", queryVector);
        command.Parameters.AddWithValue("depth", candidateDepth);
        command.Parameters.AddWithValue("take", take);
        command.Parameters.AddWithValue("k", options.RrfK);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // Both named: the SELECT lists Language before score, while the
            // record declares Score before Language, and positional arguments
            // would silently transpose them.
            results.Add(new RetrievalResult(
                ChunkId: reader.GetGuid(0),
                RelativePath: reader.GetString(1),
                Content: reader.GetString(2),
                StartLine: reader.GetInt32(3),
                EndLine: reader.GetInt32(4),
                Language: reader.IsDBNull(5) ? null : reader.GetString(5),
                Score: reader.GetDouble(6)));
        }

        return results;
    }

    /// <summary>
    /// Escapes LIKE wildcards so a query containing <c>%</c> or <c>_</c> matches
    /// them literally instead of silently becoming a broader search.
    /// </summary>
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);
}
