using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoPilot.Application.Approval;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Runs;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;
using RepoPilot.Evals;
using RepoPilot.Evals.Reporting;
using RepoPilot.Evals.Tasks;
using RepoPilot.Infrastructure.Events;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.Infrastructure.Retrieval;
using RepoPilot.IntegrationTests.Infrastructure;
using RepoPilot.IntegrationTests.Runs;
using RepoPilot.IntegrationTests.Security;
using Xunit;

namespace RepoPilot.IntegrationTests.Evals;

/// <summary>
/// SC-007's precondition: an evaluation never builds an index.
/// <para>
/// Determinism is checked elsewhere by measuring twice and comparing. This checks
/// the thing that makes that comparison meaningful — that the harness cannot be
/// the reason an index changed between two evaluations. It refuses an unindexed
/// fixture rather than preparing one, so a repeat evaluation reads the same
/// vectors as the first by construction rather than by luck.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NeverReindexesTests(PostgresFixture postgres) : IDisposable
{
    private readonly List<string> _roots = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Builds a harness over the real stores. Only the pieces that would reach a
    /// model or a container are substituted; everything the test asserts on is the
    /// production type.
    /// </summary>
    private EvaluationHarness Build(RepoPilotDbContext db, string resultsDirectory)
    {
        var recorder = new RunEventRecorder(new EfRunEventStore(db), new NullRunEventPublisher());
        var workspaces = new TempWorkspaceProvisioner();
        _disposables.Add(workspaces);

        var orchestrator = new RunOrchestrator(
            new EfRunStore(db),
            new EfRepositoryFixtureStore(db),
            new EfProposalStore(db),
            new EfApprovalStore(db),
            workspaces,
            ScriptedAgent.ThatCannotFindAnything(),
            new ScriptedCapabilities(new EfProposalStore(db), testsPass: true, recorder),
            recorder,
            Options.Create(new RunConcurrencyOptions()),
            Options.Create(new RetrievalOptions()),
            new ScriptedBaselineContext());

        var decide = new DecideProposalUseCase(
            new EfProposalStore(db), new EfApprovalStore(db), new EfRunStore(db), recorder);

        return new EvaluationHarness(
            new EvaluationTaskLoader(CommittedArtifacts.TasksDirectory),
            new EfRepositoryFixtureStore(db),
            new EfRunStore(db),
            new EfProposalStore(db),
            new EfApprovalStore(db),
            new EfRunEventStore(db),
            new EfTestResultStore(db),
            new EfEvaluationStore(db),
            orchestrator,
            new ProgrammaticApproval(decide, new EfProposalStore(db), new EfRunStore(db)),
            new FixtureBaselineProbe(new RecordingSandbox(), new WorkspaceOptions()),
            new HybridRetriever(
                db,
                new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
                new RetrievalOptions()),
            new ReportWriter(resultsDirectory),
            NullLogger<EvaluationHarness>.Instance);
    }

    [RequiresDockerFact]
    public async Task AnUnindexedFixtureIsRefusedRatherThanIndexed()
    {
        await using var db = postgres.CreateContext();

        var results = Directory.CreateTempSubdirectory("repopilot-results-").FullName;
        _roots.Add(results);

        // Registered, never indexed — the state an operator leaves a fixture in
        // between `POST /api/repositories` and `POST .../index`.
        var fixture = new RepositoryFixture
        {
            Slug = "sample-dotnet-api",
            DisplayName = "Unindexed",
            RootPath = Path.Combine(CommittedArtifacts.FixturesDirectory, "sample-dotnet-api"),
            TestConfigJson = await File.ReadAllTextAsync(Path.Combine(
                CommittedArtifacts.FixturesDirectory, "sample-dotnet-api", "repopilot.fixture.json")),
        };

        db.Repositories.Add(fixture);
        await db.SaveChangesAsync();

        var refusal = await Assert.ThrowsAsync<EvaluationRefusedException>(
            () => Build(db, results).RunAsync());

        Assert.Contains("no active index", refusal.Message, StringComparison.OrdinalIgnoreCase);

        // The refusal did not quietly prepare one on the way out. If it had, a
        // second evaluation would be reading vectors the first one created, and
        // SC-007's comparison would be between two different indexes.
        await db.Entry(fixture).ReloadAsync();

        Assert.Null(fixture.ActiveIndexVersion);
        Assert.Equal(IndexingStatus.NeverIndexed, fixture.IndexingStatus);
        Assert.Empty(db.IndexEntries.Where(e => e.RepositoryId == fixture.Id));
    }

    [RequiresDockerFact]
    public async Task AFixtureThatIsNotRegisteredAtAllIsRefused()
    {
        await using var db = postgres.CreateContext();

        var results = Directory.CreateTempSubdirectory("repopilot-results-").FullName;
        _roots.Add(results);

        var refusal = await Assert.ThrowsAsync<EvaluationRefusedException>(
            () => Build(db, results).RunAsync());

        Assert.Contains("not registered", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }
}
