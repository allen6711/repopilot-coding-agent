using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoPilot.Agent.Hosting;
using RepoPilot.Evals;
using RepoPilot.Evals.Hosting;

// The evaluation CLI (FR-034).
//
// It composes the same services the API does and drives RunOrchestrator directly.
// There is no evaluation-specific run path: the figures this prints describe the
// system the API serves, or they describe nothing worth publishing.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRepoPilotCore(builder.Configuration);
builder.Services.AddEvaluationHarness(builder.Configuration);

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("RepoPilot.Evals");

// Ctrl-C stops the evaluation between tasks rather than killing it mid-run: a
// run interrupted after its change was applied but before its result was
// recorded would leave a working copy behind and a measurement that never
// existed.
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

await using var scope = host.Services.CreateAsyncScope();

try
{
    var harness = scope.ServiceProvider.GetRequiredService<EvaluationHarness>();
    var evaluation = await harness.RunAsync(stopping.Token);

    logger.LogInformation(
        "Evaluation {Id}: {Tasks} tasks, Recall@5 {Recall}, completion {Tool} tool-enabled / " +
        "{Baseline} baseline, approval coverage {Coverage}.",
        evaluation.Id,
        evaluation.TaskCount,
        Format(evaluation.RecallAt5),
        Format(evaluation.CompletionRateToolEnabled),
        Format(evaluation.CompletionRateBaseline),
        Format(evaluation.ApprovalCoverage));

    // Non-zero on a flagged evaluation, so CI fails rather than printing a
    // governance failure into a log nobody reads (FR-034, SC-001).
    return evaluation.Flagged ? 2 : 0;
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    logger.LogWarning("Evaluation cancelled.");
    return 130;
}
catch (EvaluationRefusedException ex)
{
    logger.LogError("{Message}", ex.Message);
    return 1;
}

static string Format(decimal? value) =>
    value is null ? "not measured" : value.Value.ToString("P2");
