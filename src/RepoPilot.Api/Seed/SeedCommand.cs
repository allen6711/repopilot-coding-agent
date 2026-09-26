using Microsoft.Extensions.Logging;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Schemas;
using RepoPilot.Domain.Entities;
using RepoPilot.Infrastructure.Indexing;

namespace RepoPilot.Api.Seed;

/// <summary>
/// Registers and indexes a fixture from disk.
/// <para>
/// Exists so User Story 1 is genuinely independently testable. Its acceptance
/// criteria begin "with one repository fixture pre-indexed", and the
/// operator-facing registration and indexing endpoints belong to User Story 2 —
/// without this, the MVP could not be exercised until a later story shipped.
/// </para>
/// </summary>
public sealed class SeedCommand(
    IRepositoryFixtureStore repositories,
    IndexingService indexing,
    AllowedRepositoryOptions allowed,
    WorkspaceOptions workspace,
    ILogger<SeedCommand> logger)
{
    /// <summary>
    /// Registers <paramref name="slug"/> if it is not already registered, then
    /// indexes it.
    /// </summary>
    /// <returns>The indexing report.</returns>
    /// <exception cref="InvalidOperationException">
    /// The slug is outside the configured allowed set (FR-001), or the fixture
    /// directory or its configuration is missing.
    /// </exception>
    public async Task<IndexingReport> SeedAsync(string slug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        if (!allowed.Slugs.Contains(slug, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{slug}' is not in the configured allowed set. Registration is refused for any "
                + "repository outside it (FR-001).");
        }

        var existing = await repositories.FindBySlugAsync(slug, ct);
        var fixture = existing ?? await RegisterAsync(slug, ct);

        var report = await indexing.RebuildAsync(fixture, ct);

        logger.LogInformation(
            "Seeded {Slug}: {Included} files, {Chunks} chunks at version {Version}.",
            slug, report.IncludedFileCount, report.ChunkCount, report.IndexVersion);

        return report;
    }

    private async Task<RepositoryFixture> RegisterAsync(string slug, CancellationToken ct)
    {
        var root = Path.Combine(Path.GetFullPath(workspace.FixturesRoot), slug);

        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException($"Fixture directory not found: {root}");
        }

        var configPath = Path.Combine(root, "repopilot.fixture.json");
        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException(
                $"Fixture '{slug}' has no repopilot.fixture.json. The command allow-list is the "
                + "sole source of what the sandbox may execute, so a fixture without one cannot "
                + "be registered (FR-022).");
        }

        var configJson = await File.ReadAllTextAsync(configPath, ct);

        // Validated before anything is stored: a malformed or widened
        // configuration must be refused at registration rather than discovered
        // when a container is already running.
        SchemaValidator.ValidateOrThrow(
            configJson, SchemaKind.FixtureConfig, $"Fixture '{slug}' configuration");

        var fixture = new RepositoryFixture
        {
            Slug = slug,
            DisplayName = slug,
            RootPath = root,
            TestConfigJson = configJson,
        };

        await repositories.AddAsync(fixture, ct);
        return fixture;
    }
}
