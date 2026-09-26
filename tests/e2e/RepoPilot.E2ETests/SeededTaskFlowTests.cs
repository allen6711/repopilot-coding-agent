using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using Xunit;

namespace RepoPilot.E2ETests;

/// <summary>
/// User Story 1, end to end: a seeded task becomes a proposal, a human approves
/// it, the change is written to a disposable copy, the fixture's own tests run
/// in an isolated container, and the run ends with a recorded result.
/// <para>
/// This is the test that makes the central claim checkable — nothing reached the
/// repository without a recorded human approval, and the change that was applied
/// is exactly the one that was approved.
/// </para>
/// </summary>
[Collection(RepoPilotHostCollection.Name)]
public sealed class SeededTaskFlowTests(RepoPilotHost host)
{
    private const string Actor = "reviewer@example.com";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [RequiresSandboxImageFact]
    public async Task ASeededTaskRunsFromCreationToAVerifiedResult()
    {

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Actor", Actor);

        // 1. Create -----------------------------------------------------------
        var created = await client.PostAsJsonAsync("/api/runs", new
        {
            repositoryId = host.RepositoryId,
            seededTaskId = "bugfix-null-guard-01",
            taskDescription =
                "OrderLookupService.Lookup throws when an order has no shipping address. " +
                "Add the missing guard.",
        });

        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);

        var runId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetGuid();

        // 2. The agent plans and proposes; the run stops for a human -----------
        var awaiting = await WaitForStageAsync(client, runId, "awaiting_approval");

        // FR-010: the plan is there before anyone is asked to decide.
        Assert.Equal(ScriptedChatAdapter.Plan, awaiting.GetProperty("plan").GetString());

        var proposal = await client.GetFromJsonAsync<JsonElement>(
            $"/api/runs/{runId}/proposal", Json);

        var proposalId = proposal.GetProperty("id").GetGuid();
        var diffHash = proposal.GetProperty("diffHash").GetString()!;

        Assert.Equal("pending", proposal.GetProperty("decisionStatus").GetString());
        Assert.Equal(
            "src/Orders/OrderLookupService.cs",
            proposal.GetProperty("affectedPaths")[0].GetString());

        // Nothing has been written yet. This is the assertion the whole system
        // exists to support: a proposal is not a change.
        await AssertFixtureIsUntouchedAsync();

