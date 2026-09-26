using RepoPilot.Infrastructure.Indexing;
using Xunit;

namespace RepoPilot.UnitTests.Application;

/// <summary>
/// FR-004 requires every retrieval result to carry an exact line range. These
/// tests treat that as the chunker's contract: a chunk's reported range must be
/// where its text actually came from, because a reviewer follows it back to the
/// code and the evaluation set's Recall@5 ground truth is checked against it.
/// </summary>
public sealed class LineWindowChunkerTests
{
    private static string Lines(int count) =>
        string.Join('\n', Enumerable.Range(1, count).Select(i => $"line {i}"));

    [Fact]
    public void AFileShorterThanOneWindow_YieldsASingleChunk()
    {
        var chunks = new LineWindowChunker(60, 15).Chunk(Lines(10));

        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].Ordinal);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(10, chunks[0].EndLine);
    }

    [Fact]
    public void ReportedRangesMatchTheTextTheyContain()
    {
        // The property that matters most: if a chunk says lines 46-105, then its
        // first line is line 46 and its last is line 105.
        var chunks = new LineWindowChunker(60, 15).Chunk(Lines(200));

        foreach (var chunk in chunks)
        {
            var chunkLines = chunk.Content.Split('\n');

            Assert.Equal($"line {chunk.StartLine}", chunkLines[0]);
            Assert.Equal($"line {chunk.EndLine}", chunkLines[^1]);
            Assert.Equal(chunk.EndLine - chunk.StartLine + 1, chunkLines.Length);
        }
    }

    [Fact]
    public void ConsecutiveChunksOverlapByTheConfiguredAmount()
    {
        var chunks = new LineWindowChunker(60, 15).Chunk(Lines(200));

        Assert.True(chunks.Count > 1);

        for (var i = 1; i < chunks.Count; i++)
        {
            var previousEnd = chunks[i - 1].EndLine;
            var currentStart = chunks[i].StartLine;

            // Overlap of 15 means the new chunk starts 15 lines before the
            // previous one ended — except for a final short window.
            if (chunks[i].EndLine - chunks[i].StartLine + 1 == 60)
            {
                Assert.Equal(previousEnd - 15 + 1, currentStart);
            }

            Assert.True(currentStart <= previousEnd, "Chunks must overlap, not gap.");
        }
    }

    [Fact]
    public void EveryLineAppearsInAtLeastOneChunk()
    {
        // A gap would make code unretrievable with no signal that it happened.
        var chunks = new LineWindowChunker(60, 15).Chunk(Lines(500));

        var covered = new HashSet<int>();
        foreach (var chunk in chunks)
        {
            for (var line = chunk.StartLine; line <= chunk.EndLine; line++)
            {
                covered.Add(line);
            }
        }

        Assert.Equal(500, covered.Count);
        Assert.Equal(Enumerable.Range(1, 500), covered.OrderBy(l => l));
    }

    [Fact]
    public void OrdinalsAreSequentialFromZero()
    {
        // The unique index is (repository, version, path, ordinal), so a gap or
        // a repeat would either lose a chunk or collide on insert.
        var chunks = new LineWindowChunker(60, 15).Chunk(Lines(300));

        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void NoTrailingDuplicateChunkIsEmitted()
    {
        // A file whose length lands exactly on a window boundary must not emit a
        // final chunk that merely repeats the tail — those all match the same
        // query and would inflate the index.
        var chunks = new LineWindowChunker(10, 3).Chunk(Lines(10));

        Assert.Single(chunks);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(10, chunks[0].EndLine);
    }

    [Fact]
    public void TheLastChunkEndsAtTheLastLine()
    {
        var chunks = new LineWindowChunker(60, 15).Chunk(Lines(137));

        Assert.Equal(137, chunks[^1].EndLine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    public void EmptyOrWhitespaceContent_YieldsNoChunks(string content)
    {
        // An empty chunk carries no signal and would only dilute rankings.
        Assert.Empty(new LineWindowChunker(60, 15).Chunk(content));
    }

    [Fact]
    public void LineNumbersAreIdenticalForCrlfAndLfContent()
    {
        // A Windows-authored file must not report different line numbers from an
        // otherwise identical Unix-authored one.
        var chunker = new LineWindowChunker(60, 15);

        var lf = chunker.Chunk("alpha\nbeta\ngamma");
        var crlf = chunker.Chunk("alpha\r\nbeta\r\ngamma");

        Assert.Equal(lf[0].StartLine, crlf[0].StartLine);
        Assert.Equal(lf[0].EndLine, crlf[0].EndLine);
        Assert.Equal(lf[0].Content, crlf[0].Content);
    }

    [Fact]
    public void ATrailingNewlineDoesNotCreateAnExtraLine()
    {
        var chunks = new LineWindowChunker(60, 15).Chunk("alpha\nbeta\n");

        Assert.Single(chunks);
        Assert.Equal(2, chunks[0].EndLine);
    }

    [Fact]
    public void AnOverlapNotSmallerThanTheWindow_IsRefusedAtConstruction()
    {
        // Stride would be zero or negative and chunking would not terminate.
        // Failing at construction beats hanging at index time.
        Assert.Throws<ArgumentOutOfRangeException>(() => new LineWindowChunker(10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LineWindowChunker(10, 11));
    }

    [Fact]
    public void ZeroOverlapIsPermitted()
    {
        var chunks = new LineWindowChunker(10, 0).Chunk(Lines(25));

        Assert.Equal(3, chunks.Count);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(11, chunks[1].StartLine);
        Assert.Equal(21, chunks[2].StartLine);
        Assert.Equal(25, chunks[2].EndLine);
    }
}
