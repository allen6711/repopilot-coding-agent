using System.Text.Json;
using RepoPilot.Agent.Capabilities;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Configuration;
using RepoPilot.Domain.Indexing;
using RepoPilot.Domain.Workspace;
using Xunit;

namespace RepoPilot.UnitTests.Agent;

/// <summary>
/// The per-file size limit, at every point that enforces it.
/// <para>
/// The constitution names "file-size and context-size limits" as a mandatory test
/// area. The context half is covered by <c>ToolInvokerTests</c>; this is the file
/// half, and it is written across all three enforcement points rather than one
/// because a limit applied at indexing and forgotten at read is not a limit —
/// it just moves the oversized file from the index to model context by a
/// different route.
/// </para>
/// <para>
/// The three points are: the indexing exclusion policy, the bounded file read,
/// and the file listing. They share one predicate and one configured value, and
/// the last test here is the one that says so.
/// </para>
/// </summary>
public sealed class FileSizeLimitTests : IDisposable
{
    /// <summary>
    /// Small enough to make an oversized file cheap to write, and inside the
    /// configured range so the value under test is one a deployment could hold.
    /// </summary>
    private const int Limit = 2_048;

    private static readonly IndexingOptions Options = new() { MaxFileBytes = Limit };

    private readonly string _root =
        Directory.CreateTempSubdirectory("repopilot-filesize-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException)
        {
        }
    }

    /// <summary>Writes an ASCII file of exactly <paramref name="bytes"/> bytes.</summary>
    private string Write(string relativePath, int bytes)
    {
        var absolute = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        // ASCII, so one character is one byte and the boundary under test is the
        // one the assertion names.
        File.WriteAllText(absolute, new string('x', bytes));

        Assert.Equal(bytes, new FileInfo(absolute).Length);

        return absolute;
    }

    private CapabilityContext Context() =>
        new(Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            WorkspaceRoot.Writable(_root),
            new RunContextBudget(1_000_000));

    // ---- enforcement point 1: indexing exclusion -----------------------------

    [Fact]
    public void IndexingExcludesAFileAboveTheLimit()
    {
        var verdict = new IndexingExclusionPolicy(Limit)
            .Evaluate("src/Huge.cs", Limit + 1, "content");

        Assert.False(verdict.IsIncluded);
        Assert.Equal(ExclusionReason.Size, verdict.Reason);
    }

    [Fact]
    public void IndexingIncludesAFileExactlyAtTheLimit()
    {
        // The limit is the largest permitted size, not the first forbidden one.
        // Getting this backwards silently drops one file per repository and is
        // invisible in an exclusion count.
        var verdict = new IndexingExclusionPolicy(Limit)
            .Evaluate("src/Exact.cs", Limit, "content");

        Assert.True(verdict.IsIncluded);
    }

    // ---- enforcement point 2: bounded file read ------------------------------

    [Fact]
    public async Task ReadingAFileAboveTheLimitIsRefused()
    {
        Write("src/Huge.cs", Limit + 1);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReadFileCapability(Options).InvokeAsync(
                Context(), JsonSerializer.Serialize(new { path = "src/Huge.cs" })));

        // The message names the observed size and the limit. Both come from the
        // file's metadata, so the refusal is decided before the content is
        // opened rather than after it has been loaded and measured.
        Assert.Contains((Limit + 1).ToString(), refusal.Message, StringComparison.Ordinal);
        Assert.Contains(Limit.ToString(), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadingAFileExactlyAtTheLimitIsAllowed()
    {
        Write("src/Exact.cs", Limit);

        var result = await new ReadFileCapability(Options).InvokeAsync(
            Context(), JsonSerializer.Serialize(new { path = "src/Exact.cs" }));

        Assert.Contains("xxx", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALineRangeDoesNotBuyAWayPastTheLimit()
    {
        Write("src/Huge.cs", Limit + 1);

        // Asking for ten lines of an oversized file is still opening an
        // oversized file. The limit is on the file, not on what is returned —
        // the returned volume is what the context budget bounds.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReadFileCapability(Options).InvokeAsync(
                Context(),
                JsonSerializer.Serialize(new { path = "src/Huge.cs", start_line = 1, end_line = 10 })));
    }

    // ---- enforcement point 3: file listing -----------------------------------

    [Fact]
    public async Task AnOversizedFileIsNotListed()
    {
        Write("src/Small.cs", 100);
        Write("src/Huge.cs", Limit + 1);

        var result = await new ListFilesCapability(Options).InvokeAsync(
            Context(), "{}");

        using var payload = JsonDocument.Parse(result.Content);
        var paths = payload.RootElement.GetProperty("paths")
            .EnumerateArray().Select(p => p.GetString()).ToList();

        // Naming it would tell the model the file exists and invite a read that
        // the read capability would then refuse — a wasted turn, and a disclosure
        // the listing had no reason to make.
        Assert.Contains("src/Small.cs", paths);
        Assert.DoesNotContain("src/Huge.cs", paths);
    }

    // ---- the points agree ----------------------------------------------------

    /// <summary>
    /// The property that makes the three points one limit rather than three.
    /// <para>
    /// A file the index refuses must also be unreadable and unlisted. If they
    /// could disagree, the smaller limit would be decorative: whichever path was
    /// most permissive would decide what reaches the model.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(Limit - 1, true)]
    [InlineData(Limit, true)]
    [InlineData(Limit + 1, false)]
    public async Task EveryEnforcementPointAgreesOnTheSameBoundary(int size, bool permitted)
    {
        var relative = $"src/Case{size}.cs";
        Write(relative, size);

        var indexed = new IndexingExclusionPolicy(Limit)
            .Evaluate(relative, size, "content").IsIncluded;

        var readable = true;
        try
        {
            await new ReadFileCapability(Options).InvokeAsync(
                Context(), JsonSerializer.Serialize(new { path = relative }));
        }
        catch (InvalidOperationException)
        {
            readable = false;
        }

        var listing = await new ListFilesCapability(Options).InvokeAsync(Context(), "{}");
        using var payload = JsonDocument.Parse(listing.Content);
        var listed = payload.RootElement.GetProperty("paths")
            .EnumerateArray().Any(p => p.GetString() == relative);

        Assert.Equal(permitted, indexed);
        Assert.Equal(permitted, readable);
        Assert.Equal(permitted, listed);
    }

    /// <summary>
    /// All three read the same configured value. Asserted structurally because
    /// the alternative failure — one point compiled against a different constant —
    /// would pass every test above that used a single configuration.
    /// </summary>
    [Fact]
    public async Task RaisingTheConfiguredLimitMovesEveryEnforcementPoint()
    {
        var relative = "src/Middling.cs";
        Write(relative, Limit + 1);

        var raised = new IndexingOptions { MaxFileBytes = Limit * 2 };

        Assert.True(new IndexingExclusionPolicy(raised.MaxFileBytes)
            .Evaluate(relative, Limit + 1, "content").IsIncluded);

        var read = await new ReadFileCapability(raised).InvokeAsync(
            Context(), JsonSerializer.Serialize(new { path = relative }));

        Assert.NotEqual(string.Empty, read.Content);

        var listing = await new ListFilesCapability(raised).InvokeAsync(Context(), "{}");
        Assert.Contains(relative, listing.Content, StringComparison.Ordinal);
    }
}
