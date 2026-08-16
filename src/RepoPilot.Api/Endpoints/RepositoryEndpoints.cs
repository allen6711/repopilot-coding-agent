using System.Text.Json;
using RepoPilot.Api.Contracts;
using RepoPilot.Application.Ports;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;
using RepoPilot.Infrastructure.Retrieval;

namespace RepoPilot.Api.Endpoints;

/// <summary>
/// The repository surface (FR-001, FR-003, FR-004).
/// </summary>
public static class RepositoryEndpoints
{
    public static void MapRepositoryEndpoints(this IEndpointRouteBuilder app)
    {
        var repositories = app.MapGroup("/api/repositories").WithTags("repositories");

        repositories.MapPost("/", RegisterAsync);
        repositories.MapGet("/", ListAsync);
        repositories.MapGet("/{repositoryId:guid}", GetAsync);
        repositories.MapPost("/{repositoryId:guid}/index", IndexAsync);
        repositories.MapGet("/{repositoryId:guid}/search", SearchAsync);
    }

    /// <summary>Registers a fixture from the configured allowed set (FR-001).</summary>
    private static async Task<IResult> RegisterAsync(
        RegisterRepositoryRequest request,
        RegisterRepositoryUseCase register,
        CancellationToken ct)
    {
        try
        {
            var fixture = await register.RegisterAsync(request.Slug, ct);

            return Results.Created(
                $"/api/repositories/{fixture.Id}", RepositoryDto.From(fixture));
        }
        catch (RegistrationRefusedException ex)
        {
            return ex.Reason switch
            {
                RegistrationRefusalReason.AlreadyRegistered => Results.Problem(
                    title: "Already registered",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status409Conflict),

                // Everything else is a refusal of the request itself: a slug
                // outside the allowed set, a missing or invalid config, a
                // command that assumes a shell. All 422 — the request was
                // understood and declined.
                _ => Results.Problem(
                    title: "Registration refused",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        }
    }

    private static async Task<IResult> ListAsync(
        IRepositoryFixtureStore repositories, CancellationToken ct)
    {
        var all = await repositories.ListAsync(ct);
        return Results.Ok(all.Select(RepositoryDto.From));
    }

    private static async Task<IResult> GetAsync(
        Guid repositoryId, IRepositoryFixtureStore repositories, CancellationToken ct)
    {
        var fixture = await repositories.FindByIdAsync(repositoryId, ct);

        return fixture is null
            ? NotFound(repositoryId)
            : Results.Ok(RepositoryDto.From(fixture));
    }

    /// <summary>
    /// Builds or rebuilds the index (FR-003, FR-003a).
    /// <para>
    /// Synchronous despite answering 202. Fixtures are small enough that the
    /// walk finishes inside a request, and returning 202 with the finished
    /// counts is more useful than 202 with nothing and a status to poll. The
    /// code says "accepted" because the contract does, and because a larger
    /// fixture would make this a background job without changing the contract.
    /// </para>
    /// </summary>
    private static async Task<IResult> IndexAsync(
        Guid repositoryId,
        IndexRepositoryUseCase index,
        IRepositoryFixtureStore repositories,
        CancellationToken ct)
    {
        if (await repositories.FindByIdAsync(repositoryId, ct) is null)
        {
            return NotFound(repositoryId);
        }

        try
        {
            var report = await index.RebuildAsync(repositoryId, ct);

            return Results.Accepted(
                $"/api/repositories/{repositoryId}",
                new IndexingStatusDto(
                    repositoryId,
                    "indexed",
                    report.IncludedFileCount,
                    report.ExcludedFileCount,
                    report.ExclusionBreakdown));
        }
        catch (IndexRebuildInProgressException ex)
        {
            return Results.Problem(
                title: "Rebuild already running",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Searches the active index (FR-004).
    /// <para>
    /// The same retriever the agent's <c>search_code</c> capability uses, so
    /// what an operator sees here is what the agent would get — a separate
    /// query path would let the two diverge and make this view misleading
    /// precisely when it matters.
    /// </para>
    /// </summary>
    private static async Task<IResult> SearchAsync(
        Guid repositoryId,
        string q,
        int? limit,
        IRepositoryFixtureStore repositories,
        HybridRetriever retriever,
        CancellationToken ct)
    {
        var fixture = await repositories.FindByIdAsync(repositoryId, ct);

        if (fixture is null)
        {
            return NotFound(repositoryId);
        }

        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.Problem(
                title: "Query required",
                detail: "Supply a non-empty q parameter.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (fixture.ActiveIndexVersion is null || fixture.IncludedFileCount == 0)
        {
            // Refused rather than answered with an empty list. "No results" and
            // "there is nothing to search" are different facts, and conflating
            // them makes an unindexed repository look like a bad query.
            return Results.Problem(
                title: "Repository is not indexed",
                detail:
                    $"'{fixture.Slug}' has no active index with indexed files. Build the index " +
                    "before searching.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var results = await retriever.SearchAsync(
            fixture.Id, q, limit ?? 10, documentationOnly: false, ct);

        return Results.Ok(results.Select(RetrievalResultDto.From));
    }

    private static IResult NotFound(Guid repositoryId) => Results.Problem(
        title: "Repository not found",
        detail: $"No repository with id {repositoryId}.",
        statusCode: StatusCodes.Status404NotFound);
}

/// <summary>The body of <c>POST /api/repositories</c>.</summary>
public sealed record RegisterRepositoryRequest(string Slug);

/// <summary>A repository, as the contract's <c>Repository</c> schema.</summary>
public sealed record RepositoryDto(
    Guid Id,
    string Slug,
    string DisplayName,
    string IndexingStatus,
    int? ActiveIndexVersion,
    int IncludedFileCount,
    int ExcludedFileCount,
    IReadOnlyDictionary<string, int> ExclusionBreakdown,
    DateTimeOffset? LastIndexedAt,
    IReadOnlyList<string> TestCommands)
{
    public static RepositoryDto From(RepositoryFixture fixture) => new(
        fixture.Id,
        fixture.Slug,
        fixture.DisplayName,
        fixture.IndexingStatus switch
        {
            Domain.Entities.IndexingStatus.Indexing => "indexing",
            Domain.Entities.IndexingStatus.Indexed => "indexed",
            Domain.Entities.IndexingStatus.Failed => "failed",
            _ => "never_indexed",
        },
        fixture.ActiveIndexVersion,
        fixture.IncludedFileCount,
        fixture.ExcludedFileCount,
        IndexRepositoryUseCase.FullBreakdown(fixture),
        fixture.LastIndexedAt,
        ReadCommandNames(fixture.TestConfigJson));

    /// <summary>
    /// The command names a run may execute. Names only — the argument vectors
    /// are committed configuration and showing them here would invite the idea
    /// that they are editable through the API.
    /// </summary>
    private static IReadOnlyList<string> ReadCommandNames(string testConfigJson)
    {
        using var config = JsonDocument.Parse(testConfigJson);

        if (!config.RootElement.TryGetProperty("commands", out var commands) ||
            commands.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. commands.EnumerateArray()
                .Select(c => c.TryGetProperty("name", out var n) ? n.GetString() : null)
                .OfType<string>()
        ];
    }
}

/// <summary>The result of triggering an index build.</summary>
public sealed record IndexingStatusDto(
    Guid RepositoryId,
    string Status,
    int IncludedFileCount,
    int ExcludedFileCount,
    IReadOnlyDictionary<string, int> ExclusionBreakdown);

/// <summary>One search hit, as the contract's <c>RetrievalResult</c> schema.</summary>
public sealed record RetrievalResultDto(
    string RelativePath,
    Guid ChunkId,
    string Content,
    int StartLine,
    int EndLine,
    double Score,
    string? Language)
{
    public static RetrievalResultDto From(RetrievalResult result) => new(
        result.RelativePath,
        result.ChunkId,
        result.Content,
        result.StartLine,
        result.EndLine,
        result.Score,
        result.Language);
}
