using System.Collections.Frozen;

namespace RepoPilot.Domain.Capabilities;

/// <summary>
/// What a capability is permitted to do (FR-026c). Enforced at the call site by
/// the invoker, never by instruction given to the model — a prompt cannot be
/// relied on to constrain the thing reading the prompt.
/// </summary>
public enum PermissionClass
{
    /// <summary>Reads indexed content or files inside the run's working copy.</summary>
    Read,

    /// <summary>Returns a described change. Touches no file.</summary>
    NoDirectWrite,

    /// <summary>Writes, only to the working copy, only with a matching approval.</summary>
    WriteWithApproval,

    /// <summary>Runs an allow-listed command in an isolated environment.</summary>
    SandboxExecution,
}

/// <summary>
/// When a capability may be offered to the model.
/// <para>
/// Apply and test are orchestrator-invoked rather than model-invoked. If the
/// model could call apply mid-turn, the transition into the applying stage would
/// be inferred from model output instead of implemented in backend code, which
/// Principle IV forbids. This narrows the model-visible surface; it adds nothing
/// to the capability set, which stays at exactly seven.
/// </para>
/// </summary>
public enum InvocationSurface
{
    /// <summary>Offered to the model during retrieving, planning, and proposing.</summary>
    Model,

    /// <summary>Invoked by the orchestrator during applying and testing.</summary>
    Orchestrator,
}

/// <summary>One capability and the class it is enforced under.</summary>
/// <param name="Name">Stable name, as it appears in audit records and traces.</param>
/// <param name="PermissionClass">The single class this capability is enforced under.</param>
/// <param name="Surface">Who may invoke it.</param>
public sealed record CapabilityDescriptor(
    string Name,
    PermissionClass PermissionClass,
    InvocationSurface Surface);

/// <summary>
/// The closed set of capabilities (FR-026c).
/// <para>
/// Adding an entry here is not a code change; it is a constitution amendment.
/// Principle III requires measured evaluation evidence before the tool surface
/// grows, so this registry is deliberately a hard-coded, closed list rather than
/// something assembled by scanning for attributes or registered at startup.
/// </para>
/// </summary>
public static class CapabilityRegistry
{
    /// <summary>The number of capabilities the MVP may expose. Exactly seven.</summary>
    public const int ExpectedCount = 7;

    private static readonly FrozenDictionary<string, CapabilityDescriptor> ByName =
        new CapabilityDescriptor[]
        {
            new("list_files", PermissionClass.Read, InvocationSurface.Model),
            new("search_code", PermissionClass.Read, InvocationSurface.Model),
            new("read_file", PermissionClass.Read, InvocationSurface.Model),
            new("search_docs", PermissionClass.Read, InvocationSurface.Model),
            new("propose_patch", PermissionClass.NoDirectWrite, InvocationSurface.Model),
            new("apply_patch", PermissionClass.WriteWithApproval, InvocationSurface.Orchestrator),
            new("run_tests", PermissionClass.SandboxExecution, InvocationSurface.Orchestrator),
        }.ToFrozenDictionary(c => c.Name, StringComparer.Ordinal);

    /// <summary>Every capability, in registration order.</summary>
    public static IReadOnlyCollection<CapabilityDescriptor> All => ByName.Values;

    /// <summary>Capabilities the model may be offered at the given stage group.</summary>
    public static IEnumerable<CapabilityDescriptor> ForSurface(InvocationSurface surface) =>
        ByName.Values.Where(c => c.Surface == surface);

    /// <summary>
    /// Looks up a capability by name.
    /// </summary>
    /// <exception cref="UnknownCapabilityException">
    /// The name is not in the registry. An unrecognised capability is refused
    /// rather than defaulted, because a default would be a permission decision.
    /// </exception>
    public static CapabilityDescriptor Require(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return ByName.TryGetValue(name, out var descriptor)
            ? descriptor
            : throw new UnknownCapabilityException(name);
    }

    /// <summary>Whether <paramref name="name"/> is a known capability.</summary>
    public static bool IsKnown(string name) =>
        !string.IsNullOrWhiteSpace(name) && ByName.ContainsKey(name);
}

/// <summary>Raised when an unregistered capability name is invoked.</summary>
public sealed class UnknownCapabilityException(string name)
    : InvalidOperationException(
        $"'{name}' is not one of the {CapabilityRegistry.ExpectedCount} defined capabilities. " +
        "Adding one requires a constitution amendment citing measured evaluation evidence.")
{
    public string Name { get; } = name;
}
