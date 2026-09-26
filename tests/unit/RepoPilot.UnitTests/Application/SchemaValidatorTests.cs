using RepoPilot.Application.Schemas;
using Xunit;

namespace RepoPilot.UnitTests.Application;

/// <summary>
/// The fixture configuration is the sole source of commands the sandbox may
/// execute (FR-022), so these tests are about a governance boundary rather than
/// about JSON handling: the committed artifacts must validate, and a widened or
/// malformed configuration must be refused.
/// </summary>
public sealed class SchemaValidatorTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RepoPilot.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void CommittedFixtureConfiguration_IsValid()
    {
        var path = Path.Combine(
            RepoRoot(), "evals", "fixtures", "sample-dotnet-api", "repopilot.fixture.json");
        var json = File.ReadAllText(path);

        var errors = SchemaValidator.Validate(json, SchemaKind.FixtureConfig);

        Assert.Empty(errors);
    }

    [Fact]
    public void CommittedSeededTask_IsValid()
    {
        var path = Path.Combine(RepoRoot(), "evals", "tasks", "bugfix-null-guard-01.json");
        var json = File.ReadAllText(path);

        var errors = SchemaValidator.Validate(json, SchemaKind.EvaluationTask);

        Assert.Empty(errors);
    }

    [Fact]
    public void FixtureConfiguration_WithNoCommands_IsRefused()
    {
        // An empty allow-list would mean "no command is permitted", but accepting
        // the shape invites a later reading of "no restriction".
        const string json = """
        {
          "slug": "empty-commands",
          "displayName": "Empty",
          "sandbox": { "image": "repopilot/fixture-dotnet:1" },
          "commands": []
        }
        """;

        var errors = SchemaValidator.Validate(json, SchemaKind.FixtureConfig);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void FixtureConfiguration_WithCommandMissingArgv_IsRefused()
    {
        const string json = """
        {
          "slug": "no-argv",
          "displayName": "No argv",
          "sandbox": { "image": "repopilot/fixture-dotnet:1" },
          "commands": [ { "name": "unit" } ]
        }
        """;

        var errors = SchemaValidator.Validate(json, SchemaKind.FixtureConfig);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void FixtureConfiguration_WithUnknownProperty_IsRefused()
    {
        // additionalProperties:false matters here. A typo'd or smuggled key such
        // as "shell" must fail loudly rather than being ignored.
        const string json = """
        {
          "slug": "extra-key",
          "displayName": "Extra",
          "sandbox": { "image": "repopilot/fixture-dotnet:1" },
          "commands": [ { "name": "unit", "argv": ["dotnet", "test"] } ],
          "shell": "/bin/bash -c"
        }
        """;

        var errors = SchemaValidator.Validate(json, SchemaKind.FixtureConfig);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void FixtureConfiguration_WithTimeoutAboveUpperBound_IsRefused()
    {
        // FR-023a: the configured limit must fall within documented bounds so a
        // misconfiguration cannot disable the timeout in practice.
        const string json = """
        {
          "slug": "no-timeout",
          "displayName": "Unbounded",
          "sandbox": { "image": "repopilot/fixture-dotnet:1", "timeoutSeconds": 86400 },
          "commands": [ { "name": "unit", "argv": ["dotnet", "test"] } ]
        }
        """;

        var errors = SchemaValidator.Validate(json, SchemaKind.FixtureConfig);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void EvaluationTask_ReferencePatchOutsideReferenceDirectory_IsRefused()
    {
        // FR-035: reference patches live under _reference/, outside every fixture
        // root. A path that escapes that prefix could place a solution somewhere
        // the indexer can reach.
        const string json = """
        {
          "id": "leaky-reference-01",
          "repositorySlug": "sample-dotnet-api",
          "category": "bug_fix",
          "description": "A task whose reference patch is stored inside the fixture itself.",
          "relevantFiles": ["src/Orders/OrderLookupService.cs"],
          "baselineTestCommand": "unit",
          "successTestCommand": "unit",
          "successCondition": { "type": "tests_pass" },
          "referencePatch": "../fixtures/sample-dotnet-api/solution.patch"
        }
        """;

        var errors = SchemaValidator.Validate(json, SchemaKind.EvaluationTask);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void MalformedJson_ReportsRatherThanThrows()
    {
        var errors = SchemaValidator.Validate("{ not json", SchemaKind.FixtureConfig);

        Assert.Single(errors);
        Assert.Contains("not valid JSON", errors[0].Message, StringComparison.Ordinal);
    }
}
