using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;
using RepoPilot.Evals;

namespace RepoPilot.Api.Endpoints;

/// <summary>
/// The evaluation surface (FR-031 to FR-035).
/// <para>
/// Both endpoints are thin. Starting an evaluation delegates to the same
/// <see cref="EvaluationHarness"/> the CLI runs, and reading one returns the
/// stored record — so an evaluation started here and one started from the command
/// line are the same evaluation, measured the same way and citable
/// interchangeably.
/// </para>
/// </summary>
public static class EvaluationEndpoints
{
    public static void MapEvaluationEndpoints(this IEndpointRouteBuilder app)
    {
        var evaluations = app.MapGroup("/api/evaluations").WithTags("evaluations");

        evaluations.MapPost("/", StartAsync);
        evaluations.MapGet("/{evaluationId:guid}", GetAsync);
    }

    /// <summary>
    /// Starts an evaluation over the committed task set.
    /// <para>
    /// 202 with the record as it stands, not 200 with the finished metrics: the
    /// set takes minutes to run and every figure on the returned record is still
    /// null. The caller polls the read endpoint.
    /// </para>
    /// </summary>
    private static async Task<IResult> StartAsync(
        EvaluationHarness harness,
        IServiceScopeFactory scopes,
        ILogger<EvaluationHarness> logger,
        CancellationToken ct)
    {
        EvaluationRun evaluation;

        try
        {
            // Awaited, so the record exists before the id is handed out and the
            // caller's first poll finds it.
            evaluation = await harness.BeginAsync(ct);
        }
        catch (EvaluationRefusedException ex)
        {
            return Results.Problem(
                title: "Evaluation refused",
                detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var evaluationId = evaluation.Id;

        // Its own scope and its own lifetime, and not the request's cancellation
        // token. The request's scope disposes when the 202 is written, and an
        // evaluation that stopped because a client hung up would leave runs
        // mid-flight for startup recovery to clean up.
        _ = Task.Run(async () =>
        {
            await using var scope = scopes.CreateAsyncScope();

            try
            {
                var store = scope.ServiceProvider.GetRequiredService<IEvaluationStore>();
                var started = await store.FindAsync(evaluationId, CancellationToken.None);

                if (started is null)
                {
                    logger.LogError("Evaluation {Id} vanished before it started.", evaluationId);
                    return;
                }

                await scope.ServiceProvider
                    .GetRequiredService<EvaluationHarness>()
                    .ExecuteAsync(started, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Logged rather than surfaced: the caller already has its 202.
                // The stored record keeps its null metrics and no end time, which
                // is what the poll will show.
                logger.LogError(ex, "Evaluation {Id} failed.", evaluationId);
            }
        }, CancellationToken.None);

        return Results.Accepted(
            $"/api/evaluations/{evaluationId}", EvaluationRunDto.From(evaluation));
    }

    private static async Task<IResult> GetAsync(
        Guid evaluationId, IEvaluationStore evaluations, CancellationToken ct)
    {
        var evaluation = await evaluations.FindAsync(evaluationId, ct);

        return evaluation is null
            ? Results.Problem(
                title: "Evaluation not found",
                detail: $"No evaluation with id {evaluationId}.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(EvaluationRunDto.From(evaluation));
    }
}

/// <summary>Wire shape of an evaluation run, matching <c>contracts/rest-api.yaml</c>.</summary>
public sealed record EvaluationRunDto(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int TaskCount,
    decimal? RecallAt5,
    decimal? CompletionRateToolEnabled,
    decimal? CompletionRateBaseline,
    decimal? ApprovalCoverage,
    decimal? ToolSuccessRate,
    decimal? AvgToolCallsPerCompletedTask,
    int? P50LatencyMs,
    int? P95LatencyMs,
    bool Flagged)
{
    public static EvaluationRunDto From(EvaluationRun evaluation) => new(
        evaluation.Id,
        evaluation.StartedAt,
        evaluation.EndedAt,
        evaluation.TaskCount,
        evaluation.RecallAt5,
        evaluation.CompletionRateToolEnabled,
        evaluation.CompletionRateBaseline,
        evaluation.ApprovalCoverage,
        evaluation.ToolSuccessRate,
        evaluation.AvgToolCallsPerCompletedTask,
        evaluation.P50LatencyMs,
        evaluation.P95LatencyMs,
        evaluation.Flagged);
}
