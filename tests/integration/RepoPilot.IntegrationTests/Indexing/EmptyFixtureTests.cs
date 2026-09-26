using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;
using RepoPilot.Infrastructure.Indexing;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.Infrastructure.Retrieval;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Indexing;

/// <summary>
/// A fixture with nothing indexable.
/// <para>
/// It registers and indexes without error — an empty repository is a legitimate
/// state, not a malformed one — and then everything that depends on retrieval
/// refuses clearly. The alternative, letting a run start against an empty index,
/// produces a run that ends as insufficient-context and looks like a retrieval
/// quality problem rather than an operator one.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmptyFixtureTests(PostgresFixture postgres) : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
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

    /// <summary>A tree containing only files no policy would index.</summary>
    private string CreateEmptyTree()
    {
        var root = Directory.CreateTempSubdirectory("repopilot-empty-").FullName;
        _roots.Add(root);

        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        File.WriteAllText(Path.Combine(root, "node_modules", "vendor.js"), "module.exports = 1;\n");

        return root;
    }

    private async Task<RepositoryFixture> IndexEmptyAsync(RepoPilotDbContext db)
    {
        var fixture = new RepositoryFixture
        {
            Slug = "empty-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Empty fixture",
            RootPath = CreateEmptyTree(),
            TestConfigJson = """{"slug":"empty","commands":[]}""",
        };

        db.Repositories.Add(fixture);
        await db.SaveChangesAsync();

        var useCase = new IndexRepositoryUseCase(
            new EfRepositoryFixtureStore(db),
            new IndexingService(
                db,
                new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
                new IndexingOptions { EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions },
                NullLogger<IndexingService>.Instance),
            NullLogger<IndexRepositoryUseCase>.Instance);

        var report = await useCase.RebuildAsync(fixture.Id);

        Assert.Equal(0, report.IncludedFileCount);

        return fixture;
    }

    [RequiresDockerFact]
    public async Task AnEmptyFixtureIndexesSuccessfullyAndReportsZero()
    {
        await using var db = postgres.CreateContext();

        var fixture = await IndexEmptyAsync(db);

        await using var fresh = postgres.CreateContext();
        var stored = await new EfRepositoryFixtureStore(fresh).FindByIdAsync(fixture.Id);

        // Indexed, not failed. Nothing went wrong; there was simply nothing to
        // index, and the reason breakdown says so.
        Assert.Equal(IndexingStatus.Indexed, stored!.IndexingStatus);
        Assert.Equal(0, stored.IncludedFileCount);
        Assert.True(stored.ExcludedFileCount > 0);
        Assert.NotNull(stored.ActiveIndexVersion);
    }

    [RequiresDockerFact]
    public async Task SearchingAnEmptyIndexReturnsNothingRatherThanFailing()
    {
        await using var db = postgres.CreateContext();

        var fixture = await IndexEmptyAsync(db);

        var retriever = new HybridRetriever(
            db,
            new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
            new RetrievalOptions());

        // The retriever's own contract: no matches is an empty list. The API
        // layer is what turns "this repository has nothing indexed" into a
        // refusal, because that distinction matters to an operator and not to
        // the query.
        Assert.Empty(await retriever.SearchAsync(fixture.Id, "order lookup"));
    }

    [RequiresDockerFact]
    public async Task ARunCannotBeStartedAgainstAnEmptyFixture()
    {
        await using var db = postgres.CreateContext();

        var fixture = await IndexEmptyAsync(db);

        await using var fresh = postgres.CreateContext();
        var stored = await new EfRepositoryFixtureStore(fresh).FindByIdAsync(fixture.Id);

        // The condition POST /api/runs checks. Asserting it here keeps the rule
        // testable without a host, and the contract test asserts the 409 that
        // this condition produces.
        Assert.True(
            stored!.ActiveIndexVersion is null || stored.IncludedFileCount == 0,
            "An empty fixture must satisfy the condition that refuses run creation.");
    }

    [RequiresDockerFact]
    public async Task AnEmptyFixtureCanBeReindexedOnceItHasContent()
    {
        await using var db = postgres.CreateContext();

        var fixture = await IndexEmptyAsync(db);

        // An empty fixture is not a dead end: adding a file and rebuilding
        // brings it into service without re-registering it.
        File.WriteAllText(
            Path.Combine(fixture.RootPath, "Service.cs"),
            "namespace Sample;\n\npublic sealed class Service\n{\n    public int Value => 1;\n}\n");

        var useCase = new IndexRepositoryUseCase(
            new EfRepositoryFixtureStore(db),
            new IndexingService(
                db,
                new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
                new IndexingOptions { EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions },
                NullLogger<IndexingService>.Instance),
            NullLogger<IndexRepositoryUseCase>.Instance);

        var report = await useCase.RebuildAsync(fixture.Id);

        Assert.Equal(1, report.IncludedFileCount);
        Assert.Equal(2, report.IndexVersion);
    }
}
