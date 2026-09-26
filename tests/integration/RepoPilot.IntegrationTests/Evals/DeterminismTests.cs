using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;
using RepoPilot.Evals.Tasks;
using RepoPilot.Infrastructure.Indexing;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.Infrastructure.Retrieval;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Evals;

/// <summary>
/// SC-007: repeating an evaluation on unchanged fixtures reproduces identical
/// retrieval metrics.
/// <para>
/// The retrieval half of an evaluation is the half that can be checked without
/// spending a model call per task, and it is the half the criterion is about. So
/// this measures Recall@5 over the whole committed task set twice, against one
/// index that is never rebuilt between the passes, and requires the two results
/// to be equal task by task rather than equal in aggregate — two runs that
/// disagreed on which tasks hit could still average to the same number.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeterminismTests(PostgresFixture postgres)
{
    /// <summary>The depth SC-005 and FR-032 are stated at.</summary>
    private const int RecallDepth = 5;

    /// <summary>Which tasks had a relevant file in the top five, in task order.</summary>
    private static async Task<IReadOnlyList<(string TaskId, bool Hit)>> MeasureRecallAsync(
        RepoPilotDbContext db,
        IReadOnlyList<EvaluationTaskDefinition> tasks,
        IReadOnlyDictionary<string, Guid> repositoriesBySlug)
    {
        var retriever = CommittedFixtureIndexer.Retriever(db);
        var measured = new List<(string, bool)>(tasks.Count);

        foreach (var task in tasks)
        {
            if (!repositoriesBySlug.TryGetValue(task.RepositorySlug, out var repositoryId))
            {
                continue;
            }

            var results = await retriever.SearchAsync(
                repositoryId, task.Description, RecallDepth, documentationOnly: false);

            var paths = results
                .Take(RecallDepth)
                .Select(r => r.RelativePath.Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            measured.Add((task.Id, task.RelevantFiles.Any(f => paths.Contains(f.Replace('\\', '/')))));
        }

        return measured;
    }

    [RequiresDockerFact]
    public async Task TwoPassesOverAnUnchangedIndexAgreeTaskByTask()
    {
        await using var db = postgres.CreateContext();

        var tasks = await new EvaluationTaskLoader(CommittedArtifacts.TasksDirectory).LoadAsync();

        var repositories = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var slug in tasks.Select(t => t.RepositorySlug).Distinct(StringComparer.Ordinal))
        {
            var fixture = await CommittedFixtureIndexer.IndexOnceAsync(db, slug);
            repositories[slug] = fixture.Id;
        }

        var first = await MeasureRecallAsync(db, tasks, repositories);
        var second = await MeasureRecallAsync(db, tasks, repositories);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [RequiresDockerFact]
    public async Task TheSameQueryReturnsTheSameRankingEveryTime()
    {
        await using var db = postgres.CreateContext();

        var fixture = await CommittedFixtureIndexer.IndexOnceAsync(db, "sample-dotnet-api");
        var retriever = CommittedFixtureIndexer.Retriever(db);

        const string Query =
            "order lookup throws when the customer has no shipping address on file";

        var first = await retriever.SearchAsync(fixture.Id, Query, RecallDepth);
        var second = await retriever.SearchAsync(fixture.Id, Query, RecallDepth);

        // Ranking, not just membership. A retriever whose fusion depended on row
        // order would return the same set in a different order, and Recall@5
        // would then be stable only while the result count stayed below five.
        Assert.Equal(
            first.Select(r => (r.RelativePath, r.StartLine, r.EndLine)),
            second.Select(r => (r.RelativePath, r.StartLine, r.EndLine)));
    }

    [RequiresDockerFact]
    public async Task IndexingTheSameFixtureTwiceProducesTheSameVectors()
    {
        await using var db = postgres.CreateContext();

        // Two separate registrations of the same directory. An evaluation never
        // does this — it is how the test distinguishes "the index happens to be
        // stable" from "indexing is a function of the content".
        var first = await CommittedFixtureIndexer.IndexOnceAsync(db, "sample-dotnet-billing");
        var second = await CommittedFixtureIndexer.IndexOnceAsync(db, "sample-dotnet-billing");

        var firstEntries = db.IndexEntries
            .Where(e => e.RepositoryId == first.Id && e.IndexVersion == first.ActiveIndexVersion)
            .OrderBy(e => e.RelativePath).ThenBy(e => e.ChunkOrdinal)
            .Select(e => new { e.RelativePath, e.ChunkOrdinal, e.StartLine, e.EndLine, e.Content })
            .ToList();

        var secondEntries = db.IndexEntries
            .Where(e => e.RepositoryId == second.Id && e.IndexVersion == second.ActiveIndexVersion)
            .OrderBy(e => e.RelativePath).ThenBy(e => e.ChunkOrdinal)
            .Select(e => new { e.RelativePath, e.ChunkOrdinal, e.StartLine, e.EndLine, e.Content })
            .ToList();

        Assert.NotEmpty(firstEntries);
        Assert.Equal(firstEntries, secondEntries);
    }
}

/// <summary>
/// Locates the committed evaluation artefacts from a test binary, by walking up
/// for the repository marker rather than counting path segments.
/// </summary>
internal static class CommittedArtifacts
{
    internal static string RepositoryRoot { get; } = FindRoot();

    internal static string TasksDirectory { get; } =
        Path.Combine(RepositoryRoot, "evals", "tasks");

    internal static string FixturesDirectory { get; } =
        Path.Combine(RepositoryRoot, "evals", "fixtures");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RepoPilot.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"No repository root above {AppContext.BaseDirectory}.");
    }
}
