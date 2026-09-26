using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RepoPilot.Agent.Capabilities;
using RepoPilot.Agent.Invocation;
using RepoPilot.Application.Approval;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Proposals;
using RepoPilot.Application.Runs;
using RepoPilot.Application.UseCases;
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

namespace RepoPilot.Agent.Hosting;

/// <summary>
/// The composition root shared by every host that runs the agent.
/// <para>
/// It exists so the API and the evaluation harness cannot drift apart. FR-034
/// requires an evaluation to exercise the same path an interactive run takes;
/// two hand-maintained registration lists would satisfy that on the day they were
/// written and stop satisfying it the first time a capability was added to one of
/// them — and nothing would fail, the evaluation would just quietly be measuring
/// a different system.
/// </para>
/// </summary>
public static class RepoPilotServices
{
    /// <summary>
    /// Registers everything needed to execute runs: configuration, persistence,
    /// providers, retrieval, the capability set, and the orchestrator.
    /// </summary>
    /// <remarks>
    /// Hosted services and HTTP concerns are deliberately not here. A host that
    /// wants a background executor or an endpoint surface adds it; a host that
    /// only wants to drive runs in-process, as the harness does, must not get one
    /// by accident.
    /// </remarks>
    public static IServiceCollection AddRepoPilotCore(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var workspace = Bind<WorkspaceOptions>(configuration, WorkspaceOptions.SectionName);
        var concurrency = Bind<RunConcurrencyOptions>(
            configuration, RunConcurrencyOptions.SectionName);
        var retrieval = Bind<RetrievalOptions>(configuration, RetrievalOptions.SectionName);
        var indexing = Bind<IndexingOptions>(configuration, IndexingOptions.SectionName);
        var allowed = Bind<AllowedRepositoryOptions>(
            configuration, AllowedRepositoryOptions.SectionName);
        var proposals = Bind<ProposalLimitOptions>(configuration, ProposalLimitOptions.SectionName);
        var chat = Bind<AnthropicChatOptions>(configuration, AnthropicChatOptions.SectionName);

        services.AddSingleton(workspace);
        services.AddSingleton(concurrency);
        services.AddSingleton(retrieval);
        services.AddSingleton(indexing);
        services.AddSingleton(allowed);
        services.AddSingleton(proposals);
        services.AddSingleton(chat);

        // Registered both ways because the orchestrator reads through IOptions and
        // most other consumers take the option object directly.
        services.AddSingleton(Options.Create(concurrency));
        services.AddSingleton(Options.Create(retrieval));
        services.AddSingleton(Options.Create(workspace));
        services.AddSingleton(Options.Create(allowed));

        // Persistence ----------------------------------------------------------
        services.AddDbContext<RepoPilotDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("RepoPilot")
                    ?? "Host=localhost;Port=5432;Database=repopilot;Username=repopilot;Password=repopilot",
                npgsql => npgsql.UseVector()));

        services.AddScoped<IRepositoryFixtureStore, EfRepositoryFixtureStore>();
        services.AddScoped<IRunStore, EfRunStore>();
        services.AddScoped<IRunEventStore, EfRunEventStore>();
        services.AddScoped<IProposalStore, EfProposalStore>();
        services.AddScoped<IApprovalStore, EfApprovalStore>();
        services.AddScoped<IWorkingCopyStore, EfWorkingCopyStore>();
        services.AddScoped<ITestResultStore, EfTestResultStore>();
        services.AddScoped<IEvaluationStore, EfEvaluationStore>();

        // Event delivery -------------------------------------------------------
        // Singleton: subscribers outlive any one request, which is the whole point
        // of a live stream. Persist-then-publish ordering lives in
        // RunEventRecorder.
        services.AddSingleton<IRunEventPublisher, InProcessRunEventPublisher>();
        services.AddScoped<RunEventRecorder>();

        // Providers ------------------------------------------------------------
        services.AddSingleton<IChatProviderAdapter, AnthropicChatAdapter>();
        services.AddSingleton<IEmbeddingProviderAdapter>(
            _ => new DeterministicEmbeddingAdapter(indexing.EmbeddingDimensions));
        services.AddScoped<RepoPilotAgent>();
        services.AddScoped<IAgentTurnRunner>(sp => sp.GetRequiredService<RepoPilotAgent>());

        // Retrieval and workspace ----------------------------------------------
        services.AddScoped<HybridRetriever>();
        services.AddScoped<IndexingService>();
        services.AddScoped<IBaselineContextProvider, RetrievalOnlyContext>();
        services.AddScoped<WorkingCopyManager>();
        services.AddScoped<IWorkspaceProvisioner>(
            sp => sp.GetRequiredService<WorkingCopyManager>());

        // Proposals ------------------------------------------------------------
        services.AddSingleton<DiffRenderer>();
        services.AddScoped<ProposalValidator>();

        // Sandbox --------------------------------------------------------------
        // Singleton: the client holds a connection to the daemon, and there is no
        // per-request state. A run that cannot get an isolated environment fails;
        // there is deliberately no unisolated fallback registration.
        services.AddSingleton<ISandboxRunner, DockerSandboxRunner>();

        // Capabilities ---------------------------------------------------------
        // Registered as the closed set the registry names. The invoker applies the
        // permission class and the audit record around whichever one is selected,
        // so adding an implementation here cannot bypass those (FR-026c).
        services.AddScoped<ICapability, ListFilesCapability>();
        services.AddScoped<ICapability, ReadFileCapability>();
        services.AddScoped<ICapability, SearchCodeCapability>();
        services.AddScoped<ICapability, SearchDocsCapability>();
        services.AddScoped<ICapability, ProposePatchCapability>();
        services.AddScoped<ICapability, ApplyPatchCapability>();
        services.AddScoped<ICapability>(sp => new RunTestsCapability(
            sp.GetRequiredService<ISandboxRunner>(),
            sp.GetRequiredService<IRepositoryFixtureStore>(),
            sp.GetRequiredService<ITestResultStore>(),
            sp.GetRequiredService<IRunStore>(),
            sp.GetRequiredService<WorkingCopyManager>().PathFor));

        services.AddScoped<ToolInvoker>();
        services.AddScoped<ICapabilityInvoker>(sp => sp.GetRequiredService<ToolInvoker>());

        // Orchestration --------------------------------------------------------
        // The queue is a singleton because its limiter is the process-wide bound;
        // the orchestrator is scoped because it works through a scoped DbContext.
        services.AddSingleton<RunQueue>();
        services.AddScoped<RunOrchestrator>();
        services.AddScoped<DecideProposalUseCase>();
        services.AddScoped<ProgrammaticApproval>();
        services.AddScoped<RegisterRepositoryUseCase>();
        services.AddScoped<IndexRepositoryUseCase>();
        services.AddScoped<IIndexBuilder>(sp => sp.GetRequiredService<IndexingService>());
        services.AddScoped<CancelRunUseCase>();

        // Observability --------------------------------------------------------
        services.AddRepoPilotTelemetry(configuration["RepoPilot:Telemetry:OtlpEndpoint"]);

        return services;
    }

    private static T Bind<T>(IConfiguration configuration, string section)
        where T : class, new() =>
        configuration.GetSection(section).Get<T>() ?? new T();
}
