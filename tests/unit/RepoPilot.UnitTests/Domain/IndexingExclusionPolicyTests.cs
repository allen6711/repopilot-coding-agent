using RepoPilot.Domain.Indexing;
using Xunit;

namespace RepoPilot.UnitTests.Domain;

/// <summary>
/// FR-002 and FR-003b. The acceptance scenario behind these is concrete: a
/// repository containing binaries, build output, dependency directories, and
/// oversized files must produce none of them in search results.
/// </summary>
public sealed class IndexingExclusionPolicyTests
{
    private static IndexingExclusionPolicy Policy(
        int maxBytes = 262_144,
        params string[] extraGlobs) => new(maxBytes, extraGlobs);

    [Theory]
    [InlineData("node_modules/react/index.js")]
    [InlineData("src/bin/Debug/App.dll")]
    [InlineData("obj/project.assets.json")]
    [InlineData(".git/config")]
    [InlineData("target/classes/Main.class")]
    [InlineData("web/dist/bundle.js")]
    [InlineData("__pycache__/module.cpython-313.pyc")]
    public void DependencyAndBuildDirectories_AreExcluded(string path)
    {
        var verdict = Policy().Evaluate(path, 100, "content");

        Assert.False(verdict.IsIncluded);
        Assert.Equal(ExclusionReason.ExcludedDirectory, verdict.Reason);
    }

    [Fact]
    public void ADirectoryNameAppearingAsAFileName_IsNotExcluded()
    {
        // "build" as a file, not a directory, is ordinary source.
        var verdict = Policy().Evaluate("scripts/build", 100, "#!/bin/sh");

        Assert.True(verdict.IsIncluded);
    }

    [Fact]
    public void OversizedFiles_AreExcluded()
    {
        var verdict = Policy(maxBytes: 1024).Evaluate("src/Generated.cs", 2048, "content");

        Assert.False(verdict.IsIncluded);
        Assert.Equal(ExclusionReason.Size, verdict.Reason);
    }

    [Fact]
    public void AFileExactlyAtTheLimit_IsIncluded()
    {
        // The limit is "exceeding", so the boundary value is allowed.
        var verdict = Policy(maxBytes: 1024).Evaluate("src/Service.cs", 1024, "content");

        Assert.True(verdict.IsIncluded);
    }

    [Fact]
    public void BinaryContent_IsExcluded()
    {
        var verdict = Policy().Evaluate("assets/logo.png", 100, "PNG\0data");

        Assert.False(verdict.IsIncluded);
        Assert.Equal(ExclusionReason.Binary, verdict.Reason);
    }

    [Fact]
    public void SecretBearingFilename_IsExcluded()
    {
        var verdict = Policy().Evaluate("config/.env.production", 100, "PORT=8080");

        Assert.False(verdict.IsIncluded);
        Assert.Equal(ExclusionReason.SecretFilename, verdict.Reason);
    }

    [Fact]
    public void SecretBearingContent_ExcludesTheWholeFile()
    {
        // Not redacted — dropped. Partial redaction has false negatives, and at
        // fixture scale exclusion costs nothing.
        // Assembled at run time so no complete credential shape appears in this
        // file — see the note in SecretRedactorTests. A scanner matches on shape
        // and cannot tell a fixture from a live key.
        var awsKey = "AKIA" + "IOSFODNN7EXAMPLE";

        var verdict = Policy().Evaluate(
            "src/Config.cs", 100, $"const string Key = \"{awsKey}\";");

        Assert.False(verdict.IsIncluded);
        Assert.Equal(ExclusionReason.SecretContent, verdict.Reason);
    }

    [Fact]
    public void OrdinarySource_IsIncluded()
    {
        var verdict = Policy().Evaluate(
            "src/Orders/OrderLookupService.cs", 900, "public sealed class OrderLookupService { }");

        Assert.True(verdict.IsIncluded);
        Assert.Equal(ExclusionReason.None, verdict.Reason);
    }

    [Fact]
    public void FixtureGlobs_CanNarrowIndexing()
    {
        var verdict = Policy(262_144, "**/Generated/**").Evaluate(
            "src/Generated/Api.g.cs", 100, "// generated");

        Assert.False(verdict.IsIncluded);
    }

    [Fact]
    public void FixtureGlobs_CannotWidenIndexing()
    {
        // FR-003b: a fixture may narrow what is indexed, never widen it. The
        // policy takes only exclusions, so there is no configuration shape that
        // could re-include a secret or a dependency directory. This test asserts
        // the API has no such affordance rather than that a flag is ignored.
        var policy = Policy(262_144, "**/*.md");

        Assert.False(policy.Evaluate("node_modules/pkg/index.js", 10, "x").IsIncluded);
        Assert.False(policy.Evaluate("config/.env", 10, "x").IsIncluded);
        Assert.False(policy.Evaluate("src/Big.cs", 999_999, "x").IsIncluded);
    }

    [Fact]
    public void DirectoryExclusion_IsReportedAheadOfContentReasons()
    {
        // A vendored file that also looks secret is reported as a directory
        // exclusion, because that is the reason an operator can act on.
        var verdict = Policy().Evaluate(
            "node_modules/pkg/.env", 100, "api_key=abcdef123456");

        Assert.Equal(ExclusionReason.ExcludedDirectory, verdict.Reason);
    }

    [Fact]
    public void ContentChecksAreSkipped_WhenNoSampleIsSupplied()
    {
        // Callers that have already excluded on cheaper grounds pass null rather
        // than reading the file.
        var verdict = Policy().Evaluate("src/Service.cs", 100, contentSample: null);

        Assert.True(verdict.IsIncluded);
    }

    [Fact]
    public void LooksBinary_DetectsNullBytesOnly()
    {
        Assert.True(IndexingExclusionPolicy.LooksBinary("abc\0def"));
        Assert.False(IndexingExclusionPolicy.LooksBinary("plain text with émojis 🎉"));
    }
}
