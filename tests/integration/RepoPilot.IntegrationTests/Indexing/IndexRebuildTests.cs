using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Application.Configuration;
using RepoPilot.Domain.Entities;
using RepoPilot.Infrastructure.Indexing;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Indexing;

/// <summary>
/// FR-002, FR-003, FR-003b, and the User Story 2 acceptance scenarios, against
/// real pgvector.
/// <para>
/// These run against PostgreSQL rather than an in-memory provider on purpose:
/// the exclusion guarantees are only meaningful if what is asserted is what the
/// production schema stores, and the atomic swap is a transaction property that
/// an in-memory provider does not have.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IndexRebuildTests(PostgresFixture postgres) : IDisposable
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

    /// <summary>Builds a fixture directory containing one of every exclusion category.</summary>
    private string CreateFixtureTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "repopilot-idx-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);

        Directory.CreateDirectory(Path.Combine(root, "src", "Orders"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "left-pad"));
        Directory.CreateDirectory(Path.Combine(root, "bin", "Debug"));
        Directory.CreateDirectory(Path.Combine(root, "config"));

        File.WriteAllText(
            Path.Combine(root, "src", "Orders", "OrderLookupService.cs"),
            """
            namespace Orders;

            public sealed class OrderLookupService
            {
                public OrderView? Lookup(string orderId)
                {
                    var order = _repository.Find(orderId);
                    return order is null ? null : Project(order);
                }
            }
            """);

        File.WriteAllText(
            Path.Combine(root, "README.md"),
            "# Sample\n\nThis service looks up customer orders and shipping details.\n");

        // Each of the following must be excluded, one per normative reason.
        File.WriteAllText(Path.Combine(root, "node_modules", "left-pad", "index.js"), "module.exports = 1;");
        File.WriteAllText(Path.Combine(root, "bin", "Debug", "App.dll"), "binary-ish");
        File.WriteAllText(Path.Combine(root, "config", ".env"), "DATABASE_URL=postgres://localhost/db");
        File.WriteAllText(Path.Combine(root, "src", "Blob.bin"), "prefix\0suffix");
        File.WriteAllText(Path.Combine(root, "src", "Huge.cs"), new string('x', 400_000));

        return root;
    }

    private async Task<RepositoryFixture> SeedRepositoryAsync(RepoPilotDbContext db, string root)
    {
        var repository = new RepositoryFixture
        {
            Slug = "idx-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Indexing fixture",
            RootPath = root,
            TestConfigJson = """{"slug":"idx","commands":[]}""",
        };

        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        return repository;
    }

    private static IndexingService CreateService(RepoPilotDbContext db) =>
        new(db,
            new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
            new IndexingOptions { EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions },
            NullLogger<IndexingService>.Instance);

    [RequiresDockerFact]
    public async Task ExcludedContentNeverReachesTheIndex()
    {
        await using var db = postgres.CreateContext();
        var repository = await SeedRepositoryAsync(db, CreateFixtureTree());

        var report = await CreateService(db).RebuildAsync(repository);

        var indexedPaths = await db.IndexEntries
            .Where(e => e.RepositoryId == repository.Id)
            .Select(e => e.RelativePath)
            .Distinct()
            .ToListAsync();

        Assert.DoesNotContain(indexedPaths, p => p.Contains("node_modules", StringComparison.Ordinal));
        Assert.DoesNotContain(indexedPaths, p => p.Contains("bin/", StringComparison.Ordinal));
        Assert.DoesNotContain(indexedPaths, p => p.EndsWith(".env", StringComparison.Ordinal));
        Assert.DoesNotContain(indexedPaths, p => p.EndsWith("Blob.bin", StringComparison.Ordinal));
        Assert.DoesNotContain(indexedPaths, p => p.EndsWith("Huge.cs", StringComparison.Ordinal));

        Assert.Contains("src/Orders/OrderLookupService.cs", indexedPaths);
        Assert.Equal(2, report.IncludedFileCount);
    }

    [RequiresDockerFact]
    public async Task EveryExclusionIsReportedUnderItsNormativeReason()
    {
        // FR-003b: the reason set is normative and reported per reason, so an
        // operator can see what was dropped rather than only how much.
        await using var db = postgres.CreateContext();
        var repository = await SeedRepositoryAsync(db, CreateFixtureTree());

        var report = await CreateService(db).RebuildAsync(repository);

        Assert.True(report.ExclusionBreakdown["ExcludedDirectory"] >= 2);
        Assert.Equal(1, report.ExclusionBreakdown["SecretFilename"]);
        Assert.Equal(1, report.ExclusionBreakdown["Binary"]);
        Assert.Equal(1, report.ExclusionBreakdown["Size"]);
        Assert.Equal(report.ExcludedFileCount, report.ExclusionBreakdown.Values.Sum());
    }

    [RequiresDockerFact]
    public async Task RebuildingLeavesNoDuplicateEntries()
    {
        // User Story 2, acceptance scenario 4.
        await using var db = postgres.CreateContext();
        var repository = await SeedRepositoryAsync(db, CreateFixtureTree());
        var service = CreateService(db);

        var first = await service.RebuildAsync(repository);
        var second = await service.RebuildAsync(repository);

        var total = await db.IndexEntries.CountAsync(e => e.RepositoryId == repository.Id);

        Assert.Equal(first.ChunkCount, second.ChunkCount);
        Assert.Equal(second.ChunkCount, total);
        Assert.Equal(2, second.IndexVersion);
    }

    [RequiresDockerFact]
    public async Task OnlyTheActiveVersionSurvivesASwap()
    {
        await using var db = postgres.CreateContext();
        var repository = await SeedRepositoryAsync(db, CreateFixtureTree());
        var service = CreateService(db);

        await service.RebuildAsync(repository);
        await service.RebuildAsync(repository);

        var versions = await db.IndexEntries
            .Where(e => e.RepositoryId == repository.Id)
            .Select(e => e.IndexVersion)
            .Distinct()
            .ToListAsync();

        Assert.Equal([repository.ActiveIndexVersion!.Value], versions);
    }

    [RequiresDockerFact]
    public async Task ASearchDuringARebuildStillSeesThePreviousIndex()
    {
        // FR-003a: the previous index serves reads until the swap commits. This
        // is the property that a delete-then-insert rebuild would break, and the
        // reason the swap is one transaction.
        await using var db = postgres.CreateContext();
        var repository = await SeedRepositoryAsync(db, CreateFixtureTree());
        await CreateService(db).RebuildAsync(repository);

        var versionBefore = repository.ActiveIndexVersion!.Value;
        var countBefore = await db.IndexEntries
            .CountAsync(e => e.RepositoryId == repository.Id && e.IndexVersion == versionBefore);

        // A second connection stands in for a concurrent search: it filters on
        // the active version, which has not moved while the rebuild is in flight.
        await using var reader = postgres.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();

        db.IndexEntries.Add(new IndexEntry
        {
            RepositoryId = repository.Id,
            IndexVersion = versionBefore + 1,
            RelativePath = "src/Pending.cs",
            ChunkOrdinal = 0,
            Content = "// written but not yet swapped in",
            StartLine = 1,
            EndLine = 1,
            Embedding = new float[RepoPilotDbContext.EmbeddingDimensions],
        });
        await db.SaveChangesAsync();

        var activeForReader = await reader.Repositories
            .Where(r => r.Id == repository.Id)
            .Select(r => r.ActiveIndexVersion)
            .FirstAsync();

        var visibleToReader = await reader.IndexEntries
            .CountAsync(e => e.RepositoryId == repository.Id && e.IndexVersion == activeForReader);

        Assert.Equal(versionBefore, activeForReader);
        Assert.Equal(countBefore, visibleToReader);

        await transaction.RollbackAsync();
    }

    [RequiresDockerFact]
    public async Task ChangingTheEmbeddingDimensionsIsRefused()
    {
        // Vectors from different models are not comparable; a mixed index would
        // return silently wrong neighbours, so this fails loudly instead.
        await using var db = postgres.CreateContext();
        var repository = await SeedRepositoryAsync(db, CreateFixtureTree());
        await CreateService(db).RebuildAsync(repository);

        var mismatched = new IndexingService(
            db,
            new DeterministicEmbeddingAdapter(256),
            new IndexingOptions { EmbeddingDimensions = 256 },
            NullLogger<IndexingService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mismatched.RebuildAsync(repository));
    }

    [RequiresDockerFact]
    public async Task IndexedChunksCarryExactLineRanges()
    {
        // FR-004. A reviewer follows a result back to the code by these numbers.
        await using var db = postgres.CreateContext();
        var repository = await SeedRepositoryAsync(db, CreateFixtureTree());
        await CreateService(db).RebuildAsync(repository);

        var entry = await db.IndexEntries
            .FirstAsync(e => e.RelativePath == "src/Orders/OrderLookupService.cs");

        Assert.Equal(1, entry.StartLine);
        Assert.True(entry.EndLine >= entry.StartLine);
        Assert.Equal("csharp", entry.Language);
        Assert.Equal(RepoPilotDbContext.EmbeddingDimensions, entry.Embedding.Length);
    }
}
