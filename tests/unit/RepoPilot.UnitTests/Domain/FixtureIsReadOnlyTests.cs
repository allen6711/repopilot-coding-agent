using RepoPilot.Domain.Workspace;
using Xunit;

namespace RepoPilot.UnitTests.Domain;

/// <summary>
/// FR-016a: the registered repository fixture is never opened for writing on any
/// code path. SC-002 checks the same property after the fact by comparing
/// directories; these tests check it preventively, at the only place a path
/// becomes usable.
/// </summary>
public sealed class FixtureIsReadOnlyTests : IDisposable
{
    private readonly string _temp;
    private readonly WorkspaceRoot _fixture;

    public FixtureIsReadOnlyTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "repopilot-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_temp, "src"));
        File.WriteAllText(Path.Combine(_temp, "src", "Service.cs"), "// fixture source");

        _fixture = WorkspaceRoot.ReadOnly(_temp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ReadingFromTheFixture_IsAllowed()
    {
        // Indexing and working-copy creation both read the fixture. Read-only
        // must mean read-only, not unreachable.
        var result = PathGuard.Resolve(_fixture, "src/Service.cs", AccessIntent.Read);

        Assert.True(result.IsAllowed);
    }

    [Theory]
    [InlineData("src/Service.cs")]           // an existing file
    [InlineData("src/New.cs")]               // a file that does not exist yet
    [InlineData("newdir/nested/New.cs")]     // a path needing new directories
    public void EveryWriteToTheFixture_IsRefused(string candidate)
    {
        var result = PathGuard.Resolve(_fixture, candidate, AccessIntent.Write);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRefusalReason.WriteToReadOnlyRoot, result.Reason);
        Assert.Null(result.FullPath);
    }

    [Fact]
    public void WriteRefusal_TakesPrecedenceOverPathShape()
    {
        // The reason must describe the actual objection. Reporting "escapes
        // root" for a perfectly ordinary path would send a reader looking for a
        // traversal bug that is not there.
        var valid = PathGuard.Resolve(_fixture, "src/Service.cs", AccessIntent.Write);
        Assert.Equal(PathRefusalReason.WriteToReadOnlyRoot, valid.Reason);

        // And a write attempt that is *also* a traversal is still reported as a
        // read-only violation, because that check runs first and is absolute.
        var traversal = PathGuard.Resolve(_fixture, "../elsewhere/x.cs", AccessIntent.Write);
        Assert.False(traversal.IsAllowed);
        Assert.Equal(PathRefusalReason.WriteToReadOnlyRoot, traversal.Reason);
    }

    [Fact]
    public void AWritableRootWithTheSamePath_StillAllowsWrites()
    {
        // Read-only is a property of the root object the caller was handed, so
        // the working copy — a different root over different bytes — is
        // unaffected by the fixture being protected.
        var writable = WorkspaceRoot.Writable(_temp);

        Assert.True(PathGuard.Resolve(writable, "src/New.cs", AccessIntent.Write).IsAllowed);
    }

    [Fact]
    public void ResolveOrThrow_RefusesWritesToTheFixture()
    {
        var ex = Assert.Throws<PathAccessRefusedException>(
            () => PathGuard.ResolveOrThrow(_fixture, "src/Service.cs", AccessIntent.Write));

        Assert.Equal(PathRefusalReason.WriteToReadOnlyRoot, ex.Reason);
    }
}
