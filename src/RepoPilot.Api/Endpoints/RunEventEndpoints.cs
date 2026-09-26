using System.Text;
using System.Text.Json;
using RepoPilot.Api.Contracts;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;

namespace RepoPilot.Api.Endpoints;

/// <summary>
/// The run event stream (FR-028a, FR-029, SC-013).
/// <para>
/// One endpoint, two representations. <c>text/event-stream</c> is the live view;
/// <c>application/json</c> is the persisted list. They read the same table, which
/// is what makes them agree — a separate live-only path would be able to show
/// something a later replay could not.
/// </para>
/// </summary>
public static class RunEventEndpoints
{
    /// <summary>
    /// How often a comment frame is written on an idle stream.
    /// <para>
    /// Proxies and load balancers close connections that go quiet. Fifteen
    /// seconds is well inside the usual sixty-second idle timeouts, and a comment
    /// frame is ignored by every SSE client rather than being delivered as an
    /// event a reader has to filter out.
    /// </para>
    /// </summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapRunEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runs/{runId:guid}/events", StreamAsync).WithTags("runs");
    }

    private static async Task<IResult> StreamAsync(
        Guid runId,
        HttpContext context,
        IRunStore runs,
        IRunEventStore store,
        IRunEventPublisher publisher,
        CancellationToken ct)
    {
        if (await runs.FindAsync(runId, ct) is null)
        {
            return Results.Problem(
                title: "Run not found",
                detail: $"No run with id {runId}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (!WantsEventStream(context.Request))
        {
            // T114: the JSON variant. Useful for a client that wants the history
            // without holding a connection — and for anything reconstructing a
            // finished run, where a stream would add nothing.
            var persisted = await store.ListAsync(runId, afterSequence: 0, ct);
            return Results.Ok(persisted.Select(RunEventDto.From));
        }

        await WriteStreamAsync(runId, context, store, publisher, ct);

        return Results.Empty;
    }

    private static async Task WriteStreamAsync(
        Guid runId,
        HttpContext context,
        IRunEventStore store,
        IRunEventPublisher publisher,
        CancellationToken ct)
    {
        var response = context.Response;

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";

        // Buffering a stream defeats it. An intermediary that accumulated frames
        // would deliver them in a batch at the end, which is indistinguishable
        // from the feature not working.
        response.Headers["X-Accel-Buffering"] = "no";

        var lastSequence = ReadLastEventId(context.Request);

        // Subscribe before replaying. An event persisted between the replay
        // query and the subscription would otherwise fall in the gap between
        // them — the one case where persist-then-publish is not enough on its
        // own, because the reader has two sources to join.
        var live = publisher.SubscribeAsync(runId, ct).GetAsyncEnumerator(ct);

        try
        {
            var replayed = await store.ListAsync(runId, lastSequence, ct);

            foreach (var recorded in replayed)
            {
                await WriteEventAsync(response, recorded, ct);
                lastSequence = Math.Max(lastSequence, recorded.Sequence);

                if (recorded.EventType == RunEventType.RunEnded)
                {
                    // The run finished before this client attached. Everything
                    // there is to say has been said.
                    return;
                }
            }

            await PumpAsync(response, live, lastSequence, ct);
        }
        catch (OperationCanceledException)
        {
            // The client went away. Nothing to record: the events are durable
            // and a reconnect will replay from Last-Event-ID.
        }
        finally
        {
            await live.DisposeAsync();
        }
    }

    /// <summary>Writes live events until the run ends or the client leaves.</summary>
    private static async Task PumpAsync(
        HttpResponse response,
        IAsyncEnumerator<RunEvent> live,
        long lastSequence,
        CancellationToken ct)
    {
        // The pending move is held across iterations rather than re-issued.
        // Abandoning it and calling MoveNextAsync again would be a re-entrant
        // call on an async iterator, which is undefined — in practice it faults
        // on a thread-pool thread and takes the host process down, not just this
        // request.
        Task<bool>? pending = null;

        while (true)
        {
            pending ??= live.MoveNextAsync().AsTask();

            var keepAlive = Task.Delay(KeepAliveInterval, ct);

            if (await Task.WhenAny(pending, keepAlive) == keepAlive)
            {
                // Also the path a cancelled token takes: the delay completes
                // immediately, and the write below throws OperationCanceled,
                // which the caller treats as the client having left.
                await response.WriteAsync(": keepalive\n\n", ct);
                await response.Body.FlushAsync(ct);
                continue;
            }

            if (!await pending)
            {
                return;
            }

            pending = null;

            var runEvent = live.Current;

            // Skip anything the replay already wrote. Subscribing first means the
            // two sources can overlap, and a duplicate frame would break a client
            // that treats the sequence as a cursor.
            if (runEvent.Sequence <= lastSequence)
            {
                continue;
            }

            await WriteEventAsync(response, runEvent, ct);
            lastSequence = runEvent.Sequence;

            if (runEvent.EventType == RunEventType.RunEnded)
            {
                return;
            }
        }
    }

    private static async Task WriteEventAsync(
        HttpResponse response, RunEvent runEvent, CancellationToken ct)
    {
        var frame = new StringBuilder();

        // The per-run sequence is the frame id, so a reconnecting client's
        // Last-Event-ID is a cursor into the same ordering the store uses.
        frame.Append("id: ").Append(runEvent.Sequence).Append('\n');
        frame.Append("event: ").Append(RunEventDto.WireEventType(runEvent.EventType)).Append('\n');
        frame.Append("data: ")
             .Append(JsonSerializer.Serialize(RunEventDto.From(runEvent), Json))
             .Append("\n\n");

        await response.WriteAsync(frame.ToString(), ct);
        await response.Body.FlushAsync(ct);
    }

    private static bool WantsEventStream(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();

        // Defaults to the stream: the contract describes this endpoint as SSE,
        // and a client that asked for nothing in particular at a URL documented
        // as a stream wants the stream.
        return accept.Length == 0 ||
               accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) ||
               !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the reconnect cursor.
    /// </summary>
    /// <returns>
    /// The last sequence the client saw, or 0. Zero means a full replay — a
    /// fresh reader always gets the whole history, so the UI needs no separate
    /// "fetch history then subscribe" path.
    /// </returns>
    private static long ReadLastEventId(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Last-Event-ID", out var header) &&
            long.TryParse(header.ToString(), out var sequence) &&
            sequence > 0)
        {
            return sequence;
        }

        // Also accepted as a query parameter: EventSource sets the header
        // automatically, but a client reconnecting by hand cannot set headers on
        // an EventSource at all.
        if (request.Query.TryGetValue("lastEventId", out var query) &&
            long.TryParse(query.ToString(), out var fromQuery) &&
            fromQuery > 0)
        {
            return fromQuery;
        }

        return 0;
    }
}
