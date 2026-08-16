using System.Net.Http.Headers;
using System.Net.Http.Json;
using RepoPilot.Domain.Entities;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Events;

/// <summary>
/// FR-028a: a client that drops and reconnects sees no gap and no duplicate.
/// <para>
/// This is the property that lets the same table serve the live stream and the
/// audit record. If a reconnect could miss an event, a reviewer watching a run
/// would end up with a different account of it than someone reading the history
/// afterwards — and there would be no way to tell which was right.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReconnectReplayTests(PostgresFixture postgres) : IDisposable
{
    private readonly EventStreamHost _host = new(postgres);
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("repopilot-events-").FullName;

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

    private static HttpRequestMessage StreamRequest(Guid runId, long? lastEventId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{runId}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (lastEventId is { } id)
        {
            request.Headers.Add("Last-Event-ID", id.ToString());
        }

        return request;
    }

    [RequiresDockerFact]
    public async Task AFreshReaderGetsTheWholeHistoryWithoutAskingForIt()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.StageChanged, summary: """{"trigger":"Start"}""");
        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.ToolCall, toolName: "search_code");
        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded, summary: """{"terminalOutcome":"Succeeded"}""");

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(
            StreamRequest(run.Id), HttpCompletionOption.ResponseHeadersRead);

        using var reader = EventStreamHost.OpenReader(await response.Content.ReadAsStreamAsync());
        var frames = await EventStreamHost.ReadFramesAsync(reader, 3, TimeSpan.FromSeconds(10));

        // No separate "fetch history then subscribe" path is needed, which is
        // why the UI has none.
        Assert.Equal(3, frames.Count);
        Assert.Equal([1, 2, 3], frames.Select(f => f.Id));
    }

    [RequiresDockerFact]
    public async Task ReconnectingWithLastEventIdSkipsWhatWasAlreadySeen()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        for (var i = 0; i < 5; i++)
        {
            await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.ToolCall, toolName: $"tool_{i}");
        }

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(
            StreamRequest(run.Id, lastEventId: 3), HttpCompletionOption.ResponseHeadersRead);

        using var reader = EventStreamHost.OpenReader(await response.Content.ReadAsStreamAsync());
        var frames = await EventStreamHost.ReadFramesAsync(reader, 3, TimeSpan.FromSeconds(10));

        // Exactly what follows the cursor — no gap at 4, no duplicate of 3.
        Assert.Equal([4, 5, 6], frames.Select(f => f.Id));
    }

    [RequiresDockerFact]
    public async Task ADroppedAndReconnectedStreamProducesNoGapAndNoDuplicate()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        for (var i = 0; i < 4; i++)
        {
            await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.ToolCall, toolName: $"before_{i}");
        }

        using var client = factory.CreateClient();

        // First connection: read two frames, then drop mid-stream the way a
        // client on a flaky connection would.
        long lastSeen;

        using (var first = await client.SendAsync(
            StreamRequest(run.Id), HttpCompletionOption.ResponseHeadersRead))
        {
            using var reader = EventStreamHost.OpenReader(await first.Content.ReadAsStreamAsync());
            var frames = await EventStreamHost.ReadFramesAsync(reader, 2, TimeSpan.FromSeconds(10));

            Assert.Equal(2, frames.Count);
            lastSeen = frames[^1].Id;
        }

        // More happens while nobody is watching.
        for (var i = 0; i < 3; i++)
        {
            await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.ToolCall, toolName: $"during_{i}");
        }

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        using var second = await client.SendAsync(
            StreamRequest(run.Id, lastSeen), HttpCompletionOption.ResponseHeadersRead);

        using var resumedReader =
            EventStreamHost.OpenReader(await second.Content.ReadAsStreamAsync());
        var resumed = await EventStreamHost.ReadFramesAsync(
            resumedReader, 10, TimeSpan.FromSeconds(10));

        var ids = resumed.Select(f => f.Id).ToList();

        // Contiguous from where the first connection stopped, through what
        // happened while it was gone, to the end.
        Assert.Equal(Enumerable.Range((int)lastSeen + 1, ids.Count).Select(i => (long)i), ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal("run_ended", resumed[^1].EventType);
    }

    [RequiresDockerFact]
    public async Task AStreamAttachedAfterTheRunEndedClosesImmediately()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.StageChanged);
        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(
            StreamRequest(run.Id), HttpCompletionOption.ResponseHeadersRead);

        // Asking for more frames than exist: the stream must end rather than
        // hold the connection open waiting for a run that already finished.
        using var reader = EventStreamHost.OpenReader(await response.Content.ReadAsStreamAsync());
        var frames = await EventStreamHost.ReadFramesAsync(reader, 10, TimeSpan.FromSeconds(10));

        Assert.Equal(2, frames.Count);
        Assert.Equal("run_ended", frames[^1].EventType);
    }

    [RequiresDockerFact]
    public async Task LiveEventsReachAnAttachedSubscriber()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.StageChanged);

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(
            StreamRequest(run.Id), HttpCompletionOption.ResponseHeadersRead);

        using var reader = EventStreamHost.OpenReader(await response.Content.ReadAsStreamAsync());

        // Read the replayed frame first so the subscriber is definitely attached.
        var replayed = await EventStreamHost.ReadFramesAsync(reader, 1, TimeSpan.FromSeconds(10));
        Assert.Single(replayed);

        // Now produce events with the stream open. These arrive through the
        // publisher rather than the replay query.
        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.ToolCall, toolName: "read_file");
        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        var live = await EventStreamHost.ReadFramesAsync(reader, 2, TimeSpan.FromSeconds(10));

        Assert.Equal(2, live.Count);
        Assert.Equal("tool_call", live[0].EventType);
        Assert.Equal("run_ended", live[1].EventType);
    }

    [RequiresDockerFact]
    public async Task TheJsonVariantReturnsThePersistedList()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.StageChanged);
        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.ToolCall, toolName: "search_code");

        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{run.Id}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request);
        var events = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
            EventStreamHost.JsonOptions);

        // The same table the stream reads. A client that wants history without
        // holding a connection gets exactly what a stream would have replayed.
        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal("stage_changed", events[0].GetProperty("eventType").GetString());
        Assert.Equal("search_code", events[1].GetProperty("toolName").GetString());
    }
}
