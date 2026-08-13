using System.Text.Json;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Capabilities;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Runs;
using RepoPilot.Domain.Workspace;

namespace RepoPilot.IntegrationTests.Runs;

/// <summary>
/// In-memory stand-ins for the stores, the provider, and the capability surface.
/// <para>
/// The orchestration tests are about control flow — how many revisions are
/// allowed, whether an approval is required before a write, what a run ends as.
/// Those questions are answered by the orchestrator alone, and running them
/// against PostgreSQL and a live model would test everything except the thing in
/// question while making the failures ambiguous. The persistence behaviour has
/// its own tests.
/// </para>
/// </summary>
internal sealed class InMemoryRunStore : IRunStore
{
    private readonly Dictionary<Guid, Run> _runs = [];

    public void Seed(Run run) => _runs[run.Id] = run;

    public Task<Run?> FindAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_runs.GetValueOrDefault(id));

    public Task<IReadOnlyList<Run>> ListAsync(
        Guid? repositoryId = null, RunStage? stage = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Run>>(
            [.. _runs.Values
                .Where(r => repositoryId is null || r.RepositoryId == repositoryId)
                .Where(r => stage is null || r.Stage == stage)]);

    public Task AddAsync(Run run, CancellationToken ct = default)
    {
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Run run, CancellationToken ct = default)
    {
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Run>> ListNonTerminalAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Run>>(
            [.. _runs.Values.Where(r => !RunStateMachine.IsTerminal(r.Stage))]);
}

internal sealed class InMemoryFixtureStore(RepositoryFixture fixture) : IRepositoryFixtureStore
{
    public Task<RepositoryFixture?> FindBySlugAsync(string slug, CancellationToken ct = default) =>
        Task.FromResult<RepositoryFixture?>(fixture.Slug == slug ? fixture : null);

    public Task<RepositoryFixture?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<RepositoryFixture?>(fixture.Id == id ? fixture : null);

    public Task<IReadOnlyList<RepositoryFixture>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RepositoryFixture>>([fixture]);

    public Task AddAsync(RepositoryFixture f, CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateAsync(RepositoryFixture f, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class InMemoryProposalStore : IProposalStore
{
    private readonly List<ChangeProposal> _proposals = [];

    public IReadOnlyList<ChangeProposal> All => _proposals;

    public Task<ChangeProposal?> FindAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_proposals.FirstOrDefault(p => p.Id == id));

    public Task<ChangeProposal?> FindCurrentForRunAsync(Guid runId, CancellationToken ct = default) =>
        Task.FromResult(_proposals.LastOrDefault(p => p.RunId == runId));

    public Task AddAsync(ChangeProposal proposal, CancellationToken ct = default)
    {
        _proposals.Add(proposal);
        return Task.CompletedTask;
    }

    public Task UpdateStatusAsync(
        Guid proposalId, ProposalDecisionStatus status, CancellationToken ct = default)
    {
        var proposal = _proposals.First(p => p.Id == proposalId);
        proposal.DecisionStatus = status;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly List<ApprovalDecision> _decisions = [];

    public IReadOnlyList<ApprovalDecision> All => _decisions;

    public Task AddAsync(ApprovalDecision decision, CancellationToken ct = default)
    {
        if (_decisions.Any(d => d.ProposalId == decision.ProposalId))
        {
            throw new ProposalAlreadyDecidedException(decision.ProposalId);
        }

        _decisions.Add(decision);
        return Task.CompletedTask;
    }

    public Task<ApprovalDecision?> FindByProposalAsync(Guid proposalId, CancellationToken ct = default) =>
        Task.FromResult(_decisions.FirstOrDefault(d => d.ProposalId == proposalId));

    public Task<IReadOnlyList<ApprovalDecision>> ListForRunAsync(
        Guid runId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApprovalDecision>>(
            [.. _decisions.Where(d => d.RunId == runId)]);
}

internal sealed class InMemoryRunEventStore : IRunEventStore
{
    private readonly List<RunEvent> _events = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<RunEvent> All
    {
        get { lock (_gate) { return [.. _events]; } }
    }

    public Task<RunEvent> AppendAsync(RunEvent runEvent, CancellationToken ct = default)
    {
        lock (_gate)
        {
            runEvent.Sequence = _events.Count(e => e.RunId == runEvent.RunId) + 1;
            _events.Add(runEvent);
            return Task.FromResult(runEvent);
        }
    }

    public Task<IReadOnlyList<RunEvent>> ListAsync(
        Guid runId, long afterSequence = 0, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<RunEvent>>(
                [.. _events.Where(e => e.RunId == runId && e.Sequence > afterSequence)]);
        }
    }
}

internal sealed class NullRunEventPublisher : IRunEventPublisher
{
    public void Publish(RunEvent runEvent)
    {
    }

    public async IAsyncEnumerable<RunEvent> SubscribeAsync(
        Guid runId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// A workspace that exists only as a temporary directory. The orchestration
/// tests care that a copy is made and destroyed, not what is in it.
/// </summary>
internal sealed class TempWorkspaceProvisioner : IWorkspaceProvisioner, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repopilot-orch-").FullName;

    public List<Guid> Created { get; } = [];

    public List<Guid> Destroyed { get; } = [];

    public string PathFor(Guid runId) => Path.Combine(_root, runId.ToString());

    public Task<WorkspaceRoot> CreateAsync(
        Guid runId, RepositoryFixture fixture, CancellationToken ct = default)
    {
        Created.Add(runId);
        Directory.CreateDirectory(PathFor(runId));
        return Task.FromResult(WorkspaceRoot.Writable(PathFor(runId)));
    }

    public Task DestroyAsync(Guid runId, CancellationToken ct = default)
    {
        Destroyed.Add(runId);

        if (Directory.Exists(PathFor(runId)))
        {
            Directory.Delete(PathFor(runId), recursive: true);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

/// <summary>
/// A model that follows a fixed script.
/// <para>
/// Scripted rather than live because the claims under test are about what the
/// backend does with the model's output, and a live model would make the tests
/// non-deterministic without making them test anything more. The scripts mirror
/// the shape a real response has: text and tool calls arrive in the same turn,
/// and <c>end_turn</c> means the model is genuinely finished.
/// </para>
/// </summary>
internal sealed class ScriptedAgent : IAgentTurnRunner
{
    private readonly Func<int, ChatCompletion> _script;

    private ScriptedAgent(Func<int, ChatCompletion> script) => _script = script;

    public int TurnCount { get; private set; }

    /// <summary>Plans and proposes in one turn, the way a real turn arrives.</summary>
    public static ScriptedAgent ThatProposes() => new(_ => new ChatCompletion(
        "Plan: add a null guard in the order lookup path.",
        [new ToolCall("call-1", "propose_patch", """{"summary":"guard","entries":[]}""")],
        ChatStopReason.ToolUse,
        default,
        "scripted"));

    /// <summary>Searches, then concludes nothing should change (FR-008b).</summary>
    public static ScriptedAgent ThatDeclinesAfterLooking() => new(turn => turn == 1
        ? new ChatCompletion(
            "Checking the order lookup path.",
            [new ToolCall("call-1", "search_code", """{"query":"order lookup"}""")],
            ChatStopReason.ToolUse,
            default,
            "scripted")
        : new ChatCompletion(
            "The guard is already present; no change is needed.",
            [],
            ChatStopReason.EndTurn,
            default,
            "scripted"));

    /// <summary>Never calls anything — it could not find the code at all.</summary>
    public static ScriptedAgent ThatCannotFindAnything() => new(_ => new ChatCompletion(
        "I could not locate code relevant to this task.",
        [],
        ChatStopReason.EndTurn,
        default,
        "scripted"));

    public Task<ChatCompletion> TurnAsync(
        string repositorySlug,
        IReadOnlyList<ChatMessage> conversation,
        EffortLevel effort,
        int maxOutputTokens,
        CancellationToken ct = default)
    {
        TurnCount++;
        return Task.FromResult(_script(TurnCount));
    }
}

/// <summary>
/// Stands in for the capability surface: records every invocation, creates a
/// proposal when the model proposes, and reports whatever test outcome the test
/// asked for.
/// </summary>
internal sealed class ScriptedCapabilities(
    InMemoryProposalStore proposals, bool testsPass) : ICapabilityInvoker
{
    public List<(string Name, InvocationSurface Surface)> Invocations { get; } = [];

    public int ApplyCount => Invocations.Count(i => i.Name == "apply_patch");

    public Task<CapabilityOutcome> InvokeAsync(
        CapabilityContext context,
        string capabilityName,
        string argumentsJson,
        InvocationSurface callerSurface,
        CancellationToken ct = default)
    {
        Invocations.Add((capabilityName, callerSurface));

        // The registry decides which surface may reach which capability; the
        // real invoker enforces it. Reproducing that here keeps the double from
        // being more permissive than the thing it replaces.
        var descriptor = CapabilityRegistry.Require(capabilityName);

        if (descriptor.Surface != callerSurface)
        {
            throw new CapabilitySurfaceViolationException(
                capabilityName, callerSurface, descriptor.Surface);
        }

        switch (capabilityName)
        {
            case "propose_patch":
                var entries = new List<ProposalEntry>
                {
                    new("src/Orders/OrderLookupService.cs", ProposalOperation.Modify, "guarded content"),
                };

                proposals.AddAsync(
                    new ChangeProposal
                    {
                        RunId = context.RunId,
                        EntriesJson = JsonSerializer.Serialize(entries),
                        UnifiedDiff = "--- a\n+++ b\n",
                        AffectedPaths = [.. entries.Select(e => e.Path)],
                        DiffHash = DiffHash.Compute(entries),
                    },
                    ct);

                return Task.FromResult(new CapabilityOutcome("proposal created", 1));

            case "run_tests":
                return Task.FromResult(new CapabilityOutcome(
                    $$"""{"passed":{{(testsPass ? "true" : "false")}},"exit_code":{{(testsPass ? 0 : 1)}},"output":"scripted","duration_ms":5,"timed_out":false}""",
                    5));

            default:
                return Task.FromResult(new CapabilityOutcome("ok", 1));
        }
    }
}
