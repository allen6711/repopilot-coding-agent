using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;
using RepoPilot.Infrastructure.Indexing;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.Infrastructure.Retrieval;

// CommittedArtifacts locates evals/tasks and evals/fixtures from a test binary.
// It lives beside the evaluation tests that six other files already import it
// from, so it is imported rather than moved: relocating it would edit six files
// to spare this one line.
using RepoPilot.IntegrationTests.Evals;

namespace RepoPilot.IntegrationTests.Infrastructure;

/// <summary>
/// Registers and indexes a committed fixture, once.
/// <para>
/// Shared by the tests that measure retrieval over the committed task set, so
/// that two of them cannot index the same fixture in two slightly different ways
/// and then disagree about Recall@5 for a reason that has nothing to do with the
/// retriever.
/// </para>
/// <para>
/// Deliberately offers no re-index helper. An evaluation never rebuilds an index
/// — SC-007 requires a repeat measurement to read the same vectors — and a test
/// helper that made rebuilding easy would be handing out the one operation the
/// criterion forbids.
/// </para>
/// </summary>
internal static class CommittedFixtureIndexer
{
    /// <summary>
    /// Registers <paramref name="slug"/> under a unique name and builds its index.
    /// </summary>
    /// <remarks>
    /// The slug is suffixed so one class can index the same fixture twice as two
    /// separate repositories, which is how a test distinguishes "the index
    /// happens to be stable" from "indexing is a function of the content".
    /// </remarks>
    internal static async Task<RepositoryFixture> IndexOnceAsync(
        RepoPilotDbContext db, string slug)
    {
        var fixture = new RepositoryFixture
        {
            Slug = slug + "-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = slug,
            RootPath = Path.Combine(CommittedArtifacts.FixturesDirectory, slug),
            TestConfigJson = await File.ReadAllTextAsync(
                Path.Combine(CommittedArtifacts.FixturesDirectory, slug, "repopilot.fixture.json")),
        };

        db.Repositories.Add(fixture);
        await db.SaveChangesAsync();

        var useCase = new IndexRepositoryUseCase(
            new EfRepositoryFixtureStore(db),
            new IndexingService(
                db,
                new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
                new IndexingOptions
                {
                    EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions,
                },
                NullLogger<IndexingService>.Instance),
            NullLogger<IndexRepositoryUseCase>.Instance);

        await useCase.RebuildAsync(fixture.Id);

        return (await new EfRepositoryFixtureStore(db).FindByIdAsync(fixture.Id))!;
    }

    /// <summary>A retriever over the same deterministic embeddings indexing used.</summary>
    internal static HybridRetriever Retriever(RepoPilotDbContext db) =>
        new(
            db,
            new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
            new RetrievalOptions());
}
