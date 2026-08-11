using Microsoft.EntityFrameworkCore;
using RepoPilot.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace RepoPilot.IntegrationTests.Infrastructure;

/// <summary>
/// A PostgreSQL 17 container with pgvector, migrated once and shared across a
/// test class.
/// <para>
/// The image is the same one <c>docker-compose.yml</c> uses, so the schema these
/// tests exercise is the schema that runs — a test against a different database
/// engine would prove nothing about constraints that only PostgreSQL enforces.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>Null when Docker was unavailable and the fixture did not start.</summary>
    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        if (DockerAvailability.UnavailableReason is not null)
        {
            // Every test in the class carries [RequiresDockerFact] and will skip,
            // so starting nothing here is correct rather than a silent failure.
            return;
        }

        _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithDatabase("repopilot")
            .WithUsername("repopilot")
            .WithPassword("repopilot")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates a context against the container.</summary>
    public RepoPilotDbContext CreateContext()
    {
        if (ConnectionString is null)
        {
            throw new InvalidOperationException(
                "The PostgreSQL container did not start. Tests using it must be marked " +
                "[RequiresDockerFact] so they skip rather than reaching this point.");
        }

        var options = new DbContextOptionsBuilder<RepoPilotDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new RepoPilotDbContext(options);
    }
}

/// <summary>Collection marker so one container is shared across related classes.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
