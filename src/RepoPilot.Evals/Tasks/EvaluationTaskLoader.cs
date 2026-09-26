using System.Text.Json;
using RepoPilot.Application.Schemas;

namespace RepoPilot.Evals.Tasks;

/// <summary>
/// Raised when the committed task set cannot be loaded as written.
/// </summary>
public sealed class EvaluationTaskSetException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Loads the committed task set from disk, validating every definition against
/// the committed schema (FR-031).
/// <para>
/// Two things it deliberately does not do. It does not recurse: reference
/// solutions live in <c>_reference/</c> beneath this same directory, and a
/// recursive walk would be one rename away from loading one as a task (FR-035).
/// And it does not skip a malformed file — a task set that silently drops a
/// definition reports metrics over a different denominator than the one
/// committed, which is worse than not reporting at all.
/// </para>
/// </summary>
public sealed class EvaluationTaskLoader(string tasksDirectory)
{
    /// <summary>The directory the set is read from.</summary>
    public string TasksDirectory { get; } = Path.GetFullPath(tasksDirectory);

    /// <summary>
    /// Loads every committed task, ordered by id so two evaluations over an
    /// unchanged set execute in the same order (SC-007).
    /// </summary>
    /// <exception cref="EvaluationTaskSetException">
    /// The directory is missing, a definition fails schema validation, or two
    /// definitions claim the same id.
    /// </exception>
    public async Task<IReadOnlyList<EvaluationTaskDefinition>> LoadAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(TasksDirectory))
        {
            throw new EvaluationTaskSetException(
                $"Evaluation task directory not found: {TasksDirectory}");
        }

        // Top level only. See the type comment: _reference/ is a sibling of the
        // task files, not a place to look for them.
        var files = Directory.GetFiles(TasksDirectory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);

        var loaded = new List<EvaluationTaskDefinition>(files.Length);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var json = await File.ReadAllTextAsync(file, ct);

            SchemaValidator.ValidateOrThrow(
                json, SchemaKind.EvaluationTask, $"Evaluation task '{Path.GetFileName(file)}'");

            var task = Parse(json, file);

            if (seen.TryGetValue(task.Id, out var firstFile))
            {
                throw new EvaluationTaskSetException(
                    $"Task id '{task.Id}' is claimed by both {Path.GetFileName(firstFile)} and " +
                    $"{Path.GetFileName(file)}. Ids identify a task across evaluations, so a " +
                    "duplicate makes results incomparable.");
            }

            seen[task.Id] = file;
            loaded.Add(task);
        }

        return [.. loaded.OrderBy(t => t.Id, StringComparer.Ordinal)];
    }

    private static EvaluationTaskDefinition Parse(string json, string file)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new EvaluationTaskSetException($"{Path.GetFileName(file)} is not valid JSON.", ex);
        }

        using var _ = document;
        var root = document.RootElement;

        // Every field read here is already known to be present and well shaped:
        // schema validation ran before this point and throws on anything else.
        return new EvaluationTaskDefinition(
            Id: root.GetProperty("id").GetString()!,
            RepositorySlug: root.GetProperty("repositorySlug").GetString()!,
            Category: ParseCategory(root.GetProperty("category").GetString()!, file),
            Description: root.GetProperty("description").GetString()!,
            RelevantFiles: ReadStringArray(root, "relevantFiles"),
            BaselineTestCommand: root.GetProperty("baselineTestCommand").GetString()!,
            SuccessTestCommand: root.GetProperty("successTestCommand").GetString()!,
            SuccessCondition: ParseCondition(root.GetProperty("successCondition"), file),
            ExpectedChangedFiles: ReadStringArray(root, "expectedChangedFiles"),
            ReferencePatch: root.TryGetProperty("referencePatch", out var reference)
                ? reference.GetString()
                : null);
    }

    private static EvaluationTaskCategory ParseCategory(string value, string file) => value switch
    {
        "bug_fix" => EvaluationTaskCategory.BugFix,
        "input_validation" => EvaluationTaskCategory.InputValidation,
        "api_behavior" => EvaluationTaskCategory.ApiBehavior,
        "refactor" => EvaluationTaskCategory.Refactor,
        "test_work" => EvaluationTaskCategory.TestWork,

        // Unreachable while the schema and this switch agree. Kept because the
        // failure mode if they ever diverge is a task silently scored in the
        // wrong category, which no metric would reveal.
        _ => throw new EvaluationTaskSetException(
            $"{Path.GetFileName(file)} declares unknown category '{value}'."),
    };

    private static SuccessCondition ParseCondition(JsonElement element, string file)
    {
        var type = element.GetProperty("type").GetString()!;

        var kind = type switch
        {
            "tests_pass" => SuccessConditionKind.TestsPass,
            "tests_pass_and_baseline_failed" => SuccessConditionKind.TestsPassAndBaselineFailed,
            "output_contains" => SuccessConditionKind.OutputContains,
            "output_matches" => SuccessConditionKind.OutputMatches,
            _ => throw new EvaluationTaskSetException(
                $"{Path.GetFileName(file)} declares unknown success condition '{type}'."),
        };

        return new SuccessCondition(
            kind,
            Substring: Optional(element, "substring"),
            Pattern: Optional(element, "pattern"),
            Note: Optional(element, "note"));
    }

    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. array.EnumerateArray().Select(e => e.GetString()).OfType<string>()];
    }
}
