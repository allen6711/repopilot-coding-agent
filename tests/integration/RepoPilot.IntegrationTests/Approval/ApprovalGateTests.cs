using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Agent.Capabilities;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Runs;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Runs;
using RepoPilot.Domain.Workspace;
using RepoPilot.Infrastructure.Events;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Workspace;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Approval;

/// <summary>
/// Principle I, in executable form. These are the tests tasks.md marks as
/// release blockers: a failure here means an unapproved write is possible, which
/// is the one thing the whole design exists to prevent.
/// <para>
/// Each asserts on the filesystem, not on a return value. A refusal that still
/// wrote the file would satisfy an exception-only assertion.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ApprovalGateTests(PostgresFixture postgres) : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed record Scaffold(
        RepoPilotDbContext Db,
        Run Run,
        ChangeProposal Proposal,
        WorkspaceRoot Workspace,
        string FixturePath,
        List<ProposalEntry> Entries);

    private const string OriginalContent = "original content\n";
    private const string ProposedContent = "changed content\n";

    /// <summary>Builds a fixture, a working copy, a run, and a pending proposal.</summary>
    private async Task<Scaffold> ScaffoldAsync(RepoPilotDbContext db, RunStage stage = RunStage.Applying)
    {
        var temp = Path.Combine(Path.GetTempPath(), "repopilot-gate-" + Guid.NewGuid().ToString("N"));
        _roots.Add(temp);

        var fixturePath = Path.Combine(temp, "fixture");
        Directory.CreateDirectory(Path.Combine(fixturePath, "src"));
        await File.WriteAllTextAsync(Path.Combine(fixturePath, "src", "Service.cs"), OriginalContent);

        var repository = new RepositoryFixture
        {
            Slug = "gate-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Gate fixture",
            RootPath = fixturePath,
            TestConfigJson = """{"slug":"gate","commands":[]}""",
        };
        db.Repositories.Add(repository);

        var run = new Run
        {
            RepositoryId = repository.Id,
            TaskDescription = "Change the service.",
            Stage = stage,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();

        var workspaceRoot = Path.Combine(temp, "workspace");
        var manager = CreateManager(db, workspaceRoot);
        var workspace = await manager.CreateAsync(run.Id, repository);

        var entries = new List<ProposalEntry>
        {
            new("src/Service.cs", ProposalOperation.Modify, ProposedContent),
        };

        var proposal = new ChangeProposal
        {
            RunId = run.Id,
            EntriesJson = JsonSerializer.Serialize(entries),
            UnifiedDiff = "--- a/src/Service.cs\n+++ b/src/Service.cs\n",
            AffectedPaths = ["src/Service.cs"],
            DiffHash = DiffHash.Compute(entries),
        };
        db.ChangeProposals.Add(proposal);
        await db.SaveChangesAsync();

        return new Scaffold(db, run, proposal, workspace, fixturePath, entries);
    }

    private static WorkingCopyManager CreateManager(RepoPilotDbContext db, string workspaceRoot) =>
        new(new EfWorkingCopyStore(db),
            new WorkspaceOptions { Root = workspaceRoot },
            NullLogger<WorkingCopyManager>.Instance);

    private static ApplyPatchCapability CreateApply(RepoPilotDbContext db, WorkingCopyManager manager) =>
        new(new EfProposalStore(db), new EfApprovalStore(db), new EfRunStore(db), manager);

    private static DecideProposalUseCase CreateDecide(RepoPilotDbContext db) =>
        new(new EfProposalStore(db),
            new EfApprovalStore(db),
            new EfRunStore(db),
            new RunEventRecorder(new EfRunEventStore(db), new InProcessRunEventPublisher()));

    private static CapabilityContext Context(Scaffold s) =>
        new(s.Run.Id, s.Run.RepositoryId, s.Workspace, new RunContextBudget(60_000));

    private static string ReadWorkspaceFile(Scaffold s) =>
        File.ReadAllText(Path.Combine(s.Workspace.FullPath, "src", "Service.cs"));

    private static string HashTree(string root)
    {
        var accumulator = new List<byte>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            accumulator.AddRange(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file)));
            accumulator.AddRange(File.ReadAllBytes(file));
        }

        return Convert.ToHexStringLower(SHA256.HashData([.. accumulator]));
    }

    // ---------------------------------------------------------------- T061

    [RequiresDockerFact]
    public async Task ApplyingWithoutAnApprovalIsRefusedAndWritesNothing()
    {
        await using var db = postgres.CreateContext();
        var s = await ScaffoldAsync(db);
        var apply = CreateApply(db, CreateManager(db, Path.GetDirectoryName(s.Workspace.FullPath)!));

        var before = HashTree(s.Workspace.FullPath);

        var ex = await Assert.ThrowsAsync<ApplyRefusedException>(() => apply.InvokeAsync(
            Context(s), $$"""{"proposal_id":"{{s.Proposal.Id}}"}"""));

        Assert.Equal(ApplyRefusalReason.ApprovalRequired, ex.Reason);
        Assert.Equal(OriginalContent, ReadWorkspaceFile(s));
        Assert.Equal(before, HashTree(s.Workspace.FullPath));
    }

    // ---------------------------------------------------------------- T062

    [RequiresDockerFact]
    public async Task ARejectedProposalNeverApplies()
    {
        await using var db = postgres.CreateContext();
        var s = await ScaffoldAsync(db);

        await CreateDecide(db).DecideAsync(
            s.Proposal.Id, ApprovalDecisionKind.Reject, s.Proposal.DiffHash, "reviewer@example.test");

        var before = HashTree(s.Workspace.FullPath);
        var apply = CreateApply(db, CreateManager(db, Path.GetDirectoryName(s.Workspace.FullPath)!));

        var ex = await Assert.ThrowsAsync<ApplyRefusedException>(() => apply.InvokeAsync(
            Context(s), $$"""{"proposal_id":"{{s.Proposal.Id}}"}"""));

        Assert.Equal(ApplyRefusalReason.ApprovalRequired, ex.Reason);
        Assert.Equal(OriginalContent, ReadWorkspaceFile(s));
        Assert.Equal(before, HashTree(s.Workspace.FullPath));
    }

    // ---------------------------------------------------------------- T063

    [RequiresDockerFact]
    public async Task AHashMismatchIsRefusedAndWritesNothing()
    {
        // The scenario FR-020 exists for: the stored content changed after the
        // reviewer decided, so what would be written is not what was shown.
        await using var db = postgres.CreateContext();
        var s = await ScaffoldAsync(db);

        await CreateDecide(db).DecideAsync(
            s.Proposal.Id, ApprovalDecisionKind.Approve, s.Proposal.DiffHash, "reviewer@example.test");

        var tampered = new List<ProposalEntry>
        {
            new("src/Service.cs", ProposalOperation.Modify, "something else entirely\n"),
        };
        var stored = await db.ChangeProposals.FindAsync(s.Proposal.Id);
        stored!.EntriesJson = JsonSerializer.Serialize(tampered);
        await db.SaveChangesAsync();

        var apply = CreateApply(db, CreateManager(db, Path.GetDirectoryName(s.Workspace.FullPath)!));

        var ex = await Assert.ThrowsAsync<ApplyRefusedException>(() => apply.InvokeAsync(
            Context(s), $$"""{"proposal_id":"{{s.Proposal.Id}}"}"""));

        Assert.Equal(ApplyRefusalReason.DiffHashMismatch, ex.Reason);
        Assert.Equal(OriginalContent, ReadWorkspaceFile(s));
    }

    [RequiresDockerFact]
    public async Task ApplyingOutsideTheApplyingStageIsRefused()
    {
        await using var db = postgres.CreateContext();
        var s = await ScaffoldAsync(db, RunStage.AwaitingApproval);

        await CreateDecide(db).DecideAsync(
            s.Proposal.Id, ApprovalDecisionKind.Approve, s.Proposal.DiffHash, "reviewer@example.test");

        var apply = CreateApply(db, CreateManager(db, Path.GetDirectoryName(s.Workspace.FullPath)!));

        var ex = await Assert.ThrowsAsync<ApplyRefusedException>(() => apply.InvokeAsync(
            Context(s), $$"""{"proposal_id":"{{s.Proposal.Id}}"}"""));

        Assert.Equal(ApplyRefusalReason.IllegalStage, ex.Reason);
        Assert.Equal(OriginalContent, ReadWorkspaceFile(s));
    }

    [RequiresDockerFact]
    public async Task AnApprovedProposalApplies()
    {
        // The gate must permit, not only refuse. A gate that never opens would
        // pass every other test here.
        await using var db = postgres.CreateContext();
        var s = await ScaffoldAsync(db);

        await CreateDecide(db).DecideAsync(
            s.Proposal.Id, ApprovalDecisionKind.Approve, s.Proposal.DiffHash, "reviewer@example.test");

        var apply = CreateApply(db, CreateManager(db, Path.GetDirectoryName(s.Workspace.FullPath)!));
        await apply.InvokeAsync(Context(s), $$"""{"proposal_id":"{{s.Proposal.Id}}"}""");

        Assert.Equal(ProposedContent, ReadWorkspaceFile(s));
    }

    // ---------------------------------------------------------------- T063a (SC-002)

    [RequiresDockerFact]
    public async Task ApplyingNeverTouchesTheRegisteredFixture()
    {
        // SC-002, per run rather than across the evaluation set: the fixture is
        // the source every working copy is made from, and a write to it would
        // silently corrupt every later run.
        await using var db = postgres.CreateContext();
        var s = await ScaffoldAsync(db);

        var fixtureBefore = HashTree(s.FixturePath);

        await CreateDecide(db).DecideAsync(
            s.Proposal.Id, ApprovalDecisionKind.Approve, s.Proposal.DiffHash, "reviewer@example.test");

        var apply = CreateApply(db, CreateManager(db, Path.GetDirectoryName(s.Workspace.FullPath)!));
        await apply.InvokeAsync(Context(s), $$"""{"proposal_id":"{{s.Proposal.Id}}"}""");

        Assert.Equal(fixtureBefore, HashTree(s.FixturePath));
        Assert.Equal(OriginalContent,
            await File.ReadAllTextAsync(Path.Combine(s.FixturePath, "src", "Service.cs")));
    }

    // ---------------------------------------------------------------- T059

    [RequiresDockerFact]
    public async Task AnInterruptedApplyLeavesThePreApplyState()
    {
        // FR-016b. The second entry cannot be written — its path is a directory —
        // so the apply fails part-way. The first file must not be left changed.
        await using var db = postgres.CreateContext();
        var s = await ScaffoldAsync(db);
        var manager = CreateManager(db, Path.GetDirectoryName(s.Workspace.FullPath)!);

        Directory.CreateDirectory(Path.Combine(s.Workspace.FullPath, "src", "Blocked.cs"));

        var entries = new List<ProposalEntry>
        {
            new("src/Service.cs", ProposalOperation.Modify, ProposedContent),
            new("src/Blocked.cs", ProposalOperation.Modify, "cannot be written"),
        };

        Assert.ThrowsAny<Exception>(() => manager.Apply(s.Workspace, entries));

        Assert.Equal(OriginalContent, ReadWorkspaceFile(s));
    }

    [RequiresDockerFact]
    public async Task NoStagingOrBackupArtefactsSurviveAnApply()
    {
        // A leftover .repopilot-staged file would be indexed on a later pass and
        // would show up in a diff as a mysterious new file.
        await using var db = postgres.CreateContext();
        var s = await ScaffoldAsync(db);
        var manager = CreateManager(db, Path.GetDirectoryName(s.Workspace.FullPath)!);

        manager.Apply(s.Workspace, s.Entries);

        var leftovers = Directory
            .EnumerateFiles(s.Workspace.FullPath, "*.repopilot-*", SearchOption.AllDirectories)
            .ToList();

        Assert.Empty(leftovers);
    }
}
