using Microsoft.EntityFrameworkCore;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Infrastructure.Persistence;
using RepoPilot.Infrastructure.Persistence.Repositories;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Events;

/// <summary>
/// The run event sequence is the SSE frame id and the replay cursor, so a gap or
/// a repeat is not a cosmetic defect: a reconnecting client resuming from
/// <c>Last-Event-ID</c> would silently miss an event or receive one twice, and
/// SC-008's reconstruction would no longer be faithful.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunEventSequenceTests(PostgresFixture postgres)
{
    private static async Task<Guid> SeedRunAsync(RepoPilotDbContext db)
    {
        var repository = new RepositoryFixture
        {
            Slug = "evt-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Events fixture",
            RootPath = "evals/fixtures/sample",
            TestConfigJson = """{"slug":"evt","commands":[]}""",
        };
        db.Repositories.Add(repository);

        var run = new Run
        {
            RepositoryId = repository.Id,
            TaskDescription = "Record some events.",
            Stage = RunStage.Retrieving,
        };
        db.Runs.Add(run);

        await db.SaveChangesAsync();
        return run.Id;
    }

    private static RunEvent Event(Guid runId, string tool) => new()
    {
        RunId = runId,
        EventType = RunEventType.ToolCall,
        ToolName = tool,
        Status = RunEventStatus.Succeeded,
    };

    [RequiresDockerFact]
    public async Task SequencesStartAtOneAndIncrement()
    {
        await using var db = postgres.CreateContext();
        var runId = await SeedRunAsync(db);
        var store = new EfRunEventStore(db);

        var first = await store.AppendAsync(Event(runId, "list_files"));
        var second = await store.AppendAsync(Event(runId, "read_file"));

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
    }

    [RequiresDockerFact]
    public async Task SequencesAreIndependentPerRun()
    {
        // Two runs must not share a counter, or one run's replay cursor would
        // skip past the other's events.
        await using var db = postgres.CreateContext();
        var runA = await SeedRunAsync(db);
        var runB = await SeedRunAsync(db);
        var store = new EfRunEventStore(db);

        await store.AppendAsync(Event(runA, "list_files"));
        var firstOfB = await store.AppendAsync(Event(runB, "list_files"));

        Assert.Equal(1, firstOfB.Sequence);
    }

    [RequiresDockerFact]
    public async Task ConcurrentAppendsProduceAGapFreeSequence()
    {
        // The real risk: several capability invocations completing at once, each
        // reading the current maximum before any has written. The unique index
        // turns a lost race into a retry rather than into two events sharing an
        // id.
        await using var seedDb = postgres.CreateContext();
        var runId = await SeedRunAsync(seedDb);

        const int writers = 12;

        var appends = Enumerable.Range(0, writers).Select(async i =>
        {
            await using var db = postgres.CreateContext();
            await new EfRunEventStore(db).AppendAsync(Event(runId, $"tool_{i}"));
        });

        await Task.WhenAll(appends);

        var sequences = await seedDb.RunEvents
            .Where(e => e.RunId == runId)
            .Select(e => e.Sequence)
            .OrderBy(s => s)
            .ToListAsync();

        Assert.Equal(writers, sequences.Count);
        Assert.Equal(Enumerable.Range(1, writers).Select(i => (long)i), sequences);
    }

    [RequiresDockerFact]
    public async Task ListingAfterACursorReturnsOnlyLaterEvents()
    {
        // This is exactly what a reconnecting SSE client does with Last-Event-ID.
        await using var db = postgres.CreateContext();
        var runId = await SeedRunAsync(db);
        var store = new EfRunEventStore(db);

        for (var i = 0; i < 5; i++)
        {
            await store.AppendAsync(Event(runId, $"tool_{i}"));
        }

        var after = await store.ListAsync(runId, afterSequence: 2);

        Assert.Equal(3, after.Count);
        Assert.Equal([3L, 4L, 5L], after.Select(e => e.Sequence));
    }

    [RequiresDockerFact]
    public async Task EventsAreReturnedInSequenceOrder()
    {
        await using var db = postgres.CreateContext();
        var runId = await SeedRunAsync(db);
        var store = new EfRunEventStore(db);

        for (var i = 0; i < 6; i++)
        {
            await store.AppendAsync(Event(runId, $"tool_{i}"));
        }

        var all = await store.ListAsync(runId);

        Assert.Equal(all.Select(e => e.Sequence).OrderBy(s => s), all.Select(e => e.Sequence));
    }
}
