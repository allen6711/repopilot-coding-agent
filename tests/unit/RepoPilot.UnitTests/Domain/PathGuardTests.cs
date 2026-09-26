using RepoPilot.Domain.Workspace;
using Xunit;

namespace RepoPilot.UnitTests.Domain;

/// <summary>
/// SC-010: every attempt to access a path outside the workspace is refused, and
/// the refusal happens before any file operation. These tests build real
/// directories and real symlinks rather than mocking the filesystem — a path
/// guard that is only correct against a fake is not a path guard.
/// </summary>
public sealed class PathGuardTests : IDisposable
{
    private readonly string _temp;
    private readonly string _rootPath;
    private readonly string _outsidePath;
    private readonly WorkspaceRoot _root;

    public PathGuardTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "repopilot-guard-" + Guid.NewGuid().ToString("N"));
        _rootPath = Path.Combine(_temp, "workspace");
        _outsidePath = Path.Combine(_temp, "outside");

        Directory.CreateDirectory(Path.Combine(_rootPath, "src"));
        Directory.CreateDirectory(_outsidePath);
        File.WriteAllText(Path.Combine(_rootPath, "src", "Service.cs"), "// in workspace");
        File.WriteAllText(Path.Combine(_outsidePath, "secrets.env"), "API_KEY=leak-me");

        _root = WorkspaceRoot.Writable(_rootPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Cleanup failure must not mask a test result.
        }
    }

    [Fact]
    public void PathInsideRoot_IsAllowed()
    {
        var result = PathGuard.Resolve(_root, "src/Service.cs", AccessIntent.Read);

        Assert.True(result.IsAllowed);
        Assert.Equal(PathRefusalReason.None, result.Reason);
        Assert.NotNull(result.FullPath);
        Assert.StartsWith(_root.FullPath, result.FullPath!, StringComparison.Ordinal);
    }

    [Fact]
    public void PathThatDoesNotExistYet_IsAllowed_ForCreate()
    {
        // A create operation targets a path with no file behind it yet. The
        // guard must still confine it rather than refusing everything new.
        var result = PathGuard.Resolve(_root, "src/NewFile.cs", AccessIntent.Write);

        Assert.True(result.IsAllowed);
    }

    [Theory]
    [InlineData("../outside/secrets.env")]
    [InlineData("src/../../outside/secrets.env")]
    [InlineData("..")]
    [InlineData("src/../..")]
    public void TraversalSegments_AreRefused(string candidate)
    {
        var result = PathGuard.Resolve(_root, candidate, AccessIntent.Read);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRefusalReason.TraversalSegment, result.Reason);
        Assert.Null(result.FullPath);
    }

    [Fact]
    public void AbsolutePath_IsRefused()
    {
        var result = PathGuard.Resolve(
            _root, Path.Combine(_outsidePath, "secrets.env"), AccessIntent.Read);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRefusalReason.AbsolutePath, result.Reason);
    }

    [Theory]
    [InlineData("%2e%2e/outside/secrets.env")]
    [InlineData("%2e%2e%2foutside%2fsecrets.env")]
    [InlineData("src%2f%2e%2e%2f%2e%2e%2foutside")]
    public void EncodedTraversal_IsRefused(string candidate)
    {
        // The guard does not decode paths, but a caller downstream might. A
        // candidate whose decoded form introduces separators or dot segments is
        // refused rather than passed along to be decoded later.
        var result = PathGuard.Resolve(_root, candidate, AccessIntent.Read);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRefusalReason.EncodedTraversal, result.Reason);
    }

    [Fact]
    public void EmptyOrWhitespacePath_IsRefused()
    {
        Assert.Equal(
            PathRefusalReason.EmptyPath,
            PathGuard.Resolve(_root, "   ", AccessIntent.Read).Reason);
    }

    [Fact]
    public void SiblingDirectoryWithSharedPrefix_IsRefused()
    {
        // "/tmp/x/workspace-evil" must not pass for root "/tmp/x/workspace".
        // A naive StartsWith without a trailing separator would let it through.
        var evil = _rootPath + "-evil";
        Directory.CreateDirectory(evil);
        File.WriteAllText(Path.Combine(evil, "planted.cs"), "// not in workspace");

        var relative = Path.Combine("..", Path.GetFileName(evil), "planted.cs");
        var result = PathGuard.Resolve(_root, relative, AccessIntent.Read);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void SymlinkPointingOutsideRoot_IsRefused()
    {
        // FR-024b: a link that resolves outside the workspace is refused,
        // including one present in the fixture the working copy was made from.
        var linkPath = Path.Combine(_rootPath, "escape");
        try
        {
            Directory.CreateSymbolicLink(linkPath, _outsidePath);
        }
        catch (UnauthorizedAccessException)
        {
            return; // Symlink creation not permitted here; nothing to assert.
        }
        catch (IOException)
        {
            return;
        }

        var result = PathGuard.Resolve(_root, "escape/secrets.env", AccessIntent.Read);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRefusalReason.SymlinkEscape, result.Reason);
    }

    [Fact]
    public void IntermediateSymlink_IsCaught_NotOnlyTheFinalSegment()
    {
        // The guard walks every segment. A link partway down the path escapes
        // just as effectively as one at the end.
        var linkPath = Path.Combine(_rootPath, "src", "vendor");
        try
        {
            Directory.CreateSymbolicLink(linkPath, _outsidePath);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var result = PathGuard.Resolve(_root, "src/vendor/secrets.env", AccessIntent.Read);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRefusalReason.SymlinkEscape, result.Reason);
    }

    [Fact]
    public void SymlinkStayingInsideRoot_IsAllowed()
    {
        // Confinement, not a blanket ban on links.
        var target = Path.Combine(_rootPath, "src");
        var linkPath = Path.Combine(_rootPath, "alias");
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var result = PathGuard.Resolve(_root, "alias/Service.cs", AccessIntent.Read);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void ResolveOrThrow_CarriesTheRefusalReason()
    {
        var ex = Assert.Throws<PathAccessRefusedException>(
            () => PathGuard.ResolveOrThrow(_root, "../outside/secrets.env", AccessIntent.Read));

        Assert.Equal(PathRefusalReason.TraversalSegment, ex.Reason);
        Assert.Equal(AccessIntent.Read, ex.Intent);
    }
}
