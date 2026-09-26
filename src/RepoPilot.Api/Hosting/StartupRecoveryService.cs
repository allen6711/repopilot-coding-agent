using Microsoft.Extensions.Logging;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;

namespace RepoPilot.Api.Hosting;

/// <summary>
/// What a recovery pass did.
/// </summary>
/// <param name="RunsFailed">Non-terminal runs ended as failed.</param>
/// <param name="WorkingCopiesRemoved">Directories deleted.</param>
public sealed record RecoveryReport(int RunsFailed, int WorkingCopiesRemoved);

/// <summary>
/// Brings the system to a coherent state at startup (FR-030a).
/// <para>
/// Runs are not resumable across restarts — a spec assumption — so a run left
/// mid-flight is not merely stale, it is a run whose working copy may hold a
/// partially applied change and whose slot is held by nobody. Ending those runs
/// and sweeping their directories is what makes SC-012 hold after a crash rather
/// than only after a clean shutdown.
/// </para>
/// <para>
/// Directories are swept by difference against the database rather than by age
/// or by name pattern: a directory whose run id is unknown or terminal has no
/// owner, and no heuristic is needed to say so.
/// </para>
/// </summary>
public sealed class StartupRecoveryService(
    IRunStore runs,
    IWorkingCopyStore workingCopies,
    WorkspaceOptions workspace,
    ILogger<StartupRecoveryService> logger)
{
    /// <summary>Runs one recovery pass.</summary>
    public async Task<RecoveryReport> RecoverAsync(CancellationToken ct = default)
    {
        var failed = await FailNonTerminalRunsAsync(ct);
        var removed = await SweepWorkingCopiesAsync(ct);

        if (failed > 0 || removed > 0)
        {
            logger.LogWarning(
                "Startup recovery: {Failed} run(s) ended as failed, {Removed} working copy(ies) removed.",
                failed, removed);
        }

        return new RecoveryReport(failed, removed);
    }

    private async Task<int> FailNonTerminalRunsAsync(CancellationToken ct)
    {
        var stranded = await runs.ListNonTerminalAsync(ct);

        foreach (var run in stranded)
        {
            // The stage the run died at is preserved as the failure stage, so
            // FR-030's "reason and stage of failure" survives the restart that
            // caused it.
            run.FailureStage = run.Stage;
            run.Stage = RunStage.Failed;
            run.TerminalOutcome = TerminalOutcome.Failed;
            run.OutcomeReason = OutcomeReason.ServiceRestarted;
            run.EndedAt = DateTimeOffset.UtcNow;

            await runs.UpdateAsync(run, ct);
            await workingCopies.MarkDestroyedAsync(run.Id, ct);
        }

        return stranded.Count;
    }

    private async Task<int> SweepWorkingCopiesAsync(CancellationToken ct)
    {
        var root = Path.Combine(Path.GetFullPath(workspace.Root), "runs");
        if (!Directory.Exists(root))
        {
            return 0;
        }

        // After the pass above, every run is terminal, so anything still on disk
        // is by definition unowned.
        var removed = 0;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A directory that cannot be removed is reported rather than
                // retried into a startup hang. SC-012 will catch it as a
                // leftover, which is the honest outcome.
                logger.LogError(ex, "Could not remove orphaned working copy at {Path}.", directory);
            }
        }

        return removed;
    }
}
