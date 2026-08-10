namespace RepoPilot.Domain.Workspace;

/// <summary>What the caller intends to do with the resolved path.</summary>
public enum AccessIntent
{
    Read,
    Write,
}

/// <summary>Why a path was refused. <see cref="None"/> means it was allowed.</summary>
public enum PathRefusalReason
{
    None,
    EmptyPath,
    AbsolutePath,
    TraversalSegment,
    EncodedTraversal,
    EscapesRoot,
    SymlinkEscape,
    WriteToReadOnlyRoot,
}

/// <summary>
/// A directory that file access may be confined to.
/// <para>
/// Two kinds exist. A run's disposable working copy is writable and is the only
/// place an approved change may be written (FR-016, FR-024a). A registered
/// repository fixture is read-only: no code path may open anything beneath it
/// for writing (FR-016a). Making that a property of the root rather than a
/// convention means the refusal happens at the guard, where it can be tested,
/// instead of depending on every future caller remembering.
/// </para>
/// </summary>
public sealed class WorkspaceRoot
{
    private WorkspaceRoot(string fullPath, bool isReadOnly)
    {
        FullPath = fullPath;
        IsReadOnly = isReadOnly;
    }

    /// <summary>Canonical absolute path of the root, with symlinks resolved.</summary>
    public string FullPath { get; }

    /// <summary>Whether write access beneath this root is refused outright.</summary>
    public bool IsReadOnly { get; }

    /// <summary>A run's disposable working copy.</summary>
    public static WorkspaceRoot Writable(string path) => Create(path, isReadOnly: false);

    /// <summary>A registered repository fixture.</summary>
    public static WorkspaceRoot ReadOnly(string path) => Create(path, isReadOnly: true);

    private static WorkspaceRoot Create(string path, bool isReadOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        // Resolve the root itself once, so a symlinked root does not make every
        // contained path look like an escape.
        if (Directory.Exists(full))
        {
            var resolved = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
            {
                full = Path.TrimEndingDirectorySeparator(resolved.FullName);
            }
        }

        return new WorkspaceRoot(full, isReadOnly);
    }
}

/// <summary>The outcome of a guarded path resolution.</summary>
public readonly record struct PathResolution
{
    private PathResolution(bool isAllowed, string? fullPath, PathRefusalReason reason)
    {
        IsAllowed = isAllowed;
        FullPath = fullPath;
        Reason = reason;
    }

    /// <summary>Whether the access may proceed.</summary>
    public bool IsAllowed { get; }

    /// <summary>The canonical absolute path. Non-null only when allowed.</summary>
    public string? FullPath { get; }

    /// <summary>Why the access was refused.</summary>
    public PathRefusalReason Reason { get; }

    internal static PathResolution Allow(string fullPath) =>
        new(true, fullPath, PathRefusalReason.None);

    internal static PathResolution Refuse(PathRefusalReason reason) =>
        new(false, null, reason);
}

/// <summary>
/// Confines file access to a workspace root (FR-024, FR-024a, FR-024b).
/// <para>
/// Every capability that touches the filesystem resolves through this type
/// before opening a handle, so a refusal happens before any I/O rather than
/// after a partial read. Refusals are returned rather than thrown so the caller
/// can record them as countable failed actions (FR-024c) — SC-010 asks for the
/// number of refused attempts, which requires them to be observable.
/// </para>
/// </summary>
public static class PathGuard
{
    // Path comparison fails closed: a case mismatch is refused rather than
    // assumed equivalent. Both sides come from the same canonicalization, so a
    // mismatch means the caller supplied something the root did not describe.
    private const StringComparison Comparison = StringComparison.Ordinal;

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against <paramref name="root"/>.
    /// </summary>
    /// <param name="root">The confining root.</param>
    /// <param name="relativePath">A repository-relative path. Never absolute.</param>
    /// <param name="intent">Whether the caller intends to read or write.</param>
    public static PathResolution Resolve(
        WorkspaceRoot root,
        string relativePath,
        AccessIntent intent)
    {
        ArgumentNullException.ThrowIfNull(root);

        // Checked first and independently of the path: a read-only root refuses
        // every write, including one whose path would otherwise be perfectly
        // legitimate. Ordering matters here — reporting "escapes root" for a
        // write to a valid fixture path would misdescribe the refusal.
        if (intent == AccessIntent.Write && root.IsReadOnly)
        {
            return PathResolution.Refuse(PathRefusalReason.WriteToReadOnlyRoot);
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return PathResolution.Refuse(PathRefusalReason.EmptyPath);
        }

        if (Path.IsPathRooted(relativePath))
        {
            return PathResolution.Refuse(PathRefusalReason.AbsolutePath);
        }

        if (HasEncodedTraversal(relativePath))
        {
            return PathResolution.Refuse(PathRefusalReason.EncodedTraversal);
        }

        if (HasTraversalSegment(relativePath))
        {
            return PathResolution.Refuse(PathRefusalReason.TraversalSegment);
        }

        var combined = Path.GetFullPath(Path.Combine(root.FullPath, relativePath));

        if (!IsInside(root.FullPath, combined))
        {
            return PathResolution.Refuse(PathRefusalReason.EscapesRoot);
        }

        // Textual containment is not enough: a symlink inside the root can point
        // outside it, including one that shipped with the fixture (FR-024b).
        var real = ResolveThroughLinks(root.FullPath, combined);
        if (!IsInside(root.FullPath, real))
        {
            return PathResolution.Refuse(PathRefusalReason.SymlinkEscape);
        }

        return PathResolution.Allow(real);
    }

