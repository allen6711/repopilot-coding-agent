using RepoPilot.Agent.Invocation;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Capabilities;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Workspace;
using Xunit;

namespace RepoPilot.UnitTests.Agent;

/// <summary>
/// FR-027 and FR-006, from the failure side.
/// <para>
/// The interesting cases here are the ones that go wrong: a capability that
/// throws must still leave a recorded action, and output that would exceed the
/// budget must be refused rather than trimmed. Those are the invocations most
/// worth auditing and the easiest to lose.
/// </para>
/// </summary>
public sealed class ToolInvokerTests
{
    private sealed class RecordingEventStore : IRunEventStore
    {
        public List<RunEvent> Events { get; } = [];

        public Task<RunEvent> AppendAsync(RunEvent runEvent, CancellationToken ct = default)
        {
            runEvent.Sequence = Events.Count + 1;
            Events.Add(runEvent);
            return Task.FromResult(runEvent);
        }

        public Task<IReadOnlyList<RunEvent>> ListAsync(
            Guid runId, long afterSequence = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunEvent>>(Events);
    }

    private sealed class NoOpPublisher : IRunEventPublisher
    {
        public void Publish(RunEvent runEvent) { }

        public async IAsyncEnumerable<RunEvent> SubscribeAsync(
            Guid runId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubCapability(
        string name,
        Func<CapabilityContext, string, CapabilityResult> behaviour) : ICapability
    {
        public string Name { get; } = name;

        public Task<CapabilityResult> InvokeAsync(
            CapabilityContext context, string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(behaviour(context, argumentsJson));
    }

    private static (ToolInvoker Invoker, RecordingEventStore Store) Build(params ICapability[] caps)
    {
        var store = new RecordingEventStore();
        var recorder = new RunEventRecorder(store, new NoOpPublisher());
        return (new ToolInvoker(caps, recorder), store);
    }

    private static CapabilityContext Context(int budget = 10_000) =>
        new(Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            WorkspaceRoot.Writable(Path.GetTempPath()),
            new RunContextBudget(budget));

    [Fact]
    public async Task ASuccessfulInvocationIsRecorded()
    {
        var (invoker, store) = Build(
            new StubCapability("read_file", (_, _) => new CapabilityResult("contents", 8)));

        await invoker.InvokeAsync(
            Context(), "read_file", """{"path":"src/A.cs"}""", InvocationSurface.Model);

        var recorded = Assert.Single(store.Events);
        Assert.Equal(RunEventType.ToolCall, recorded.EventType);
        Assert.Equal("read_file", recorded.ToolName);
        Assert.Equal(RunEventStatus.Succeeded, recorded.Status);
        Assert.NotNull(recorded.DurationMs);
    }

    [Fact]
    public async Task AThrowingCapabilityStillRecordsAFailedAction()
    {
        // The property the finally block exists for. Without it, a refused path
        // access would leave no trace — and SC-010 counts refusals.
        var (invoker, store) = Build(
            new StubCapability("read_file", (_, _) =>
                throw new PathAccessRefusedException(
                    "../outside", AccessIntent.Read, PathRefusalReason.TraversalSegment)));

        await Assert.ThrowsAsync<PathAccessRefusedException>(() => invoker.InvokeAsync(
            Context(), "read_file", """{"path":"../outside"}""", InvocationSurface.Model));

        var recorded = Assert.Single(store.Events);
        Assert.Equal(RunEventStatus.Failed, recorded.Status);
        Assert.Equal("read_file", recorded.ToolName);
        Assert.NotNull(recorded.ErrorMessage);
    }

    [Fact]
    public async Task OutputExceedingTheBudgetIsRefusedRatherThanTruncated()
    {
        var (invoker, _) = Build(
            new StubCapability("read_file", (_, _) => new CapabilityResult(new string('x', 500), 500)));

        var context = Context(budget: 100);

        var ex = await Assert.ThrowsAsync<ContextBudgetExceededException>(
            () => invoker.InvokeAsync(
                context, "read_file", "{}", InvocationSurface.Model));

        Assert.Equal(500, ex.Requested);
        // Nothing was consumed: a refused charge must not partially deplete the
        // budget, or a sequence of refusals would starve the run.
        Assert.Equal(100, context.Budget.Remaining);
    }

    [Fact]
    public async Task ABudgetBreachIsRecordedAsAFailedAction()
    {
        var (invoker, store) = Build(
            new StubCapability("read_file", (_, _) => new CapabilityResult(new string('x', 500), 500)));

        await Assert.ThrowsAsync<ContextBudgetExceededException>(
            () => invoker.InvokeAsync(Context(100), "read_file", "{}", InvocationSurface.Model));

        Assert.Equal(RunEventStatus.Failed, Assert.Single(store.Events).Status);
    }

    [Fact]
    public async Task TheBudgetAccumulatesAcrossCallsWithinARun()
    {
        // Per run, not per call. A per-call limit would be defeated by making
        // more calls (FR-006).
        var (invoker, _) = Build(
            new StubCapability("read_file", (_, _) => new CapabilityResult("x", 40)));

        var context = Context(budget: 100);

        await invoker.InvokeAsync(context, "read_file", "{}", InvocationSurface.Model);
        await invoker.InvokeAsync(context, "read_file", "{}", InvocationSurface.Model);

        Assert.Equal(80, context.Budget.Consumed);

        await Assert.ThrowsAsync<ContextBudgetExceededException>(
            () => invoker.InvokeAsync(context, "read_file", "{}", InvocationSurface.Model));
    }

    [Fact]
    public async Task TheModelCannotInvokeAWriteCapability()
    {
        // Principle IV: if the model could call apply_patch, the transition into
        // the applying stage would follow from model output rather than from
        // backend code.
        var (invoker, _) = Build(
            new StubCapability("apply_patch", (_, _) => new CapabilityResult("applied", 0)));

        var ex = await Assert.ThrowsAsync<CapabilitySurfaceViolationException>(
            () => invoker.InvokeAsync(
                Context(), "apply_patch", "{}", InvocationSurface.Model));

        Assert.Equal(InvocationSurface.Orchestrator, ex.Allowed);
    }

    [Fact]
    public async Task TheModelCannotInvokeTheSandboxCapability()
    {
        var (invoker, _) = Build(
            new StubCapability("run_tests", (_, _) => new CapabilityResult("passed", 0)));

        await Assert.ThrowsAsync<CapabilitySurfaceViolationException>(
            () => invoker.InvokeAsync(Context(), "run_tests", "{}", InvocationSurface.Model));
    }

    [Fact]
    public async Task TheOrchestratorCanInvokeApplyPatch()
    {
        // The surface check must constrain, not forbid outright.
        var (invoker, _) = Build(
            new StubCapability("apply_patch", (_, _) => new CapabilityResult("applied", 0)));

        var outcome = await invoker.InvokeAsync(
            Context(), "apply_patch", "{}", InvocationSurface.Orchestrator);

        Assert.Equal("applied", outcome.Content);
    }

    [Fact]
    public async Task AnUnknownCapabilityIsRefused()
    {
        var (invoker, store) = Build();

        await Assert.ThrowsAsync<UnknownCapabilityException>(
            () => invoker.InvokeAsync(Context(), "run_shell", "{}", InvocationSurface.Model));

        // No audit record for a capability that does not exist — recording one
        // would imply the system has such a capability.
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task ARecordedSummaryIsNeverTheRawArguments()
    {
        var (invoker, store) = Build(
            new StubCapability("read_file", (_, _) => new CapabilityResult("contents", 8)));

        var secret = "ghp_" + new string('a', 36);

        await invoker.InvokeAsync(
            Context(),
            "read_file",
            $$"""{"path":"src/Service.cs","content":"{{secret}}"}""",
            InvocationSurface.Model);

        // FR-027a. The detailed rules live in ArgumentSummarizerTests; what
        // matters here is that the invoker routes through them rather than
        // persisting what it was handed.
        var summary = Assert.Single(store.Events).ArgumentsSummary!;

        Assert.DoesNotContain(secret, summary, StringComparison.Ordinal);
        Assert.Contains("src/Service.cs", summary, StringComparison.Ordinal);
    }
}
