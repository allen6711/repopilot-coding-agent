using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using RepoPilot.Application.Runs;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Events;

/// <summary>
/// One parsed SSE frame.
/// </summary>
/// <param name="Id">The <c>id:</c> field — the event's per-run sequence.</param>
/// <param name="EventType">The <c>event:</c> field.</param>
/// <param name="Data">The <c>data:</c> payload, parsed.</param>
public sealed record SseFrame(long Id, string EventType, JsonElement Data);

/// <summary>
/// A host for the event-stream tests, with helpers for reading SSE.
/// <para>
/// The stream is read by hand rather than with a client library because the
/// frame format <em>is</em> the contract — id, event name, and data, separated
/// by a blank line. A library that normalized any of that away would test the
/// library.
/// </para>
/// </summary>
public sealed class EventStreamHost(PostgresFixture postgres)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public WebApplicationFactory<Program> CreateFactory(string workspaceRoot)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:RepoPilot", postgres.ConnectionString!);
            builder.UseSetting("RepoPilot:Workspace:Root", workspaceRoot);
        });

        // Startup recovery ends every non-terminal run it finds, so the host has
        // to be up before anything is seeded.
        factory.CreateClient().Dispose();

        return factory;
    }

    /// <summary>Seeds a run in a stage the tests can drive from.</summary>
    public async Task<Run> SeedRunAsync(RunStage stage = RunStage.AwaitingApproval)
    {
        await using var db = postgres.CreateContext();

        var repository = new RepositoryFixture
        {
            Slug = "events-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Event fixture",
            RootPath = Path.GetTempPath(),
            TestConfigJson = """{"slug":"events","commands":[]}""",
            ActiveIndexVersion = 1,
            IncludedFileCount = 3,
        };

        var run = new Run
        {
            RepositoryId = repository.Id,
            TaskDescription = "Watch this run.",
            Stage = stage,
        };

        db.Repositories.Add(repository);
        db.Runs.Add(run);
        await db.SaveChangesAsync();

        return run;
    }

    /// <summary>
    /// Records an event through the host, standing in for orchestrator activity.
    /// <para>
    /// Through the host's own <c>RunEventRecorder</c>, not straight into the
    /// store. The recorder is what persists and then publishes, and a helper
    /// that wrote directly would produce events no live subscriber ever sees —
    /// making the streaming tests pass or fail for reasons unrelated to
    /// streaming.
    /// </para>
    /// </summary>
    public static async Task<RunEvent> AppendAsync(
        WebApplicationFactory<Program> factory,
        Guid runId,
        RunEventType type,
        string? toolName = null,
        string? summary = null,
        RunEventStatus status = RunEventStatus.Succeeded)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        var recorder = scope.ServiceProvider.GetRequiredService<RunEventRecorder>();

        return await recorder.RecordAsync(new RunEvent
        {
            RunId = runId,
            EventType = type,
            ToolName = toolName,
            ArgumentsSummary = summary,
            Status = status,
        });
    }

    /// <summary>
    /// Opens a reader over an SSE response.
    /// <para>
    /// One reader per stream, held by the caller. A second reader over the same
    /// stream would start with an empty buffer and lose whatever the first had
    /// already pulled — and disposing one closes the response, which ends the
    /// stream a test is still reading.
    /// </para>
    /// </summary>
    public static StreamReader OpenReader(Stream stream) =>
        new(stream, leaveOpen: true);

    /// <summary>
    /// Reads SSE frames until <paramref name="count"/> arrive or the stream ends.
    /// </summary>
    public static async Task<List<SseFrame>> ReadFramesAsync(
        StreamReader reader, int count, TimeSpan timeout)
    {
        var frames = new List<SseFrame>();
        using var deadline = new CancellationTokenSource(timeout);

        long? id = null;
        string? eventType = null;

        try
        {
            while (frames.Count < count)
            {
                var line = await reader.ReadLineAsync(deadline.Token);

                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    id = long.Parse(line[4..]);
                }
                else if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    eventType = line[7..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    var data = JsonDocument.Parse(line[6..]).RootElement.Clone();
                    frames.Add(new SseFrame(id ?? 0, eventType ?? "", data));
                    id = null;
                    eventType = null;
                }

                // Comment frames (": keepalive") and the blank separator need no
                // handling: they carry no data and end no frame.
            }
        }
        catch (OperationCanceledException)
        {
            // Returning what arrived lets a caller assert on partial output,
            // which is more useful than a bare timeout when a frame is missing.
        }

        return frames;
    }

    public RepoPilotDbContext CreateContext() => postgres.CreateContext();

    public static JsonSerializerOptions JsonOptions => Json;
}
