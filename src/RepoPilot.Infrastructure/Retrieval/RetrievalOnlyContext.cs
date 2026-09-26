using System.Text;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;

namespace RepoPilot.Infrastructure.Retrieval;

/// <summary>
/// The retrieval-only baseline's context (FR-033).
/// <para>
/// One search on the task description, rendered into the prompt. No second
/// query, no follow-up read: the point of the baseline is to measure what
/// retrieval alone buys, so anything that lets the condition react to what it
/// found would make it a weaker tool-enabled agent rather than a baseline.
/// </para>
/// </summary>
public sealed class RetrievalOnlyContext(HybridRetriever retriever, RetrievalOptions options)
    : IBaselineContextProvider
{
    public async Task<string> RetrieveAsync(
        Guid repositoryId,
        string taskDescription,
        int maxCharacters,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDescription);

        var results = await retriever.SearchAsync(
            repositoryId, taskDescription, options.DefaultResultLimit, documentationOnly: false, ct);

        if (results.Count == 0)
        {
            return string.Empty;
        }

        var rendered = new StringBuilder();
        rendered.AppendLine("Repository excerpts retrieved for this task:");

        foreach (var result in results)
        {
            var block =
                $"\n--- {result.RelativePath}:{result.StartLine}-{result.EndLine} ---\n"
                + result.Content
                + "\n";

            if (rendered.Length + block.Length > maxCharacters)
            {
                // Whole results only. A chunk cut off mid-line looks to the model
                // like the file ends there, and a proposal written against that
                // belief is worse than one written with a result fewer.
                break;
            }

            rendered.Append(block);
        }

        return rendered.ToString();
    }
}
