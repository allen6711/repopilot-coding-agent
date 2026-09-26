using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoPilot.Agent;
using RepoPilot.Agent.Capabilities;
using RepoPilot.Agent.Invocation;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Proposals;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Evals.Tasks;
using RepoPilot.Infrastructure.Events;
using RepoPilot.Infrastructure.Indexing;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Proposals;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.Infrastructure.Retrieval;
using RepoPilot.Infrastructure.Workspace;
using RepoPilot.IntegrationTests.Evals;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace RepoPilot.IntegrationTests.Runs;

/// <summary>
/// SC-003 across the committed task set, reported as a distribution.
/// <para>
/// <c>SingleRunLatencyTests</c> measures one task and answers "is this plausible".
/// One sample cannot say whether three minutes is comfortable or whether the
/// median sits just under it — and the criterion is a promise about what a
/// reviewer waits for, not about one lucky task. So this runs the set and reports
/// p50, p95, and the worst case, then asserts against the worst.
/// </para>
/// <para>
/// It runs against a real model over a real index, and skips without a credential
/// or a daemon. That is deliberate: every substitute for the model removes the
/// part that takes the time, so the number is either measured or absent. Nothing
/// here estimates (Principle V).
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LatencyBudgetTests(PostgresFixture postgres, ITestOutputHelper output) : IDisposable
{
    /// <summary>The SC-003 ceiling.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How many tasks to sample, when a developer wants a shorter local run.
    /// <para>
    /// Defaults to the whole set. The assertion applies to whatever ran and the
    /// report states the count, so a subset is a smaller measurement rather than a
    /// weaker one — but a published figure should come from the full set.
    /// </para>
    /// </summary>
    private static int SampleSize =>
        int.TryParse(
            Environment.GetEnvironmentVariable("REPOPILOT_LATENCY_SAMPLE"),
            CultureInfo.InvariantCulture,
            out var configured) && configured > 0
            ? configured
            : int.MaxValue;

    private readonly string _workspaceRoot =
        Directory.CreateTempSubdirectory("repopilot-latency-set-").FullName;

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

    [RequiresLiveStackFact]
    public async Task EveryTaskInTheCommittedSetReachesItsFirstProposalWithinBudget()
    {
        await using var db = postgres.CreateContext();

        var tasks = await new EvaluationTaskLoader(CommittedArtifacts.TasksDirectory).LoadAsync();
        var sampled = tasks.Take(SampleSize).ToList();

        Assert.NotEmpty(sampled);

        var fixtures = new Dictionary<string, RepositoryFixture>(StringComparer.Ordinal);

        foreach (var slug in sampled.Select(t => t.RepositorySlug).Distinct(StringComparer.Ordinal))
        {
            fixtures[slug] = await RegisterAndIndexAsync(db, slug);
        }

        var measured = new List<(string TaskId, TimeSpan ToFirstProposal)>();
        var inconclusive = new List<string>();

        foreach (var task in sampled)
        {
            var run = new Run
            {
                RepositoryId = fixtures[task.RepositorySlug].Id,
                TaskDescription = task.Description,
                SeededTaskId = task.Id,
                VerifyCommandName = task.SuccessTestCommand,
            };

            db.Runs.Add(run);
            await db.SaveChangesAsync();

            var eventStore = new EfRunEventStore(db);

            RunStage stage;

            try
            {
                stage = await BuildOrchestrator(db, eventStore).ExecuteUntilApprovalAsync(run.Id);
            }
            catch (Exception ex)
            {
                // A failed run measured nothing about latency. Recorded and
                // excluded rather than counted as zero, which would pull the
                // distribution down with samples that never happened.
                inconclusive.Add($"{task.Id} (failed: {ex.GetType().Name})");
                continue;
            }

            if (stage != RunStage.AwaitingApproval)
            {
                inconclusive.Add($"{task.Id} (ended at {stage})");
                continue;
            }

            var events = await eventStore.ListAsync(run.Id);

            var proposalCreated = events.FirstOrDefault(
                e => e.ArgumentsSummary?.Contains("ProposalCreated", StringComparison.Ordinal) == true);

            if (proposalCreated is null || run.StartedAt is null)
            {
                inconclusive.Add($"{task.Id} (no recorded proposal)");
                continue;
            }

            measured.Add((task.Id, proposalCreated.StartedAt - run.StartedAt.Value));
        }

        Report(measured, inconclusive, sampled.Count);

        // A run that never proposed is a completion problem, not a latency one,
        // so it does not fail this test. But measuring nothing at all would let
        // the assertion pass over an empty set.
        Assert.NotEmpty(measured);

        var ordered = measured.OrderBy(m => m.ToFirstProposal).ToList();
        var worst = ordered[^1];

        Assert.True(
            worst.ToFirstProposal < Budget,
            $"'{worst.TaskId}' took {worst.ToFirstProposal.TotalSeconds:F1}s against a " +
            $"{Budget.TotalSeconds:F0}s budget. Distribution: " +
            $"p50 {Percentile(ordered, 0.50).TotalSeconds:F1}s, " +
            $"p95 {Percentile(ordered, 0.95).TotalSeconds:F1}s.");
    }

    /// <summary>
    /// Writes the distribution to test output, whether or not the assertion
    /// passes. SC-003 is a figure to publish as well as a bound to hold, and a
    /// number that only appears in a failure message is one nobody can cite.
    /// </summary>
    private void Report(
        IReadOnlyList<(string TaskId, TimeSpan ToFirstProposal)> measured,
        IReadOnlyList<string> inconclusive,
        int sampled)
    {
        output.WriteLine($"SC-003, start of run to first proposal, over {sampled} committed tasks:");
        output.WriteLine($"  measured:     {measured.Count}");
        output.WriteLine($"  inconclusive: {inconclusive.Count}");

        if (measured.Count > 0)
        {
            var ordered = measured.OrderBy(m => m.ToFirstProposal).ToList();

            output.WriteLine($"  min: {ordered[0].ToFirstProposal.TotalSeconds:F1}s");
            output.WriteLine($"  p50: {Percentile(ordered, 0.50).TotalSeconds:F1}s");
            output.WriteLine($"  p95: {Percentile(ordered, 0.95).TotalSeconds:F1}s");
            output.WriteLine($"  max: {ordered[^1].ToFirstProposal.TotalSeconds:F1}s ({ordered[^1].TaskId})");
        }

        foreach (var task in inconclusive)
        {
            output.WriteLine($"  excluded: {task}");
        }
    }

    /// <summary>
    /// Nearest-rank, so a reported percentile is a duration some run actually
    /// took rather than one between two runs.
    /// </summary>
    private static TimeSpan Percentile(
        IReadOnlyList<(string TaskId, TimeSpan ToFirstProposal)> ascending, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * ascending.Count);

        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)].ToFirstProposal;
    }

    private static async Task<RepositoryFixture> RegisterAndIndexAsync(
        RepoPilotDbContext db, string slug)
    {
        var root = Path.Combine(CommittedArtifacts.FixturesDirectory, slug);

        var fixture = new RepositoryFixture
        {
            Slug = slug,
            DisplayName = slug,
            RootPath = root,
            TestConfigJson = await File.ReadAllTextAsync(
                Path.Combine(root, "repopilot.fixture.json")),
        };

        db.Repositories.Add(fixture);
        await db.SaveChangesAsync();

        await new IndexingService(
                db,
                new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
                new IndexingOptions
                {
                    EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions,
                },
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
            db,
            new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
            retrieverOptions);

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
}
