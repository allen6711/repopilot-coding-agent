using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RepoPilot.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef</c>.
/// <para>
/// Migrations are generated from the model, not from a live database, so this
/// deliberately does not connect to anything. That keeps schema generation
/// runnable in CI and on a machine with no daemon — the schema is reviewable
/// before any environment exists to apply it to.
/// </para>
/// </summary>
public sealed class RepoPilotDbContextFactory : IDesignTimeDbContextFactory<RepoPilotDbContext>
{
    public RepoPilotDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("REPOPILOT_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=repopilot;Username=repopilot;Password=repopilot";

        var options = new DbContextOptionsBuilder<RepoPilotDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .Options;

        return new RepoPilotDbContext(options);
    }
}
