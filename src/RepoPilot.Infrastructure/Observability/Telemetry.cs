using System.Diagnostics;
using System.Diagnostics.Metrics;
using RepoPilot.Application.Ports;

namespace RepoPilot.Infrastructure.Observability;

/// <summary>
/// The single activity source and meter for RepoPilot.
/// <para>
/// Principle IV requires runs to emit traces covering latency, tool calls,
/// errors, and token or cost metadata where the provider exposes it. Keeping one
/// source and one meter here means instrumentation is added in one place rather
/// than remembered at each call site.
/// </para>
/// </summary>
public static class Telemetry
{
    public const string ActivitySourceName = "RepoPilot";

    public const string MeterName = "RepoPilot";

    public static ActivitySource Source { get; } = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Input tokens consumed, by model and token category.</summary>
    public static Counter<long> InputTokens { get; } =
        Meter.CreateCounter<long>(
            "repopilot.provider.input_tokens",
            unit: "{token}",
            description: "Input tokens consumed, tagged by model and cache category.");

    /// <summary>Output tokens generated, by model.</summary>
    public static Counter<long> OutputTokens { get; } =
        Meter.CreateCounter<long>(
            "repopilot.provider.output_tokens",
            unit: "{token}",
            description: "Output tokens generated, tagged by model.");

    /// <summary>Estimated spend, by model. See the note on estimation below.</summary>
    public static Counter<double> EstimatedCostUsd { get; } =
        Meter.CreateCounter<double>(
            "repopilot.provider.estimated_cost_usd",
            unit: "USD",
            description:
                "Estimated provider spend from configured list rates. An estimate, " +
                "not billing data — never publish it as an actual cost (Principle V).");

    /// <summary>Model request duration.</summary>
    public static Histogram<double> ProviderLatency { get; } =
        Meter.CreateHistogram<double>(
            "repopilot.provider.request_duration",
            unit: "ms",
            description: "Model provider request duration, tagged by model and outcome.");

    /// <summary>
    /// Attaches usage to the current span and records it as metrics.
    /// <para>
    /// Both, deliberately: the span makes one run's spend explainable, and the
    /// metrics make spend across runs aggregatable. Neither substitutes for the
    /// other when the question is "why did this run cost that much".
    /// </para>
    /// </summary>
    /// <param name="usage">What the request consumed.</param>
    /// <param name="modelId">The model that served it.</param>
    /// <param name="rates">Configured list rates, or null to skip cost estimation.</param>
    public static void RecordProviderUsage(
        ProviderUsage usage,
        string modelId,
        ProviderRates? rates = null)
    {
        var modelTag = new KeyValuePair<string, object?>("model", modelId);

        InputTokens.Add(usage.InputTokens, modelTag, new("cache", "miss"));
        InputTokens.Add(usage.CacheReadTokens, modelTag, new("cache", "read"));
        InputTokens.Add(usage.CacheWriteTokens, modelTag, new("cache", "write"));
        OutputTokens.Add(usage.OutputTokens, modelTag);

        var activity = Activity.Current;
        if (activity is not null)
        {
            // Named to match the metric so a trace and a dashboard can be read
            // against each other without a translation table.
            activity.SetTag("repopilot.provider.model", modelId);
            activity.SetTag("repopilot.provider.input_tokens", usage.InputTokens);
            activity.SetTag("repopilot.provider.output_tokens", usage.OutputTokens);
            activity.SetTag("repopilot.provider.cache_read_tokens", usage.CacheReadTokens);
            activity.SetTag("repopilot.provider.cache_write_tokens", usage.CacheWriteTokens);
        }

        if (rates is null)
        {
            return;
        }

        var cost = rates.EstimateUsd(usage);
        EstimatedCostUsd.Add(cost, modelTag);
        activity?.SetTag("repopilot.provider.estimated_cost_usd", cost);
    }
}

/// <summary>
/// Per-million-token list rates used to estimate spend.
/// <para>
/// Configured rather than hard-coded, and named "estimated" everywhere it
/// surfaces, because published list prices are not the same thing as what an
/// account is billed and they change without notice. Principle V forbids
/// presenting an unmeasured figure as an outcome; this is a planning signal for
/// operators, not a cost report.
/// </para>
/// </summary>
/// <param name="InputPerMillion">List rate for uncached input tokens.</param>
/// <param name="OutputPerMillion">List rate for output tokens.</param>
/// <param name="CacheReadPerMillion">List rate for cache-read input tokens.</param>
/// <param name="CacheWritePerMillion">List rate for cache-write input tokens.</param>
public sealed record ProviderRates(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheReadPerMillion,
    decimal CacheWritePerMillion)
{
    /// <summary>Estimates spend for one request.</summary>
    public double EstimateUsd(ProviderUsage usage)
    {
        const decimal million = 1_000_000m;

        var total =
            (usage.InputTokens / million * InputPerMillion) +
            (usage.OutputTokens / million * OutputPerMillion) +
            (usage.CacheReadTokens / million * CacheReadPerMillion) +
            (usage.CacheWriteTokens / million * CacheWritePerMillion);

        return (double)total;
    }
}
