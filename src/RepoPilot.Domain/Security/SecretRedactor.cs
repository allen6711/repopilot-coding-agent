using System.Text.RegularExpressions;

namespace RepoPilot.Domain.Security;

/// <summary>
/// The normative definition of a secret (FR-025a), applied identically wherever
/// content can reach the model or a reviewer.
/// <para>
/// There are four such places, and they all route here: indexed chunks, bounded
/// file reads, sandbox output returned for a revision attempt (FR-025b), and
/// recorded action argument summaries (FR-027a). A separate predicate at any one
/// of them would be a place for the definition to drift.
/// </para>
/// <para>
/// Detection is deliberately conservative in the direction that matters: a false
/// positive costs a redacted string, a false negative puts a credential in a
/// model context or an audit record. Exclusion beats redaction where the whole
/// file is suspect, which is why <see cref="IsSecretPath"/> exists separately.
/// </para>
/// </summary>
public static partial class SecretRedactor
{
    /// <summary>What replaces a detected secret.</summary>
    public const string Placeholder = "[redacted]";

    private static readonly string[] SecretFileNames =
    [
        ".env", ".envrc", "credentials", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
        ".npmrc", ".pypirc", ".netrc", "secrets.json",
    ];

    private static readonly string[] SecretExtensions =
    [
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".ppk",
    ];

    [GeneratedRegex(
        // Private key blocks and provider-issued tokens with recognisable
        // prefixes. Literal spaces are written as \x20 because
        // IgnorePatternWhitespace strips unescaped whitespace outside character
        // classes — an unescaped space here silently matches nothing.
        """
        (-----BEGIN[\x20A-Z]*PRIVATE\x20KEY-----)
        |(\bAKIA[0-9A-Z]{16}\b)
        |(\bASIA[0-9A-Z]{16}\b)
        |(\bgh[pousr]_[A-Za-z0-9]{16,}\b)
        |(\bglpat-[A-Za-z0-9_\-]{16,}\b)
        |(\bxox[baprs]-[A-Za-z0-9\-]{10,}\b)
        |(\bsk-[A-Za-z0-9\-]{16,}\b)
        |(\bAIza[0-9A-Za-z_\-]{28,}\b)
        |(\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b)
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(
        // KEY=value / "key": "value" where the key names a credential. The value
        // is captured so it can be replaced without losing the key, which is
        // what keeps a redacted audit record still readable.
        """
        (?<key>\b(?:api[_\-]?key|apikey|secret|password|passwd|pwd|token|access[_\-]?key
        |private[_\-]?key|client[_\-]?secret|auth[_\-]?token|bearer)\b)
        # The optional closing quote handles JSON-style "password": "value",
        # where the key's own quote sits between the key and the separator.
        (?<sep>"?\s*[:=]\s*"?)
        # Skip a value that is already the placeholder, so redaction is
        # idempotent — otherwise a second pass would redact "[redacted".
        (?<value>(?!\[redacted\])[^\s"',;)}\]]{4,})
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex AssignmentPattern();

    /// <summary>
    /// Whether a file is secret-bearing by name alone, and so must be excluded
    /// from indexing entirely rather than redacted.
    /// </summary>
    /// <param name="relativePath">Repository-relative path, forward slashes.</param>
    public static bool IsSecretPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var fileName = relativePath.Split('/', '\\')[^1];

        foreach (var extension in SecretExtensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var name in SecretFileNames)
        {
            // Matches ".env" and ".env.production", "id_rsa" and "id_rsa.bak".
            if (fileName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // "credentials" appearing anywhere in the name, e.g. aws-credentials.txt.
        return fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="content"/> appears to contain a secret.</summary>
    public static bool ContainsSecret(string? content) =>
        !string.IsNullOrEmpty(content) &&
        (TokenPattern().IsMatch(content) || AssignmentPattern().IsMatch(content));

    /// <summary>
    /// Replaces detected secrets with <see cref="Placeholder"/>, preserving the
    /// surrounding text so the result is still useful to read.
    /// </summary>
    /// <returns>The redacted text, or the original when nothing was detected.</returns>
    public static string Redact(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        var result = TokenPattern().Replace(content, Placeholder);
        result = AssignmentPattern().Replace(
            result,
            match => match.Groups["key"].Value + match.Groups["sep"].Value + Placeholder);

        return result;
    }
}
