using RepoPilot.Infrastructure.Sandbox;
using Xunit;

namespace RepoPilot.UnitTests.Infrastructure;

/// <summary>
/// FR-025b — the boundary where container output becomes stored, displayed, and
/// model-visible text.
/// </summary>
public sealed class OutputRedactionTests
{
    [Fact]
    public void ASecretPrintedByTestOutputDoesNotSurvive()
    {
        // Assembled at runtime so the literal is not committed.
        var token = "ghp_" + new string('a', 36);

        var prepared = OutputRedaction.Prepare(
            $"Connecting with token {token}\nFAILED: Orders.Tests.LookupTests", null, timedOut: false);

        Assert.DoesNotContain(token, prepared);

        // The failure itself must still be readable — the whole reason this
        // output re-enters model context is so a revision can act on it.
        Assert.Contains("FAILED: Orders.Tests.LookupTests", prepared);
    }

    [Fact]
    public void StderrIsRedactedToo()
    {
        var secret = "AKIA" + new string('Q', 16);

        var prepared = OutputRedaction.Prepare("ok", $"warning: using {secret}", timedOut: false);

        Assert.DoesNotContain(secret, prepared);
    }

    [Fact]
    public void BothStreamsAreKept()
    {
        var prepared = OutputRedaction.Prepare("from-stdout", "from-stderr", timedOut: false);

        Assert.Contains("from-stdout", prepared);
        Assert.Contains("from-stderr", prepared);
    }

    [Fact]
    public void ATimeoutIsStatedInTheOutput()
    {
        var prepared = OutputRedaction.Prepare("partial progress", null, timedOut: true);

        Assert.Contains("partial progress", prepared);
        Assert.Contains(OutputRedaction.TimeoutNotice, prepared);
    }

    [Fact]
    public void ATimeoutWithNoOutputStillSaysWhatHappened()
    {
        var prepared = OutputRedaction.Prepare(null, null, timedOut: true);

        // A record reading only "" would leave a reviewer unable to tell a
        // silent success from a kill.
        Assert.Equal(OutputRedaction.TimeoutNotice, prepared);
    }

    [Fact]
    public void OversizedOutputIsBoundedAndSaysSo()
    {
        var prepared = OutputRedaction.Prepare(
            new string('x', OutputRedaction.MaxCharacters + 5_000), null, timedOut: false);

        Assert.Contains("[output truncated]", prepared);
        Assert.True(prepared.Length < OutputRedaction.MaxCharacters + 100);
    }

    [Fact]
    public void ASecretIsRedactedEvenWhenTheOutputIsOversized()
    {
        // Truncating first could cut a secret in half, leaving a fragment that
        // no pattern matches and that is then stored verbatim.
        var secret = "ghp_" + new string('b', 36);
        var noise = new string('y', OutputRedaction.MaxCharacters - 10);

        var prepared = OutputRedaction.Prepare(noise + secret + noise, null, timedOut: false);

        Assert.DoesNotContain(secret, prepared);
        Assert.DoesNotContain(secret[..20], prepared);
    }

    [Fact]
    public void EmptyOutputStaysEmpty()
    {
        Assert.Equal(string.Empty, OutputRedaction.Prepare(null, null, timedOut: false));
        Assert.Equal(string.Empty, OutputRedaction.Prepare("", "", timedOut: false));
    }
}
