using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RepoPilot.Domain.Entities;
using RepoPilot.IntegrationTests.Events;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Contracts;

/// <summary>
/// The events endpoint against <c>contracts/run-events.md</c> and
/// <c>rest-api.yaml</c>.
/// <para>
/// The frame format is the contract, so these assertions are about the wire
/// bytes: the <c>id</c> is the sequence, the <c>event</c> name is the catalogue
/// value, and <c>data</c> is the RunEvent object. A client written against the
/// document has to work against this.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunEventContractTests(PostgresFixture postgres) : IDisposable
{
    private readonly EventStreamHost _host = new(postgres);
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("repopilot-event-contract-").FullName;

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

    private static HttpRequestMessage StreamRequest(Guid runId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{runId}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    [RequiresDockerFact]
    public async Task TheStreamIsServedAsEventStream()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(
            StreamRequest(run.Id), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // Buffering a stream defeats it: an intermediary that accumulated frames
        // would deliver them in one batch at the end, which is indistinguishable
        // from the feature not working.
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [RequiresDockerFact]
    public async Task EachFrameCarriesItsSequenceAsTheIdAndItsTypeAsTheEventName()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        await EventStreamHost.AppendAsync(
            factory, run.Id, RunEventType.StageChanged,
            summary: """{"fromStage":"Created","trigger":"Start","toStage":"Retrieving"}""");

        await EventStreamHost.AppendAsync(
            factory, run.Id, RunEventType.ToolCall, toolName: "search_code");

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(
            StreamRequest(run.Id), HttpCompletionOption.ResponseHeadersRead);

        using var reader = EventStreamHost.OpenReader(await response.Content.ReadAsStreamAsync());
        var frames = await EventStreamHost.ReadFramesAsync(reader, 3, TimeSpan.FromSeconds(10));

        Assert.Equal(3, frames.Count);

        // id is the per-run sequence, which is what makes Last-Event-ID a cursor
        // into the same ordering the store uses.
        Assert.Equal([1, 2, 3], frames.Select(f => f.Id));
        Assert.Equal(frames.Select(f => f.Id), frames.Select(f => f.Data.GetProperty("sequence").GetInt64()));

        // event is the catalogue value, in snake_case.
        Assert.Equal(["stage_changed", "tool_call", "run_ended"], frames.Select(f => f.EventType));
        Assert.Equal(frames.Select(f => f.EventType), frames.Select(f => f.Data.GetProperty("eventType").GetString()));
    }

    [RequiresDockerFact]
    public async Task TheDataPayloadMatchesTheRunEventSchema()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        await EventStreamHost.AppendAsync(
            factory, run.Id, RunEventType.ToolCall,
            toolName: "read_file",
            summary: """{"path":"src/Service.cs"}""");

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(
            StreamRequest(run.Id), HttpCompletionOption.ResponseHeadersRead);

        using var reader = EventStreamHost.OpenReader(await response.Content.ReadAsStreamAsync());
        var frames = await EventStreamHost.ReadFramesAsync(reader, 1, TimeSpan.FromSeconds(10));

        var data = Assert.Single(frames).Data;

        foreach (var required in new[] { "id", "sequence", "eventType", "status", "startedAt" })
        {
            Assert.True(
                data.TryGetProperty(required, out _),
                $"The RunEvent payload omits required field '{required}'.");
        }

        Assert.Equal("read_file", data.GetProperty("toolName").GetString());
        Assert.Equal("succeeded", data.GetProperty("status").GetString());

        // An object, not a JSON-encoded string. The contract types it as an
        // object, and handing back a string would make every consumer parse it
        // twice.
        var summary = data.GetProperty("argumentsSummary");
        Assert.Equal(JsonValueKind.Object, summary.ValueKind);
        Assert.Equal("src/Service.cs", summary.GetProperty("path").GetString());
    }

    [RequiresDockerFact]
    public async Task AFailedActionIsReportedWithFailedStatus()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        await EventStreamHost.AppendAsync(
            factory, run.Id, RunEventType.StageTransitionRejected,
            summary: """{"fromStage":"Succeeded","attemptedTrigger":"Approved"}""",
            status: RunEventStatus.Failed);

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(
            StreamRequest(run.Id), HttpCompletionOption.ResponseHeadersRead);

        using var reader = EventStreamHost.OpenReader(await response.Content.ReadAsStreamAsync());
        var frames = await EventStreamHost.ReadFramesAsync(reader, 1, TimeSpan.FromSeconds(10));

        var frame = Assert.Single(frames);

        // FR-009: an illegal transition is a first-class event, not an absence.
        Assert.Equal("stage_transition_rejected", frame.EventType);
        Assert.Equal("failed", frame.Data.GetProperty("status").GetString());
    }

    [RequiresDockerFact]
    public async Task TheJsonVariantReturnsTheSameEventsAsTheStream()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.StageChanged);
        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.ToolCall, toolName: "list_files");
        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        using var client = factory.CreateClient();

        using var streamResponse = await client.SendAsync(
            StreamRequest(run.Id), HttpCompletionOption.ResponseHeadersRead);

        using var reader = EventStreamHost.OpenReader(
            await streamResponse.Content.ReadAsStreamAsync());
        var frames = await EventStreamHost.ReadFramesAsync(reader, 3, TimeSpan.FromSeconds(10));

        var jsonRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{run.Id}/events");
        jsonRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var jsonResponse = await client.SendAsync(jsonRequest);

        Assert.Equal(HttpStatusCode.OK, jsonResponse.StatusCode);
        Assert.Equal("application/json", jsonResponse.Content.Headers.ContentType?.MediaType);

        using var listed = JsonDocument.Parse(await jsonResponse.Content.ReadAsStringAsync());

        // Two representations of one table. If they could differ, a client would
        // have to know which one to trust.
        Assert.Equal(frames.Count, listed.RootElement.GetArrayLength());

        Assert.Equal(
            frames.Select(f => f.Data.GetProperty("sequence").GetInt64()),
            listed.RootElement.EnumerateArray().Select(e => e.GetProperty("sequence").GetInt64()));

        Assert.Equal(
            frames.Select(f => f.EventType),
            listed.RootElement.EnumerateArray().Select(e => e.GetProperty("eventType").GetString()));
    }

    [RequiresDockerFact]
    public async Task StreamingAnUnknownRunIsNotFound()
    {
        using var factory = _host.CreateFactory(_workspace);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(StreamRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [RequiresDockerFact]
    public async Task RunEndedIsAlwaysTheFinalFrame()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.StageChanged);
        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(
            StreamRequest(run.Id), HttpCompletionOption.ResponseHeadersRead);

        using var reader = EventStreamHost.OpenReader(await response.Content.ReadAsStreamAsync());

        // Asking for far more than exist: the server must close after run_ended
        // rather than hold the connection. A client treats closure without this
        // frame as a transport drop, so emitting it is what distinguishes
        // "finished" from "reconnect".
        var frames = await EventStreamHost.ReadFramesAsync(reader, 20, TimeSpan.FromSeconds(10));

        Assert.Equal(2, frames.Count);
        Assert.Equal("run_ended", frames[^1].EventType);
    }
}
