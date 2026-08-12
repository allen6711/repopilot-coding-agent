using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using RepoPilot.Application.Ports;
using RepoPilot.Infrastructure.Observability;

namespace RepoPilot.Infrastructure.Providers;

/// <summary>Configuration for <see cref="AnthropicChatAdapter"/>.</summary>
public sealed class AnthropicChatOptions
{
    public const string SectionName = "RepoPilot:ChatProvider";

    /// <summary>
    /// Model id. Claude Opus 5 is the default for agentic coding work; the id is
    /// a fixed string with no date suffix.
    /// </summary>
    public string ModelId { get; init; } = "claude-opus-5";

    /// <summary>
    /// Credential. Left null so the SDK resolves from the environment, which
    /// keeps the key out of configuration files that might be committed.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// List rates for cost estimation, or null to record tokens only. Configured
    /// rather than hard-coded because published prices change and an estimate
    /// must never be mistaken for billing data.
    /// </summary>
    public ProviderRates? Rates { get; init; }
}

/// <summary>
/// The default <see cref="IChatProviderAdapter"/>, over the Anthropic SDK.
/// <para>
/// This is the only class in the system that knows which provider is in use.
/// Everything above it sees the provider-neutral port, which is what makes a
/// provider comparison possible later without touching the orchestrator — and
/// what the architecture test enforces.
/// </para>
/// </summary>
public sealed class AnthropicChatAdapter : IChatProviderAdapter
{
    private readonly AnthropicClient _client;
    private readonly AnthropicChatOptions _options;
    private readonly ILogger<AnthropicChatAdapter> _logger;

    public AnthropicChatAdapter(
        AnthropicChatOptions options,
        ILogger<AnthropicChatAdapter> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _client = string.IsNullOrWhiteSpace(options.ApiKey)
            ? new AnthropicClient()
            : new AnthropicClient { ApiKey = options.ApiKey };
    }

    /// <inheritdoc />
    public string ModelId => _options.ModelId;

    /// <inheritdoc />
    public async Task<ChatCompletion> CompleteAsync(
        ChatRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = Telemetry.Source.StartActivity("provider.complete");
        activity?.SetTag("repopilot.provider.effort", request.Effort.ToString());
        activity?.SetTag("repopilot.provider.tool_count", request.Tools.Count);

        var stopwatch = Stopwatch.StartNew();
        Message response;

        try
        {
            response = await _client.Messages.Create(BuildParams(request), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Telemetry.ProviderLatency.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("model", ModelId),
                new KeyValuePair<string, object?>("outcome", "error"));

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Model provider request failed for model {ModelId}.", ModelId);

            // Mapped to a distinct type so the orchestrator can end the run with
            // ProviderUnavailable recorded rather than as an unclassified failure
            // (FR-030).
            throw new ProviderUnavailableException(
                $"The model provider could not serve the request: {ex.Message}", ex);
        }

        stopwatch.Stop();

        var usage = ReadUsage(response);
        // The SDK types StopReason as nullable even though a completed response
        // always carries one. A null here means the response is not shaped the
        // way the contract says, so it is classified as Other rather than
        // assumed to be a normal end of turn.
        var stopReason = response.StopReason is { } reason
            ? MapStopReason(reason.Value())
            : ChatStopReason.Other;

        Telemetry.ProviderLatency.Record(
            stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("model", ModelId),
            new KeyValuePair<string, object?>("outcome", stopReason.ToString()));

        // T031a: the constitution requires traces to carry token and cost
        // metadata where the provider exposes it.
        Telemetry.RecordProviderUsage(usage, response.Model ?? ModelId, _options.Rates);

        var (text, toolCalls) = ReadContent(response);

        if (stopReason == ChatStopReason.Refusal)
        {
            // A refusal arrives as a successful HTTP response with empty or
            // partial content, so it has to be classified rather than parsed —
            // code that reads content unconditionally would silently treat a
            // decline as an answer.
            _logger.LogWarning(
                "Model provider declined the request for model {ModelId}.", ModelId);
        }

        return new ChatCompletion(
            text,
            toolCalls,
            stopReason,
            usage,
            response.Model ?? ModelId);
    }

