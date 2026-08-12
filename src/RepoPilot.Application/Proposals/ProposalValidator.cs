using RepoPilot.Application.Configuration;
using RepoPilot.Domain.Indexing;
using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Workspace;

namespace RepoPilot.Application.Proposals;

/// <summary>Why a proposal was refused.</summary>
public enum ProposalRejectionReason
{
    None,
    Empty,
    TooManyEntries,
    FileTooLarge,
    TotalTooLarge,
    BinaryContent,
    DuplicatePath,
    PathOutsideWorkspace,
}

/// <summary>
/// Raised when a proposal cannot be accepted.
/// </summary>
public sealed class ProposalRejectedException(ProposalRejectionReason reason, string detail)
    : InvalidOperationException(detail)
{
    public ProposalRejectionReason Reason { get; } = reason;
}

/// <summary>
/// Validates a proposal before it is stored (FR-011a).
/// <para>
/// Enforced at creation rather than at display time, which is what makes SC-004
/// hold without an exception: every proposal that reaches a reviewer is one that
/// can be shown in full. A cap applied only when rendering would leave oversized
/// proposals sitting in the database awaiting a decision nobody can make
/// properly.
/// </para>
/// </summary>
public sealed class ProposalValidator(ProposalLimitOptions limits)
{
    /// <summary>
    /// Validates the entries, throwing on the first problem.
    /// </summary>
    /// <param name="workspace">The run's working copy, for path confinement.</param>
    /// <param name="entries">The proposed changes.</param>
    /// <exception cref="ProposalRejectedException">The proposal is not acceptable.</exception>
    public void Validate(WorkspaceRoot workspace, IReadOnlyList<ProposalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            // FR-008b: an empty proposal is a no-change outcome, never something
            // offered for approval.
            throw new ProposalRejectedException(
                ProposalRejectionReason.Empty,
                "A proposal with no entries is a no-change outcome, not a proposal.");
        }

        if (entries.Count > limits.MaxEntries)
        {
            throw new ProposalRejectedException(
                ProposalRejectionReason.TooManyEntries,
                $"The proposal affects {entries.Count} files, above the {limits.MaxEntries} limit.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0L;

        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Path))
            {
                // Two entries for one file would make the applied result depend
                // on ordering, and the hash would not describe a single outcome.
                throw new ProposalRejectedException(
                    ProposalRejectionReason.DuplicatePath,
                    $"'{entry.Path}' appears more than once in the proposal.");
            }

            // Write intent: this is the check that would refuse a path escaping
            // the working copy, and it runs before anything is stored — let
            // alone written (FR-024).
            var resolved = PathGuard.Resolve(workspace, entry.Path, AccessIntent.Write);
            if (!resolved.IsAllowed)
            {
                throw new ProposalRejectedException(
                    ProposalRejectionReason.PathOutsideWorkspace,
                    $"'{entry.Path}' resolves outside the workspace ({resolved.Reason}).");
            }

            if (IndexingExclusionPolicy.LooksBinary(entry.NewContent))
            {
                // Binary changes are out of scope per the specification's
                // assumptions, and a diff of them would not be reviewable.
                throw new ProposalRejectedException(
                    ProposalRejectionReason.BinaryContent,
                    $"'{entry.Path}' contains binary content, which is out of scope.");
            }

            var size = System.Text.Encoding.UTF8.GetByteCount(entry.NewContent);
            if (size > limits.MaxBytesPerFile)
            {
                throw new ProposalRejectedException(
                    ProposalRejectionReason.FileTooLarge,
                    $"'{entry.Path}' is {size} bytes, above the {limits.MaxBytesPerFile} limit.");
            }

            total += size;
        }

        if (total > limits.MaxTotalBytes)
        {
            throw new ProposalRejectedException(
                ProposalRejectionReason.TotalTooLarge,
                $"The proposal totals {total} bytes, above the {limits.MaxTotalBytes} limit.");
        }
    }
}
