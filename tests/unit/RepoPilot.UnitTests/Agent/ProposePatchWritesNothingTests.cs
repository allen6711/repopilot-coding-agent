using System.Security.Cryptography;
using System.Text.Json;
using RepoPilot.Agent.Capabilities;
using RepoPilot.Agent.Invocation;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Proposals;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Capabilities;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Domain.Workspace;
using RepoPilot.Infrastructure.Proposals;
using Xunit;

namespace RepoPilot.UnitTests.Agent;

/// <summary>
/// Principle I, at the one capability that could break it quietly.
/// <para>
/// <c>propose_patch</c> is handed the complete new content of every file the
/// agent wants changed. Everything it needs to write is in its hand; the only
/// reason nothing is written is that it has no writer. That is a claim about the
/// code as it stands today, and the next person to add a dependency to this type
/// will not be reminded of it — so it is asserted here against a real directory
/// rather than left to the constructor to imply.
/// </para>
/// <para>
/// The assertions are on the filesystem, not on a return value. A capability that
/// wrote and then reported success would satisfy any check made on what it
/// returned.
/// </para>
/// </summary>
public sealed class ProposePatchWritesNothingTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("repopilot-propose-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException)
        {
        }
    }

    // ---- doubles -------------------------------------------------------------

    private sealed class InMemoryProposalStore : IProposalStore
    {
        public List<ChangeProposal> Proposals { get; } = [];

        public Task<ChangeProposal?> FindAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Proposals.FirstOrDefault(p => p.Id == id));

        public Task<ChangeProposal?> FindCurrentForRunAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult(Proposals.LastOrDefault(p => p.RunId == runId));

        public Task<IReadOnlyList<ChangeProposal>> ListForRunAsync(
            Guid runId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChangeProposal>>(
                [.. Proposals.Where(p => p.RunId == runId)]);

        public Task AddAsync(ChangeProposal proposal, CancellationToken ct = default)
        {
            Proposals.Add(proposal);
            return Task.CompletedTask;
        }

        public Task UpdateStatusAsync(
            Guid proposalId, ProposalDecisionStatus status, CancellationToken ct = default)
        {
            var proposal = Proposals.Single(p => p.Id == proposalId);
            proposal.DecisionStatus = status;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryRunStore(Run run) : IRunStore
    {
        public Task<Run?> FindAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<Run?>(id == run.Id ? run : null);

        public Task<IReadOnlyList<Run>> ListAsync(
            Guid? repositoryId = null, RunStage? stage = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Run>>([run]);

        public Task AddAsync(Run added, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(Run updated, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Run>> ListNonTerminalAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Run>>([]);
    }

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

    // ---- scaffolding ---------------------------------------------------------

    /// <summary>
    /// A working copy with a file to modify and a directory to create into.
    /// </summary>
    private WorkspaceRoot CreateWorkingCopy()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "Orders"));

        File.WriteAllText(
            Path.Combine(_root, "src", "Orders", "OrderLookupService.cs"),
            "namespace Orders;\n\npublic sealed class OrderLookupService\n{\n}\n");

        File.WriteAllText(Path.Combine(_root, "README.md"), "# Fixture\n");

        return WorkspaceRoot.Writable(_root);
    }

    /// <summary>
    /// Path, size, and content hash of every file under the working copy.
    /// <para>
    /// Content and size together: a rewrite with identical bytes is not a
    /// modification anyone can act on, but a truncation to zero would be, and a
    /// hash alone would catch the second only because the first is impossible.
    /// </para>
    /// </summary>
    private Dictionary<string, string> Snapshot()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(file);

            snapshot[Path.GetRelativePath(_root, file).Replace('\\', '/')] =
                $"{new FileInfo(file).Length}:{Convert.ToHexString(SHA256.HashData(stream))}";
        }

        return snapshot;
    }

    private static string Arguments(params (string Path, string Operation, string Content)[] entries) =>
        JsonSerializer.Serialize(new
        {
            summary = "guard the shipping address projection",
            entries = entries.Select(e => new
            {
                path = e.Path,
                operation = e.Operation,
                new_content = e.Content,
            }),
        });

    private (ProposePatchCapability Capability, InMemoryProposalStore Proposals, Run Run) Build()
    {
        var run = new Run
        {
            RepositoryId = Guid.CreateVersion7(),
            TaskDescription = "add the missing guard",
            Stage = RunStage.Proposing,
        };

        var proposals = new InMemoryProposalStore();

        var capability = new ProposePatchCapability(
            proposals,
            new InMemoryRunStore(run),
            new ProposalValidator(new ProposalLimitOptions()),
            new DiffRenderer());

        return (capability, proposals, run);
    }

    private CapabilityContext Context(WorkspaceRoot workspace, Run run) =>
        new(run.Id, run.RepositoryId, workspace, new RunContextBudget(60_000));

    // ---- the property --------------------------------------------------------

    [Fact]
    public async Task ModifyingAnExistingFileWritesNothing()
    {
        var workspace = CreateWorkingCopy();
        var (capability, proposals, run) = Build();

        var before = Snapshot();

        await capability.InvokeAsync(
            Context(workspace, run),
            Arguments(("src/Orders/OrderLookupService.cs", "modify", "namespace Orders;\n// rewritten\n")));

        // The proposal exists, so the capability really ran. Without this the
        // assertion below would pass just as happily against a no-op.
        Assert.Single(proposals.Proposals);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task CreatingANewFileWritesNothing()
    {
        var workspace = CreateWorkingCopy();
        var (capability, proposals, run) = Build();

        var before = Snapshot();

        await capability.InvokeAsync(
            Context(workspace, run),
            Arguments(("src/Orders/OrderGuard.cs", "create", "namespace Orders;\n")));

        Assert.Single(proposals.Proposals);

        // A create is the case where "wrote nothing" and "changed nothing" come
        // apart: a new file leaves every existing one untouched. The snapshot
        // covers the whole tree, so an added path fails the comparison.
        Assert.Equal(before, Snapshot());
        Assert.False(File.Exists(Path.Combine(_root, "src", "Orders", "OrderGuard.cs")));
    }

    [Fact]
    public async Task ProposingSeveralFilesAtOnceWritesNothing()
    {
        var workspace = CreateWorkingCopy();
        var (capability, proposals, run) = Build();

        var before = Snapshot();

        await capability.InvokeAsync(
            Context(workspace, run),
            Arguments(
                ("src/Orders/OrderLookupService.cs", "modify", "// one\n"),
                ("README.md", "modify", "# two\n"),
                ("src/Orders/New.cs", "create", "// three\n")));

        Assert.Single(proposals.Proposals);
        Assert.Equal(3, proposals.Proposals[0].AffectedPaths.Length);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task ProposingTwiceWritesNothing()
    {
        var workspace = CreateWorkingCopy();
        var (capability, proposals, run) = Build();

        var before = Snapshot();

        await capability.InvokeAsync(
            Context(workspace, run),
            Arguments(("src/Orders/OrderLookupService.cs", "modify", "// first\n")));

        await capability.InvokeAsync(
            Context(workspace, run),
            Arguments(("src/Orders/OrderLookupService.cs", "modify", "// revised\n")));

        // A revision proposes against the same working copy the first attempt
        // saw. If the first had written, the second would be diffed against the
        // wrong baseline and the approval would bind content nobody reviewed.
        Assert.Equal(2, proposals.Proposals.Count);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task ARejectedProposalWritesNothingEither()
    {
        var workspace = CreateWorkingCopy();
        var (capability, _, run) = Build();

        var before = Snapshot();

        // Escapes the workspace, so the validator refuses it. The refusal path is
        // worth its own case: a capability that wrote before validating would
        // leave the file behind and report a refusal.
        await Assert.ThrowsAnyAsync<Exception>(() => capability.InvokeAsync(
            Context(workspace, run),
            Arguments(("../outside.cs", "create", "// escaped\n"))));

        Assert.Equal(before, Snapshot());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.cs")));
    }

    [Fact]
    public async Task NoStagingOrBackupArtefactsAreLeftBehind()
    {
        var workspace = CreateWorkingCopy();
        var (capability, _, run) = Build();

        await capability.InvokeAsync(
            Context(workspace, run),
            Arguments(("src/Orders/OrderLookupService.cs", "modify", "// rewritten\n")));

        // Apply stages through .repopilot-staged and .repopilot-backup files.
        // Proposing shares none of that machinery, and a stray artefact here
        // would mean it had started to.
        Assert.Empty(Directory.EnumerateFiles(_root, "*.repopilot-*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// The same property through the real invoker, which is how the capability is
    /// actually reached. It also shows the audit record being written for an
    /// invocation that wrote nothing — the record is evidence of a proposal, not
    /// of a change.
    /// </summary>
    [Fact]
    public async Task ProposingThroughTheInvokerWritesNothingAndIsRecorded()
    {
        var workspace = CreateWorkingCopy();
        var (capability, proposals, run) = Build();

        var events = new RecordingEventStore();
        var invoker = new ToolInvoker([capability], new RunEventRecorder(events, new NoOpPublisher()));

        var before = Snapshot();

        await invoker.InvokeAsync(
            Context(workspace, run),
            "propose_patch",
            Arguments(("src/Orders/OrderLookupService.cs", "modify", "// rewritten\n")),
            InvocationSurface.Model);

        Assert.Single(proposals.Proposals);
        Assert.Equal(before, Snapshot());

        var recorded = Assert.Single(events.Events);
        Assert.Equal("propose_patch", recorded.ToolName);
        Assert.Equal(RunEventStatus.Succeeded, recorded.Status);
    }

    /// <summary>
    /// The structural reason the above holds: nothing that can write is reachable
    /// from this type. Asserted so that adding one is a test failure rather than a
    /// silent widening of what proposing can do.
    /// </summary>
    [Fact]
    public void TheCapabilityHasNoFileWritingDependency()
    {
        var parameters = typeof(ProposePatchCapability)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToList();

        Assert.DoesNotContain(parameters, t =>
            t.Name.Contains("Workspace", StringComparison.Ordinal) ||
            t.Name.Contains("WorkingCopy", StringComparison.Ordinal) ||
            t.Name.Contains("File", StringComparison.Ordinal) ||
            t.Name.Contains("Writer", StringComparison.Ordinal));
    }
}
