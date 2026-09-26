using System.Text;
using System.Text.Json;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Configuration;
using RepoPilot.Domain.Indexing;
using RepoPilot.Domain.Workspace;
using RepoPilot.Infrastructure.Retrieval;

namespace RepoPilot.Agent.Capabilities;

/// <summary>
/// Shared argument parsing for the read capabilities.
/// </summary>
internal static class Arguments
{
    public static JsonElement Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // Model-produced arguments are untrusted input like any other. A
            // malformed payload is refused with a message the model can act on
            // rather than crashing the run.
            throw new ArgumentException($"Arguments were not valid JSON: {ex.Message}", nameof(json));
        }
    }

    public static string RequireString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"Required argument '{name}' is missing.");

    public static string? OptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? OptionalInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}

/// <summary>
/// Lists repository files (permission class: read).
/// </summary>
public sealed class ListFilesCapability(IndexingOptions indexing) : ICapability
{
    public string Name => "list_files";

    public Task<CapabilityResult> InvokeAsync(
        CapabilityContext context, string argumentsJson, CancellationToken ct = default)
    {
        var args = Arguments.Parse(argumentsJson);
        var directory = Arguments.OptionalString(args, "directory") ?? ".";
        var glob = Arguments.OptionalString(args, "glob");
        var maxResults = Math.Clamp(Arguments.OptionalInt(args, "max_results") ?? 200, 1, 500);

        var resolved = PathGuard.Resolve(context.Workspace, directory, AccessIntent.Read);
        if (!resolved.IsAllowed)
        {
            throw new PathAccessRefusedException(directory, AccessIntent.Read, resolved.Reason);
        }

        var policy = new IndexingExclusionPolicy(indexing.MaxFileBytes);
        var paths = new List<string>();
        var truncated = false;

        foreach (var absolute in Directory.EnumerateFiles(
            resolved.FullPath!, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(context.Workspace.FullPath, absolute)
                .Replace('\\', '/');

            // The same exclusion set indexing uses. Listing a secret-bearing file
            // would tell the model it exists and invite a read, which the read
            // capability would then refuse — better not to name it at all.
            if (!policy.Evaluate(relative, new FileInfo(absolute).Length, null).IsIncluded)
            {
                continue;
            }

            if (glob is not null && !MatchesGlob(glob, relative))
            {
                continue;
            }

            if (paths.Count >= maxResults)
            {
                truncated = true;
                break;
            }

            paths.Add(relative);
        }

        paths.Sort(StringComparer.Ordinal);
        var content = JsonSerializer.Serialize(new { paths, truncated });

        return Task.FromResult(new CapabilityResult(
            content, content.Length, $$"""{"directory":"{{directory}}","matched":{{paths.Count}}}"""));
    }

    private static bool MatchesGlob(string pattern, string path)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            path, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(500));
    }
}

/// <summary>
/// Reads bounded file content (permission class: read).
/// </summary>
public sealed class ReadFileCapability(IndexingOptions indexing) : ICapability
{
    public string Name => "read_file";

    public async Task<CapabilityResult> InvokeAsync(
        CapabilityContext context, string argumentsJson, CancellationToken ct = default)
    {
        var args = Arguments.Parse(argumentsJson);
        var path = Arguments.RequireString(args, "path");
        var startLine = Arguments.OptionalInt(args, "start_line");
        var endLine = Arguments.OptionalInt(args, "end_line");

        var resolved = PathGuard.Resolve(context.Workspace, path, AccessIntent.Read);
        if (!resolved.IsAllowed)
        {
            throw new PathAccessRefusedException(path, AccessIntent.Read, resolved.Reason);
        }

        var info = new FileInfo(resolved.FullPath!);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"'{path}' does not exist in the workspace.", path);
        }

        // Checked before reading, so an oversized file is never loaded.
        if (info.Length > indexing.MaxFileBytes)
        {
            throw new InvalidOperationException(
                $"'{path}' is {info.Length} bytes, above the {indexing.MaxFileBytes}-byte limit.");
        }

        var text = await File.ReadAllTextAsync(resolved.FullPath!, ct);

        var policy = new IndexingExclusionPolicy(indexing.MaxFileBytes);
        var verdict = policy.Evaluate(path, info.Length, text);
        if (!verdict.IsIncluded)
        {
            // The same predicate as indexing. A file excluded from the index for
            // carrying a credential must not be readable by another route
            // (FR-025a).
            throw new InvalidOperationException(
                $"'{path}' is excluded from agent access ({verdict.Reason}).");
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var from = Math.Max(startLine ?? 1, 1);
        var to = Math.Min(endLine ?? lines.Length, lines.Length);

        var numbered = new StringBuilder();
        for (var i = from; i <= to; i++)
        {
            numbered.Append(i).Append(": ").AppendLine(lines[i - 1]);
        }

        var content = numbered.ToString();
        return new CapabilityResult(
            content, content.Length, $$"""{"path":"{{path}}","lines":"{{from}}-{{to}}"}""");
    }
}

/// <summary>
/// Searches indexed source (permission class: read).
/// </summary>
public sealed class SearchCodeCapability(HybridRetriever retriever) : ICapability
{
    public string Name => "search_code";

    public Task<CapabilityResult> InvokeAsync(
        CapabilityContext context, string argumentsJson, CancellationToken ct = default) =>
        SearchCapability.RunAsync(retriever, context, argumentsJson, documentationOnly: false, ct);
}

/// <summary>
/// Searches indexed documentation (permission class: read).
/// </summary>
public sealed class SearchDocsCapability(HybridRetriever retriever) : ICapability
{
    public string Name => "search_docs";

    public Task<CapabilityResult> InvokeAsync(
        CapabilityContext context, string argumentsJson, CancellationToken ct = default) =>
        SearchCapability.RunAsync(retriever, context, argumentsJson, documentationOnly: true, ct);
}

internal static class SearchCapability
{
    public static async Task<CapabilityResult> RunAsync(
        HybridRetriever retriever,
        CapabilityContext context,
        string argumentsJson,
        bool documentationOnly,
        CancellationToken ct)
    {
        var args = Arguments.Parse(argumentsJson);
        var query = Arguments.RequireString(args, "query");
        var limit = Arguments.OptionalInt(args, "limit");

        var results = await retriever.SearchAsync(
            context.RepositoryId, query, limit, documentationOnly, ct);

        // Every field FR-004 requires, in the shape the contract documents.
        var payload = results.Select(r => new
        {
            relative_path = r.RelativePath,
            chunk_id = r.ChunkId,
            content = r.Content,
            start_line = r.StartLine,
            end_line = r.EndLine,
            score = r.Score,
            language = r.Language,
        });

        var content = JsonSerializer.Serialize(payload);

        return new CapabilityResult(
            content, content.Length, $$"""{"query":"{{JsonEncodedText.Encode(query)}}","results":{{results.Count}}}""");
    }
}
