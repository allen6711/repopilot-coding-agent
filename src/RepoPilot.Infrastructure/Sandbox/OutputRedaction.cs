using System.Text;
using RepoPilot.Domain.Security;

namespace RepoPilot.Infrastructure.Sandbox;

/// <summary>
/// Prepares sandbox output for storage, display, and re-entry into model
/// context (FR-025b).
/// <para>
/// This runs once, at the boundary where output leaves the container, rather
/// than at each of the three places it is later used. Redacting at the point of
/// use would mean three chances to forget, and the stored copy — the one an
/// audit reads — would be the unredacted one.
/// </para>
/// </summary>
public static class OutputRedaction
{
    /// <summary>Ceiling on stored output; beyond this the tail is dropped.</summary>
    public const int MaxCharacters = 200_000;

    private const string TruncationNotice = "\n[output truncated]";

    /// <summary>Marks output that was cut short by the execution time limit.</summary>
    public const string TimeoutNotice = "[terminated: execution time limit reached]";

    /// <summary>
    /// Combines the two streams, redacts, and bounds the result.
    /// </summary>
    /// <param name="stdout">Standard output, as produced.</param>
    /// <param name="stderr">Standard error, as produced.</param>
    /// <param name="timedOut">Whether the limit stopped the command.</param>
    public static string Prepare(string? stdout, string? stderr, bool timedOut)
    {
        var combined = new StringBuilder();

        if (!string.IsNullOrEmpty(stdout))
        {
            combined.Append(stdout);
        }

        if (!string.IsNullOrEmpty(stderr))
        {
            if (combined.Length > 0)
            {
                combined.Append('\n');
            }

            combined.Append(stderr);
        }

        if (timedOut)
        {
            if (combined.Length > 0)
            {
                combined.Append('\n');
            }

            combined.Append(TimeoutNotice);
        }

        // Redaction precedes truncation. The other order could cut a secret in
        // half and store a fragment that no longer matches any pattern.
        var redacted = SecretRedactor.Redact(combined.ToString());

        if (redacted.Length <= MaxCharacters)
        {
            return redacted;
        }

        // The head is kept: a failing suite prints what failed before it prints
        // the noise that made the output large.
        return string.Concat(redacted.AsSpan(0, MaxCharacters), TruncationNotice);
    }
}
