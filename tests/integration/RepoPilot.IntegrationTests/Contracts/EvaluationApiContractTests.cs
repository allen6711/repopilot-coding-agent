using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Contracts;

/// <summary>
/// The evaluation endpoints against <c>contracts/rest-api.yaml</c>.
/// <para>
/// Contract, not behaviour: these check the shapes and status codes the
/// specification names. Whether the evaluation produces good numbers is what the
/// harness's own tests are for — what this pins is that a caller who reads the
/// contract can start one, get an id back, and poll it.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EvaluationApiContractTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Every property <c>EvaluationRun</c> declares. Checked as a set so that
    /// removing one from the response is a failure here rather than a null in a
    /// consumer.
    /// </summary>
    private static readonly string[] ContractProperties =
    [
        "id", "startedAt", "endedAt", "taskCount", "recallAt5",
        "completionRateToolEnabled", "completionRateBaseline", "approvalCoverage",
        "toolSuccessRate", "avgToolCallsPerCompletedTask",
        "p50LatencyMs", "p95LatencyMs", "flagged",
    ];

    private WebApplicationFactory<Program>? _factory;
    private string? _workspaceRoot;

    public Task InitializeAsync()
    {
        if (postgres.ConnectionString is null)
        {
            return Task.CompletedTask;
        }

        _workspaceRoot = Directory.CreateTempSubdirectory("repopilot-evalapi-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:RepoPilot", postgres.ConnectionString);
            builder.UseSetting("RepoPilot:Workspace:Root", _workspaceRoot);
            // Pointed at the committed set explicitly. The default is relative to
            // the host's working directory, which under a test runner is a bin
            // folder — and an evaluation that found no tasks would refuse, which
            // is the right behaviour and the wrong thing to be testing here.
            builder.UseSetting(
                "RepoPilot:Evaluation:TasksDirectory", Evals.CommittedArtifacts.TasksDirectory);
            builder.UseSetting(
                "RepoPilot:Evaluation:ResultsDirectory", Path.Combine(_workspaceRoot, "results"));
        });

        _factory.CreateClient().Dispose();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();

        if (_workspaceRoot is not null && Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private HttpClient Client() =>
        (_factory ?? throw new InvalidOperationException("Host did not start.")).CreateClient();

    [RequiresDockerFact]
    public async Task StartingAnEvaluationReturns202WithAReadableId()
    {
        using var client = Client();

        var response = await client.PostAsync("/api/evaluations", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = body.GetProperty("id").GetGuid();

        Assert.NotEqual(Guid.Empty, id);

        // 202 promises the resource exists at the location it names. An id that
        // only becomes readable once the evaluation finishes would make a poll
        // indistinguishable from a lost evaluation.
        Assert.Equal(
            $"/api/evaluations/{id}",
            response.Headers.Location?.ToString());

        var polled = await client.GetAsync($"/api/evaluations/{id}");

        Assert.Equal(HttpStatusCode.OK, polled.StatusCode);
    }

    [RequiresDockerFact]
    public async Task TheResponseCarriesEveryPropertyTheContractDeclares()
    {
        using var client = Client();

        var started = await client.PostAsync("/api/evaluations", content: null);
        var body = await started.Content.ReadFromJsonAsync<JsonElement>(Json);

        foreach (var property in ContractProperties)
        {
            Assert.True(
                body.TryGetProperty(property, out _),
                $"The contract declares '{property}' and the response omits it.");
        }

        // Every figure is null on a freshly started evaluation, and the contract
        // types them as nullable for exactly that reason. Reporting a zero here
        // would claim a measurement that has not been taken.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("recallAt5").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("endedAt").ValueKind);
        Assert.False(body.GetProperty("flagged").GetBoolean());

        // taskCount is set before any task runs: it is the size of the committed
        // set, not a count of what has finished.
        Assert.True(body.GetProperty("taskCount").GetInt32() >= 30);
    }

    [RequiresDockerFact]
    public async Task ReadingAnUnknownEvaluationReturnsProblemDetails()
    {
        using var client = Client();

        var response = await client.GetAsync($"/api/evaluations/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal("Evaluation not found", problem.GetProperty("title").GetString());
    }
}
