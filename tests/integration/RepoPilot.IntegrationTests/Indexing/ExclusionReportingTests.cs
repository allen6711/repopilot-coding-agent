using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Indexing;
using RepoPilot.Infrastructure.Indexing;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Indexing;

/// <summary>
/// FR-003 and FR-003b: an operator can see what was left out and why.
/// <para>
/// A count on its own is not useful. "412 files excluded" could be a correctly
/// configured index or a misconfigured one that dropped the source tree, and the
/// only thing that distinguishes them is the reason breakdown — vendored
/// directories are expected, secret-bearing files are worth a look.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ExclusionReportingTests(PostgresFixture postgres) : IDisposable
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

    /// <summary>Builds a tree containing one file of every exclusion category.</summary>
    private string CreateTreeWithEveryCategory()
    {
        var root = Directory.CreateTempSubdirectory("repopilot-exclusion-").FullName;
        _roots.Add(root);

        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "left-pad"));
        Directory.CreateDirectory(Path.Combine(root, "config"));

        // Included.
        File.WriteAllText(
            Path.Combine(root, "src", "Service.cs"),
            "namespace Sample;\n\npublic sealed class Service\n{\n    public int Value => 42;\n}\n");

        // ExcludedDirectory.
        File.WriteAllText(
            Path.Combine(root, "node_modules", "left-pad", "index.js"),
            "module.exports = function leftPad() { return ''; };\n");

        // Binary.
        File.WriteAllBytes(
            Path.Combine(root, "src", "logo.png"),
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x01]);

        // Size — larger than the limit configured below.
        File.WriteAllText(
            Path.Combine(root, "src", "Huge.cs"),
            new string('x', 40_000));

        // Secret-bearing, by path.
        File.WriteAllText(Path.Combine(root, "config", ".env"), "TOKEN=redact-me\n");

        // Empty: whitespace only, so it yields no chunks.
        File.WriteAllText(Path.Combine(root, "src", "Blank.cs"), "   \n\n  \n");

        return root;
    }

    private async Task<(RepositoryFixture Fixture, RepoPilot.Application.Ports.IndexingReport Report)>
        IndexAsync(RepoPilotDbContext db, string root)
    {
        var fixture = new RepositoryFixture
        {
            Slug = "exclusion-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Exclusion fixture",
            RootPath = root,
            TestConfigJson = """{"slug":"exclusion","commands":[]}""",
        };

        db.Repositories.Add(fixture);
        await db.SaveChangesAsync();

        var options = new IndexingOptions
        {
            MaxFileBytes = 8_192,
            EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions,
        };

        var useCase = new IndexRepositoryUseCase(
            new EfRepositoryFixtureStore(db),
            new IndexingService(
                db,
                new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
                options,
                NullLogger<IndexingService>.Instance),
            NullLogger<IndexRepositoryUseCase>.Instance);

        var report = await useCase.RebuildAsync(fixture.Id);

        return (fixture, report);
    }

    [RequiresDockerFact]
    public async Task EveryExclusionCategoryIsCountedUnderItsOwnReason()
    {
        await using var db = postgres.CreateContext();

        var (_, report) = await IndexAsync(db, CreateTreeWithEveryCategory());

        Assert.Equal(1, report.IncludedFileCount);

        var breakdown = report.ExclusionBreakdown;

        Assert.Equal(1, breakdown[nameof(ExclusionReason.ExcludedDirectory)]);
        Assert.Equal(1, breakdown[nameof(ExclusionReason.Binary)]);
        Assert.Equal(1, breakdown[nameof(ExclusionReason.Size)]);
        Assert.Equal(1, breakdown[nameof(ExclusionReason.SecretFilename)]);
        Assert.Equal(1, breakdown["Empty"]);
    }

    [RequiresDockerFact]
    public async Task TheCountsAddUpToTheFilesWalked()
    {
        await using var db = postgres.CreateContext();

        var root = CreateTreeWithEveryCategory();
        var (_, report) = await IndexAsync(db, root);

        var onDisk = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();

        // If they did not add up, a file would have been dropped without any
        // reason recorded — which is the failure mode the breakdown exists to
        // make impossible to have silently.
        Assert.Equal(
            onDisk,
            report.IncludedFileCount + report.ExcludedFileCount);

        Assert.Equal(
            report.ExcludedFileCount,
            report.ExclusionBreakdown.Values.Sum());
    }

    [RequiresDockerFact]
    public async Task TheBreakdownIsPersistedOnTheRepository()
    {
        await using var db = postgres.CreateContext();

        var (fixture, report) = await IndexAsync(db, CreateTreeWithEveryCategory());

        await using var fresh = postgres.CreateContext();
        var stored = await new EfRepositoryFixtureStore(fresh).FindByIdAsync(fixture.Id);

        // Read back through a second context: an operator opening the page later
        // sees the same account the build reported, without the build still
        // being in memory.
        Assert.NotNull(stored);
        Assert.Equal(report.IncludedFileCount, stored!.IncludedFileCount);
        Assert.Equal(report.ExcludedFileCount, stored.ExcludedFileCount);
        Assert.Equal(IndexingStatus.Indexed, stored.IndexingStatus);
    }

    [RequiresDockerFact]
    public async Task ReasonsThatDidNotOccurAreReportedAsZeroRatherThanOmitted()
    {
        await using var db = postgres.CreateContext();

        var (fixture, _) = await IndexAsync(db, CreateTreeWithEveryCategory());

        await using var fresh = postgres.CreateContext();
        var stored = await new EfRepositoryFixtureStore(fresh).FindByIdAsync(fixture.Id);

        var full = IndexRepositoryUseCase.FullBreakdown(stored!);

        // Every normative reason appears. Omitting the zeroes would make "no
        // secret-bearing files were found" indistinguishable from "secret
        // detection did not run" (FR-003b).
        foreach (var reason in Enum.GetValues<ExclusionReason>())
        {
            if (reason == ExclusionReason.None)
            {
                continue;
            }

            Assert.True(
                full.ContainsKey(reason.ToString()),
                $"The breakdown omits '{reason}'.");
        }
    }

    [RequiresDockerFact]
    public async Task AFailedRebuildLeavesTheRepositoryMarkedFailedRatherThanIndexing()
    {
        await using var db = postgres.CreateContext();

        // A real, documented failure: the repository was indexed with one
        // embedding model and a rebuild is attempted with a different vector
        // width. Mixing vector spaces returns silently wrong neighbours, so the
        // service refuses rather than reconciling.
        var (fixture, _) = await IndexAsync(db, CreateTreeWithEveryCategory());

        var mismatched = new IndexRepositoryUseCase(
            new EfRepositoryFixtureStore(db),
            new IndexingService(
                db,
                new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions / 2),
                new IndexingOptions
                {
                    EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions / 2,
                },
                NullLogger<IndexingService>.Instance),
            NullLogger<IndexRepositoryUseCase>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mismatched.RebuildAsync(fixture.Id));

        await using var fresh = postgres.CreateContext();
        var stored = await new EfRepositoryFixtureStore(fresh).FindByIdAsync(fixture.Id);

        // Left as Indexing, every later rebuild would be refused as already
        // running — one transient failure would wedge the repository for good.
        Assert.Equal(IndexingStatus.Failed, stored!.IndexingStatus);
    }
}