        // 3. A human approves --------------------------------------------------
        var decision = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/approval",
            new { proposalId, decision = "approve", diffHash });

        Assert.Equal(HttpStatusCode.Created, decision.StatusCode);

        // 4. Apply, then test in the sandbox -----------------------------------
        var finished = await WaitForTerminalAsync(client, runId, TimeSpan.FromMinutes(5));

        Assert.Equal("succeeded", finished.GetProperty("stage").GetString());
        Assert.Equal("succeeded", finished.GetProperty("terminalOutcome").GetString());
        Assert.Equal("completed", finished.GetProperty("outcomeReason").GetString());
        Assert.Equal(0, finished.GetProperty("revisionAttempt").GetInt32());

        // 5. The tests really ran, in a container, and really passed -----------
        var tests = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}/tests", Json);

        var result = tests[0];
        Assert.Equal("unit", result.GetProperty("commandName").GetString());
        Assert.True(result.GetProperty("passed").GetBoolean());
        Assert.False(result.GetProperty("timedOut").GetBoolean());
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());

        // The fixture's suite has three tests, one of which failed before the
        // change. Asserting on the output distinguishes "the tests passed" from
        // "no tests ran and the command exited zero".
        var output = result.GetProperty("output").GetString()!;
        Assert.Contains("Passed!", output);
        Assert.Contains("Passed:     3", output);

        // 6. The record supports every claim above -----------------------------
        await using var db = host.CreateContext();

        var approval = await db.ApprovalDecisions.SingleAsync(a => a.RunId == runId);
        Assert.Equal(Actor, approval.DecidedBy);

        // FR-019a, FR-020a: the approval names the hash of the content that was
        // applied. If these differed, something other than what was approved
        // reached the working copy.
        var storedProposal = await db.ChangeProposals.SingleAsync(p => p.RunId == runId);
        Assert.Equal(storedProposal.DiffHash, approval.DiffHash);
        Assert.Equal(diffHash, approval.DiffHash);

        // FR-026a, SC-012: the working copy is gone, while everything needed to
        // audit the run persists without it.
        Assert.False(Directory.Exists(Path.Combine(host.WorkspaceRoot, "runs", runId.ToString())));

        var workingCopy = await db.WorkingCopies.SingleAsync(w => w.RunId == runId);
        Assert.NotNull(workingCopy.DestroyedAt);

        // FR-016a: the fixture itself was never opened for writing.
        await AssertFixtureIsUntouchedAsync();
    }

    [RequiresSandboxImageFact]
    public async Task TheRunIsReconstructableFromItsRecordedEventsAlone()
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

        await WaitForStageAsync(client, runId, "awaiting_approval");

        var proposal = await client.GetFromJsonAsync<JsonElement>(
            $"/api/runs/{runId}/proposal", Json);

        await client.PostAsJsonAsync(
            $"/api/runs/{runId}/approval",
            new
            {
                proposalId = proposal.GetProperty("id").GetGuid(),
                decision = "approve",
                diffHash = proposal.GetProperty("diffHash").GetString(),
            });

        await WaitForTerminalAsync(client, runId, TimeSpan.FromMinutes(5));

        await using var db = host.CreateContext();

        var events = await db.RunEvents
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();

        // SC-008: the sequence is dense and monotonic, so a reader can tell
        // "nothing happened" from "an event was lost".
        Assert.Equal(
            Enumerable.Range(1, events.Count).Select(i => (long)i),
            events.Select(e => e.Sequence));

        // Summaries are stored as jsonb, so PostgreSQL returns them normalized —
        // keys reordered, spacing changed. Substring matching on them is
        // unreliable for that reason; read the field.
        static string? TriggerOf(RepoPilot.Domain.Entities.RunEvent recorded)
        {
            if (recorded.ArgumentsSummary is null)
            {
                return null;
            }

            using var summary = JsonDocument.Parse(recorded.ArgumentsSummary);

            return summary.RootElement.TryGetProperty("trigger", out var trigger)
                ? trigger.GetString()
                : null;
        }

        int IndexOfType(RunEventType type) =>
            events.FindIndex(e => e.EventType == type);

        int IndexOfTrigger(string trigger) =>
            events.FindIndex(e =>
                e.EventType == RunEventType.StageChanged &&
                TriggerOf(e) == trigger);

        // The order the events tell is the order the run actually took: plan,
        // proposal, approval, write, tests.
        var plan = IndexOfTrigger(nameof(RunTrigger.PlanProduced));
        var proposed = IndexOfTrigger(nameof(RunTrigger.ProposalCreated));
        var approved = IndexOfType(RunEventType.ApprovalRecorded);
        var applied = IndexOfTrigger(nameof(RunTrigger.PatchApplied));
        var tested = IndexOfTrigger(nameof(RunTrigger.TestsPassed));

        Assert.True(plan >= 0, "No plan transition was recorded.");
        Assert.True(proposed > plan, "The plan does not precede the proposal.");
        Assert.True(approved > proposed, "The approval does not follow the proposal.");
        Assert.True(applied > approved, "The write does not follow the approval.");
        Assert.True(tested > applied, "The test run does not follow the write.");

        // FR-027: the write and the sandbox execution are both recorded actions,
        // attributable to the orchestrator rather than to the model.
        var toolNames = events.Select(e => e.ToolName).Where(n => n is not null).ToList();
        Assert.Contains("apply_patch", toolNames);
        Assert.Contains("run_tests", toolNames);
    }

    // ---- helpers ------------------------------------------------------------

    private static async Task<JsonElement> WaitForStageAsync(
        HttpClient client, Guid runId, string stage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);

        while (DateTime.UtcNow < deadline)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}", Json);
            var current = run.GetProperty("stage").GetString();

            if (current == stage)
            {
                return run;
            }

            Assert.False(
                current is "failed" or "cancelled" or "rejected" or "no_change",
                $"The run ended as '{current}' before reaching '{stage}'. " +
                $"Reason: {run.GetProperty("outcomeReason")}");

            await Task.Delay(250);
        }

        throw new TimeoutException($"Run {runId} did not reach '{stage}'.");
    }

    private static async Task<JsonElement> WaitForTerminalAsync(
        HttpClient client, Guid runId, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;

        while (DateTime.UtcNow < deadline)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}", Json);

            if (run.GetProperty("terminalOutcome").ValueKind != JsonValueKind.Null)
            {
                return run;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Run {runId} did not finish within {within}.");
    }

    /// <summary>
    /// FR-016a: the registered fixture is read-only to every code path. The
    /// deliberate defect still being present is the check — if a run had written
    /// through to the fixture, the guard would be there.
    /// </summary>
    private async Task AssertFixtureIsUntouchedAsync()
    {
        var source = await File.ReadAllTextAsync(
            Path.Combine(host.FixtureRoot, "src", "Orders", "OrderLookupService.cs"));

        Assert.Contains("order.ShippingAddress.City", source);
        Assert.DoesNotContain("order.ShippingAddress?.City", source);
    }
}
