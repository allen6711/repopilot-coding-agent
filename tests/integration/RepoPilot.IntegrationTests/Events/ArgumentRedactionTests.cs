using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RepoPilot.Agent.Capabilities;
using RepoPilot.Agent.Invocation;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Proposals;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Capabilities;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Workspace;
using RepoPilot.Infrastructure.Events;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.Infrastructure.Proposals;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Events;

/// <summary>
/// FR-027a: no secret value reaches a recorded argument summary.
/// <para>
/// The event table is the one place in the system that is meant to be read
/// later, by people, possibly in bulk. A credential that lands here has been
/// written somewhere nobody thinks to look for credentials — which is worse than
/// leaking it somewhere obvious, because nothing will ever prompt a rotation.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ArgumentRedactionTests(PostgresFixture postgres) : IDisposable
{
    /// <summary>Assembled at runtime so no literal credential is committed.</summary>
    private static readonly string[] Secrets =
    [
        "ghp_" + new string('a', 36),
        "xox" + "b-000000000000-111111111111-" + new string('c', 24),
        "AKIA" + new string('Q', 16),
        "sk-ant-" + new string('d', 40),
    ];

    private readonly string _workspace =
        Directory.CreateTempSubdirectory("repopilot-redaction-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(Run Run, RepositoryFixture Fixture)> SeedAsync(RepoPilotDbContext db)
    {
        var fixture = new RepositoryFixture
        {
            Slug = "redaction-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Redaction fixture",
            RootPath = _workspace,
            TestConfigJson = """{"slug":"redaction","commands":[]}""",
        };

        var run = new Run
        {
            RepositoryId = fixture.Id,
            TaskDescription = "Rotate the credentials.",
            Stage = Domain.Runs.RunStage.Proposing,
        };

        db.Repositories.Add(fixture);
        db.Runs.Add(run);
        await db.SaveChangesAsync();

        return (run, fixture);
    }

    private ToolInvoker BuildInvoker(RepoPilotDbContext db) => new(
        [
            new ReadFileCapability(new IndexingOptions()),
            new ProposePatchCapability(
                new EfProposalStore(db),
                new EfRunStore(db),
                new ProposalValidator(new ProposalLimitOptions()),
                new DiffRenderer()),
        ],
        new RunEventRecorder(new EfRunEventStore(db), new InProcessRunEventPublisher()));

    private CapabilityContext Context(Run run, RepositoryFixture fixture) =>
        new(run.Id, fixture.Id, WorkspaceRoot.Writable(_workspace), new RunContextBudget(1_000_000));

    [RequiresDockerFact]
    public async Task NoSecretValueAppearsInAnyRecordedSummaryAcrossARun()
    {
        await using var db = postgres.CreateContext();
        var (run, fixture) = await SeedAsync(db);

        var invoker = BuildInvoker(db);
        var context = Context(run, fixture);

        // A file whose content is a credentials block. This is the realistic
        // case: an agent proposing a change to a config file it read.
        var secretBearingContent = string.Join(
            "\n",
            Secrets.Select((s, i) => $"TOKEN_{i}={s}"));

        await invoker.InvokeAsync(
            context,
            "propose_patch",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                summary = "Rotate tokens",
                entries = new[]
                {
                    new
                    {
                        path = "config/appsettings.json",
                        operation = "modify",
                        new_content = secretBearingContent,
                    },
                },
            }),
            InvocationSurface.Model);

        // A read whose arguments carry a secret in a non-content field.
        try
        {
            await invoker.InvokeAsync(
                context,
                "read_file",
                $$"""{"path":"config/{{Secrets[0]}}.json"}""",
                InvocationSurface.Model);
        }
        catch (Exception)
        {
            // The read fails — that path does not exist — and the failure is
            // itself recorded, which is exactly the summary worth checking.
        }

        await using var fresh = postgres.CreateContext();

        var recorded = await fresh.RunEvents
            .Where(e => e.RunId == run.Id)
            .ToListAsync();

        Assert.NotEmpty(recorded);

        foreach (var runEvent in recorded)
        {
            var text = (runEvent.ArgumentsSummary ?? "") + (runEvent.ErrorMessage ?? "");

            foreach (var secret in Secrets)
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }
    }

    [RequiresDockerFact]
    public async Task ProposedFileContentIsNeverStoredInAnEventSummary()
    {
        await using var db = postgres.CreateContext();
        var (run, fixture) = await SeedAsync(db);

        var marker = "UNIQUE-CONTENT-MARKER-" + Guid.NewGuid().ToString("N");
        var content = $"namespace Sample;\n\n// {marker}\npublic sealed class Service {{ }}\n";

        await BuildInvoker(db).InvokeAsync(
            Context(run, fixture),
            "propose_patch",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                summary = "Add a service",
                entries = new[]
                {
                    new { path = "src/Service.cs", operation = "create", new_content = content },
                },
            }),
            InvocationSurface.Model);

        await using var fresh = postgres.CreateContext();

        var summaries = await fresh.RunEvents
            .Where(e => e.RunId == run.Id)
            .Select(e => e.ArgumentsSummary)
            .ToListAsync();

        // The content lives in the proposal, where a reviewer reads it as a
        // diff. Duplicating it into the audit trail would make that table a
        // second copy of the repository with none of its access controls.
        Assert.All(summaries, s => Assert.DoesNotContain(marker, s ?? "", StringComparison.Ordinal));

        // And it really was proposed, so the absence above is redaction rather
        // than the call not having happened.
        var proposal = await fresh.ChangeProposals.SingleAsync(p => p.RunId == run.Id);
        Assert.Contains(marker, proposal.UnifiedDiff, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task ThePathAndOperationSurviveRedaction()
    {
        await using var db = postgres.CreateContext();
        var (run, fixture) = await SeedAsync(db);

        await BuildInvoker(db).InvokeAsync(
            Context(run, fixture),
            "propose_patch",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                summary = "Add a service",
                entries = new[]
                {
                    new
                    {
                        path = "src/Orders/OrderLookupService.cs",
                        operation = "modify",
                        new_content = "class X { }",
                    },
                },
            }),
            InvocationSurface.Model);

        await using var fresh = postgres.CreateContext();

        var summary = await fresh.RunEvents
            .Where(e => e.RunId == run.Id && e.ToolName == "propose_patch")
            .Select(e => e.ArgumentsSummary)
            .FirstAsync();

        // Redaction that removed the path too would leave a record saying only
        // that something was proposed, which is not an audit trail.
        Assert.Contains("src/Orders/OrderLookupService.cs", summary!, StringComparison.Ordinal);
    }
}
