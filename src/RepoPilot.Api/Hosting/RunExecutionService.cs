using RepoPilot.Application.Ports;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Runs;

namespace RepoPilot.Api.Hosting;

/// <summary>
/// Drains the run queue, executing one segment per dequeued run.
/// <para>
/// Which segment to run is read from the run's persisted stage rather than
/// carried on the queue message. That is what lets the same queue serve both
/// halves of a run: a message means "this run has work to do", and the database
/// says what the work is. A message carrying the segment would go stale the
/// moment anything else moved the run — a cancellation, most obviously.
/// </para>
/// </summary>
public sealed class RunExecutionService(
    RunQueue queue,
    IServiceScopeFactory scopes,
    ILogger<RunExecutionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in queue.DequeueAllAsync(stoppingToken))
        {
            // Not awaited: one slow run must not hold up the queue reader, and
            // the slot inside ExecuteSegmentAsync is what actually bounds
            // concurrency (FR-013a).
            _ = ExecuteSegmentAsync(runId, stoppingToken);
        }
    }

    private async Task ExecuteSegmentAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            await queue.WithSlotAsync(async token =>
            {
                await using var scope = scopes.CreateAsyncScope();

                var runs = scope.ServiceProvider.GetRequiredService<IRunStore>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<RunOrchestrator>();

                var run = await runs.FindAsync(runId, token);

                if (run is null)
                {
                    logger.LogWarning("Queued run {RunId} no longer exists.", runId);
                    return RunStage.Failed;
                }

                return run.Stage switch
                {
                    RunStage.Created => await orchestrator.ExecuteUntilApprovalAsync(runId, token),

                    RunStage.AwaitingApproval =>
                        await orchestrator.ApplyAndTestAsync(runId, token),

                    // Cancelled between being queued and being picked up. Not an
                    // error: the queue is allowed to hold work that has since
                    // become moot, and doing nothing is the correct response.
                    _ => run.Stage,
                };
            },
            ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down. The orchestrator already recorded whatever it
            // reached, and startup recovery ends anything left non-terminal.
        }
        catch (Exception ex)
        {
            // The orchestrator records the failure on the run itself before it
            // rethrows, so this is a log line rather than the place the outcome
            // is decided.
            logger.LogError(ex, "Run {RunId} failed during execution.", runId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        await base.StopAsync(cancellationToken);
    }
}
