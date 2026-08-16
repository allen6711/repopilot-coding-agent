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
/// Which capabilities a turn offers the model.
/// <para>
/// The distinction exists for the evaluation baseline (FR-033), which has to be
/// a genuinely retrieval-only condition rather than a tool-enabled agent that was
/// asked not to search. Withholding the tools is the only version of that claim
/// a reader can check.
/// </para>
/// </summary>
public enum OfferedCapabilities
{
    /// <summary>Every model-surface capability. The normal agent.</summary>
    All,

    /// <summary>
    /// <c>propose_patch</c> alone. Retrieved context is supplied up front instead
    /// of being searched for, so the run still reaches a proposal and the two
    /// conditions stay comparable on the same task set.
    /// </summary>
    ProposeOnly,
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
        OfferedCapabilities offered = OfferedCapabilities.All,
        CancellationToken ct = default);
}

/// <summary>
/// Supplies the context a retrieval-only run is given up front (FR-033).
/// <para>
/// A port rather than a direct call because the orchestrator must not know how
/// retrieval works, and because the baseline is a measurement condition — the
/// thing being compared against — so it belongs behind the same seam as
/// everything else the orchestrator depends on.
/// </para>
/// </summary>
public interface IBaselineContextProvider
{
    /// <summary>
    /// Retrieves context for <paramref name="taskDescription"/>, rendered for a
    /// prompt.
    /// </summary>
    /// <param name="maxCharacters">
    /// Ceiling on the returned text. Whole results are included until the next one
    /// would exceed it; nothing is cut mid-result, because a half-shown chunk
    /// reads to the model as a complete one (FR-006).
    /// </param>
    /// <returns>An empty string when nothing was retrieved.</returns>
    Task<string> RetrieveAsync(
        Guid repositoryId,
        string taskDescription,
        int maxCharacters,
        CancellationToken ct = default);
}
