namespace RepoPilot.Infrastructure.Indexing;

/// <summary>
/// One chunk of a file.
/// </summary>
/// <param name="Ordinal">0-based position within the file.</param>
/// <param name="Content">The chunk's text.</param>
/// <param name="StartLine">1-based, inclusive.</param>
/// <param name="EndLine">1-based, inclusive.</param>
public sealed record TextChunk(int Ordinal, string Content, int StartLine, int EndLine);

/// <summary>
/// Splits file content into overlapping line windows.
/// <para>
/// Every retrieval result must carry a repository-relative path and exact start
/// and end lines (FR-004). Line windows make those exact by construction — a
/// chunk's range is where its text came from, not an estimate — which is what
/// lets a reviewer follow a search result back to the code, and what makes the
/// Recall@5 ground truth in the evaluation set checkable.
/// </para>
/// <para>
/// Chunks overlap so a construct spanning a window boundary is not split beyond
/// recognition in both halves. Syntax-aware splitting would do better, but it
/// costs a parser per language; Principle III asks for measured evidence before
/// that complexity, and SC-005 is the measurement that would justify it.
/// </para>
/// </summary>
public sealed class LineWindowChunker
{
    private readonly int _windowLines;
    private readonly int _overlapLines;

    /// <param name="windowLines">Lines per chunk.</param>
    /// <param name="overlapLines">
    /// Lines repeated between consecutive chunks. Must be smaller than
    /// <paramref name="windowLines"/>, or the window would never advance.
    /// </param>
    public LineWindowChunker(int windowLines = 60, int overlapLines = 15)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowLines, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapLines);

        if (overlapLines >= windowLines)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlapLines),
                overlapLines,
                $"Overlap must be smaller than the window ({windowLines}); otherwise the " +
                "window never advances and chunking does not terminate.");
        }

        _windowLines = windowLines;
        _overlapLines = overlapLines;
    }

    /// <summary>Lines the window advances between chunks.</summary>
    private int Stride => _windowLines - _overlapLines;

    /// <summary>
    /// Splits <paramref name="content"/> into chunks.
    /// </summary>
    /// <returns>
    /// Chunks in file order. A file shorter than one window yields exactly one
    /// chunk; empty or whitespace-only content yields none, because there is
    /// nothing to retrieve and an empty chunk would only dilute rankings.
    /// </returns>
    public IReadOnlyList<TextChunk> Chunk(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var lines = SplitLines(content);
        if (lines.Length == 0)
        {
            return [];
        }

        var chunks = new List<TextChunk>();
        var ordinal = 0;

        for (var start = 0; start < lines.Length; start += Stride)
        {
            var end = Math.Min(start + _windowLines, lines.Length);

            chunks.Add(new TextChunk(
                ordinal++,
                string.Join('\n', lines[start..end]),
                start + 1,      // 1-based, inclusive
                end));          // already 1-based because end is exclusive here

            if (end == lines.Length)
            {
                // The last window reached the end. Without this the loop would
                // emit further chunks that only repeat the tail, inflating the
                // index with duplicates that all match the same query.
                break;
            }
        }

        return chunks;
    }

    /// <summary>
    /// Splits on newlines, normalising CRLF and CR so a Windows-authored file
    /// produces the same line numbers as a Unix-authored one. A trailing newline
    /// does not create a final empty line.
    /// </summary>
    private static string[] SplitLines(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
                                .Replace('\r', '\n');

        var lines = normalized.Split('\n');

        return lines.Length > 0 && lines[^1].Length == 0
            ? lines[..^1]
            : lines;
    }
}
