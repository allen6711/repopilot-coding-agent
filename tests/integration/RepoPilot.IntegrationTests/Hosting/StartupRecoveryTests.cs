using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Api.Hosting;
using RepoPilot.Application.Configuration;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Hosting;

/// <summary>
/// FR-030a and SC-012 for the case that actually matters: not a clean shutdown,
/// but a process that died mid-run.
/// <para>
/// A run left in <c>Applying</c> is the dangerous one — its working copy may
/// hold a partially applied change and nothing owns it. Recovery has to end the
/// run, say why, and remove the directory.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StartupRecoveryTests(PostgresFixture postgres) : IDisposable
{
    private readonly List<string> _workspaces = [];

    public void Dispose()
    {
        foreach (var workspace in _workspaces)
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string CreateWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "repopilot-rec-" + Guid.NewGuid().ToString("N"));
        _workspaces.Add(root);
        Directory.CreateDirectory(Path.Combine(root, "runs"));
        return root;
    }

    private static async Task<Run> SeedRunAsync(RepoPilotDbContext db, RunStage stage)
    {
        var repository = new RepositoryFixture
        {
            Slug = "rec-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Recovery fixture",
            RootPath = "evals/fixtures/sample",
            TestConfigJson = """{"slug":"rec","commands":[]}""",
        };
        db.Repositories.Add(repository);

        var run = new Run
        {
            RepositoryId = repository.Id,
            TaskDescription = "Interrupted mid-flight.",
            Stage = stage,
        };
        db.Runs.Add(run);

        await db.SaveChangesAsync();
        return run;
    }

    private static StartupRecoveryService CreateService(RepoPilotDbContext db, string workspaceRoot) =>
        new(new EfRunStore(db),
            new EfWorkingCopyStore(db),
            new WorkspaceOptions { Root = workspaceRoot },
            NullLogger<StartupRecoveryService>.Instance);

    [RequiresDockerFact]
    public async Task ARunLeftMidApplyIsEndedWithTheRestartRecorded()
    {
        await using var db = postgres.CreateContext();
        var run = await SeedRunAsync(db, RunStage.Applying);
        var workspace = CreateWorkspace();

        await CreateService(db, workspace).RecoverAsync();

        var recovered = await db.Runs.AsNoTracking().FirstAsync(r => r.Id == run.Id);

        Assert.Equal(RunStage.Failed, recovered.Stage);
        Assert.Equal(TerminalOutcome.Failed, recovered.TerminalOutcome);
        Assert.Equal(OutcomeReason.ServiceRestarted, recovered.OutcomeReason);
        Assert.NotNull(recovered.EndedAt);
    }

    [RequiresDockerFact]
    public async Task TheStageTheRunDiedAtIsPreserved()
    {
        // FR-030 asks for the reason and the stage. The restart is the reason;
        // without capturing the stage first it would be lost to the recovery
        // that reports it.
        await using var db = postgres.CreateContext();
        var run = await SeedRunAsync(db, RunStage.Testing);

        await CreateService(db, CreateWorkspace()).RecoverAsync();

        var recovered = await db.Runs.AsNoTracking().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(RunStage.Testing, recovered.FailureStage);
    }

    [RequiresDockerFact]
    public async Task OrphanedWorkingCopiesAreRemoved()
    {
        await using var db = postgres.CreateContext();
        var run = await SeedRunAsync(db, RunStage.Applying);
        var workspace = CreateWorkspace();

        var copyPath = Path.Combine(workspace, "runs", run.Id.ToString());
        Directory.CreateDirectory(copyPath);
        await File.WriteAllTextAsync(
            Path.Combine(copyPath, "PartiallyApplied.cs"), "// half-written change");

        db.WorkingCopies.Add(new WorkingCopy { RunId = run.Id, AbsolutePath = copyPath });
        await db.SaveChangesAsync();

        var report = await CreateService(db, workspace).RecoverAsync();

        Assert.False(Directory.Exists(copyPath));
        Assert.Equal(1, report.WorkingCopiesRemoved);

        var record = await db.WorkingCopies.AsNoTracking().FirstAsync(w => w.RunId == run.Id);
        Assert.NotNull(record.DestroyedAt);
    }

    [RequiresDockerFact]
    public async Task ADirectoryWithNoDatabaseRecordIsAlsoRemoved()
    {
        // The sweep works by difference against the database, so a directory the
        // crash left before its row was written is still unowned and still goes.
        await using var db = postgres.CreateContext();
        var workspace = CreateWorkspace();

        var strayPath = Path.Combine(workspace, "runs", Guid.CreateVersion7().ToString());
        Directory.CreateDirectory(strayPath);

        var report = await CreateService(db, workspace).RecoverAsync();

        Assert.False(Directory.Exists(strayPath));
        Assert.True(report.WorkingCopiesRemoved >= 1);
    }

    [RequiresDockerFact]
    public async Task AlreadyTerminalRunsAreLeftAlone()
    {
        // Recovery must not rewrite the outcome of a run that finished properly,
        // or a restart would relabel successes as failures.
        await using var db = postgres.CreateContext();
        var run = await SeedRunAsync(db, RunStage.Succeeded);
        run.TerminalOutcome = TerminalOutcome.Succeeded;
        run.OutcomeReason = OutcomeReason.Completed;
        run.EndedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await CreateService(db, CreateWorkspace()).RecoverAsync();

        // Asserted about this run rather than about the pass's total. Recovery is
        // global by design — it must end every stranded run, not only the ones a
        // given test created — so a count assertion here would really be
        // measuring what other tests left in the shared database.
        var after = await db.Runs.AsNoTracking().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(TerminalOutcome.Succeeded, after.TerminalOutcome);
        Assert.Equal(OutcomeReason.Completed, after.OutcomeReason);
        Assert.Null(after.FailureStage);
    }

    [RequiresDockerFact]
    public async Task RecoveryIsIdempotent()
    {
        // It runs on every start, including one that follows a clean shutdown.
        await using var db = postgres.CreateContext();
        await SeedRunAsync(db, RunStage.Retrieving);
        var workspace = CreateWorkspace();
        var service = CreateService(db, workspace);

        var first = await service.RecoverAsync();
        var second = await service.RecoverAsync();

        // The first pass ends at least this run; the second finds nothing left,
        // whatever else was in the shared database when it started.
        Assert.True(first.RunsFailed >= 1);
        Assert.Equal(0, second.RunsFailed);
    }

    [RequiresDockerFact]
    public async Task AMissingWorkspaceRootIsNotAnError()
    {
        // First start on a fresh machine.
        await using var db = postgres.CreateContext();

        var report = await CreateService(db, Path.Combine(Path.GetTempPath(), "never-created-" + Guid.NewGuid()))
            .RecoverAsync();

        Assert.Equal(0, report.WorkingCopiesRemoved);
    }
}
