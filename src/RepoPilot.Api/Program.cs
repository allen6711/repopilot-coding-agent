using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RepoPilot.Agent;
using RepoPilot.Agent.Capabilities;
using RepoPilot.Agent.Invocation;
using RepoPilot.Api.Endpoints;
using RepoPilot.Api.Hosting;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Proposals;
using RepoPilot.Application.UseCases;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Runs;
using RepoPilot.Infrastructure.Events;
using RepoPilot.Infrastructure.Indexing;
using RepoPilot.Infrastructure.Observability;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Proposals;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.Infrastructure.Retrieval;
using RepoPilot.Infrastructure.Sandbox;
using RepoPilot.Infrastructure.Workspace;

var builder = WebApplication.CreateBuilder(args);

// Configuration -------------------------------------------------------------
var workspace = builder.Configuration.GetSection(WorkspaceOptions.SectionName)
    .Get<WorkspaceOptions>() ?? new WorkspaceOptions();
var concurrency = builder.Configuration.GetSection(RunConcurrencyOptions.SectionName)
    .Get<RunConcurrencyOptions>() ?? new RunConcurrencyOptions();
var retrieval = builder.Configuration.GetSection(RetrievalOptions.SectionName)
    .Get<RetrievalOptions>() ?? new RetrievalOptions();
var indexing = builder.Configuration.GetSection(IndexingOptions.SectionName)
    .Get<IndexingOptions>() ?? new IndexingOptions();
var allowed = builder.Configuration.GetSection(AllowedRepositoryOptions.SectionName)
    .Get<AllowedRepositoryOptions>() ?? new AllowedRepositoryOptions();
var proposals = builder.Configuration.GetSection(ProposalLimitOptions.SectionName)
    .Get<ProposalLimitOptions>() ?? new ProposalLimitOptions();
var chatOptions = builder.Configuration.GetSection(AnthropicChatOptions.SectionName)
    .Get<AnthropicChatOptions>() ?? new AnthropicChatOptions();

builder.Services.AddSingleton(workspace);
builder.Services.AddSingleton(concurrency);
builder.Services.AddSingleton(retrieval);
builder.Services.AddSingleton(indexing);
builder.Services.AddSingleton(allowed);
builder.Services.AddSingleton(proposals);
builder.Services.AddSingleton(chatOptions);

// Persistence ---------------------------------------------------------------
builder.Services.AddDbContext<RepoPilotDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("RepoPilot")
            ?? "Host=localhost;Port=5432;Database=repopilot;Username=repopilot;Password=repopilot",
        npgsql => npgsql.UseVector()));

builder.Services.AddScoped<IRepositoryFixtureStore, EfRepositoryFixtureStore>();
builder.Services.AddScoped<IRunStore, EfRunStore>();
builder.Services.AddScoped<IRunEventStore, EfRunEventStore>();
builder.Services.AddScoped<IProposalStore, EfProposalStore>();
builder.Services.AddScoped<IApprovalStore, EfApprovalStore>();
builder.Services.AddScoped<IWorkingCopyStore, EfWorkingCopyStore>();
builder.Services.AddScoped<ITestResultStore, EfTestResultStore>();

// Event delivery ------------------------------------------------------------
// Singleton: subscribers outlive any one request, which is the whole point of
// a live stream. Persist-then-publish ordering lives in RunEventRecorder.
builder.Services.AddSingleton<IRunEventPublisher, InProcessRunEventPublisher>();
builder.Services.AddScoped<RunEventRecorder>();

// Providers -----------------------------------------------------------------
builder.Services.AddSingleton<IChatProviderAdapter, AnthropicChatAdapter>();
builder.Services.AddSingleton<IEmbeddingProviderAdapter>(
    _ => new DeterministicEmbeddingAdapter(indexing.EmbeddingDimensions));
builder.Services.AddScoped<RepoPilotAgent>();
builder.Services.AddScoped<IAgentTurnRunner>(sp => sp.GetRequiredService<RepoPilotAgent>());

// Retrieval and workspace ---------------------------------------------------
builder.Services.AddScoped<HybridRetriever>();
builder.Services.AddScoped<IndexingService>();
builder.Services.AddScoped<WorkingCopyManager>();
builder.Services.AddScoped<IWorkspaceProvisioner>(
    sp => sp.GetRequiredService<WorkingCopyManager>());

// Proposals -----------------------------------------------------------------
builder.Services.AddSingleton<DiffRenderer>();
builder.Services.AddScoped<ProposalValidator>();

// Sandbox -------------------------------------------------------------------
// Singleton: the client holds a connection to the daemon, and there is no
// per-request state. A run that cannot get an isolated environment fails; there
// is deliberately no unisolated fallback registration.
builder.Services.AddSingleton<ISandboxRunner, DockerSandboxRunner>();

// Capabilities --------------------------------------------------------------
// Registered as the closed set the registry names. The invoker applies the
// permission class and the audit record around whichever one is selected, so
// adding an implementation here cannot bypass those (FR-026c).
builder.Services.AddScoped<ICapability, ListFilesCapability>();
builder.Services.AddScoped<ICapability, ReadFileCapability>();
builder.Services.AddScoped<ICapability, SearchCodeCapability>();
builder.Services.AddScoped<ICapability, SearchDocsCapability>();
builder.Services.AddScoped<ICapability, ProposePatchCapability>();
builder.Services.AddScoped<ICapability, ApplyPatchCapability>();
builder.Services.AddScoped<ICapability>(sp => new RunTestsCapability(
    sp.GetRequiredService<ISandboxRunner>(),
    sp.GetRequiredService<IRepositoryFixtureStore>(),
    sp.GetRequiredService<ITestResultStore>(),
    sp.GetRequiredService<IRunStore>(),
    sp.GetRequiredService<WorkingCopyManager>().PathFor));

builder.Services.AddScoped<ToolInvoker>();
builder.Services.AddScoped<ICapabilityInvoker>(sp => sp.GetRequiredService<ToolInvoker>());

// Orchestration -------------------------------------------------------------
// The queue is a singleton because its limiter is the process-wide bound; the
// orchestrator is scoped because it works through a scoped DbContext.
builder.Services.AddSingleton<RunQueue>();
builder.Services.AddScoped<RunOrchestrator>();
builder.Services.AddScoped<DecideProposalUseCase>();
builder.Services.AddScoped<RegisterRepositoryUseCase>();
builder.Services.AddScoped<IndexRepositoryUseCase>();
builder.Services.AddScoped<IIndexBuilder>(sp => sp.GetRequiredService<IndexingService>());
builder.Services.AddScoped<CancelRunUseCase>();
builder.Services.AddHostedService<RunExecutionService>();

// Options the orchestrator reads through IOptions ---------------------------
builder.Services.AddSingleton(Options.Create(concurrency));
builder.Services.AddSingleton(Options.Create(retrieval));
builder.Services.AddSingleton(Options.Create(workspace));
builder.Services.AddSingleton(Options.Create(allowed));

// Recovery ------------------------------------------------------------------
builder.Services.AddScoped<StartupRecoveryService>();

// Observability -------------------------------------------------------------
builder.Services.AddRepoPilotTelemetry(
    builder.Configuration["RepoPilot:Telemetry:OtlpEndpoint"]);

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
app.MapApprovalEndpoints();

app.Run();

/// <summary>Exposed so the integration test host can reference the entry point.</summary>
public partial class Program;
