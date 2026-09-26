using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoPilot.Agent.Capabilities;
using RepoPilot.Agent.Invocation;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Proposals;
using RepoPilot.Application.Runs;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Evals.Tasks;
using RepoPilot.Infrastructure.Events;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Proposals;
using RepoPilot.Infrastructure.Workspace;
using RepoPilot.IntegrationTests.Evals;
using RepoPilot.IntegrationTests.Infrastructure;
using RepoPilot.IntegrationTests.Runs;
using Xunit;

namespace RepoPilot.IntegrationTests.Security;

/// <summary>
/// SC-002, across the committed task set: zero file modifications occur outside a
/// disposable working copy.
/// <para>
/// The per-run form of this check lives in <c>ApprovalGateTests</c> and looks at
/// one fixture around one apply. This is the sweep the criterion actually asks
/// for — every task in the committed set, with the whole <c>evals/</c> tree
/// hashed before and after, so a write to a fixture, to the task definitions, or
/// to a reference solution is caught wherever it came from.
/// </para>
/// <para>
/// The model is scripted rather than live. What decides whether a write lands
/// outside the workspace is the path guard and the working-copy manager, and
/// neither consults the model — so substituting it removes the cost and the
/// non-determinism without removing anything the criterion is about. Everything
/// downstream of the proposal is the real thing: the real validator, the real
/// approval gate, the real apply, on a real filesystem. Only the sandbox is a
/// double, because executing thirty containers measures test execution rather
/// than confinement.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EvaluationSetNoOutsideWritesTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _workspaceRoot =
        Directory.CreateTempSubdirectory("repopilot-sweep-").FullName;

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

    /// <summary>
    /// Content hash of every file under <paramref name="root"/>, keyed by
    /// root-relative path.
    /// <para>
    /// Content rather than timestamps: a copy operation that rewrote a file with
    /// identical bytes is not a modification anyone cares about, and a
    /// timestamp-based check would report one.
    /// </para>
    /// </summary>
    private static Dictionary<string, string> HashTree(string root)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(file);

            hashes[Path.GetRelativePath(root, file).Replace('\\', '/')] =
                Convert.ToHexString(SHA256.HashData(stream));
        }

        return hashes;
    }

    /// <summary>Paths under the workspace root, relative and normalised.</summary>
    private static HashSet<string> Listing(string root) =>
        Directory.Exists(root)
            ? [.. Directory
                .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))]
            : [];

    private async Task<RepositoryFixture> RegisterAsync(RepoPilotDbContext db, string slug)
    {
        var root = Path.Combine(CommittedArtifacts.FixturesDirectory, slug);

        var fixture = new RepositoryFixture
        {
            Slug = slug + "-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = slug,
            RootPath = root,
            TestConfigJson = await File.ReadAllTextAsync(
                Path.Combine(root, "repopilot.fixture.json")),
        };

        db.Repositories.Add(fixture);
        await db.SaveChangesAsync();

        return fixture;
    }

    /// <summary>
    /// The orchestrator, with everything downstream of the proposal real.
    /// </summary>
    private (RunOrchestrator Orchestrator, WorkingCopyManager Copies, DecideProposalUseCase Decide)
        Build(RepoPilotDbContext db, EvaluationTaskDefinition task)
    {
        var recorder = new RunEventRecorder(new EfRunEventStore(db), new NullRunEventPublisher());

        var copies = new WorkingCopyManager(
            new EfWorkingCopyStore(db),
            new WorkspaceOptions { Root = _workspaceRoot },
            NullLogger<WorkingCopyManager>.Instance);

        var invoker = new ToolInvoker(
            [
                new ProposePatchCapability(
                    new EfProposalStore(db),
                    new EfRunStore(db),
                    new ProposalValidator(new ProposalLimitOptions()),
                    new DiffRenderer()),
                new ApplyPatchCapability(
                    new EfProposalStore(db),
                    new EfApprovalStore(db),
                    new EfRunStore(db),
                    copies),
                new RunTestsCapability(
                    new RecordingSandbox(),
                    new EfRepositoryFixtureStore(db),
                    new EfTestResultStore(db),
                    new EfRunStore(db),
                    copies.PathFor),
            ],
            recorder);

        var orchestrator = new RunOrchestrator(
            new EfRunStore(db),
            new EfRepositoryFixtureStore(db),
            new EfProposalStore(db),
            new EfApprovalStore(db),
            copies,
            ScriptedAgent.ThatProposesAChangeTo(
                task.RelevantFiles[0],
                $"// rewritten by the SC-002 sweep for {task.Id}\n"),
            invoker,
            recorder,
            Options.Create(new RunConcurrencyOptions()),
            Options.Create(new RetrievalOptions()),
            new ScriptedBaselineContext());

        var decide = new DecideProposalUseCase(
            new EfProposalStore(db), new EfApprovalStore(db), new EfRunStore(db), recorder);

        return (orchestrator, copies, decide);
    }

    [RequiresDockerFact]
    public async Task NoTaskInTheCommittedSetWritesOutsideItsWorkingCopy()
    {
        await using var db = postgres.CreateContext();

        var tasks = await new EvaluationTaskLoader(CommittedArtifacts.TasksDirectory).LoadAsync();

        Assert.NotEmpty(tasks);

        var fixtures = new Dictionary<string, RepositoryFixture>(StringComparer.Ordinal);

        foreach (var slug in tasks.Select(t => t.RepositorySlug).Distinct(StringComparer.Ordinal))
        {
            fixtures[slug] = await RegisterAsync(db, slug);
        }

        // The whole committed tree: every fixture, every task definition, and
        // every reference solution. Anything a run touches here is a write
        // outside a working copy, whichever of the three it was.
        var evalsRoot = Path.Combine(CommittedArtifacts.RepositoryRoot, "evals");
        var before = HashTree(evalsRoot);

        Assert.NotEmpty(before);

        var applied = 0;

        foreach (var task in tasks)
        {
            var fixture = fixtures[task.RepositorySlug];

            var run = new Run
            {
                RepositoryId = fixture.Id,
                TaskDescription = task.Description,
                SeededTaskId = task.Id,
                VerifyCommandName = task.SuccessTestCommand,
            };

            db.Runs.Add(run);
            await db.SaveChangesAsync();

            var workspaceBefore = Listing(_workspaceRoot);
            var (orchestrator, copies, decide) = Build(db, task);

            var stage = await orchestrator.ExecuteUntilApprovalAsync(run.Id);

            Assert.Equal(RunStage.AwaitingApproval, stage);

            var proposal = await new EfProposalStore(db).FindCurrentForRunAsync(run.Id);

            Assert.NotNull(proposal);

            await decide.DecideAsync(
                proposal!.Id,
                ApprovalDecisionKind.Approve,
                proposal.DiffHash,
                "sweep@example.test");

            var finalStage = await orchestrator.ApplyAndTestAsync(run.Id);

            // The run has to actually write, or the sweep proves nothing about
            // where writes land.
            Assert.Equal(RunStage.Succeeded, finalStage);
            applied++;

            // Under the workspace root, the only thing this run may have left
            // behind is its own directory — and it destroys that on a terminal
            // outcome, so the listing must be back where it started.
            //
            // The `runs` container itself is exempt: the first run creates it and
            // nothing removes it, which is the workspace layout rather than a
            // leftover. Its contents are not exempt, and that is what the check
            // is for.
            var workspaceAfter = Listing(_workspaceRoot);
            var strays = workspaceAfter
                .Except(workspaceBefore)
                .Where(p => p != "runs" && !p.StartsWith($"runs/{run.Id}", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                strays.Count == 0,
                $"Task '{task.Id}' left {strays.Count} paths outside its working copy: " +
                string.Join(", ", strays.Take(5)));

            Assert.False(
                Directory.Exists(copies.PathFor(run.Id)),
                $"Task '{task.Id}' left its working copy behind (SC-012).");
        }

        Assert.Equal(tasks.Count, applied);

        // The criterion, across the whole set.
        var after = HashTree(evalsRoot);

        var changed = after
            .Where(entry => !before.TryGetValue(entry.Key, out var hash) || hash != entry.Value)
            .Select(entry => entry.Key)
            .Concat(before.Keys.Except(after.Keys))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            changed.Count == 0,
            $"{changed.Count} committed paths changed across the evaluation set: " +
            string.Join(", ", changed.Take(10)));

        // Nothing is left in the workspace root either. SC-002 is about writes
        // landing in a disposable copy; SC-012 is about the copy then going away,
        // and a sweep that satisfied the first while accumulating thirty
        // directories would be reporting half the story.
        Assert.DoesNotContain(
            Listing(_workspaceRoot), p => p.StartsWith("runs/", StringComparison.Ordinal));
    }
}
