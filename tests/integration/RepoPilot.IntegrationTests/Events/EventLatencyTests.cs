using System.Diagnostics;
using System.Net.Http.Headers;
using RepoPilot.Domain.Entities;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Events;

/// <summary>
/// SC-013: 95% of stage transitions and recorded actions reach a watching
/// reviewer within two seconds, with no manual refresh.
/// <para>
/// Measured from the moment the event is recorded to the moment a subscriber
/// holding an open stream has the frame in hand. That span is what a reviewer
/// experiences; timing the publish call alone would measure the easy half.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EventLatencyTests(PostgresFixture postgres) : IDisposable
{
    private const int EventCount = 40;

    /// <summary>The SC-013 ceiling.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private readonly EventStreamHost _host = new(postgres);
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("repopilot-latency-").FullName;

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

    [RequiresDockerFact]
    public async Task NinetyFivePercentOfEventsReachASubscriberWithinTwoSeconds()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{run.Id}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);

        using var reader = EventStreamHost.OpenReader(await response.Content.ReadAsStreamAsync());

        // One event first, so the subscriber is provably attached before any
        // measurement starts. Timing a frame that was actually replayed rather
        // than published would measure the wrong path and flatter the result.
        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.StageChanged);
        Assert.Single(await EventStreamHost.ReadFramesAsync(reader, 1, TimeSpan.FromSeconds(10)));

        var latencies = new List<TimeSpan>(EventCount);

        for (var i = 0; i < EventCount; i++)
        {
            var stopwatch = Stopwatch.StartNew();

            await EventStreamHost.AppendAsync(
                factory, run.Id, RunEventType.ToolCall, toolName: $"tool_{i}");

            var frames = await EventStreamHost.ReadFramesAsync(reader, 1, Budget * 3);
            stopwatch.Stop();

            Assert.Single(frames);
            latencies.Add(stopwatch.Elapsed);
        }

        var withinBudget = latencies.Count(l => l < Budget);
        var percentage = 100.0 * withinBudget / latencies.Count;

        var sorted = latencies.OrderBy(l => l).ToList();
        var p95 = sorted[(int)Math.Floor(0.95 * (sorted.Count - 1))];

        Assert.True(
            percentage >= 95.0,
            $"Only {percentage:F1}% of events arrived within {Budget.TotalSeconds:F0}s. " +
            $"p95 was {p95.TotalMilliseconds:F0}ms.");
    }

    [RequiresDockerFact]
    public async Task EveryRecordedEventReachesTheSubscriberInOrder()
    {
        using var factory = _host.CreateFactory(_workspace);
        var run = await _host.SeedRunAsync();

        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{run.Id}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);

        using var reader = EventStreamHost.OpenReader(await response.Content.ReadAsStreamAsync());

        for (var i = 0; i < 20; i++)
        {
            await EventStreamHost.AppendAsync(
                factory, run.Id, RunEventType.ToolCall, toolName: $"tool_{i}");
        }

        await EventStreamHost.AppendAsync(factory, run.Id, RunEventType.RunEnded);

        var frames = await EventStreamHost.ReadFramesAsync(reader, 21, TimeSpan.FromSeconds(20));

        // Delivery is complete and ordered. Latency does not help a reviewer if
        // the stream drops events under load — and an unbounded subscriber
        // channel is the design decision that makes this hold.
        Assert.Equal(21, frames.Count);
        Assert.Equal(Enumerable.Range(1, 21).Select(i => (long)i), frames.Select(f => f.Id));
    }
}
