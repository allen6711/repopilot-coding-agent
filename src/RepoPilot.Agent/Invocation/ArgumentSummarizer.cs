using System.Text;
using System.Text.Json;
using RepoPilot.Domain.Security;

namespace RepoPilot.Agent.Invocation;

/// <summary>
/// Turns raw capability arguments into the summary that is persisted and shown
/// (FR-027a).
/// <para>
/// Two rules, and both exist because this text outlives the run. File contents
/// are replaced by a shape — <c>{path, byteCount}</c> — rather than stored,
/// because an audit trail that contains every proposed file is a second copy of
/// the repository with none of its access controls. And anything matching a
/// credential shape is redacted, because a secret that reaches this table has
/// been written somewhere nobody thinks to look for secrets.
/// </para>
/// <para>
/// The output is JSON so it can live in a <c>jsonb</c> column and be read back
/// by field. A summary that were free text would be searchable but not
/// queryable, and every consumer would end up parsing it differently.
/// </para>
/// </summary>
public static class ArgumentSummarizer
{
    /// <summary>Ceiling on the stored summary.</summary>
    public const int MaxLength = 1_024;

    /// <summary>
    /// Argument names whose values are file content rather than a parameter.
    /// Replaced by a byte count wherever they appear.
    /// </summary>
    private static readonly string[] ContentFields =
        ["new_content", "content", "old_content", "text", "body"];

    /// <summary>
    /// Summarizes raw JSON arguments.
    /// </summary>
    /// <returns>A bounded, redacted JSON object. Never the original arguments.</returns>
    public static string Summarize(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);

            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteSummarized(writer, document.RootElement, propertyName: null);
            }

            var summary = Encoding.UTF8.GetString(buffer.ToArray());

            // Redaction runs over the finished object rather than per value, so
            // a secret split across the structure — a token in a key, say — is
            // still caught.
            return Bound(SecretRedactor.Redact(summary));
        }
        catch (JsonException)
        {
            // Not valid JSON. It still has to be recorded: a malformed call is
            // exactly the kind of thing an audit reader wants to see, and
            // dropping it would make the failure invisible.
            return Bound(
                JsonSerializer.Serialize(new
                {
                    unparsed = SecretRedactor.Redact(Truncate(argumentsJson!, 200)),
                }));
        }
    }

    private static void WriteSummarized(
        Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteSummarized(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                {
                    WriteSummarized(writer, item, propertyName);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;

                if (propertyName is not null && IsContentField(propertyName))
                {
                    // The shape, not the content. A reviewer needs to know a file
                    // of this size was proposed; the diff is where they read it.
                    writer.WriteNumberValue(Encoding.UTF8.GetByteCount(value));
                }
                else
                {
                    writer.WriteStringValue(Truncate(value, 200));
                }

                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsContentField(string propertyName) =>
        ContentFields.Contains(propertyName, StringComparer.OrdinalIgnoreCase);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string Bound(string summary) =>
        summary.Length <= MaxLength
            ? summary
            // Kept parseable: a truncated JSON object would break every reader,
            // so an oversized summary becomes a valid object saying so.
            : JsonSerializer.Serialize(new
            {
                truncated = true,
                length = summary.Length,
                head = summary[..Math.Min(400, summary.Length)],
            });
}
