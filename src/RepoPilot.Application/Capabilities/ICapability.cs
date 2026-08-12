using RepoPilot.Domain.Capabilities;
using RepoPilot.Domain.Workspace;

namespace RepoPilot.Application.Capabilities;

/// <summary>
/// Per-run state a capability needs, supplied by the orchestrator rather than
/// resolved by the capability itself.
/// </summary>
/// <param name="RunId">The run this invocation belongs to.</param>
/// <param name="RepositoryId">The repository being worked on.</param>
/// <param name="Workspace">
/// The run's disposable working copy, and the only root any file access resolves
/// against (FR-024a).
/// </param>
/// <param name="Budget">The run's remaining retrieved-content allowance.</param>
public sealed record CapabilityContext(
    Guid RunId,
    Guid RepositoryId,
    WorkspaceRoot Workspace,
    RunContextBudget Budget);

/// <summary>
/// What a capability produced.
/// </summary>
/// <param name="Content">
/// The value returned to the caller, and — for read capabilities — the text that
/// will reach model context.
/// </param>
/// <param name="ChargeableCharacters">
/// How much of <paramref name="Content"/> counts against the run's context
/// budget. Zero for capabilities whose output never enters model context.
/// </param>
/// <param name="ArgumentsSummary">
/// A redacted, bounded description of the arguments for the audit record. Never
/// full file contents (FR-027a).
/// </param>
public sealed record CapabilityResult(
    string Content,
    int ChargeableCharacters,
    string? ArgumentsSummary = null);

/// <summary>
/// One of the seven defined capabilities (FR-026c).
/// <para>
/// A capability implements behaviour only. Permission class, path confinement,
/// budget accounting, and the audit record are applied by
/// <c>ToolInvoker</c> around it — so a capability cannot forget them, and a new
/// one cannot be added that quietly skips them.
/// </para>
/// </summary>
public interface ICapability
{
    /// <summary>Name, matching an entry in the closed registry.</summary>
    string Name { get; }

    /// <summary>
    /// Executes the capability.
    /// </summary>
    /// <param name="context">Per-run state.</param>
    /// <param name="argumentsJson">Raw JSON arguments, as produced by the model.</param>
    Task<CapabilityResult> InvokeAsync(
        CapabilityContext context,
        string argumentsJson,
        CancellationToken ct = default);
}

/// <summary>
/// Tracks how much retrieved content a run has consumed (FR-006).
/// <para>
/// Per run rather than per call: the limit exists to bound what reaches model
/// context over a whole run, and a per-call limit would be trivially defeated by
/// making more calls.
/// </para>
/// </summary>
public sealed class RunContextBudget(int totalCharacters)
{
    private readonly Lock _gate = new();
    private int _consumed;

    /// <summary>Total allowance.</summary>
    public int TotalCharacters { get; } = totalCharacters > 0
        ? totalCharacters
        : throw new ArgumentOutOfRangeException(nameof(totalCharacters));

    /// <summary>How much has been consumed so far.</summary>
    public int Consumed
    {
        get { lock (_gate) { return _consumed; } }
    }

    /// <summary>How much remains.</summary>
    public int Remaining => TotalCharacters - Consumed;

    /// <summary>
    /// Charges <paramref name="characters"/> against the budget.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the charge would exceed the allowance, in which case
    /// nothing is consumed. The caller refuses the retrieval rather than
    /// truncating it — a silently shortened file reads as a complete one to the
    /// model, which is worse than an explicit refusal.
    /// </returns>
    public bool TryCharge(int characters)
    {
        if (characters <= 0)
        {
            return true;
        }

        lock (_gate)
        {
            if (_consumed + characters > TotalCharacters)
            {
                return false;
            }

            _consumed += characters;
            return true;
        }
    }
}

/// <summary>
/// Raised when a capability's output would exceed the run's context budget.
/// </summary>
public sealed class ContextBudgetExceededException(string capabilityName, int requested, int remaining)
    : InvalidOperationException(
        $"'{capabilityName}' would return {requested} characters but only {remaining} remain in " +
        "the run's context budget. The retrieval is refused rather than truncated (FR-006).")
{
    public string CapabilityName { get; } = capabilityName;

    public int Requested { get; } = requested;

    public int Remaining { get; } = remaining;
}

/// <summary>
/// Raised when a capability is invoked from a surface not permitted to call it.
/// </summary>
public sealed class CapabilitySurfaceViolationException(
    string capabilityName,
    InvocationSurface attempted,
    InvocationSurface allowed)
    : InvalidOperationException(
        $"'{capabilityName}' may only be invoked by {allowed} but was invoked by {attempted}. " +
        "Apply and test are orchestrator-invoked so that the stage transition into applying is " +
        "decided by backend code rather than inferred from model output (Principle IV).")
{
    public string CapabilityName { get; } = capabilityName;

    public InvocationSurface Attempted { get; } = attempted;

    public InvocationSurface Allowed { get; } = allowed;
}
