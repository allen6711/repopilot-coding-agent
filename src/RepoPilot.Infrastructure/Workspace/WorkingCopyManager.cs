using Microsoft.Extensions.Logging;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Workspace;

namespace RepoPilot.Infrastructure.Workspace;

/// <summary>
/// Creates, writes to, and destroys a run's disposable working copy.
/// <para>
/// The copy exists for the whole run, from <c>retrieving</c> onward, so "inside
/// the workspace" has one meaning at every stage (FR-024a). It is destroyed when
/// the run reaches any terminal outcome, including cancelled and rejected
/// (FR-026a) — the diff, test output, and events persist independently, so
/// nothing needs the directory to survive (FR-026b).
/// </para>
/// </summary>
public sealed class WorkingCopyManager(
    IWorkingCopyStore store,
    WorkspaceOptions options,
    ILogger<WorkingCopyManager> logger) : IWorkspaceProvisioner
{
    /// <summary>
    /// Directories not copied into a working copy. Version control metadata and
    /// build output are regenerable and large; the sandbox image already carries
    /// restored dependencies, so copying them would only slow every run down.
    /// </summary>
    private static readonly string[] SkippedDirectories =
        [".git", ".hg", ".svn", "node_modules", "bin", "obj", "target", "dist", ".venv"];

    /// <summary>Absolute path of a run's working copy, whether or not it exists.</summary>
    public string PathFor(Guid runId) =>
        Path.Combine(Path.GetFullPath(options.Root), "runs", runId.ToString());

    /// <summary>
    /// Creates the working copy by copying the fixture.
    /// </summary>
    /// <returns>The writable root the run's file access resolves against.</returns>
    public async Task<WorkspaceRoot> CreateAsync(
        Guid runId, RepositoryFixture fixture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var destination = PathFor(runId);

        if (Directory.Exists(destination))
        {
            // A leftover from a crashed run with the same id should be
            // impossible, but starting from a stale tree would produce a diff
            // against the wrong baseline — which the approval would then bind.
            Directory.Delete(destination, recursive: true);
        }

        // The fixture is opened read-only, so nothing on this path can write to
        // it (FR-016a). Only the destination is writable.
        var source = WorkspaceRoot.ReadOnly(Path.GetFullPath(fixture.RootPath));
        CopyTree(source.FullPath, destination, ct);

        await store.AddAsync(
            new WorkingCopy { RunId = runId, AbsolutePath = destination }, ct);

        return WorkspaceRoot.Writable(destination);
    }

    /// <summary>
    /// Applies proposal entries atomically (FR-016b).
    /// <para>
    /// Every file is staged and every original backed up before anything is moved
    /// into place; a failure part-way restores what was already moved. The
    /// filesystem offers no multi-file transaction, so this is what "either every
    /// file is updated or none is" has to mean in practice — and it is what stops
    /// an interruption mid-apply from leaving a half-changed tree that a later
    /// diff would describe incorrectly.
    /// </para>
    /// </summary>
    public void Apply(WorkspaceRoot workspace, IReadOnlyList<ProposalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(entries);

        var staged = new List<(string Target, string Staging, string? Backup)>(entries.Count);

        try
        {
            foreach (var entry in entries)
            {
                // Write intent, resolved through the guard. A path escaping the
                // workspace is refused here, before any handle is opened.
                var target = PathGuard.ResolveOrThrow(workspace, entry.Path, AccessIntent.Write);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                var staging = target + ".repopilot-staged";
                File.WriteAllText(staging, entry.NewContent);

                string? backup = null;
                if (File.Exists(target))
                {
                    backup = target + ".repopilot-backup";
                    File.Copy(target, backup, overwrite: true);
                }

                staged.Add((target, staging, backup));
            }

            // Nothing has changed in place until this loop. Moves are the only
            // destructive step and they happen only once every file is ready.
            foreach (var (target, staging, _) in staged)
            {
                File.Move(staging, target, overwrite: true);
            }
        }
        catch
        {
            Rollback(staged);
            throw;
        }
        finally
        {
            Cleanup(staged);
        }
    }

    /// <summary>Destroys the working copy and records that it is gone.</summary>
    public async Task DestroyAsync(Guid runId, CancellationToken ct = default)
    {
        var path = PathFor(runId);

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported rather than retried into a hang. SC-012 checks for
            // leftovers, and a leftover that is logged is better than a run that
            // will not finish.
            logger.LogError(ex, "Could not remove working copy at {Path}.", path);
        }

        await store.MarkDestroyedAsync(runId, ct);
    }

    private static void Rollback(List<(string Target, string Staging, string? Backup)> staged)
    {
        foreach (var (target, _, backup) in staged)
        {
            try
            {
                if (backup is not null && File.Exists(backup))
                {
                    File.Copy(backup, target, overwrite: true);
                }
                else if (File.Exists(target))
                {
                    // Created by this apply, so removing it restores the
                    // pre-apply state.
                    File.Delete(target);
                }
            }
            catch (IOException)
            {
                // Best effort. A rollback failure is reported by the original
                // exception propagating; masking it with this one would hide the
                // cause.
            }
        }
    }

    private static void Cleanup(List<(string Target, string Staging, string? Backup)> staged)
    {
        foreach (var (_, staging, backup) in staged)
        {
            TryDelete(staging);
            if (backup is not null)
            {
                TryDelete(backup);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Copies a fixture tree the way a run's working copy is built.
    /// <para>
    /// Exposed for the evaluation harness's baseline probe, which measures how a
    /// fixture's command behaves before any change. That measurement is only
    /// comparable with the run's own result if both are taken against the same
    /// tree — same skip list, same contents — so the probe reuses this rather
    /// than copying the fixture its own way.
    /// </para>
    /// </summary>
    public static void CopyFixtureTo(string source, string destination, CancellationToken ct = default)
    {
        // Read-only resolution of the source, for the same reason CreateAsync does
        // it: a copy is a read of the fixture, and the guard is where that is
        // established rather than assumed (FR-016a).
        var root = WorkspaceRoot.ReadOnly(Path.GetFullPath(source));

        CopyTree(root.FullPath, destination, ct);
    }

    private static void CopyTree(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);
            if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyTree(directory, Path.Combine(destination, name), ct);
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }
}
