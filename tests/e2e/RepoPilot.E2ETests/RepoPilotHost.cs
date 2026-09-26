using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;
using RepoPilot.Infrastructure.Indexing;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Providers;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.E2ETests;

/// <summary>
/// A running RepoPilot: real HTTP host, real PostgreSQL, real Docker sandbox,
/// real fixture on disk.
/// <para>
/// One thing is substituted — the model. <see cref="ScriptedChatAdapter"/>
/// stands in for the provider so a run is deterministic and needs no credential.
/// Everything the governance claims rest on is genuine: the approval gate, the
/// change-content hash, the file write, the isolated test execution, the audit
/// trail. Substituting the model removes only the part these tests are not
/// about — whether Claude picks a good fix — while leaving the part they are
/// about, which is what the system does with a fix once it has one.
/// </para>
/// </summary>
public sealed class RepoPilotHost : IAsyncLifetime
{
    /// <summary>Pre-baked by <c>sandbox/Dockerfile.dotnet</c>.</summary>
    public const string SandboxImage = "repopilot/fixture-dotnet:1";

    private PostgresFixture _postgres = null!;
    private WebApplicationFactory<Program>? _factory;

    /// <summary>The fixture directory these runs work against. Never written to.</summary>
    public string FixtureRoot { get; private set; } = string.Empty;

    /// <summary>Root under which run working copies are created and destroyed.</summary>
    public string WorkspaceRoot { get; private set; } = string.Empty;

    public Guid RepositoryId { get; private set; }

    /// <summary>Null when Docker was unavailable and the host did not start.</summary>
    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        UnavailableReason = DockerAvailability.UnavailableReason;

        if (UnavailableReason is not null)
        {
            return;
        }

        if (!await SandboxImageExistsAsync())
        {
            // Stated rather than silently skipped. The image is built by
            // `docker build -f sandbox/Dockerfile.dotnet -t repopilot/fixture-dotnet:1 .`
            // and a missing one is a setup gap, not an absent daemon.
            UnavailableReason =
                $"the sandbox image '{SandboxImage}' is not built. " +
                "Run: docker build -f sandbox/Dockerfile.dotnet -t repopilot/fixture-dotnet:1 .";
            return;
        }

        _postgres = new PostgresFixture();
        await _postgres.InitializeAsync();

        WorkspaceRoot = Directory.CreateTempSubdirectory("repopilot-e2e-workspace-").FullName;
        FixtureRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../../evals/fixtures/sample-dotnet-api"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:RepoPilot", _postgres.ConnectionString!);
            builder.UseSetting("RepoPilot:Workspace:Root", WorkspaceRoot);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IChatProviderAdapter>();
                services.AddSingleton<IChatProviderAdapter, ScriptedChatAdapter>();
            });
        });

        // Started before anything is seeded: startup recovery ends every
        // non-terminal run it finds, so a run seeded first would arrive failed.
        _factory.CreateClient().Dispose();

        RepositoryId = await RegisterAndIndexAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        if (WorkspaceRoot.Length > 0 && Directory.Exists(WorkspaceRoot))
        {
            Directory.Delete(WorkspaceRoot, recursive: true);
        }
    }

    public HttpClient CreateClient() => _factory!.CreateClient();

    public RepoPilotDbContext CreateContext() => _postgres.CreateContext();

    private static async Task<bool> SandboxImageExistsAsync() =>
        (await DockerCli.RunAsync($"images -q {SandboxImage}")).Trim().Length > 0;

    private async Task<Guid> RegisterAndIndexAsync()
    {
        await using var db = CreateContext();

        var repository = new RepositoryFixture
        {
            Slug = "sample-dotnet-api",
            DisplayName = "Sample Orders Service",
            RootPath = FixtureRoot,
            TestConfigJson = await File.ReadAllTextAsync(
                Path.Combine(FixtureRoot, "repopilot.fixture.json")),
        };

        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        await new IndexingService(
                db,
                new DeterministicEmbeddingAdapter(RepoPilotDbContext.EmbeddingDimensions),
                new IndexingOptions { EmbeddingDimensions = RepoPilotDbContext.EmbeddingDimensions },
                NullLogger<IndexingService>.Instance)
            .RebuildAsync(repository);

        return repository.Id;
    }
}

/// <summary>Collection marker: one host, shared by the end-to-end classes.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RepoPilotHostCollection : ICollectionFixture<RepoPilotHost>
{
    public const string Name = "repopilot-host";
}
