using RepoPilot.Domain.Security;
using Xunit;

namespace RepoPilot.UnitTests.Domain;

/// <summary>
/// FR-025: credentials never reach the model. This predicate is the single
/// definition applied at all four places content can escape — indexed chunks,
/// file reads, sandbox output, and recorded arguments.
/// </summary>
public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData(".env.production")]
    [InlineData("config/.env.local")]
    [InlineData("keys/server.pem")]
    [InlineData("certs/client.pfx")]
    [InlineData("deploy/private.key")]
    [InlineData(".ssh/id_rsa")]
    [InlineData("id_ed25519")]
    [InlineData("aws-credentials.txt")]
    [InlineData("secrets.json")]
    public void SecretBearingFilenames_AreDetected(string path)
    {
        Assert.True(SecretRedactor.IsSecretPath(path), path);
    }

    [Theory]
    [InlineData("src/Service.cs")]
    [InlineData("README.md")]
    [InlineData("environment.ts")]        // contains "environment", not ".env"
    [InlineData("keyboard/Handler.cs")]   // contains "key", not a .key file
    [InlineData("docs/pemberton.md")]     // contains "pem", not a .pem file
    public void OrdinarySourceFiles_AreNotFlagged(string path)
    {
        Assert.False(SecretRedactor.IsSecretPath(path), path);
    }

    /// <summary>
    /// Test tokens are assembled at run time from fragments rather than written
    /// as literals.
    /// <para>
    /// These values are fabricated, but a secret scanner cannot tell a fixture
    /// from a live credential — it matches on shape. A literal here trips
    /// GitHub push protection and any pre-commit or CI scanner, blocking pushes
    /// over a string that was never a secret. Splitting the prefix from the body
    /// means no complete pattern appears contiguously in this file, while the
    /// runtime value the assertion sees is byte-for-byte what a real one looks
    /// like — which is the only part that matters for testing the detector.
    /// </para>
    /// </summary>
    public static TheoryData<string> KnownTokenShapes() =>
    [
        "AKIA" + "IOSFODNN7EXAMPLE",
        "ghp" + "_1234567890abcdefghijklmnopqrstuvwx",
        "xox" + "b-123456789012-abcdefghijklmnop",
        "sk-" + "ant-api03-abcdefghijklmnopqrstuvwxyz",
        "gl" + "pat-abcdefghij1234567890",
        "-----BEGIN RSA PRIVATE KEY-----",
    ];

    [Theory]
    [MemberData(nameof(KnownTokenShapes))]
    public void KnownTokenShapes_AreDetected(string content)
    {
        Assert.True(SecretRedactor.ContainsSecret(content), content);
    }

    [Theory]
    [InlineData("api_key=abcdef123456")]
    [InlineData("API_KEY = \"abcdef123456\"")]
    [InlineData("\"password\": \"hunter2xyz\"")]
    [InlineData("client_secret: s3cr3t-value-here")]
    [InlineData("AUTH_TOKEN=aaaaaaaaaaaaaaaa")]
    public void CredentialAssignments_AreDetected(string content)
    {
        Assert.True(SecretRedactor.ContainsSecret(content), content);
    }

    [Theory]
    [InlineData("var count = 42;")]
    [InlineData("// the keyboard shortcut is ctrl+k")]
    [InlineData("public sealed record Order(string Id);")]
    public void OrdinaryCode_IsNotFlagged(string content)
    {
        Assert.False(SecretRedactor.ContainsSecret(content), content);
    }

    [Fact]
    public void Redact_RemovesTheValueButKeepsTheSurroundingText()
    {
        // A redacted audit record still has to be readable, so the key stays.
        const string input = "connection: api_key=abcdef123456 endpoint=https://example.test";

        var redacted = SecretRedactor.Redact(input);

        Assert.DoesNotContain("abcdef123456", redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
        Assert.Contains("endpoint=https://example.test", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_HandlesMultipleSecretsInOneDocument()
    {
        var awsKey = "AKIA" + "IOSFODNN7EXAMPLE";
        var githubToken = "ghp" + "_1234567890abcdefghijklmnopqrstuvwx";
        var input = $"""
            AWS_KEY={awsKey}
            GITHUB={githubToken}
            harmless = 5
            """;

        var redacted = SecretRedactor.Redact(input);

        Assert.DoesNotContain(awsKey, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(githubToken, redacted, StringComparison.Ordinal);
        Assert.Contains("harmless = 5", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_IsIdempotent()
    {
        var input = "token=" + "ghp" + "_1234567890abcdefghijklmnopqrstuvwx";

        var once = SecretRedactor.Redact(input);
        var twice = SecretRedactor.Redact(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Redact_OnCleanText_ChangesNothing()
    {
        const string input = "public int Total => _items.Sum(i => i.Price);";

        Assert.Equal(input, SecretRedactor.Redact(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyInput_IsHandledWithoutThrowing(string? input)
    {
        Assert.False(SecretRedactor.ContainsSecret(input));
        Assert.Equal(string.Empty, SecretRedactor.Redact(input));
    }
}
