using RepoPilot.Domain.Proposals;
using Xunit;

namespace RepoPilot.UnitTests.Domain;

/// <summary>
/// The hash is what makes "refuse to apply a change whose content differs from
/// what was shown to the approver" (FR-020) a checkable statement rather than an
/// aspiration. These tests pin the two properties it has to have: it must not
/// change when nothing about the change changed, and it must change when
/// anything about the change did.
/// </summary>
public sealed class DiffHashTests
{
    private static ProposalEntry Entry(string path, string content) =>
        new(path, ProposalOperation.Modify, content);

    [Fact]
    public void EntryOrder_DoesNotAffectTheHash()
    {
        var a = new[] { Entry("src/A.cs", "one"), Entry("src/B.cs", "two") };
        var b = new[] { Entry("src/B.cs", "two"), Entry("src/A.cs", "one") };

        Assert.Equal(DiffHash.Compute(a), DiffHash.Compute(b));
    }

    [Fact]
    public void ContentChange_ChangesTheHash()
    {
        var before = new[] { Entry("src/A.cs", "return 1;") };
        var after = new[] { Entry("src/A.cs", "return 2;") };

        Assert.NotEqual(DiffHash.Compute(before), DiffHash.Compute(after));
    }

    [Fact]
    public void WhitespaceOnlyContentChange_ChangesTheHash()
    {
        // The approver saw specific bytes. A trailing newline is a byte.
        var before = new[] { Entry("src/A.cs", "return 1;") };
        var after = new[] { Entry("src/A.cs", "return 1;\n") };

        Assert.NotEqual(DiffHash.Compute(before), DiffHash.Compute(after));
    }

    [Fact]
    public void PathChange_ChangesTheHash()
    {
        var before = new[] { Entry("src/A.cs", "same") };
        var after = new[] { Entry("src/B.cs", "same") };

        Assert.NotEqual(DiffHash.Compute(before), DiffHash.Compute(after));
    }

    [Fact]
    public void OperationChange_ChangesTheHash()
    {
        // Creating a file and overwriting an existing one are different acts even
        // when the resulting bytes match.
        var create = new[] { new ProposalEntry("src/A.cs", ProposalOperation.Create, "x") };
        var modify = new[] { new ProposalEntry("src/A.cs", ProposalOperation.Modify, "x") };

        Assert.NotEqual(DiffHash.Compute(create), DiffHash.Compute(modify));
    }

    [Fact]
    public void AddingAnEntry_ChangesTheHash()
    {
        var one = new[] { Entry("src/A.cs", "x") };
        var two = new[] { Entry("src/A.cs", "x"), Entry("src/B.cs", "") };

        Assert.NotEqual(DiffHash.Compute(one), DiffHash.Compute(two));
    }

    [Fact]
    public void FieldBoundaries_CannotBeForgedByEmbeddingSeparators()
    {
        // Content containing newlines must not be able to imitate the canonical
        // form's field separators and collide with a different proposal. Hashing
        // each content to a fixed-length digest before joining is what prevents
        // that; this test fails if the canonical form ever inlines raw content.
        var a = new[] { new ProposalEntry("src/A.cs", ProposalOperation.Modify, "x\nsrc/B.cs\nmodify\ny") };
        var b = new[] { Entry("src/A.cs", "x"), Entry("src/B.cs", "y") };

        Assert.NotEqual(DiffHash.Compute(a), DiffHash.Compute(b));
    }

    [Fact]
    public void Hash_IsStableAcrossCalls()
    {
        var entries = new[] { Entry("src/A.cs", "content") };

        Assert.Equal(DiffHash.Compute(entries), DiffHash.Compute(entries));
    }

    [Fact]
    public void Hash_IsLowercaseHexOf256Bits()
    {
        var hash = DiffHash.Compute([Entry("src/A.cs", "content")]);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[a-f0-9]{64}$", hash);
    }

    [Fact]
    public void EmptyProposal_IsRefused()
    {
        // FR-008b: no change proposed is a terminal outcome, not a proposal to
        // be hashed and offered for approval.
        Assert.Throws<ArgumentException>(() => DiffHash.Compute([]));
    }

    [Fact]
    public void DuplicatePaths_AreRefused()
    {
        var entries = new[] { Entry("src/A.cs", "first"), Entry("src/A.cs", "second") };

        Assert.Throws<ArgumentException>(() => DiffHash.Compute(entries));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("abc", null)]
    [InlineData(null, "abc")]
    [InlineData("abc", "abd")]
    [InlineData("abc", "abcd")]
    public void Matches_RejectsAnythingButAnExactPair(string? expected, string? actual)
    {
        Assert.False(DiffHash.Matches(expected, actual));
    }

    [Fact]
    public void Matches_AcceptsAnIdenticalHash()
    {
        var hash = DiffHash.Compute([Entry("src/A.cs", "content")]);

        Assert.True(DiffHash.Matches(hash, hash));
    }
}
