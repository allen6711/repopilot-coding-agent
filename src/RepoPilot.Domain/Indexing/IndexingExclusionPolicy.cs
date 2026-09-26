using RepoPilot.Domain.Security;

namespace RepoPilot.Domain.Indexing;

/// <summary>
/// Why a file was left out of the index. The set is normative (FR-003b) and is
/// reported back per-reason after indexing (FR-003), so an operator can see what
/// was dropped and why rather than only how much.
/// </summary>
public enum ExclusionReason
{
    /// <summary>Not excluded.</summary>
    None,

    /// <summary>Binary content.</summary>
    Binary,

    /// <summary>Larger than the configured per-file limit.</summary>
    Size,

    /// <summary>Inside a build-output, dependency, or VCS directory.</summary>
    ExcludedDirectory,

    /// <summary>Secret-bearing by filename.</summary>
    SecretFilename,

    /// <summary>Secret-bearing by content.</summary>
    SecretContent,
}

/// <summary>The verdict for one candidate file.</summary>
/// <param name="Reason">Why it was excluded, or <see cref="ExclusionReason.None"/>.</param>
public readonly record struct ExclusionVerdict(ExclusionReason Reason)
{
    /// <summary>Whether the file may be indexed.</summary>
    public bool IsIncluded => Reason == ExclusionReason.None;

    public static ExclusionVerdict Include() => new(ExclusionReason.None);

    public static ExclusionVerdict Exclude(ExclusionReason reason) => new(reason);
}

/// <summary>
/// Decides what may enter the index (FR-002, FR-003b).
/// <para>
/// A fixture may narrow what is indexed; it may never widen it. That asymmetry
/// is the point — a fixture is partly untrusted input, and a configuration that
/// could re-include secrets or build output would make the exclusion set
/// advisory rather than enforced.
/// </para>
/// </summary>
public sealed class IndexingExclusionPolicy
{
    private static readonly string[] ExcludedDirectories =
    [
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", "dist", "build",
        "target", "vendor", ".venv", "venv", "__pycache__", ".next", ".nuxt",
        ".gradle", ".idea", ".vs", "coverage", "TestResults",
    ];

    private readonly int _maxFileBytes;
    private readonly string[] _additionalExcludedGlobs;

    /// <param name="maxFileBytes">Per-file size limit.</param>
    /// <param name="additionalExcludedGlobs">
    /// Fixture-supplied patterns. These only ever exclude more.
    /// </param>
    public IndexingExclusionPolicy(int maxFileBytes, IEnumerable<string>? additionalExcludedGlobs = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);

        _maxFileBytes = maxFileBytes;
        _additionalExcludedGlobs = additionalExcludedGlobs?.ToArray() ?? [];
    }

    /// <summary>
    /// Evaluates one candidate file.
    /// <para>
    /// Checks run cheapest-first, but the ordering is also deliberate about what
    /// a reader is told: a file inside <c>node_modules</c> that also happens to
    /// look secret is reported as a directory exclusion, because that is the
    /// reason an operator can act on.
    /// </para>
    /// </summary>
    /// <param name="relativePath">Repository-relative path, forward slashes.</param>
    /// <param name="sizeInBytes">File size.</param>
    /// <param name="contentSample">
    /// Content, or a leading sample of it. Pass null to skip content checks when
    /// the file has already been excluded on cheaper grounds.
    /// </param>
    public ExclusionVerdict Evaluate(string relativePath, long sizeInBytes, string? contentSample)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (IsInExcludedDirectory(relativePath))
        {
            return ExclusionVerdict.Exclude(ExclusionReason.ExcludedDirectory);
        }

        if (MatchesAdditionalExclusion(relativePath))
        {
            return ExclusionVerdict.Exclude(ExclusionReason.ExcludedDirectory);
        }

        if (SecretRedactor.IsSecretPath(relativePath))
        {
            return ExclusionVerdict.Exclude(ExclusionReason.SecretFilename);
        }

        if (sizeInBytes > _maxFileBytes)
        {
            return ExclusionVerdict.Exclude(ExclusionReason.Size);
        }

        if (contentSample is not null)
        {
            if (LooksBinary(contentSample))
            {
                return ExclusionVerdict.Exclude(ExclusionReason.Binary);
            }

            if (SecretRedactor.ContainsSecret(contentSample))
            {
                // The whole file is dropped rather than redacted. Partial
                // redaction is a detection problem with false negatives, and at
                // fixture scale exclusion costs nothing.
                return ExclusionVerdict.Exclude(ExclusionReason.SecretContent);
            }
        }

        return ExclusionVerdict.Include();
    }

    /// <summary>
    /// Whether a byte sample looks binary. A null byte in the first block is the
    /// same heuristic git uses, and it is enough here: fixtures are source trees.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<char> sample)
    {
        var limit = Math.Min(sample.Length, 8192);
        for (var i = 0; i < limit; i++)
        {
            if (sample[i] == '\0')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInExcludedDirectory(string relativePath)
    {
        var segments = relativePath.Split('/', '\\');

        // The final segment is the file name; only directory segments count.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            foreach (var excluded in ExcludedDirectories)
            {
                if (segments[i].Equals(excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool MatchesAdditionalExclusion(string relativePath)
    {
        foreach (var glob in _additionalExcludedGlobs)
        {
            if (GlobMatches(glob, relativePath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Minimal glob support: <c>**</c> spans separators, <c>*</c> does not, and
    /// <c>?</c> matches one character. Enough for the exclusion patterns a
    /// fixture may add, and small enough to reason about.
    /// </summary>
    private static bool GlobMatches(string pattern, string path)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            path,
            regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(500));
    }
}