    private MessageCreateParams BuildParams(ChatRequest request)
    {
        // Tools is init-only, so an empty list is passed rather than assigned
        // afterwards. Empty is meaningful here: the read stages offer
        // capabilities, the plan stage does not.
        return new MessageCreateParams
        {
            Model = _options.ModelId,
            MaxTokens = request.MaxOutputTokens,
            System = request.System,
            Messages = [.. request.Messages.Select(ToSdkMessage)],
            OutputConfig = new OutputConfig { Effort = MapEffort(request.Effort) },
            Tools = [.. request.Tools.Select(ToSdkTool)],
        };
    }

    private static MessageParam ToSdkMessage(ChatMessage message) => new()
    {
        Role = message.Role == ChatRole.User ? Role.User : Role.Assistant,
        Content = message.Text,
    };

    /// <summary>
    /// Converts a capability's JSON Schema into the SDK's typed shape.
    /// </summary>
    private static Tool ToSdkTool(ToolDefinition definition)
    {
        using var schema = JsonDocument.Parse(definition.InputSchemaJson);
        var root = schema.RootElement;

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("properties", out var props) &&
            props.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in props.EnumerateObject())
            {
                properties[property.Name] = property.Value.Clone();
            }
        }

        var required = new List<string>();
        if (root.TryGetProperty("required", out var req) &&
            req.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in req.EnumerateArray())
            {
                var name = item.GetString();
                if (name is not null)
                {
                    required.Add(name);
                }
            }
        }

        return new Tool
        {
            Name = definition.Name,
            Description = definition.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    private static Effort MapEffort(EffortLevel level) => level switch
    {
        EffortLevel.Low => Effort.Low,
        EffortLevel.Medium => Effort.Medium,
        EffortLevel.High => Effort.High,
        EffortLevel.XHigh => Effort.Xhigh,
        EffortLevel.Max => Effort.Max,
        _ => Effort.High,
    };

    private static ProviderUsage ReadUsage(Message response)
    {
        var usage = response.Usage;
        return usage is null
            ? default
            : new ProviderUsage(
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheReadInputTokens ?? 0,
                usage.CacheCreationInputTokens ?? 0);
    }

    /// <summary>
    /// Maps the SDK's stop reason onto the port's.
    /// <para>
    /// Deliberately mapped rather than passed through. The provider's set is
    /// larger and provider-specific — <c>PauseTurn</c> and
    /// <c>ModelContextWindowExceeded</c> have no meaning above this layer — and
    /// anything unrecognised becomes <see cref="ChatStopReason.Other"/> rather
    /// than being guessed at, so a new provider value cannot be silently read as
    /// a successful turn.
    /// </para>
    /// </summary>
    private static ChatStopReason MapStopReason(StopReason? stopReason) => stopReason switch
    {
        StopReason.EndTurn or StopReason.StopSequence => ChatStopReason.EndTurn,
        StopReason.ToolUse => ChatStopReason.ToolUse,
        StopReason.MaxTokens or StopReason.ModelContextWindowExceeded => ChatStopReason.MaxTokens,
        StopReason.Refusal => ChatStopReason.Refusal,
        _ => ChatStopReason.Other,
    };

    private static (string Text, IReadOnlyList<ToolCall> ToolCalls) ReadContent(Message response)
    {
        var text = new System.Text.StringBuilder();
        var calls = new List<ToolCall>();

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? textBlock) && textBlock is not null)
            {
                text.Append(textBlock.Text);
            }
            else if (block.TryPickToolUse(out ToolUseBlock? toolUse) && toolUse is not null)
            {
                // Serialized back to JSON so the invoker parses it once, in one
                // place, rather than each capability reading a dictionary of
                // JsonElement in its own way.
                calls.Add(new ToolCall(
                    toolUse.ID,
                    toolUse.Name,
                    JsonSerializer.Serialize(toolUse.Input)));
            }
        }

        return (text.ToString(), calls);
    }
}
