using System.Security.Cryptography;
using System.Text;

namespace RepoPilot.Domain.Proposals;

/// <summary>What a proposal entry does to a file.</summary>
public enum ProposalOperation
{
    Create,
    Modify,
}

/// <summary>
/// One file's complete replacement content.
/// <para>
/// Proposals carry full content per file rather than model-authored diff hunks
/// (research decision 7). That removes the "diff does not apply cleanly" failure
/// class and makes this hash exact: the bytes hashed are the bytes that will be
/// written.
/// </para>
/// </summary>
/// <param name="Path">Repository-relative path, forward slashes.</param>
/// <param name="Operation">Create or modify.</param>
/// <param name="NewContent">The file's complete new content.</param>
public sealed record ProposalEntry(string Path, ProposalOperation Operation, string NewContent);

/// <summary>
/// The change-content hash that binds an approval to exactly one change
/// (FR-019a, FR-020a).
/// <para>
/// The hash covers content, not the rendered diff. Hashing the rendered form
/// would make every historical approval invalid the moment a diff-rendering
/// setting changed, even though nothing about the decided change had moved.
/// </para>
/// </summary>
public static class DiffHash
{
    /// <summary>
    /// Computes the canonical hash of a proposal.
    /// <para>
    /// Entries are sorted by path so enumeration order cannot change the result,
    /// and each entry contributes <c>path \n operation \n sha256(content)</c>.
    /// Hashing the content digest rather than the content itself keeps the
    /// canonical form a bounded size regardless of file size.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The proposal is empty, or two entries name the same path — which would
    /// make the applied result depend on ordering.
    /// </exception>
    public static string Compute(IReadOnlyCollection<ProposalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            throw new ArgumentException(
                "A proposal with no entries has no change to hash. An empty proposal is a " +
                "no-change outcome, not a proposal (FR-008b).",
                nameof(entries));
        }

        var ordered = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();

        for (var i = 1; i < ordered.Length; i++)
        {
            if (string.Equals(ordered[i - 1].Path, ordered[i].Path, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Duplicate path '{ordered[i].Path}' in proposal. Two entries for one file " +
                    "would make the applied result depend on ordering.",
                    nameof(entries));
            }
        }

        var canonical = new StringBuilder();
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i > 0)
            {
                canonical.Append('\n');
            }

            var entry = ordered[i];
            canonical.Append(entry.Path)
                     .Append('\n')
                     .Append(OperationToken(entry.Operation))
                     .Append('\n')
                     .Append(Sha256Hex(entry.NewContent));
        }

        return Sha256Hex(canonical.ToString());
    }

    /// <summary>
    /// Constant-time comparison of two hashes. Ordinary string equality would be
    /// fine for correctness, but this value gates a write, so it is compared
    /// without leaking position information through timing.
    /// </summary>
    public static bool Matches(string? expected, string? actual)
    {
        if (expected is null || actual is null || expected.Length != actual.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));
    }

    private static string OperationToken(ProposalOperation operation) => operation switch
    {
        ProposalOperation.Create => "create",
        ProposalOperation.Modify => "modify",
        _ => throw new ArgumentOutOfRangeException(
            nameof(operation), operation, "Unknown proposal operation."),
    };

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
