using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Workspace;

namespace RepoPilot.Application.Ports;

/// <summary>
/// Creates and destroys a run's disposable working copy.
/// <para>
/// The orchestrator needs the copy to exist from <c>retrieving</c> onward and to
/// be gone at every terminal outcome, but it has no business knowing how a
/// directory tree gets copied. This port is that seam.
/// </para>
/// </summary>
public interface IWorkspaceProvisioner
{
    /// <summary>Absolute path of a run's working copy, whether or not it exists.</summary>
    string PathFor(Guid runId);

    /// <summary>
    /// Creates the working copy from the fixture.
    /// </summary>
    /// <returns>The writable root the run's file access resolves against.</returns>
    Task<WorkspaceRoot> CreateAsync(Guid runId, RepositoryFixture fixture, CancellationToken ct = default);

    /// <summary>Destroys the working copy and records that it is gone (FR-026a).</summary>
    Task DestroyAsync(Guid runId, CancellationToken ct = default);
}
