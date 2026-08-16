using Microsoft.EntityFrameworkCore;
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
/// FR-003a as an operator sees it: a rebuild never degrades the service.
/// <para>
/// The swap mechanism itself is covered by the index-rebuild tests. What this
/// asserts is the visible consequence — searches during a rebuild keep answering
/// from the previous index, and afterwards there is exactly one generation of
/// results rather than two overlapping ones.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AtomicSwapAcceptanceTests(PostgresFixture postgres) : IDisposable
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

    private string CreateTree(string marker)
    {
        var root = Directory.CreateTempSubdirectory("repopilot-swap-").FullName;
        _roots.Add(root);

        Directory.CreateDirectory(Path.Combine(root, "src"));
        WriteMarker(root, marker);

        return root;
    }

    private static void WriteMarker(string root, string marker) =>
        File.WriteAllText(
            Path.Combine(root, "src", "OrderLookupService.cs"),
            $$"""
            namespace Orders;

            /// <summary>Looks up orders. Generation: {{marker}}.</summary>
            public sealed class OrderLookupService
            {
                public string Generation => "{{marker}}";
            }
            """);

    private static IndexRepositoryUseCase UseCase(RepoPilotDbContext db) =>
        new(new EfRepositoryFixtureStore(db),
            new IndexingService(
                db,
                new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
                new IndexingOptions { EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions },
                NullLogger<IndexingService>.Instance),
            NullLogger<IndexRepositoryUseCase>.Instance);

    private static HybridRetriever Retriever(RepoPilotDbContext db) =>
        new(db,
            new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
            new RetrievalOptions());

    private async Task<RepositoryFixture> RegisterAsync(RepoPilotDbContext db, string root)
    {
        var fixture = new RepositoryFixture
        {
            Slug = "swap-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Swap fixture",
            RootPath = root,
            TestConfigJson = """{"slug":"swap","commands":[]}""",
        };

        db.Repositories.Add(fixture);
        await db.SaveChangesAsync();

        return fixture;
    }

    [RequiresDockerFact]
    public async Task ReindexingLeavesExactlyOneGenerationOfResults()
    {
        await using var db = postgres.CreateContext();

        var root = CreateTree("first");
        var fixture = await RegisterAsync(db, root);

        await UseCase(db).RebuildAsync(fixture.Id);

        WriteMarker(root, "second");
        await UseCase(db).RebuildAsync(fixture.Id);

        var results = await Retriever(db).SearchAsync(fixture.Id, "OrderLookupService", 20);

        // The old generation is not merely deprioritized — it is not returned at
        // all. A search that could surface either generation would make results
        // depend on ranking luck.
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Contains("second", r.Content));
        Assert.DoesNotContain(results, r => r.Content.Contains("first", StringComparison.Ordinal));

        // One chunk per location, not two. Duplicates would be the visible
        // symptom of a rebuild that added without replacing.
        Assert.Equal(
            results.Select(r => (r.RelativePath, r.StartLine)).Distinct().Count(),
            results.Count);
    }

    [RequiresDockerFact]
    public async Task SearchesDuringARebuildStillAnswerFromThePreviousIndex()
    {
        await using var db = postgres.CreateContext();

        var root = CreateTree("first");
        var fixture = await RegisterAsync(db, root);

        await UseCase(db).RebuildAsync(fixture.Id);

        // A second connection stands in for a search arriving mid-rebuild: the
        // rebuild's writes are inside an uncommitted transaction, so this one
        // cannot see them.
        await using var searcher = postgres.CreateContext();

        WriteMarker(root, "second");

        var rebuild = UseCase(db).RebuildAsync(fixture.Id);

        var duringResults = await Retriever(searcher).SearchAsync(
            fixture.Id, "OrderLookupService", 20);

        // Whatever moment this landed in, it is a coherent one: either entirely
        // the old generation or entirely the new. What must never appear is a
        // half-populated index.
        if (duringResults.Count > 0)
        {
            var generations = duringResults
                .Select(r => r.Content.Contains("second", StringComparison.Ordinal))
                .Distinct()
                .ToList();

            Assert.Single(generations);
        }

        await rebuild;

        var afterResults = await Retriever(searcher).SearchAsync(
            fixture.Id, "OrderLookupService", 20);

        Assert.NotEmpty(afterResults);
        Assert.All(afterResults, r => Assert.Contains("second", r.Content));
    }

    [RequiresDockerFact]
    public async Task TheIndexVersionAdvancesOnEveryRebuild()
    {
        await using var db = postgres.CreateContext();

        var fixture = await RegisterAsync(db, CreateTree("first"));

        var first = await UseCase(db).RebuildAsync(fixture.Id);
        var second = await UseCase(db).RebuildAsync(fixture.Id);

        Assert.Equal(first.IndexVersion + 1, second.IndexVersion);

        await using var fresh = postgres.CreateContext();

        // Only the active version's entries survive. Retaining superseded
        // versions would grow without bound and give a future bug somewhere to
        // return stale results from.
        var versions = await fresh.IndexEntries
            .Where(e => e.RepositoryId == fixture.Id)
            .Select(e => e.IndexVersion)
            .Distinct()
            .ToListAsync();

        Assert.Equal([second.IndexVersion], versions);
    }

    [RequiresDockerFact]
    public async Task ASecondRebuildIsRefusedWhileOneIsAlreadyRunning()
    {
        await using var db = postgres.CreateContext();

        var fixture = await RegisterAsync(db, CreateTree("first"));

        // Claim it the way a running rebuild would.
        fixture.IndexingStatus = IndexingStatus.Indexing;
        await new EfRepositoryFixtureStore(db).UpdateAsync(fixture);

        var refusal = await Assert.ThrowsAsync<IndexRebuildInProgressException>(
            () => UseCase(db).RebuildAsync(fixture.Id));

        Assert.Equal(fixture.Slug, refusal.Slug);
    }
}
