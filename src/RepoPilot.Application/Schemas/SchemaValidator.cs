using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace RepoPilot.Application.Schemas;

/// <summary>
/// Which committed schema a document is validated against.
/// </summary>
public enum SchemaKind
{
    /// <summary>A fixture's <c>repopilot.fixture.json</c>.</summary>
    FixtureConfig,

    /// <summary>A committed evaluation task definition.</summary>
    EvaluationTask,
}

/// <summary>
/// One validation failure, with the JSON pointer that produced it.
/// </summary>
/// <param name="Location">JSON pointer into the validated document.</param>
/// <param name="Message">Human-readable reason.</param>
public readonly record struct SchemaError(string Location, string Message)
{
    public override string ToString() => $"{Location}: {Message}";
}

/// <summary>
/// Validates fixture configurations and evaluation task definitions against the
/// schemas committed under <c>specs/.../contracts/</c> and embedded here.
/// <para>
/// This is a governance boundary, not a convenience: a fixture's configuration is
/// the sole source of commands the sandbox may ever execute (FR-022), so a
/// malformed or widened configuration must be refused at registration rather
/// than discovered when a container is already running.
/// </para>
/// </summary>
public static class SchemaValidator
{
    private static readonly Lazy<JsonSchema> FixtureConfigSchema =
        new(() => Load("fixture-config.schema.json"));

    private static readonly Lazy<JsonSchema> EvaluationTaskSchema =
        new(() => Load("eval-task.schema.json"));

    // Hierarchical output keeps the nested detail tree, which is what lets a
    // failure be reported against the JSON pointer that actually caused it
    // rather than against the document root.
    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.Hierarchical,
        RequireFormatValidation = true,
    };

    /// <summary>
    /// Validates <paramref name="json"/> against the schema for <paramref name="kind"/>.
    /// </summary>
    /// <returns>An empty list when the document is valid.</returns>
    public static IReadOnlyList<SchemaError> Validate(string json, SchemaKind kind)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return [new SchemaError("#", $"Document is not valid JSON: {ex.Message}")];
        }

        using var _ = document;

        var schema = kind switch
        {
            SchemaKind.FixtureConfig => FixtureConfigSchema.Value,
            SchemaKind.EvaluationTask => EvaluationTaskSchema.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown schema kind."),
        };

        var result = schema.Evaluate(document.RootElement, Options);
        if (result.IsValid)
        {
            return [];
        }

        var errors = new List<SchemaError>();
        Collect(result, errors);

        // A schema can report a failure without an attached message when it fails
        // only through a nested applicator. Never return "invalid" with nothing
        // to act on.
        if (errors.Count == 0)
        {
            errors.Add(new SchemaError("#", "Document does not satisfy the schema."));
        }

        return errors;
    }

    /// <summary>
    /// Validates and throws when the document is invalid. Use at trust boundaries
    /// where continuing with an unvalidated document is not acceptable.
    /// </summary>
    /// <exception cref="SchemaValidationException">The document is invalid.</exception>
    public static void ValidateOrThrow(string json, SchemaKind kind, string sourceDescription)
    {
        var errors = Validate(json, kind);
        if (errors.Count > 0)
        {
            throw new SchemaValidationException(sourceDescription, errors);
        }
    }

    private static void Collect(EvaluationResults result, List<SchemaError> errors)
    {
        if (!result.IsValid && result.Errors is { Count: > 0 })
        {
            var location = result.InstanceLocation.ToString();
            if (string.IsNullOrEmpty(location))
            {
                location = "#";
            }

            foreach (var error in result.Errors)
            {
                errors.Add(new SchemaError(location, error.Value));
            }
        }

        if (result.Details is not null)
        {
            foreach (var detail in result.Details)
            {
                Collect(detail, errors);
            }
        }
    }

    private static JsonSchema Load(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"RepoPilot.Application.Schemas.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema '{resourceName}' was not found. Available resources: " +
                string.Join(", ", assembly.GetManifestResourceNames()));

        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}

/// <summary>
/// Raised when a document fails schema validation at a trust boundary.
/// </summary>
public sealed class SchemaValidationException(
    string sourceDescription,
    IReadOnlyList<SchemaError> errors)
    : Exception(BuildMessage(sourceDescription, errors))
{
    /// <summary>The validation failures, in document order.</summary>
    public IReadOnlyList<SchemaError> Errors { get; } = errors;

    private static string BuildMessage(string sourceDescription, IReadOnlyList<SchemaError> errors)
    {
        var detail = string.Join("; ", errors.Select(e => e.ToString()));
        return $"{sourceDescription} failed schema validation: {detail}";
    }
}
