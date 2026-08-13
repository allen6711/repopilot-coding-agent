using RepoPilot.Application.Capabilities;
using RepoPilot.Domain.Capabilities;

namespace RepoPilot.Application.Ports;

/// <summary>The result of one capability invocation.</summary>
/// <param name="Content">What the capability returned.</param>
/// <param name="DurationMs">How long it took.</param>
public sealed record CapabilityOutcome(string Content, int DurationMs);

/// <summary>
/// The single point capability invocations pass through.
/// <para>
/// Declared here rather than where it is implemented because the orchestrator
/// calls it, and the orchestrator is the thing that must not know which provider
/// or which telemetry stack is in use. The implementation applies the permission
/// class, the context budget, and the audit record.
/// </para>
/// </summary>
public interface ICapabilityInvoker
{
    /// <summary>
    /// Invokes a capability by name.
    /// </summary>
    /// <param name="callerSurface">
    /// Who is invoking. Passing <see cref="InvocationSurface.Model"/> for an
    /// orchestrator-only capability is refused, and vice versa — which is why
    /// this is a parameter rather than something the implementation infers.
    /// </param>
    Task<CapabilityOutcome> InvokeAsync(
        CapabilityContext context,
        string capabilityName,
        string argumentsJson,
        InvocationSurface callerSurface,
        CancellationToken ct = default);
}

/// <summary>
/// One turn against the configured model provider.
/// <para>
/// A turn, never a loop. Each iteration of the agent loop is a stage transition,
/// and Principle IV puts those in backend code — so the orchestrator owns the
/// loop and this port supplies only the individual turns.
/// </para>
/// </summary>
public interface IAgentTurnRunner
{
    Task<ChatCompletion> TurnAsync(
        string repositorySlug,
        IReadOnlyList<ChatMessage> conversation,
        EffortLevel effort,
        int maxOutputTokens,
        CancellationToken ct = default);
}
