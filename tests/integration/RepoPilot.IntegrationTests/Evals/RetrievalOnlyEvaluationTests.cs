using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Agent.Hosting;
using RepoPilot.Application.Ports;
using RepoPilot.Evals;
using RepoPilot.Evals.Hosting;
using RepoPilot.Evals.Metrics;
using RepoPilot.Evals.Modes;
using RepoPilot.Evals.Reporting;
using RepoPilot.Evals.Tasks;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Evals;

/// <summary>
/// SC-005, SC-007: the retrieval-only mode reports the figure the retriever
/// produces, and needs no model provider to do it.
/// <para>
/// The mode exists so Recall@5 can be measured in a checkout with no credential.
/// That claim has two halves and each is tested here: the number it reports is
/// the retriever's own, and obtaining it constructs no chat provider.
/// </para>
/// <para>
/// Fixtures are registered under uniquified slugs and the copied task
/// definitions are pointed at those, rather than the fixture being renamed onto
/// the committed slug. The repository slug is unique in the schema and the
/// PostgreSQL container is shared across this collection, so a test that claimed
/// a committed slug would collide with any sibling that did the same — an
/// ordering-dependent failure that says nothing about retrieval.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetrievalOnlyEvaluationTests(PostgresFixture postgres)
{
    private static string TempDirectory(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"repopilot-{prefix}-{Guid.NewGuid():N}");

    [RequiresDockerFact]
    public async Task ItReportsTheSamePerTaskHitsAsTheRetrieverDoes()
    {
        await using var db = postgres.CreateContext();

        var committed = await new EvaluationTaskLoader(CommittedArtifacts.TasksDirectory)
            .LoadAsync();

        var registered = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var slug in committed.Select(t => t.RepositorySlug).Distinct(StringComparer.Ordinal))
        {
            var fixture = await CommittedFixtureIndexer.IndexOnceAsync(db, slug);
            renames[slug] = fixture.Slug;
            registered[fixture.Slug] = fixture.Id;
        }

        var tasksDirectory = await CopyTaskSetAsync(renames);
        var resultsDirectory = TempDirectory("results");

        try
        {
            var (report, path) = await ModeOver(db, tasksDirectory, resultsDirectory).MeasureAsync();

            Assert.Equal(committed.Count, report.TaskSetSize);
            Assert.Equal(committed.Count, report.Tasks.Count);
            Assert.Equal(RecallAt5.Depth, report.RecallDepth);
            Assert.True(File.Exists(path));

            // The retriever, queried directly, task by task. The mode is a
            // reporting path over this — if it disagreed anywhere, the figure it
            // publishes would describe the mode rather than the retriever.
            var retriever = CommittedFixtureIndexer.Retriever(db);
            var byId = await new EvaluationTaskLoader(tasksDirectory).LoadAsync();

            foreach (var line in report.Tasks)
            {
                var task = byId.Single(t => t.Id == line.TaskId);

                var (expected, topPaths) = await RecallAt5.MeasureAsync(
                    retriever, registered[task.RepositorySlug], task);

                Assert.Equal(expected, line.RelevantFileInTop5);
                Assert.Equal(topPaths, line.TopPaths);
            }

            // And the summary is the lines it carries, not a separately kept
            // counter that could drift from them.
            Assert.Equal(
                report.Tasks.Count(l => l.RelevantFileInTop5), report.RelevantFileInTopFive);
            Assert.Equal(
                Math.Round((decimal)report.RelevantFileInTopFive / report.TaskSetSize, 4),
                report.RecallAt5);
        }
        finally
        {
            Directory.Delete(tasksDirectory, recursive: true);
            Directory.Delete(resultsDirectory, recursive: true);
        }
    }

    [RequiresDockerFact]
    public async Task RepeatingItOverAnUnchangedIndexReportsTheSameFigure()
    {
        await using var db = postgres.CreateContext();

        const string Slug = "sample-dotnet-api";
        var fixture = await CommittedFixtureIndexer.IndexOnceAsync(db, Slug);

        var tasksDirectory = await CopyTaskSetAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { [Slug] = fixture.Slug },
            onlyCommittedSlug: Slug);

        var resultsDirectory = TempDirectory("results");

        try
        {
            var mode = ModeOver(db, tasksDirectory, resultsDirectory);

            var (first, _) = await mode.MeasureAsync();
            var (second, _) = await mode.MeasureAsync();

            // Task by task rather than in aggregate: two passes disagreeing about
            // which tasks hit could still average to the same number (SC-007).
            Assert.Equal(
                first.Tasks.Select(t => (t.TaskId, t.RelevantFileInTop5)),
                second.Tasks.Select(t => (t.TaskId, t.RelevantFileInTop5)));
            Assert.Equal(first.RecallAt5, second.RecallAt5);
            Assert.NotEmpty(first.Tasks);
        }
        finally
        {
            Directory.Delete(tasksDirectory, recursive: true);
            Directory.Delete(resultsDirectory, recursive: true);
        }
    }

    [RequiresDockerFact]
    public async Task ItRefusesAnUnregisteredFixtureRatherThanIndexingOne()
    {
        await using var db = postgres.CreateContext();

        // A slug nothing has registered, so the refusal cannot depend on which
        // sibling test ran first.
        var tasksDirectory = await CopyTaskSetAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sample-dotnet-api"] = "never-registered-" + Guid.NewGuid().ToString("N")[..8],
            },
            onlyCommittedSlug: "sample-dotnet-api");

        var resultsDirectory = TempDirectory("results");

        try
        {
            var refused = await Assert.ThrowsAsync<EvaluationRefusedException>(
                () => ModeOver(db, tasksDirectory, resultsDirectory).MeasureAsync());

            Assert.Contains("not registered", refused.Message, StringComparison.Ordinal);

            // Nothing was written, either. A refusal that still produced a report
            // would put an unbacked figure in the results directory.
            Assert.False(Directory.Exists(resultsDirectory));
        }
        finally
        {
            Directory.Delete(tasksDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolvingItConstructsNoChatProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        // AddRepoPilotCore assumes a host that has already added logging, as both
        // real hosts have.
        services.AddLogging();
        services.AddRepoPilotCore(configuration);
        services.AddEvaluationHarness(configuration);

        // Replaces the provider adapter with one that cannot be built. If the
        // retrieval-only graph reached a chat provider, resolution would throw
        // here — which is the claim the mode makes, held as a test rather than as
        // a sentence in a doc comment.
        services.AddSingleton<IChatProviderAdapter>(
            _ => throw new InvalidOperationException(
                "A chat provider was constructed for a retrieval-only measurement."));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RetrievalOnlyEvaluation>());
    }

    /// <summary>The mode, wired to one database and one pair of directories.</summary>
    private static RetrievalOnlyEvaluation ModeOver(
        RepoPilot.Infrastructure.Persistence.RepoPilotDbContext db,
        string tasksDirectory,
        string resultsDirectory) =>
        new(
            new EvaluationTaskLoader(tasksDirectory),
            new EfRepositoryFixtureStore(db),
            CommittedFixtureIndexer.Retriever(db),
            new ReportWriter(resultsDirectory),
            NullLogger<RetrievalOnlyEvaluation>.Instance);

    /// <summary>
    /// A tasks directory holding copies of the committed definitions, with each
    /// <c>repositorySlug</c> rewritten to the slug the fixture was registered
    /// under.
    /// </summary>
    /// <param name="renames">Committed slug to registered slug.</param>
    /// <param name="onlyCommittedSlug">
    /// Restricts the copy to tasks naming this fixture, for a test that registers
    /// one.
    /// </param>
    private static async Task<string> CopyTaskSetAsync(
        IReadOnlyDictionary<string, string> renames, string? onlyCommittedSlug = null)
    {
        var directory = TempDirectory("tasks");
        Directory.CreateDirectory(directory);

        foreach (var file in Directory.EnumerateFiles(CommittedArtifacts.TasksDirectory, "*.json"))
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
            var slug = node["repositorySlug"]!.GetValue<string>();

            if (onlyCommittedSlug is not null && slug != onlyCommittedSlug)
            {
                continue;
            }

            node["repositorySlug"] = renames[slug];

            await File.WriteAllTextAsync(
                Path.Combine(directory, Path.GetFileName(file)),
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        return directory;
    }
}
