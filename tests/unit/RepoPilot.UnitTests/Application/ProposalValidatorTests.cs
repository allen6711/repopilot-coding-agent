using System.Security.Cryptography;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Proposals;
using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Workspace;
using Xunit;

namespace RepoPilot.UnitTests.Application;

/// <summary>
/// FR-011a and Principle I from the validator's side.
/// <para>
/// The caps are enforced at creation, not at display, which is what makes SC-004
/// hold without an exception: every proposal a reviewer sees is one that can be
/// shown in full. These tests also cover the case Principle I cares about most —
/// that validating a proposal, including a rejected one, changes nothing on disk.
/// </para>
/// </summary>
public sealed class ProposalValidatorTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceRoot _workspace;

    public ProposalValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "repopilot-prop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Service.cs"), "original content\n");
        _workspace = WorkspaceRoot.Writable(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ProposalValidator Validator(
        int maxEntries = 20, int maxPerFile = 262_144, int maxTotal = 524_288) =>
        new(new ProposalLimitOptions
        {
            MaxEntries = maxEntries,
            MaxBytesPerFile = maxPerFile,
            MaxTotalBytes = maxTotal,
        });

    private static ProposalEntry Entry(string path, string content = "new content") =>
        new(path, ProposalOperation.Modify, content);

    /// <summary>Hashes the whole tree so any write at all is detectable.</summary>
    private string HashTree()
    {
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal);

        using var sha = SHA256.Create();
        var accumulator = new List<byte>();

        foreach (var file in files)
        {
            accumulator.AddRange(System.Text.Encoding.UTF8.GetBytes(
                Path.GetRelativePath(_root, file)));
            accumulator.AddRange(File.ReadAllBytes(file));
        }

        return Convert.ToHexStringLower(sha.ComputeHash([.. accumulator]));
    }

    [Fact]
    public void ValidatingAProposal_WritesNothing()
    {
        // Principle I: proposing must not mutate anything. Asserted over the
        // whole tree rather than one file, so a stray temp file or a directory
        // creation would fail this too.
        var before = HashTree();

        Validator().Validate(_workspace, [Entry("src/Service.cs"), Entry("src/New.cs")]);

        Assert.Equal(before, HashTree());
    }

    [Fact]
    public void ValidatingARejectedProposal_AlsoWritesNothing()
    {
        var before = HashTree();

        Assert.Throws<ProposalRejectedException>(
            () => Validator(maxEntries: 1).Validate(
                _workspace, [Entry("src/A.cs"), Entry("src/B.cs")]));

        Assert.Equal(before, HashTree());
    }

    [Fact]
    public void AnEmptyProposalIsRefused()
    {
        var ex = Assert.Throws<ProposalRejectedException>(
            () => Validator().Validate(_workspace, []));

        Assert.Equal(ProposalRejectionReason.Empty, ex.Reason);
    }

    [Fact]
    public void TooManyEntriesIsRefused()
    {
        var entries = Enumerable.Range(0, 25).Select(i => Entry($"src/File{i}.cs")).ToList();

        var ex = Assert.Throws<ProposalRejectedException>(
            () => Validator(maxEntries: 20).Validate(_workspace, entries));

        Assert.Equal(ProposalRejectionReason.TooManyEntries, ex.Reason);
    }

    [Fact]
    public void AFileAboveThePerFileCapIsRefused()
    {
        var ex = Assert.Throws<ProposalRejectedException>(
            () => Validator(maxPerFile: 100).Validate(
                _workspace, [Entry("src/Big.cs", new string('x', 200))]));

        Assert.Equal(ProposalRejectionReason.FileTooLarge, ex.Reason);
    }

    [Fact]
    public void ATotalAboveTheOverallCapIsRefused()
    {
        // Each entry is individually fine; together they are not. Without the
        // total cap, twenty maximum-size files would pass.
        var entries = Enumerable.Range(0, 5)
            .Select(i => Entry($"src/File{i}.cs", new string('x', 100)))
            .ToList();

        var ex = Assert.Throws<ProposalRejectedException>(
            () => Validator(maxPerFile: 200, maxTotal: 300).Validate(_workspace, entries));

        Assert.Equal(ProposalRejectionReason.TotalTooLarge, ex.Reason);
    }

    [Fact]
    public void ADuplicatePathIsRefused()
    {
        // Two entries for one file would make the applied result depend on
        // ordering, so the hash would not describe a single outcome.
        var ex = Assert.Throws<ProposalRejectedException>(
            () => Validator().Validate(
                _workspace, [Entry("src/Service.cs", "first"), Entry("src/Service.cs", "second")]));

        Assert.Equal(ProposalRejectionReason.DuplicatePath, ex.Reason);
    }

    [Theory]
    [InlineData("../outside/Evil.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("src/../../escape.cs")]
    public void APathOutsideTheWorkspaceIsRefused(string path)
    {
        var ex = Assert.Throws<ProposalRejectedException>(
            () => Validator().Validate(_workspace, [Entry(path)]));

        Assert.Equal(ProposalRejectionReason.PathOutsideWorkspace, ex.Reason);
    }

    [Fact]
    public void BinaryContentIsRefused()
    {
        var ex = Assert.Throws<ProposalRejectedException>(
            () => Validator().Validate(_workspace, [Entry("src/Blob.cs", "prefix\0suffix")]));

        Assert.Equal(ProposalRejectionReason.BinaryContent, ex.Reason);
    }

    [Fact]
    public void CreatingANewFileIsAllowed()
    {
        // The path does not exist yet; confinement must still permit it.
        Validator().Validate(
            _workspace, [new ProposalEntry("src/Brand/New.cs", ProposalOperation.Create, "x")]);
    }

    [Fact]
    public void AProposalAtExactlyTheCapsIsAccepted()
    {
        // Boundary: the limits are "above", so the exact value passes.
        Validator(maxEntries: 2, maxPerFile: 10, maxTotal: 20).Validate(
            _workspace, [Entry("src/A.cs", "0123456789"), Entry("src/B.cs", "0123456789")]);
    }
}
