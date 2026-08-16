using RepoPilot.Domain.Workspace;

namespace RepoPilot.UnitTests.Evals;

/// <summary>
/// Reference solutions stay out of the agent's reach (FR-035).
/// <para>
/// The guarantee has two halves and both are checked here. The layout half:
/// <c>evals/tasks/_reference/</c> is not inside any fixture, so no indexer walk
/// and no working-copy build can pick it up. The guard half: even naming it
/// explicitly, from inside a fixture root, resolves to a refusal rather than to a
/// file.
/// </para>
/// <para>
/// Either half alone would be a weaker claim than the one FR-035 makes. A layout
/// that keeps them apart could be undone by moving a directory; a guard that
/// refuses traversal says nothing about a reference file placed inside a fixture
/// by mistake.
/// </para>
/// </summary>
public sealed class ReferenceSolutionsAreUnreachableTests
{
    [Fact]
    public void ReferenceSolutionsAreCommitted()
    {
        // Guards against the rest of this class passing vacuously: a claim that
        // nothing can reach the reference directory is worth nothing if the
        // directory is empty.
        Assert.True(Directory.Exists(CommittedTaskSet.ReferenceDirectory));

        Assert.NotEmpty(Directory.GetFiles(CommittedTaskSet.ReferenceDirectory, "*.md"));
    }

    [Fact]
    public void TheReferenceDirectoryIsOutsideEveryFixtureRoot()
    {
        var reference = Path.GetFullPath(CommittedTaskSet.ReferenceDirectory);

        foreach (var fixture in Directory.GetDirectories(CommittedTaskSet.FixturesDirectory))
        {
            var root = Path.GetFullPath(fixture) + Path.DirectorySeparatorChar;

            Assert.False(
                reference.StartsWith(root, StringComparison.Ordinal),
                $"Reference solutions sit inside fixture '{Path.GetFileName(fixture)}'. " +
                "The indexer walks a fixture root, so they would reach model context (FR-035).");
        }
    }

    [Fact]
    public void EveryReferenceFileNamedByATaskIsInThatDirectory()
    {
        var tasks = CommittedTaskSet.LoadAsync().GetAwaiter().GetResult();

        foreach (var task in tasks)
        {
            if (task.ReferencePatch is not { } reference)
            {
                continue;
            }

            Assert.StartsWith("_reference/", reference, StringComparison.Ordinal);

            var path = Path.Combine(
                CommittedTaskSet.TasksDirectory,
                reference.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"Task '{task.Id}' names a reference that is missing.");
        }
    }

    /// <summary>
    /// The guard half. A run resolves paths against its working copy, which is a
    /// copy of a fixture root — so every one of these is a path a task
    /// description could name and none of them resolves.
    /// </summary>
    [Theory]
    [InlineData("../tasks/_reference/bugfix-null-guard-01.md")]
    [InlineData("../../tasks/_reference/bugfix-null-guard-01.md")]
    [InlineData("src/../../../tasks/_reference/bugfix-null-guard-01.md")]
    [InlineData("..%2Ftasks%2F_reference%2Fbugfix-null-guard-01.md")]
    [InlineData("/evals/tasks/_reference/bugfix-null-guard-01.md")]
    public void NoRunResolvesAPathIntoTheReferenceDirectory(string candidate)
    {
        var fixtureRoot = WorkspaceRoot.ReadOnly(
            Path.Combine(CommittedTaskSet.FixturesDirectory, "sample-dotnet-api"));

        var resolution = PathGuard.Resolve(fixtureRoot, candidate, AccessIntent.Read);

        Assert.False(
            resolution.IsAllowed,
            $"'{candidate}' resolved to {resolution.FullPath}. A reference solution the agent can " +
            "read is not a reference.");
    }
}
