using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Runs;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Contracts;

/// <summary>
/// The run and approval endpoints against <c>contracts/rest-api.yaml</c>.
/// <para>
/// Run through the real host with the real DI graph rather than by calling the
/// handlers directly. The status codes are the contract — 409 for a decided
/// proposal, 422 for a hash mismatch or a missing actor — and a handler invoked
/// in isolation cannot tell you what the pipeline actually returns.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunApiContractTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    private WebApplicationFactory<Program>? _factory;
    private string? _workspaceRoot;

    public Task InitializeAsync()
    {
        if (postgres.ConnectionString is null)
        {
            return Task.CompletedTask;
        }

        _workspaceRoot = Directory.CreateTempSubdirectory("repopilot-api-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:RepoPilot", postgres.ConnectionString);
            builder.UseSetting("RepoPilot:Workspace:Root", _workspaceRoot);
        });

        // The host is started here, before any test seeds a row. Startup
        // recovery ends every non-terminal run it finds (FR-030a), so a run
        // seeded first and served second would arrive already failed — correct
        // behaviour, wrong order for a test about endpoints.
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

    private HttpClient Client() => _factory!.CreateClient();

    // ---- Fixtures -----------------------------------------------------------

    /// <summary>Registers an indexed repository so run creation is not refused.</summary>
    private async Task<RepositoryFixture> IndexedRepositoryAsync()
    {
        await using var db = postgres.CreateContext();

        var repository = new RepositoryFixture
        {
            Slug = "api-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "API contract fixture",
            RootPath = Path.Combine(_workspaceRoot!, "source"),
            TestConfigJson = """{"slug":"api","commands":[]}""",
            ActiveIndexVersion = 1,
            IncludedFileCount = 12,
        };

        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        return repository;
    }

    /// <summary>Puts a run at awaiting-approval with a proposal to decide on.</summary>
    private async Task<(Run Run, ChangeProposal Proposal)> RunAwaitingApprovalAsync()
    {
        var repository = await IndexedRepositoryAsync();

        await using var db = postgres.CreateContext();

        var run = new Run
        {
            RepositoryId = repository.Id,
            TaskDescription = "Add the missing null guard.",
            Stage = RunStage.AwaitingApproval,
            Plan = "Guard the lookup result before projecting it.",
        };

        var entries = new List<ProposalEntry>
        {
            new("src/Service.cs", ProposalOperation.Modify, "guarded content"),
        };

        var proposal = new ChangeProposal
        {
            RunId = run.Id,
            EntriesJson = JsonSerializer.Serialize(entries),
            UnifiedDiff = "--- a/src/Service.cs\n+++ b/src/Service.cs\n",
            AffectedPaths = ["src/Service.cs"],
            DiffHash = DiffHash.Compute(entries),
        };

        db.Runs.Add(run);
        db.ChangeProposals.Add(proposal);
        await db.SaveChangesAsync();

        return (run, proposal);
    }

    // ---- POST /api/runs -----------------------------------------------------

    [RequiresDockerFact]
    public async Task CreatingARunIsAcceptedAndQueued()
    {
        var repository = await IndexedRepositoryAsync();

        var response = await Client().PostAsJsonAsync(
            "/api/runs",
            new { repositoryId = repository.Id, taskDescription = "Fix the null dereference." });

        // 202, not 201: the run exists but nothing has happened to it yet.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var run = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("created", run.GetProperty("stage").GetString());
        Assert.True(run.GetProperty("toolsEnabled").GetBoolean());
        Assert.Equal(repository.Id, run.GetProperty("repositoryId").GetGuid());
    }

    [RequiresDockerFact]
    public async Task ARunWithNeitherATaskNorASeededIdIsRefused()
    {
        var repository = await IndexedRepositoryAsync();

        var response = await Client().PostAsJsonAsync(
            "/api/runs", new { repositoryId = repository.Id });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertProblemAsync(response);
    }

    [RequiresDockerFact]
    public async Task ARunAgainstAnUnindexedRepositoryIsRefused()
    {
        await using var db = postgres.CreateContext();

        var repository = new RepositoryFixture
        {
            Slug = "unindexed-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Never indexed",
            RootPath = Path.Combine(_workspaceRoot!, "source"),
            TestConfigJson = """{"slug":"u","commands":[]}""",
        };

        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        var response = await Client().PostAsJsonAsync(
            "/api/runs", new { repositoryId = repository.Id, taskDescription = "anything" });

        // Refused at submission rather than accepted and failed later, so the
        // reason reaches the caller instead of appearing as an outcome reason
        // on a run that never had a chance.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [RequiresDockerFact]
    public async Task ARunAgainstAnUnknownRepositoryIsNotFound()
    {
        var response = await Client().PostAsJsonAsync(
            "/api/runs", new { repositoryId = Guid.NewGuid(), taskDescription = "anything" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- GET /api/runs/... --------------------------------------------------

    [RequiresDockerFact]
    public async Task ARunIsReadableWithItsPlanAndStage()
    {
        var (run, _) = await RunAwaitingApprovalAsync();

        var read = await Client().GetFromJsonAsync<JsonElement>($"/api/runs/{run.Id}", Json);

        Assert.Equal("awaiting_approval", read.GetProperty("stage").GetString());
        Assert.Equal(run.Plan, read.GetProperty("plan").GetString());

        // FR-008: the outcome fields are present and null while the run is live,
        // rather than absent — a client should not have to distinguish "no
        // outcome yet" from "field missing".
        Assert.Equal(JsonValueKind.Null, read.GetProperty("terminalOutcome").ValueKind);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("outcomeReason").ValueKind);
    }

    [RequiresDockerFact]
    public async Task AnUnknownRunIsNotFound()
    {
        var response = await Client().GetAsync($"/api/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemAsync(response);
    }

    [RequiresDockerFact]
    public async Task TheProposalCarriesEveryAffectedFileAndTheWholeDiff()
    {
        var (run, proposal) = await RunAwaitingApprovalAsync();

        var read = await Client().GetFromJsonAsync<JsonElement>(
            $"/api/runs/{run.Id}/proposal", Json);

        // SC-004: everything needed to decide, in one response. A reviewer who
        // has to fetch more before approving could approve having seen less.
        Assert.Equal(proposal.DiffHash, read.GetProperty("diffHash").GetString());
        Assert.Equal("pending", read.GetProperty("decisionStatus").GetString());
        Assert.Equal("src/Service.cs", read.GetProperty("affectedPaths")[0].GetString());
        Assert.Contains("--- a/src/Service.cs", read.GetProperty("unifiedDiff").GetString());

        // Entries name the file and the operation but not the content — the diff
        // is the reviewable form, and duplicating content would let the two
        // disagree.
        var entry = read.GetProperty("entries")[0];
        Assert.Equal("modify", entry.GetProperty("operation").GetString());
        Assert.False(entry.TryGetProperty("newContent", out _));
    }

    [RequiresDockerFact]
    public async Task ARunWithNoProposalReportsThatRatherThanAnEmptyOne()
    {
        var repository = await IndexedRepositoryAsync();

        await using var db = postgres.CreateContext();
        var run = new Run { RepositoryId = repository.Id, TaskDescription = "nothing yet" };
        db.Runs.Add(run);
        await db.SaveChangesAsync();

        var response = await Client().GetAsync($"/api/runs/{run.Id}/proposal");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [RequiresDockerFact]
    public async Task TheDiffEndpointReturnsTheRequestedAttempt()
    {
        var (run, first) = await RunAwaitingApprovalAsync();

        await using (var db = postgres.CreateContext())
        {
            var entries = new List<ProposalEntry>
            {
                new("src/Service.cs", ProposalOperation.Modify, "revised content"),
            };

            db.ChangeProposals.Add(new ChangeProposal
            {
                RunId = run.Id,
                RevisionAttempt = 1,
                EntriesJson = JsonSerializer.Serialize(entries),
                UnifiedDiff = "--- a/src/Service.cs\n+++ b/src/Service.cs\n(revised)\n",
                AffectedPaths = ["src/Service.cs"],
                DiffHash = DiffHash.Compute(entries),
            });

            await db.SaveChangesAsync();
        }

        var latest = await Client().GetFromJsonAsync<JsonElement>($"/api/runs/{run.Id}/diff", Json);
        Assert.Equal(1, latest.GetProperty("revisionAttempt").GetInt32());

        // The superseded proposal stays readable: it was separately approved, and
        // what a reviewer authorised has to outlive the revision replacing it.
        var original = await Client().GetFromJsonAsync<JsonElement>(
            $"/api/runs/{run.Id}/diff?attempt=0", Json);
        Assert.Equal(first.DiffHash, original.GetProperty("diffHash").GetString());
    }

    [RequiresDockerFact]
    public async Task TestResultsAreListedForARun()
    {
        var (run, _) = await RunAwaitingApprovalAsync();

        await using (var db = postgres.CreateContext())
        {
            db.TestResults.Add(new TestResult
            {
                RunId = run.Id,
                CommandName = "unit",
                Passed = false,
                ExitCode = 1,
                Output = "1 failed",
                DurationMs = 4200,
            });

            await db.SaveChangesAsync();
        }

        var results = await Client().GetFromJsonAsync<JsonElement>($"/api/runs/{run.Id}/tests", Json);

        var result = results[0];
        Assert.Equal("unit", result.GetProperty("commandName").GetString());
        Assert.False(result.GetProperty("passed").GetBoolean());
        Assert.False(result.GetProperty("timedOut").GetBoolean());
        Assert.Equal(1, result.GetProperty("exitCode").GetInt32());
    }

    // ---- POST /api/runs/{id}/approval ---------------------------------------

    [RequiresDockerFact]
    public async Task AnApprovalRecordsTheDecisionAndWhoMadeIt()
    {
        var (run, proposal) = await RunAwaitingApprovalAsync();

        var client = Client();
        client.DefaultRequestHeaders.Add("X-Actor", "reviewer@example.com");

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{run.Id}/approval",
            new { proposalId = proposal.Id, decision = "approve", diffHash = proposal.DiffHash });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var decision = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("approve", decision.GetProperty("decision").GetString());
        Assert.Equal("reviewer@example.com", decision.GetProperty("decidedBy").GetString());
        Assert.Equal("interactive", decision.GetProperty("mode").GetString());
        Assert.Equal(proposal.DiffHash, decision.GetProperty("diffHash").GetString());
    }

    [RequiresDockerFact]
    public async Task ADecisionWithoutAnActorIsRefused()
    {
        var (run, proposal) = await RunAwaitingApprovalAsync();

        var response = await Client().PostAsJsonAsync(
            $"/api/runs/{run.Id}/approval",
            new { proposalId = proposal.Id, decision = "approve", diffHash = proposal.DiffHash });

        // SC-015: an unattributable approval is not recorded at all.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertProblemAsync(response);

        await using var db = postgres.CreateContext();
        Assert.False(await db.ApprovalDecisions.AnyAsync(d => d.ProposalId == proposal.Id));
    }

    [RequiresDockerFact]
    public async Task ABlankActorIsRefusedJustAsAnAbsentOneIs()
    {
        var (run, proposal) = await RunAwaitingApprovalAsync();

        var client = Client();
        client.DefaultRequestHeaders.Add("X-Actor", "   ");

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{run.Id}/approval",
            new { proposalId = proposal.Id, decision = "approve", diffHash = proposal.DiffHash });

        // A blank actor satisfies a NOT NULL column while leaving the decision
        // unattributable, which is precisely the failure SC-015 exists to stop.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [RequiresDockerFact]
    public async Task ADecisionNamingTheWrongHashIsRefused()
    {
        var (run, proposal) = await RunAwaitingApprovalAsync();

        var client = Client();
        client.DefaultRequestHeaders.Add("X-Actor", "reviewer@example.com");

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{run.Id}/approval",
            new { proposalId = proposal.Id, decision = "approve", diffHash = new string('a', 64) });

        // FR-020a: the reviewer decided on content other than what is stored, so
        // the approval would authorise something they never saw.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        await using var db = postgres.CreateContext();
        Assert.False(await db.ApprovalDecisions.AnyAsync(d => d.ProposalId == proposal.Id));
    }

    [RequiresDockerFact]
    public async Task ASecondDecisionOnTheSameProposalIsRefused()
    {
        var (run, proposal) = await RunAwaitingApprovalAsync();

        var client = Client();
        client.DefaultRequestHeaders.Add("X-Actor", "reviewer@example.com");

        var body = new { proposalId = proposal.Id, decision = "approve", diffHash = proposal.DiffHash };

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync($"/api/runs/{run.Id}/approval", body)).StatusCode);

        // FR-018. 409 regardless of what the second decision says — a rejection
        // arriving after an approval is just as much a duplicate.
        var second = await client.PostAsJsonAsync(
            $"/api/runs/{run.Id}/approval",
            new { proposalId = proposal.Id, decision = "reject", diffHash = proposal.DiffHash });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.ApprovalDecisions.CountAsync(d => d.ProposalId == proposal.Id));
    }

    [RequiresDockerFact]
    public async Task ADecisionForAProposalOnAnotherRunIsRefused()
    {
        var (_, proposal) = await RunAwaitingApprovalAsync();
        var (otherRun, _) = await RunAwaitingApprovalAsync();

        var client = Client();
        client.DefaultRequestHeaders.Add("X-Actor", "reviewer@example.com");

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{otherRun.Id}/approval",
            new { proposalId = proposal.Id, decision = "approve", diffHash = proposal.DiffHash });

        // The path and the body must agree, or a decision made while reviewing
        // one run could be recorded against another.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [RequiresDockerFact]
    public async Task ARejectionEndsTheRun()
    {
        var (run, proposal) = await RunAwaitingApprovalAsync();

        var client = Client();
        client.DefaultRequestHeaders.Add("X-Actor", "reviewer@example.com");

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{run.Id}/approval",
            new { proposalId = proposal.Id, decision = "reject", diffHash = proposal.DiffHash });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var read = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{run.Id}", Json);
        Assert.Equal("rejected", read.GetProperty("stage").GetString());
        Assert.Equal("proposal_rejected", read.GetProperty("outcomeReason").GetString());
    }

    // ---- POST /api/runs/{id}/cancel -----------------------------------------

    [RequiresDockerFact]
    public async Task ARunAwaitingApprovalCanBeCancelled()
    {
        var (run, _) = await RunAwaitingApprovalAsync();

        var client = Client();
        client.DefaultRequestHeaders.Add("X-Actor", "reviewer@example.com");

        var response = await client.PostAsync($"/api/runs/{run.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cancelled = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("cancelled", cancelled.GetProperty("stage").GetString());
        Assert.Equal("abandoned_by_user", cancelled.GetProperty("outcomeReason").GetString());
    }

    [RequiresDockerFact]
    public async Task CancellingWithoutAnActorIsRefused()
    {
        var (run, _) = await RunAwaitingApprovalAsync();

        var response = await Client().PostAsync($"/api/runs/{run.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [RequiresDockerFact]
    public async Task CancellingAFinishedRunIsRefused()
    {
        var (run, _) = await RunAwaitingApprovalAsync();

        var client = Client();
        client.DefaultRequestHeaders.Add("X-Actor", "reviewer@example.com");

        await client.PostAsync($"/api/runs/{run.Id}/cancel", null);

        var second = await client.PostAsync($"/api/runs/{run.Id}/cancel", null);

        // 409, not 422: the request was well formed and the state refused it. A
        // recorded outcome is not replaced by a later cancellation.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    /// <summary>Asserts the response is RFC 9457 problem details, as the contract requires.</summary>
    private static async Task AssertProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(problem.TryGetProperty("title", out _));
        Assert.Equal((int)response.StatusCode, problem.GetProperty("status").GetInt32());
    }
}
