using System.Text.Json;
using RepoPilot.Agent.Invocation;
using Xunit;

namespace RepoPilot.UnitTests.Agent;

/// <summary>
/// FR-027a: what an audit record is allowed to contain.
/// </summary>
public sealed class ArgumentSummarizerTests
{
    [Fact]
    public void FileContentIsReplacedByItsSize()
    {
        var content = new string('x', 5_000);

        var summary = ArgumentSummarizer.Summarize(
            $$"""{"path":"src/Service.cs","operation":"modify","new_content":"{{content}}"}""");

        // The shape, not the content. Storing every proposed file would make the
        // event table a second copy of the repository with none of its access
        // controls.
        Assert.DoesNotContain("xxxxx", summary, StringComparison.Ordinal);
        Assert.Contains("5000", summary, StringComparison.Ordinal);

        // What identifies the change is kept.
        Assert.Contains("src/Service.cs", summary, StringComparison.Ordinal);
        Assert.Contains("modify", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentIsReplacedWhereverItAppears()
    {
        var summary = ArgumentSummarizer.Summarize(
            """
            {"entries":[
              {"path":"a.cs","new_content":"aaaa"},
              {"path":"b.cs","new_content":"bbbbbbbb"}
            ]}
            """);

        // Nested inside an array, which is exactly where propose_patch puts it.
        Assert.DoesNotContain("aaaa", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("bbbb", summary, StringComparison.Ordinal);
        Assert.Contains("a.cs", summary, StringComparison.Ordinal);
        Assert.Contains("b.cs", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryStringsAreKept()
    {
        var summary = ArgumentSummarizer.Summarize("""{"query":"order lookup null guard"}""");

        // A search whose query is hidden tells a reviewer nothing about what the
        // agent was looking for, which is most of the value of the record.
        Assert.Contains("order lookup null guard", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsAreRedactedEvenOutsideContentFields()
    {
        var token = "ghp_" + new string('b', 36);

        var summary = ArgumentSummarizer.Summarize($$"""{"query":"find {{token}} usage"}""");

        Assert.DoesNotContain(token, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryIsAlwaysParseableJson()
    {
        string[] inputs =
        [
            """{"path":"a.cs"}""",
            """{"entries":[{"path":"a.cs","new_content":"x"}]}""",
            new string('x', 10_000),
            "not json at all",
            "",
        ];

        foreach (var input in inputs)
        {
            var summary = ArgumentSummarizer.Summarize(input);

            // Stored in a jsonb column and read back by field. A summary that
            // were sometimes not JSON would fail at insert, losing the audit
            // record for exactly the malformed calls worth recording.
            var exception = Record.Exception(() => JsonDocument.Parse(summary).Dispose());
            Assert.Null(exception);
        }
    }

    [Fact]
    public void UnparseableArgumentsAreStillRecorded()
    {
        var summary = ArgumentSummarizer.Summarize("{ this is not valid json");

        // A malformed call is the kind of thing an audit reader wants to see.
        // Dropping it would make the failure invisible.
        using var parsed = JsonDocument.Parse(summary);
        Assert.True(parsed.RootElement.TryGetProperty("unparsed", out _));
    }

    [Fact]
    public void OversizedSummariesStayBoundedAndSayThatTheyWereCut()
    {
        var many = string.Join(",", Enumerable.Range(0, 400).Select(i => $"\"k{i}\":\"{new string('v', 50)}\""));

        var summary = ArgumentSummarizer.Summarize("{" + many + "}");

        Assert.True(summary.Length <= ArgumentSummarizer.MaxLength);

        using var parsed = JsonDocument.Parse(summary);
        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void EmptyArgumentsSummarizeToAnEmptyObject()
    {
        Assert.Equal("{}", ArgumentSummarizer.Summarize(null));
        Assert.Equal("{}", ArgumentSummarizer.Summarize(""));
        Assert.Equal("{}", ArgumentSummarizer.Summarize("   "));
    }

    [Fact]
    public void NonStringValuesSurviveUnchanged()
    {
        var summary = ArgumentSummarizer.Summarize(
            """{"start_line":10,"end_line":40,"recursive":true,"cursor":null}""");

        using var parsed = JsonDocument.Parse(summary);

        Assert.Equal(10, parsed.RootElement.GetProperty("start_line").GetInt32());
        Assert.True(parsed.RootElement.GetProperty("recursive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("cursor").ValueKind);
    }
}
