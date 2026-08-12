namespace RepoPilot.Application.Ports;

/// <summary>
/// How much reasoning and exploration to spend on a request.
/// <para>
/// Expressed as a level rather than a token budget because that is the shape the
/// decision actually has — "how hard should this try" is a property of the
/// stage, not a number the orchestrator can derive. Adapters map it onto
/// whatever their provider exposes.
/// </para>
/// </summary>
public enum EffortLevel
{
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>Who produced a turn.</summary>
public enum ChatRole
{
    User,
    Assistant,
}

/// <summary>One turn of a conversation.</summary>
/// <param name="Role">Who produced it.</param>
/// <param name="Text">Its text content.</param>
public sealed record ChatMessage(ChatRole Role, string Text);

/// <summary>
/// A capability offered to the model, in provider-neutral form.
/// </summary>
/// <param name="Name">Capability name, from the closed registry.</param>
/// <param name="Description">What it does and when to use it.</param>
/// <param name="InputSchemaJson">JSON Schema for the capability's input.</param>
public sealed record ToolDefinition(string Name, string Description, string InputSchemaJson);

/// <summary>A capability invocation the model asked for.</summary>
/// <param name="Id">Provider-assigned id, echoed back with the result.</param>
/// <param name="Name">Capability name.</param>
/// <param name="ArgumentsJson">Raw JSON arguments, parsed by the invoker.</param>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Why the model stopped.</summary>
public enum ChatStopReason
{
    /// <summary>Finished its turn.</summary>
    EndTurn,

    /// <summary>Wants one or more capabilities invoked.</summary>
    ToolUse,

    /// <summary>Hit the output cap; the response may be truncated.</summary>
    MaxTokens,

    /// <summary>Declined the request.</summary>
    Refusal,

    /// <summary>Anything the adapter could not classify.</summary>
    Other,
}

/// <summary>
/// What a request consumed. Captured for every call because the constitution
/// requires traces to carry token and cost metadata where the provider exposes
/// it (Principle IV).
/// </summary>
/// <param name="InputTokens">Uncached input tokens.</param>
/// <param name="OutputTokens">Generated tokens.</param>
/// <param name="CacheReadTokens">Input tokens served from a prompt cache.</param>
/// <param name="CacheWriteTokens">Input tokens written to a prompt cache.</param>
public readonly record struct ProviderUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens)
{
    /// <summary>Total input tokens across all three input categories.</summary>
    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheWriteTokens;
}

/// <summary>A request to the model provider.</summary>
/// <param name="System">System prompt. Stable across a run so it can be cached.</param>
/// <param name="Messages">Conversation so far.</param>
/// <param name="Tools">Capabilities offered for this call.</param>
/// <param name="Effort">How hard to try.</param>
/// <param name="MaxOutputTokens">Hard cap on generated output.</param>
public sealed record ChatRequest(
    string System,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    EffortLevel Effort,
    int MaxOutputTokens);

/// <summary>A response from the model provider.</summary>
/// <param name="Text">Assistant text, empty when the turn was only tool calls.</param>
/// <param name="ToolCalls">Capabilities the model asked to invoke.</param>
/// <param name="StopReason">Why it stopped.</param>
/// <param name="Usage">What it consumed.</param>
/// <param name="ModelId">The model that actually served the request.</param>
public sealed record ChatCompletion(
    string Text,
    IReadOnlyList<ToolCall> ToolCalls,
    ChatStopReason StopReason,
    ProviderUsage Usage,
    string ModelId);

/// <summary>
/// The boundary between the run orchestrator and whichever model provider is
/// configured.
/// <para>
/// Everything on this interface is provider-neutral by design. The constitution
/// requires that provider-specific types, prompts, and SDK calls stay out of the
/// Application and Domain layers, and an architecture test enforces it — so this
/// is the only shape the rest of the system ever sees a model through.
/// </para>
/// </summary>
public interface IChatProviderAdapter
{
    /// <summary>Identifies the configured model, for audit records and traces.</summary>
    string ModelId { get; }

    /// <summary>
    /// Sends a request and returns the completion.
    /// </summary>
    /// <exception cref="ProviderUnavailableException">
    /// The provider could not be reached or refused to serve the request. The
    /// run fails at its current stage with the reason recorded; no partial change
    /// is applied (FR-030).
    /// </exception>
    Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct = default);
}

/// <summary>
/// Raised when the model provider is unreachable or unusable.
/// <para>
/// Deliberately distinct from an ordinary exception: the orchestrator maps this
/// onto the <c>ProviderUnavailable</c> outcome reason, and the run ends with
/// that recorded rather than as an unclassified failure.
/// </para>
/// </summary>
public sealed class ProviderUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