    /// <summary>
    /// Convenience wrapper that throws instead of returning a refusal. Use only
    /// where a refusal is genuinely exceptional; prefer <see cref="Resolve"/> so
    /// the refusal can be recorded.
    /// </summary>
    /// <exception cref="PathAccessRefusedException">The access was refused.</exception>
    public static string ResolveOrThrow(
        WorkspaceRoot root,
        string relativePath,
        AccessIntent intent)
    {
        var resolution = Resolve(root, relativePath, intent);
        return resolution.IsAllowed
            ? resolution.FullPath!
            : throw new PathAccessRefusedException(relativePath, intent, resolution.Reason);
    }

    private static bool HasTraversalSegment(string path)
    {
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment is "..")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEncodedTraversal(string path)
    {
        // A caller that percent-decodes later would turn "%2e%2e%2f" into "../".
        // The guard does not decode paths itself; it refuses anything whose
        // decoded form would introduce a separator or a dot segment that the raw
        // form did not already contain.
        if (!path.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            // Undecodable input is refused rather than guessed at.
            return true;
        }

        if (string.Equals(decoded, path, StringComparison.Ordinal))
        {
            return false;
        }

        return HasTraversalSegment(decoded)
            || decoded.Contains('/', StringComparison.Ordinal)
            || decoded.Contains('\\', StringComparison.Ordinal);
    }

    private static bool IsInside(string root, string candidate)
    {
        if (string.Equals(root, candidate, Comparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        // Prefix comparison alone would let "/root-evil" pass for root "/root";
        // appending the separator is what prevents that.
        return candidate.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// Walks each segment beneath the root, following symlinks as it goes, so an
    /// intermediate link is caught and not only a final one. Segments that do not
    /// exist yet — a file about to be created — are appended literally.
    /// </summary>
    private static string ResolveThroughLinks(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative is "." or "")
        {
            return root;
        }

        var current = root;
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);

            FileSystemInfo? info =
                Directory.Exists(current) ? new DirectoryInfo(current) :
                File.Exists(current) ? new FileInfo(current) :
                null;

            if (info is null)
            {
                // Nothing further exists on disk, so nothing further can be a
                // link. Append the remainder and stop.
                for (var j = i + 1; j < segments.Length; j++)
                {
                    current = Path.Combine(current, segments[j]);
                }

                return Path.GetFullPath(current);
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                current = target.FullName;
            }
        }

        return Path.GetFullPath(current);
    }
}

/// <summary>
/// Raised by <see cref="PathGuard.ResolveOrThrow"/> when access is refused.
/// </summary>
public sealed class PathAccessRefusedException(
    string relativePath,
    AccessIntent intent,
    PathRefusalReason reason)
    : UnauthorizedAccessException(
        $"{intent} access to '{relativePath}' was refused: {reason}.")
{
    public string RelativePath { get; } = relativePath;

    public AccessIntent Intent { get; } = intent;

    public PathRefusalReason Reason { get; } = reason;
}
