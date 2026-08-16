using Microsoft.Extensions.Options;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Capabilities;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;

namespace RepoPilot.Application.Runs;

/// <summary>
/// Drives a run through its stages.
/// <para>
/// Two things about the shape of this type are load-bearing.
/// </para>
/// <para>
/// First, it is split into <em>segments</em> rather than written as one method.
/// A run stops at <c>awaiting approval</c> for as long as a human takes, which
/// may be days and may span a restart. A single long-lived method would have to
/// hold that wait in memory, and the approval gate would then depend on a
/// process staying alive. Instead each segment starts by reading the run's
/// persisted stage and ends by persisting the next one, so the gate is a
/// database state rather than a suspended continuation.
/// </para>
/// <para>
/// Second, every stage change goes through <see cref="RunStateMachine"/>, and
/// nothing here decides a stage from what the model said. The model's output is
/// evidence the orchestrator interprets — "it called propose_patch" — never an
/// instruction about what stage comes next (Principle IV).
/// </para>
/// </summary>
public sealed class RunOrchestrator(
    IRunStore runs,
    IRepositoryFixtureStore repositories,
    IProposalStore proposals,
    IApprovalStore approvals,
    IWorkspaceProvisioner workspaces,
    IAgentTurnRunner agent,
    ICapabilityInvoker capabilities,
    RunEventRecorder events,
    IOptions<RunConcurrencyOptions> concurrency,
    IOptions<RetrievalOptions> retrieval,
    IBaselineContextProvider baselineContext)
{
    /// <summary>
    /// Turns the agent may take within one segment before the orchestrator gives
    /// up. This bounds a model that keeps searching without ever proposing; the
    /// context budget bounds how much it can read, and this bounds how long it
    /// can go round.
    /// </summary>
    private const int MaxTurnsPerSegment = 24;

    private const int MaxOutputTokens = 16_000;

    /// <summary>
    /// Runs the pre-approval segment: retrieve, plan, propose.
    /// <para>
    /// Ends at <c>awaiting approval</c> with a proposal a human can decide on, or
    /// at a terminal stage. It never applies anything — the only capability it
    /// offers the model is <c>propose_patch</c>, and proposing is not writing.
    /// </para>
    /// </summary>
    public async Task<RunStage> ExecuteUntilApprovalAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await RequireRunAsync(runId, ct);

        if (run.Stage != RunStage.Created)
        {
            throw new IllegalTransitionException(run.Stage, RunTrigger.Start);
        }

        var fixture = await repositories.FindByIdAsync(run.RepositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {run.RepositoryId} not found.");

        try
        {
            await TransitionAsync(run, RunTrigger.Start, ct);

            // The working copy is created here, at the start of retrieving,
            // because read capabilities resolve against it. Creating it later
            // would mean reads and writes had different notions of where the
            // repository is (FR-024a).
            var workspace = await workspaces.CreateAsync(runId, fixture, ct);

            var budget = new RunContextBudget(retrieval.Value.ContextBudgetChars);
            var context = new CapabilityContext(runId, fixture.Id, workspace, budget);

            var conversation = new List<ChatMessage>
            {
                new(ChatRole.User, run.TaskDescription),
            };

            if (!run.ToolsEnabled)
            {
                // FR-033. The baseline cannot search, so its context is retrieved
                // for it, once, and charged against the same budget the
                // tool-enabled condition spends — otherwise the comparison would
                // be between two different context allowances rather than between
                // retrieval and retrieval plus tools.
                await SeedBaselineContextAsync(run, context, conversation, ct);
            }

            var proposal = await DriveAgentAsync(run, fixture, context, conversation, ct);

            if (proposal is null)
            {
                // Nothing was proposed. Which no-change reason applies depends on
                // whether the agent got far enough to have context at all.
                return await EndWithNoChangeAsync(run, ct);
            }

            return run.Stage;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(run, ReasonFor(ex), ex.Message, ct);
            throw;
        }
    }

    /// <summary>
    /// Ends a run whose proposal was rejected (FR-008).
    /// <para>
    /// Separate from <see cref="DecideProposalUseCase"/> because that type's job
    /// is to record the decision, and this one's is to move the run. Keeping the
    /// stage change here means every transition in the system goes through the
    /// same table, rather than one of them living beside the audit write.
    /// </para>
    /// </summary>
    public async Task<RunStage> RecordRejectionAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await RequireRunAsync(runId, ct);

        await TransitionAsync(run, RunTrigger.Rejected, ct, OutcomeReason.ProposalRejected);

        // FR-026a: a rejected run's working copy goes too. Nothing was written to
        // it, but leaving it would make rejection the one terminal outcome that
        // accumulates directories.
        await workspaces.DestroyAsync(runId, ct);

        return run.Stage;
    }

    /// <summary>
    /// Runs the post-approval segment: apply, then test.
    /// <para>
    /// Entered only after a recorded approval, and it re-checks that rather than
    /// trusting the caller — this is the last point before a write, so the check
    /// belongs here even though the API endpoint checked too.
    /// </para>
    /// </summary>
    /// <returns>
    /// The stage the run reached: terminal, or back at <c>proposing</c> when a
    /// revision is due — which then needs its own approval (FR-013).
    /// </returns>
    public async Task<RunStage> ApplyAndTestAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await RequireRunAsync(runId, ct);

        var proposal = await proposals.FindCurrentForRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Run {runId} has no proposal to apply.");

        var decision = await approvals.FindByProposalAsync(proposal.Id, ct);

        if (decision is null || decision.Decision != ApprovalDecisionKind.Approve)
        {
            // Principle I, restated where it is enforced rather than only where
            // it is documented. Reaching here without an approval is a bug, and
            // it must fail rather than proceed.
            throw new InvalidOperationException(
                $"Proposal {proposal.Id} has no recorded approval. Nothing may be written.");
        }

        var fixture = await repositories.FindByIdAsync(run.RepositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {run.RepositoryId} not found.");

        try
        {
            await TransitionAsync(run, RunTrigger.Approved, ct);

            var workspace = Domain.Workspace.WorkspaceRoot.Writable(workspaces.PathFor(runId));
            var budget = new RunContextBudget(retrieval.Value.ContextBudgetChars);
            var context = new CapabilityContext(runId, fixture.Id, workspace, budget);

            // Orchestrator surface, not model surface. apply_patch is not in the
            // model's tool list at all; passing the wrong surface here is refused
            // by the invoker.
            await capabilities.InvokeAsync(
                context,
                "apply_patch",
                $$"""{"proposal_id":"{{proposal.Id}}"}""",
                InvocationSurface.Orchestrator,
                ct);

            await TransitionAsync(run, RunTrigger.PatchApplied, ct);

            var passed = await RunTestsAsync(run, context, fixture, ct);

            if (passed)
            {
                await TransitionAsync(run, RunTrigger.TestsPassed, ct, OutcomeReason.Completed);
                await workspaces.DestroyAsync(runId, ct);
                return run.Stage;
            }

            var trigger = RevisionPolicy.AfterFailure(
                run.RevisionAttempt, concurrency.Value.MaxRevisionAttempts);

            if (trigger == RunTrigger.TestsFailedRetriesExhausted)
            {
                await TransitionAsync(run, trigger, ct, OutcomeReason.RevisionLimitReached);
                await workspaces.DestroyAsync(runId, ct);
                return run.Stage;
            }

            // Back to proposing. The attempt counter increments here, before the
            // next proposal exists, so a crash mid-revision cannot produce an
            // extra attempt on restart.
            run.RevisionAttempt++;
            await TransitionAsync(run, trigger, ct);

            var revised = await DriveAgentAsync(
                run,
                fixture,
                context,
                [
                    new ChatMessage(ChatRole.User, run.TaskDescription),
                    new ChatMessage(ChatRole.User, await RevisionPromptAsync(runId, ct)),
                ],
                ct);

            if (revised is null)
            {
                return await EndWithNoChangeAsync(run, ct);
            }

            return run.Stage;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(run, ReasonFor(ex), ex.Message, ct);
            throw;
        }
    }

    /// <summary>
    /// Runs agent turns until it proposes, declines, or runs out of turns.
    /// </summary>
    /// <returns>The proposal that was created, or null when none was.</returns>
    private async Task<ChangeProposal?> DriveAgentAsync(
        Run run,
        RepositoryFixture fixture,
        CapabilityContext context,
        List<ChatMessage> conversation,
        CancellationToken ct)
    {
        var planText = run.Plan;

        var offered = run.ToolsEnabled
            ? OfferedCapabilities.All
            : OfferedCapabilities.ProposeOnly;

        for (var turn = 0; turn < MaxTurnsPerSegment; turn++)
        {
            var effort = run.Stage == RunStage.Proposing ? EffortLevel.High : EffortLevel.Medium;
            var completion = await agent.TurnAsync(
                fixture.Slug, conversation, effort, MaxOutputTokens, offered, ct);

            if (completion.Text.Length > 0)
            {
                conversation.Add(new ChatMessage(ChatRole.Assistant, completion.Text));

                // Held as a candidate rather than committed as the plan. Text on
                // a turn that goes on to call nothing may equally be the agent
                // saying it cannot find the code — recording that as a plan would
                // move the run to Proposing and turn "I could not look" into "I
                // looked and chose not to act".
                planText ??= completion.Text;
            }

            if (completion.StopReason != ChatStopReason.ToolUse || completion.ToolCalls.Count == 0)
            {
                // The agent finished its turn without proposing. That is a
                // legitimate outcome, not an error: it is how "no change is
                // needed" is expressed (FR-008b).
                return null;
            }

            // A tool call is the evidence that retrieval happened, which is what
            // separates the two no-change reasons.
            if (run.Stage == RunStage.Retrieving)
            {
                await TransitionAsync(run, RunTrigger.ContextRetrieved, ct);
            }

            foreach (var call in completion.ToolCalls)
            {
                var outcome = await capabilities.InvokeAsync(
                    context, call.Name, call.ArgumentsJson, InvocationSurface.Model, ct);

                conversation.Add(new ChatMessage(ChatRole.Assistant, outcome.Content));

                if (call.Name != "propose_patch")
                {
                    continue;
                }

                // propose_patch stored a proposal. The orchestrator reads it back
                // rather than trusting the tool result, because what the reviewer
                // will see is the stored row and that is what the approval binds.
                var proposal = await proposals.FindCurrentForRunAsync(run.Id, ct);

                if (proposal is null)
                {
                    continue;
                }

                if (run.Stage == RunStage.Planning)
                {
                    // FR-010: the plan is committed here, immediately before the
                    // proposal it explains, and a proposal with no plan behind it
                    // is refused rather than shown to a reviewer with the
                    // explanation missing.
                    if (planText is null)
                    {
                        throw new IllegalTransitionException(run.Stage, RunTrigger.ProposalCreated);
                    }

                    run.Plan = planText;
                    await TransitionAsync(run, RunTrigger.PlanProduced, ct);
                }

                await TransitionAsync(run, RunTrigger.ProposalCreated, ct);
                return proposal;
            }
        }

        // Out of turns. Not a no-change outcome — the agent never concluded
        // anything, so recording "no change was needed" would assert something
        // that was not established.
        throw new InvalidOperationException(
            $"Run {run.Id} reached {MaxTurnsPerSegment} turns without proposing or declining.");
    }

    /// <summary>
    /// Retrieves the baseline's context and puts it in front of the task, then
    /// records that retrieval happened.
    /// <para>
    /// The stage moves here rather than in <see cref="DriveAgentAsync"/> because
    /// that method infers retrieval from the first tool call, and a baseline run's
    /// first tool call is the proposal. Without this the run would reach a
    /// proposal having never been recorded as having retrieved anything.
    /// </para>
    /// </summary>
    private async Task SeedBaselineContextAsync(
        Run run,
        CapabilityContext context,
        List<ChatMessage> conversation,
        CancellationToken ct)
    {
        var retrieved = await baselineContext.RetrieveAsync(
            run.RepositoryId, run.TaskDescription, context.Budget.Remaining, ct);

        if (retrieved.Length > 0 && context.Budget.TryCharge(retrieved.Length))
        {
            conversation.Insert(0, new ChatMessage(ChatRole.User, retrieved));
        }

        // Recorded even when nothing came back. "Retrieval ran and found nothing"
        // is a different outcome from "retrieval never ran", and the no-change
        // reason the run ends with depends on telling them apart.
        await TransitionAsync(run, RunTrigger.ContextRetrieved, ct);
    }

    private async Task<bool> RunTestsAsync(
        Run run, CapabilityContext context, RepositoryFixture fixture, CancellationToken ct)
    {
        // The run's own command wins when it has one. An evaluation task names the
        // command that decides it, and grading every task against the fixture's
        // default would grade each one against every other task's outstanding
        // defect.
        var command = run.VerifyCommandName
            ?? FirstVerifyCommand(fixture.TestConfigJson)
            ?? throw new InvalidOperationException(
                $"Fixture '{fixture.Slug}' defines no test command to verify against.");

        var outcome = await capabilities.InvokeAsync(
            context,
            "run_tests",
            $$"""{"command_name":"{{command}}"}""",
            InvocationSurface.Orchestrator,
            ct);

        using var payload = System.Text.Json.JsonDocument.Parse(outcome.Content);
        return payload.RootElement.GetProperty("passed").GetBoolean();
    }

    /// <summary>
    /// The command the fixture nominates for verification, falling back to the
    /// first declared command.
    /// </summary>
    private static string? FirstVerifyCommand(string testConfigJson)
    {
        using var config = System.Text.Json.JsonDocument.Parse(testConfigJson);

        if (!config.RootElement.TryGetProperty("commands", out var commands))
        {
            return null;
        }

        string? first = null;

        foreach (var command in commands.EnumerateArray())
        {
            var name = command.GetProperty("name").GetString();
            first ??= name;

            if (command.TryGetProperty("purpose", out var purpose) &&
                purpose.GetString() == "verify")
            {
                return name;
            }
        }

        return first;
    }

    /// <summary>
    /// What the agent is told about the failure it is revising.
    /// <para>
    /// Test output only — the failure is the evidence, and it has already been
    /// redacted on the way out of the container (FR-025b). No instruction to
    /// "try harder" is added: the agent already has the task.
    /// </para>
    /// </summary>
    private async Task<string> RevisionPromptAsync(Guid runId, CancellationToken ct)
    {
        var proposal = await proposals.FindCurrentForRunAsync(runId, ct);
        var paths = proposal is null || proposal.AffectedPaths.Length == 0
            ? "(not recorded)"
            : string.Join(", ", proposal.AffectedPaths);

        return
            "The change was applied and the tests failed. The files you changed were: " +
            paths + ". Revise the change.";
    }

    private async Task<RunStage> EndWithNoChangeAsync(Run run, CancellationToken ct)
    {
        // The reason follows from how far the run got, not from asking the model
        // to classify its own outcome. Still at Retrieving means it never called
        // anything, so it had no context; past that means it looked and declined.
        var stage = run.Stage == RunStage.Planning ? RunStage.Proposing : run.Stage;

        if (!NoChangeOutcome.IsReachableFrom(stage))
        {
            throw new IllegalTransitionException(run.Stage, RunTrigger.NoModificationProposed);
        }

        var reason = NoChangeOutcome.ReasonFor(stage);

        if (stage != run.Stage)
        {
            // The agent retrieved, so a plan is owed before Proposing even when
            // the conclusion is that nothing should change.
            run.Plan ??= "No change was needed.";
            await TransitionAsync(run, RunTrigger.PlanProduced, ct);
        }

        await TransitionAsync(run, NoChangeOutcome.TriggerFor(reason), ct, reason);
        await workspaces.DestroyAsync(run.Id, ct);

        return run.Stage;
    }

    private async Task FailAsync(Run run, OutcomeReason? reason, string detail, CancellationToken ct)
    {
        try
        {
            // Not the caller's token: a failing run must record why it failed
            // even when the failure was a cancellation of the work around it.
            await TransitionAsync(run, RunTrigger.Failed, CancellationToken.None, reason, detail);
            await workspaces.DestroyAsync(run.Id, CancellationToken.None);
        }
        catch (Exception)
        {
            // The original exception is the one the caller needs. Losing it to a
            // secondary failure while recording would hide the actual cause.
        }
    }

    /// <summary>
    /// Applies a transition and persists it before anything acts on the new
    /// stage. Persisting after would leave a window where the run had done the
    /// work of a stage it was not recorded as being in.
    /// </summary>
    private async Task TransitionAsync(
        Run run,
        RunTrigger trigger,
        CancellationToken ct,
        OutcomeReason? reason = null,
        string? detail = null)
    {
        var from = run.Stage;

        if (!RunStateMachine.TryTransition(from, trigger, out var to))
        {
            // FR-009: a transition the table does not permit is recorded as
            // rejected and then thrown. Recording before throwing is the point —
            // the alternative leaves an illegal attempt visible only as a
            // stack trace in a log the audit trail does not include.
            await events.RecordAsync(
                new RunEvent
                {
                    RunId = run.Id,
                    EventType = RunEventType.StageTransitionRejected,
                    ArgumentsSummary =
                        $$"""{"fromStage":"{{from}}","attemptedTrigger":"{{trigger}}"}""",
                    Status = RunEventStatus.Failed,
                    ErrorMessage = $"Transition from {from} on {trigger} is not permitted.",
                },
                CancellationToken.None);

            throw new IllegalTransitionException(from, trigger);
        }

        run.Stage = to;

        if (RunStateMachine.IsTerminal(to))
        {
            run.TerminalOutcome = RunStateMachine.ToOutcome(to);
            run.OutcomeReason = reason;
            run.FailureStage = to == RunStage.Failed ? from : null;
            run.EndedAt = DateTimeOffset.UtcNow;
        }
        else if (from == RunStage.Created)
        {
            run.StartedAt = DateTimeOffset.UtcNow;
        }

        await runs.UpdateAsync(run, ct);

        await events.RecordAsync(
            new RunEvent
            {
                RunId = run.Id,
                EventType = RunEventType.StageChanged,
                ArgumentsSummary =
                    $$"""{"fromStage":"{{from}}","trigger":"{{trigger}}","toStage":"{{to}}"{{(reason is null ? "" : $",\"outcomeReason\":\"{reason}\"")}}}""",
                Status = RunEventStatus.Succeeded,
                ErrorMessage = detail,
            },
            ct);

        if (!RunStateMachine.IsTerminal(to))
        {
            return;
        }

        // FR-030: a non-success outcome says which stage it happened at, not
        // just that it happened. "Failed" alone leaves a reader unable to tell a
        // provider outage during planning from a sandbox failure during testing.
        if (to != RunStage.Succeeded)
        {
            await events.RecordAsync(
                new RunEvent
                {
                    RunId = run.Id,
                    EventType = RunEventType.RunFailed,
                    ArgumentsSummary =
                        $$"""{"failureStage":"{{from}}","outcomeReason":"{{reason?.ToString() ?? "unspecified"}}","terminalOutcome":"{{run.TerminalOutcome}}"}""",
                    Status = RunEventStatus.Failed,
                    ErrorMessage = detail,
                },
                CancellationToken.None);
        }

        // Always last. The stream contract lets a client treat closure without
        // this frame as a transport drop rather than as completion, so emitting
        // it is what makes reconnect-versus-finished distinguishable.
        await events.RecordAsync(
            new RunEvent
            {
                RunId = run.Id,
                EventType = RunEventType.RunEnded,
                ArgumentsSummary =
                    $$"""{"terminalOutcome":"{{run.TerminalOutcome}}","outcomeReason":"{{reason?.ToString() ?? "unspecified"}}"}""",
                Status = RunEventStatus.Succeeded,
            },
            CancellationToken.None);
    }

    private async Task<Run> RequireRunAsync(Guid runId, CancellationToken ct) =>
        await runs.FindAsync(runId, ct)
        ?? throw new InvalidOperationException($"Run {runId} not found.");

    /// <summary>
    /// Maps a failure to a normative outcome reason (FR-008c). Anything the set
    /// does not name is recorded without a reason rather than with an invented
    /// one — the stage and the error message still say what happened.
    /// </summary>
    private static OutcomeReason? ReasonFor(Exception ex) => ex switch
    {
        ProviderUnavailableException => OutcomeReason.ProviderUnavailable,
        SandboxNotTerminableException => OutcomeReason.IsolatedEnvNotTerminable,
        SandboxUnavailableException => OutcomeReason.IsolatedEnvUnavailable,

        // Deliberately null. Picking the nearest-looking reason for an
        // unclassified failure would put a specific, wrong claim in the record,
        // and FR-008c's set exists so that reasons mean what they say.
        _ => null,
    };
}
