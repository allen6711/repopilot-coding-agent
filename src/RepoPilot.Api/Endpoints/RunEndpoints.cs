using RepoPilot.Api.Contracts;
using RepoPilot.Api.Middleware;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Runs;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;

namespace RepoPilot.Api.Endpoints;

/// <summary>
/// The run surface (FR-007, FR-008a, FR-028).
/// </summary>
public static class RunEndpoints
{
    public static void MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        var runs = app.MapGroup("/api/runs").WithTags("runs");

        runs.MapPost("/", CreateAsync);
        runs.MapGet("/", ListAsync);
        runs.MapGet("/{runId:guid}", GetAsync);
        runs.MapGet("/{runId:guid}/proposal", GetProposalAsync);
        runs.MapGet("/{runId:guid}/diff", GetDiffAsync);
        runs.MapGet("/{runId:guid}/tests", GetTestsAsync);
        runs.MapPost("/{runId:guid}/cancel", CancelAsync);
    }

    /// <summary>
    /// Creates a run and queues it (FR-007, FR-013a).
    /// <para>
    /// 202 rather than 201: the run exists, but nothing has happened to it yet.
    /// Above the concurrency limit it waits in the queue rather than being
    /// refused, so a burst of submissions is slow rather than lossy.
    /// </para>
    /// </summary>
    private static async Task<IResult> CreateAsync(
        CreateRunRequest request,
        IRepositoryFixtureStore repositories,
        IRunStore runs,
        RunQueue queue,
        CancellationToken ct)
    {
        var task = request.TaskDescription?.Trim();
        var seeded = request.SeededTaskId?.Trim();

        if (string.IsNullOrEmpty(task) && string.IsNullOrEmpty(seeded))
        {
            return Results.Problem(
                title: "Task required",
                detail: "Supply either taskDescription or seededTaskId.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var repository = await repositories.FindByIdAsync(request.RepositoryId, ct);

        if (repository is null)
        {
            return Results.Problem(
                title: "Repository not found",
                detail: $"No repository with id {request.RepositoryId}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (repository.ActiveIndexVersion is null || repository.IncludedFileCount == 0)
        {
            // Refused at submission rather than accepted and failed later. A run
            // against an empty index cannot retrieve anything, so it would end as
            // insufficient-context and look like a retrieval quality problem.
            return Results.Problem(
                title: "Repository is not indexed",
                detail:
                    $"'{repository.Slug}' has no active index with indexed files. Build the index " +
                    "before starting a run.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var run = new Run
        {
            RepositoryId = repository.Id,
            TaskDescription = task ?? $"Seeded task {seeded}",
            SeededTaskId = string.IsNullOrEmpty(seeded) ? null : seeded,
            ToolsEnabled = request.ToolsEnabled ?? true,
        };

        // Persisted before it is queued. The other order could hand the executor
        // an id that is not yet readable.
        await runs.AddAsync(run, ct);
        await queue.EnqueueAsync(run.Id, ct);

        return Results.Accepted($"/api/runs/{run.Id}", RunDto.From(run));
    }

    private static async Task<IResult> ListAsync(
        IRunStore runs,
        Guid? repositoryId,
        string? stage,
        CancellationToken ct)
    {
        try
        {
            var found = await runs.ListAsync(repositoryId, WireNames.ParseStage(stage), ct);
            return Results.Ok(found.Select(RunDto.From));
        }
        catch (FormatException ex)
        {
            return Results.Problem(
                title: "Unknown stage",
                detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> GetAsync(Guid runId, IRunStore runs, CancellationToken ct)
    {
        var run = await runs.FindAsync(runId, ct);
        return run is null ? NotFound(runId) : Results.Ok(RunDto.From(run));
    }

    /// <summary>
    /// The proposal a reviewer decides on (SC-004).
    /// <para>
    /// Returns the affected paths and the whole unified diff in one response.
    /// Paging it would mean a reviewer could approve having seen part of it, and
    /// FR-020a's "what was shown" would stop being a single thing.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetProposalAsync(
        Guid runId, IProposalStore proposals, CancellationToken ct)
    {
        var proposal = await proposals.FindCurrentForRunAsync(runId, ct);

        return proposal is null
            ? Results.Problem(
                title: "No proposal",
                detail: $"Run {runId} has no change proposal.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(ChangeProposalDto.From(proposal));
    }

    /// <summary>
    /// The diff for a given revision attempt, defaulting to the latest.
    /// <para>
    /// Earlier attempts stay readable after a revision replaces them, because
    /// each one was separately approved and the record of what was approved must
    /// outlive the proposal being superseded (FR-019b).
    /// </para>
    /// </summary>
    private static async Task<IResult> GetDiffAsync(
        Guid runId,
        int? attempt,
        IProposalStore proposals,
        IRunStore runs,
        CancellationToken ct)
    {
        if (await runs.FindAsync(runId, ct) is null)
        {
            return NotFound(runId);
        }

        var proposal = attempt is null
            ? await proposals.FindCurrentForRunAsync(runId, ct)
            : (await proposals.ListForRunAsync(runId, ct))
                .FirstOrDefault(p => p.RevisionAttempt == attempt);

        return proposal is null
            ? Results.Problem(
                title: "No proposal",
                detail: attempt is null
                    ? $"Run {runId} has no change proposal."
                    : $"Run {runId} has no proposal for attempt {attempt}.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(ChangeProposalDto.From(proposal));
    }

    private static async Task<IResult> GetTestsAsync(
        Guid runId, ITestResultStore results, CancellationToken ct)
    {
        var found = await results.ListForRunAsync(runId, ct);
        return Results.Ok(found.Select(TestResultDto.From));
    }

    /// <summary>Abandons a run (FR-008a).</summary>
    private static async Task<IResult> CancelAsync(
        Guid runId,
        HttpRequest request,
        CancelRunUseCase cancel,
        CancellationToken ct)
    {
        if (!ActorIdentityBinding.TryRead(request, out var actor, out var problem))
        {
            return problem;
        }

        try
        {
            return Results.Ok(RunDto.From(await cancel.CancelAsync(runId, actor.Value, ct)));
        }
        catch (CancellationRefusedException ex)
        {
            return ex.Reason switch
            {
                CancellationRefusalReason.RunNotFound => NotFound(runId),

                // 409, not 422: the request was well formed and the state refused
                // it. A finished run's outcome is not overwritten by a later
                // cancellation.
                CancellationRefusalReason.AlreadyTerminal => Results.Problem(
                    title: "Run already ended",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status409Conflict),

                _ => Results.Problem(
                    title: "Cancellation refused",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        }
    }

    private static IResult NotFound(Guid runId) => Results.Problem(
        title: "Run not found",
        detail: $"No run with id {runId}.",
        statusCode: StatusCodes.Status404NotFound);
}
