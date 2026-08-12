using System.Diagnostics;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Runs;
using RepoPilot.Domain.Capabilities;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Security;
using RepoPilot.Infrastructure.Observability;

namespace RepoPilot.Agent.Invocation;

/// <summary>The result of an invocation that the caller sees.</summary>
/// <param name="Content">What the capability returned.</param>
/// <param name="DurationMs">How long it took.</param>
public sealed record CapabilityOutcome(string Content, int DurationMs);

/// <summary>
/// The single point every capability invocation passes through.
/// <para>
/// The controls live here rather than in each capability because a control that
/// each implementation has to remember is a control that a future implementation
/// will forget. Permission class, context budget, the audit record, and the
/// trace span are applied around the capability, so a new capability gets them
/// whether or not its author thought about them.
/// </para>
/// <para>
/// The audit record is written in a <c>finally</c>. That is what makes FR-027
/// hold for the case that matters: a capability that throws still produces a
/// recorded action with <c>failed</c> status, so a refused path access or a
/// budget breach is countable (FR-024c, SC-010) rather than invisible.
/// </para>
/// </summary>
public sealed class ToolInvoker(
    IEnumerable<ICapability> capabilities,
    RunEventRecorder recorder)
{
    private readonly Dictionary<string, ICapability> _capabilities =
        capabilities.ToDictionary(c => c.Name, StringComparer.Ordinal);

    /// <summary>
    /// Invokes a capability by name.
    /// </summary>
    /// <param name="context">Per-run state.</param>
    /// <param name="capabilityName">Name from the closed registry.</param>
    /// <param name="argumentsJson">Raw JSON arguments.</param>
    /// <param name="callerSurface">
    /// Who is invoking. The model may only reach capabilities declared
    /// <see cref="InvocationSurface.Model"/>; apply and test are
    /// orchestrator-invoked (Principle IV).
    /// </param>
    /// <exception cref="UnknownCapabilityException">Not a defined capability.</exception>
    /// <exception cref="CapabilitySurfaceViolationException">Wrong caller.</exception>
    /// <exception cref="ContextBudgetExceededException">Output exceeds the run's budget.</exception>
    public async Task<CapabilityOutcome> InvokeAsync(
        CapabilityContext context,
        string capabilityName,
        string argumentsJson,
        InvocationSurface callerSurface,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        // Resolved before the try so an unknown name fails without inventing an
        // audit record for a capability that does not exist. Defaulting an
        // unrecognised name to any permission class would be a permission
        // decision made by accident.
        var descriptor = CapabilityRegistry.Require(capabilityName);

        if (descriptor.Surface != callerSurface)
        {
            throw new CapabilitySurfaceViolationException(
                capabilityName, callerSurface, descriptor.Surface);
        }

        if (!_capabilities.TryGetValue(capabilityName, out var capability))
        {
            throw new InvalidOperationException(
                $"'{capabilityName}' is registered but no implementation is wired up.");
        }

        using var activity = Telemetry.Source.StartActivity($"capability.{capabilityName}");
        activity?.SetTag("repopilot.run_id", context.RunId);
        activity?.SetTag("repopilot.capability", capabilityName);
        activity?.SetTag("repopilot.permission_class", descriptor.PermissionClass.ToString());

        string? errorMessage = null;
        string? argumentsSummary = null;
        var status = RunEventStatus.Succeeded;

        try
        {
            var result = await capability.InvokeAsync(context, argumentsJson, ct);
            argumentsSummary = result.ArgumentsSummary;

            if (!context.Budget.TryCharge(result.ChargeableCharacters))
            {
                // The content is discarded rather than truncated. A silently
                // shortened file reads as a complete one to the model, which is
                // a worse failure than an explicit refusal (FR-006).
                throw new ContextBudgetExceededException(
                    capabilityName, result.ChargeableCharacters, context.Budget.Remaining);
            }

            activity?.SetTag("repopilot.budget_charged", result.ChargeableCharacters);
            activity?.SetTag("repopilot.budget_remaining", context.Budget.Remaining);

            return new CapabilityOutcome(result.Content, (int)stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            status = RunEventStatus.Failed;
            errorMessage = ex.Message;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            stopwatch.Stop();

            // In a finally so a throwing capability still records a failed
            // action. Without this, exactly the invocations worth auditing —
            // refused paths, disallowed commands, budget breaches — would be the
            // ones that left no trace.
            await RecordAsync(
                context.RunId,
                capabilityName,
                argumentsSummary ?? SummarizeArguments(argumentsJson),
                status,
                errorMessage,
                startedAt,
                (int)stopwatch.ElapsedMilliseconds,
                ct);
        }
    }

    private async Task RecordAsync(
        Guid runId,
        string capabilityName,
        string? argumentsSummary,
        RunEventStatus status,
        string? errorMessage,
        DateTimeOffset startedAt,
        int durationMs,
        CancellationToken ct)
    {
        try
        {
            await recorder.RecordAsync(
                new RunEvent
                {
                    RunId = runId,
                    EventType = RunEventType.ToolCall,
                    ToolName = capabilityName,
                    ArgumentsSummary = argumentsSummary,
                    Status = status,
                    ErrorMessage = errorMessage is null ? null : SecretRedactor.Redact(errorMessage),
                    StartedAt = startedAt,
                    EndedAt = startedAt.AddMilliseconds(durationMs),
                    DurationMs = durationMs,
                },
                // Not the caller's token: a cancelled run must still record what
                // it did before being cancelled, or the audit trail loses exactly
                // the actions that led to the cancellation.
                CancellationToken.None);
        }
        catch (Exception)
        {
            // A failure to record must not replace the capability's own
            // exception, which is the one the caller needs to see. The throw in
            // the catch block above has already been issued; swallowing here
            // preserves it.
        }
    }

    /// <summary>
    /// Produces a bounded, redacted summary of the raw arguments.
    /// <para>
    /// Summaries are persisted and shown to reviewers (FR-027a), so full file
    /// contents never go in and anything matching a credential shape is redacted
    /// on the way through.
    /// </para>
    /// </summary>
    internal static string SummarizeArguments(string argumentsJson)
    {
        const int maxLength = 512;

        if (string.IsNullOrEmpty(argumentsJson))
        {
            return "{}";
        }

        var redacted = SecretRedactor.Redact(argumentsJson);

        return redacted.Length <= maxLength
            ? redacted
            : redacted[..maxLength] + $"… ({redacted.Length} chars)";
    }
}
