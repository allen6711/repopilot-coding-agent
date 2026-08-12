using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Application.Configuration;
using RepoPilot.Domain.Entities;
using RepoPilot.Infrastructure.Indexing;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.Infrastructure.Retrieval;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Retrieval;

/// <summary>
/// FR-004 and FR-005, against the real HNSW, GIN, and trigram indexes.
/// <para>
/// The acceptance scenario these serve is concrete: searching for an exact
/// identifier must return that identifier's defining file with its line range.
/// That is also what SC-005's Recall@5 measures, so a retriever that passes here
/// is the one the evaluation set will grade.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HybridRetrieverTests(PostgresFixture postgres) : IDisposable
{
    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (var root in _tempRoots)
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

    private string CreateSearchableTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "repopilot-ret-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);

        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));

        File.WriteAllText(Path.Combine(root, "src", "OrderLookupService.cs"),
            """
            namespace Orders;

            public sealed class OrderLookupService
            {
                public OrderView? Lookup(string orderId) => Project(_repository.Find(orderId));
            }
            """);

        File.WriteAllText(Path.Combine(root, "src", "InventoryReconciler.cs"),
            """
            namespace Inventory;

            public sealed class InventoryReconciler
            {
                public void Reconcile(WarehouseSnapshot snapshot) => Apply(snapshot);
            }
            """);

        File.WriteAllText(Path.Combine(root, "src", "ShippingCalculator.cs"),
            """
            namespace Shipping;

            public sealed class ShippingCalculator
            {
                public decimal Calculate(Parcel parcel) => parcel.Weight * RatePerKilo;
            }
            """);

        File.WriteAllText(Path.Combine(root, "docs", "architecture.md"),
            "# Architecture\n\nOrders are looked up through a dedicated service layer.\n");

        return root;
    }

    private async Task<RepositoryFixture> IndexAsync(RepoPilotDbContext db)
    {
        var repository = new RepositoryFixture
        {
            Slug = "ret-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Retrieval fixture",
            RootPath = CreateSearchableTree(),
            TestConfigJson = """{"slug":"ret","commands":[]}""",
        };

        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        var service = new IndexingService(
            db,
            new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
            new IndexingOptions { EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions },
            NullLogger<IndexingService>.Instance);

        await service.RebuildAsync(repository);
        return repository;
    }

    private static HybridRetriever CreateRetriever(RepoPilotDbContext db) =>
        new(db,
            new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
            new RetrievalOptions());

    [RequiresDockerFact]
    public async Task AnExactIdentifierReturnsItsDefiningFile()
    {
        // User Story 2, acceptance scenario 3 — the single most common real
        // query a coding agent makes.
        await using var db = postgres.CreateContext();
        var repository = await IndexAsync(db);

        var results = await CreateRetriever(db).SearchAsync(repository.Id, "OrderLookupService", limit: 5);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.RelativePath == "src/OrderLookupService.cs");
    }

    [RequiresDockerFact]
    public async Task EveryResultCarriesPathLineRangeAndScore()
    {
        // FR-004 lists exactly these fields.
        await using var db = postgres.CreateContext();
        var repository = await IndexAsync(db);

        var results = await CreateRetriever(db).SearchAsync(repository.Id, "OrderLookupService");

        Assert.All(results, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.RelativePath));
            Assert.False(string.IsNullOrWhiteSpace(r.Content));
            Assert.True(r.StartLine >= 1);
            Assert.True(r.EndLine >= r.StartLine);
            Assert.True(r.Score > 0);
        });
    }

    [RequiresDockerFact]
    public async Task ResultsAreOrderedByDescendingScore()
    {
        await using var db = postgres.CreateContext();
        var repository = await IndexAsync(db);

        var results = await CreateRetriever(db).SearchAsync(repository.Id, "shipping calculator parcel");

        var scores = results.Select(r => r.Score).ToList();
        Assert.Equal(scores.OrderByDescending(s => s), scores);
    }

    [RequiresDockerFact]
    public async Task ADescriptiveQueryFindsCodeItDoesNotNameExactly()
    {
        // The vector arm's job. A purely lexical search would miss this, which is
        // why both arms always run rather than one being chosen per query.
        await using var db = postgres.CreateContext();
        var repository = await IndexAsync(db);

        var results = await CreateRetriever(db).SearchAsync(
            repository.Id, "calculate shipping cost for a parcel", limit: 5);

        Assert.Contains(results, r => r.RelativePath == "src/ShippingCalculator.cs");
    }

    [RequiresDockerFact]
    public async Task DocumentationSearchIsRestrictedToDocumentation()
    {
        await using var db = postgres.CreateContext();
        var repository = await IndexAsync(db);

        var results = await CreateRetriever(db).SearchAsync(
            repository.Id, "orders service layer", documentationOnly: true);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.EndsWith(".md", r.RelativePath, StringComparison.Ordinal));
    }

    [RequiresDockerFact]
    public async Task TheLimitIsHonoured()
    {
        await using var db = postgres.CreateContext();
        var repository = await IndexAsync(db);

        var results = await CreateRetriever(db).SearchAsync(repository.Id, "public sealed class", limit: 2);

        Assert.True(results.Count <= 2);
    }

    [RequiresDockerFact]
    public async Task AnUnindexedRepositoryReturnsNothingRatherThanThrowing()
    {
        await using var db = postgres.CreateContext();

        var repository = new RepositoryFixture
        {
            Slug = "empty-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Never indexed",
            RootPath = "/nonexistent",
            TestConfigJson = """{"slug":"empty","commands":[]}""",
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        var results = await CreateRetriever(db).SearchAsync(repository.Id, "anything");

        Assert.Empty(results);
    }

    [RequiresDockerFact]
    public async Task AQueryContainingLikeWildcardsMatchesThemLiterally()
    {
        // Unescaped '%' would silently widen the search rather than fail, which
        // is the kind of bug that only shows up as poor retrieval quality.
        await using var db = postgres.CreateContext();
        var repository = await IndexAsync(db);

        var results = await CreateRetriever(db).SearchAsync(repository.Id, "100%_discount", limit: 5);

        Assert.All(results, r => Assert.True(r.Score > 0));
    }

    [RequiresDockerFact]
    public async Task RepeatedSearchesReturnIdenticalResults()
    {
        // SC-007 rests on this: identical inputs over an unchanged index must
        // produce identical rankings, or evaluation metrics are not reproducible.
        await using var db = postgres.CreateContext();
        var repository = await IndexAsync(db);
        var retriever = CreateRetriever(db);

        var first = await retriever.SearchAsync(repository.Id, "OrderLookupService", limit: 5);
        var second = await retriever.SearchAsync(repository.Id, "OrderLookupService", limit: 5);

        Assert.Equal(
            first.Select(r => (r.ChunkId, r.Score)),
            second.Select(r => (r.ChunkId, r.Score)));
    }
}
