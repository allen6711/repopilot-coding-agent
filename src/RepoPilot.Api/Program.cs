using RepoPilot.Agent.Hosting;
using RepoPilot.Api.Endpoints;
using RepoPilot.Api.Hosting;
using RepoPilot.Evals.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Everything needed to execute a run: configuration, persistence, providers,
// retrieval, the capability set, and the orchestrator. Shared with the
// evaluation harness so the two cannot drift into measuring different systems
// (FR-034).
builder.Services.AddRepoPilotCore(builder.Configuration);

// The harness, so POST /api/evaluations delegates to the same library the CLI
// runs rather than to a second implementation of the same measurement.
builder.Services.AddEvaluationHarness(builder.Configuration);

// API-only concerns ---------------------------------------------------------
builder.Services.AddHostedService<RunExecutionService>();
builder.Services.AddScoped<StartupRecoveryService>();
builder.Services.AddScoped<RepoPilot.Api.Seed.SeedCommand>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// FR-030a: every start brings the system to a coherent state before serving.
// Runs are not resumable, so a run left mid-flight is ended and its working copy
// removed rather than left to look live.
await using (var scope = app.Services.CreateAsyncScope())
{
    var recovery = scope.ServiceProvider.GetRequiredService<StartupRecoveryService>();
    try
    {
        await recovery.RecoverAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Startup recovery failed; refusing to serve with unknown run state.");
        throw;
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapRepositoryEndpoints();
app.MapRunEndpoints();
app.MapRunEventEndpoints();
app.MapApprovalEndpoints();
app.MapEvaluationEndpoints();

app.Run();

/// <summary>Exposed so the integration test host can reference the entry point.</summary>
public partial class Program;
