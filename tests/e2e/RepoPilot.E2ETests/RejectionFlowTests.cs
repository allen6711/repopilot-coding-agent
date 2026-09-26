using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RepoPilot.Domain.Entities;
using Xunit;

namespace RepoPilot.E2ETests;

/// <summary>
/// The rejection path, end to end.
/// <para>
/// The approval gate is only meaningful if declining it actually stops
/// everything. These tests assert the negative: after a rejection, the fixture
/// is unchanged, the working copy is gone, no write was attempted, and the
/// decision is on record with who made it.
/// </para>
/// </summary>
[Collection(RepoPilotHostCollection.Name)]
public sealed class RejectionFlowTests(RepoPilotHost host)
{
    private const string Actor = "reviewer@example.com";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [RequiresSandboxImageFact]
    public async Task ARejectedProposalChangesNothingAndIsRecorded()
    {

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Actor", Actor);

        var fixtureBefore = await ReadFixtureSourceAsync();

        var created = await client.PostAsJsonAsync("/api/runs", new
        {
            repositoryId = host.RepositoryId,
            taskDescription = "Guard the shipping address projection.",
        });

        var runId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetGuid();

        var proposal = await WaitForProposalAsync(client, runId);
        var proposalId = proposal.GetProperty("id").GetGuid();
        var diffHash = proposal.GetProperty("diffHash").GetString()!;

        // Reject ---------------------------------------------------------------
        var decision = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/approval",
            new { proposalId, decision = "reject", diffHash });

        Assert.Equal(HttpStatusCode.Created, decision.StatusCode);

        var run = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}", Json);

        Assert.Equal("rejected", run.GetProperty("stage").GetString());
        Assert.Equal("rejected", run.GetProperty("terminalOutcome").GetString());
        Assert.Equal("proposal_rejected", run.GetProperty("outcomeReason").GetString());

        // The fixture is byte-for-byte what it was ------------------------------
        Assert.Equal(fixtureBefore, await ReadFixtureSourceAsync());

        // The working copy is gone, and so is the change that was staged in it.
        var workingCopyPath = Path.Combine(host.WorkspaceRoot, "runs", runId.ToString());
        Assert.False(Directory.Exists(workingCopyPath));

        await using var db = host.CreateContext();

        var recorded = await db.ApprovalDecisions.SingleAsync(a => a.RunId == runId);
        Assert.Equal(ApprovalDecisionKind.Reject, recorded.Decision);
        Assert.Equal(Actor, recorded.DecidedBy);

        // FR-019a: a rejection binds to content just as an approval does, so the
        // record says which change was declined rather than merely that one was.
        Assert.Equal(diffHash, recorded.DiffHash);

        var storedProposal = await db.ChangeProposals.SingleAsync(p => p.RunId == runId);
        Assert.Equal(ProposalDecisionStatus.Rejected, storedProposal.DecisionStatus);

        // Nothing was written and nothing was executed. A rejected run that had
        // reached either would mean the gate ran after the fact.
        var actions = await db.RunEvents
            .Where(e => e.RunId == runId && e.ToolName != null)
            .Select(e => e.ToolName!)
            .ToListAsync();

        Assert.DoesNotContain("apply_patch", actions);
        Assert.DoesNotContain("run_tests", actions);

        Assert.Empty(await db.TestResults.Where(t => t.RunId == runId).ToListAsync());
    }

    [RequiresSandboxImageFact]
    public async Task ARejectedProposalCannotThenBeApproved()
    {

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Actor", Actor);

        var created = await client.PostAsJsonAsync("/api/runs", new
        {
            repositoryId = host.RepositoryId,
            taskDescription = "Guard the shipping address projection.",
        });

        var runId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetGuid();

        var proposal = await WaitForProposalAsync(client, runId);
        var body = new
        {
            proposalId = proposal.GetProperty("id").GetGuid(),
            diffHash = proposal.GetProperty("diffHash").GetString(),
        };

        await client.PostAsJsonAsync(
            $"/api/runs/{runId}/approval",
            new { body.proposalId, decision = "reject", body.diffHash });

        // FR-018: one decision per proposal, whichever way it went. Without
        // this, a rejection could be walked back into an approval and the
        // audit trail would show both.
        var second = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/approval",
            new { body.proposalId, decision = "approve", body.diffHash });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await using var db = host.CreateContext();
        Assert.Equal(1, await db.ApprovalDecisions.CountAsync(a => a.RunId == runId));
        Assert.Equal(0, await db.TestResults.CountAsync(t => t.RunId == runId));
    }

    [RequiresSandboxImageFact]
    public async Task ARunCancelledWhileAwaitingApprovalWritesNothing()
    {

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Actor", Actor);

        var fixtureBefore = await ReadFixtureSourceAsync();

        var created = await client.PostAsJsonAsync("/api/runs", new
        {
            repositoryId = host.RepositoryId,
            taskDescription = "Guard the shipping address projection.",
        });

        var runId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetGuid();

        await WaitForProposalAsync(client, runId);

        var cancelled = await client.PostAsync($"/api/runs/{runId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var run = await cancelled.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("cancelled", run.GetProperty("stage").GetString());
        Assert.Equal("abandoned_by_user", run.GetProperty("outcomeReason").GetString());

        Assert.Equal(fixtureBefore, await ReadFixtureSourceAsync());
        Assert.False(Directory.Exists(Path.Combine(host.WorkspaceRoot, "runs", runId.ToString())));

        await using var db = host.CreateContext();

        // Cancelling instead of deciding leaves no decision. The proposal stays
        // pending forever, which is the honest record of what happened.
        Assert.Empty(await db.ApprovalDecisions.Where(a => a.RunId == runId).ToListAsync());

        var proposal = await db.ChangeProposals.SingleAsync(p => p.RunId == runId);
        Assert.Equal(ProposalDecisionStatus.Pending, proposal.DecisionStatus);
    }

    private Task<string> ReadFixtureSourceAsync() =>
        File.ReadAllTextAsync(
            Path.Combine(host.FixtureRoot, "src", "Orders", "OrderLookupService.cs"));

    private static async Task<JsonElement> WaitForProposalAsync(HttpClient client, Guid runId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);

        while (DateTime.UtcNow < deadline)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}", Json);

            if (run.GetProperty("stage").GetString() == "awaiting_approval")
            {
                return await client.GetFromJsonAsync<JsonElement>(
                    $"/api/runs/{runId}/proposal", Json);
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Run {runId} never reached awaiting_approval.");
    }
}
