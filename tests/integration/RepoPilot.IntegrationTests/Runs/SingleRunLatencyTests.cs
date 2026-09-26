using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoPilot.Agent;
using RepoPilot.Agent.Capabilities;
using RepoPilot.Agent.Invocation;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Proposals;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Infrastructure.Events;
using RepoPilot.Infrastructure.Indexing;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Proposals;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.Infrastructure.Retrieval;
using RepoPilot.Infrastructure.Workspace;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Runs;

/// <summary>
/// SC-003: a single run at or below the concurrency limit reaches its first
/// proposal within three minutes.
/// <para>
/// Measured against a real model over a real index, because the criterion is
/// about wall-clock time a person waits and every substitute removes the part
/// that actually takes the time. It skips without a credential or a daemon —
/// which means the number is either measured or absent, never estimated.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SingleRunLatencyTests(PostgresFixture postgres) : IDisposable
{
    /// <summary>The SC-003 ceiling.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    private readonly string _workspaceRoot =
        Directory.CreateTempSubdirectory("repopilot-latency-").FullName;

    [RequiresLiveStackFact]
    public async Task ASeededTaskReachesItsFirstProposalWithinThreeMinutes()
    {
        await using var db = postgres.CreateContext();

        var fixture = await RegisterAndIndexFixtureAsync(db);

        var run = new Run
        {
            RepositoryId = fixture.Id,
            TaskDescription =
                "OrderLookupService.Lookup dereferences the result of the repository lookup " +
                "without checking for null. Add the missing guard.",
            SeededTaskId = "bugfix-null-guard-01",
        };

        db.Runs.Add(run);
        await db.SaveChangesAsync();

        var eventStore = new EfRunEventStore(db);
        var orchestrator = BuildOrchestrator(db, eventStore);

        var stopwatch = Stopwatch.StartNew();
        var stage = await orchestrator.ExecuteUntilApprovalAsync(run.Id);
        stopwatch.Stop();

        // A run that ended in no-change measured nothing about proposal latency,
        // so the assertion would pass while proving nothing.
        Assert.Equal(RunStage.AwaitingApproval, stage);

        // The recorded events are the evidence, not the stopwatch alone: SC-003
        // is about the run reaching a reviewable proposal, and that is the event
        // a reviewer's client waits for.
        var events = await eventStore.ListAsync(run.Id);
        var proposalCreated = events.FirstOrDefault(
            e => e.ArgumentsSummary?.Contains("ProposalCreated", StringComparison.Ordinal) == true);

        Assert.NotNull(proposalCreated);

        var observed = proposalCreated!.StartedAt - run.StartedAt!.Value;

        Assert.True(
            observed < Budget,
            $"First proposal took {observed.TotalSeconds:F1}s against a {Budget.TotalSeconds:F0}s budget.");

        Assert.True(
            stopwatch.Elapsed < Budget,
            $"The segment took {stopwatch.Elapsed.TotalSeconds:F1}s end to end.");
    }

    private async Task<RepositoryFixture> RegisterAndIndexFixtureAsync(RepoPilotDbContext db)
    {
        var fixture = new RepositoryFixture
        {
            Slug = "sample-dotnet-api",
            DisplayName = "Sample Orders Service",
            RootPath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "../../../../../../evals/fixtures/sample-dotnet-api")),
            TestConfigJson = await File.ReadAllTextAsync(
                Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../../../../evals/fixtures/sample-dotnet-api/repopilot.fixture.json"))),
        };

        db.Repositories.Add(fixture);
        await db.SaveChangesAsync();

        var indexing = new IndexingOptions
        {
            EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions,
        };

        await new IndexingService(
                db,
                new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
                indexing,
                NullLogger<IndexingService>.Instance)
            .RebuildAsync(fixture);

        return fixture;
    }

    private RunOrchestrator BuildOrchestrator(RepoPilotDbContext db, EfRunEventStore eventStore)
    {
        var indexing = new IndexingOptions
        {
            EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions,
        };

        var retrieverOptions = new RetrievalOptions();
        var retriever = new HybridRetriever(
            db, new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions), retrieverOptions);

        var recorder = new RunEventRecorder(eventStore, new InProcessRunEventPublisher());

        var workingCopies = new WorkingCopyManager(
            new EfWorkingCopyStore(db),
            new WorkspaceOptions { Root = _workspaceRoot },
            NullLogger<WorkingCopyManager>.Instance);

        var invoker = new ToolInvoker(
            [
                new ListFilesCapability(indexing),
                new ReadFileCapability(indexing),
                new SearchCodeCapability(retriever),
                new SearchDocsCapability(retriever),
                new ProposePatchCapability(
                    new EfProposalStore(db),
                    new EfRunStore(db),
                    new ProposalValidator(new ProposalLimitOptions()),
                    new DiffRenderer()),
            ],
            recorder);

        return new RunOrchestrator(
            new EfRunStore(db),
            new EfRepositoryFixtureStore(db),
            new EfProposalStore(db),
            new EfApprovalStore(db),
            workingCopies,
            new RepoPilotAgent(
                new AnthropicChatAdapter(
                    new AnthropicChatOptions(), NullLogger<AnthropicChatAdapter>.Instance)),
            invoker,
            recorder,
            Options.Create(new RunConcurrencyOptions()),
            Options.Create(retrieverOptions),
            new RetrievalOnlyContext(retriever, retrieverOptions));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException)
        {
        }
    }
}
