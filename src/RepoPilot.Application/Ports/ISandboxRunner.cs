namespace RepoPilot.Application.Ports;

/// <summary>
/// One execution request.
/// </summary>
/// <param name="Image">Pre-baked image carrying the fixture's dependencies.</param>
/// <param name="Argv">
/// Argument vector, never a shell string. There is no parameter here that
/// accepts a command line, which is what stops a separator, a redirection, or a
/// substitution from being introduced through configuration or model output
/// (FR-022a).
/// </param>
/// <param name="WorkingCopyPath">Host path bind-mounted as the workspace.</param>
/// <param name="WorkDir">Mount point inside the container.</param>
/// <param name="Timeout">Hard limit; on expiry the environment is destroyed.</param>
/// <param name="MemoryMegabytes">Memory ceiling.</param>
/// <param name="CpuCount">Processor share.</param>
/// <param name="PidsLimit">Process count ceiling.</param>
public sealed record SandboxRequest(
    string Image,
    IReadOnlyList<string> Argv,
    string WorkingCopyPath,
    string WorkDir,
    TimeSpan Timeout,
    int MemoryMegabytes,
    double CpuCount,
    int PidsLimit);

/// <summary>What an execution produced.</summary>
/// <param name="Passed">Whether the command reported success.</param>
/// <param name="ExitCode">Exit status, or null when terminated by timeout.</param>
/// <param name="Output">Combined stdout and stderr, redacted.</param>
/// <param name="Duration">Wall-clock duration.</param>
/// <param name="TimedOut">
/// Whether the limit stopped it. Reported separately from a clean finish because
/// SC-009 counts the two distinctly — a timeout is not a test failure.
/// </param>
public sealed record SandboxResult(
    bool Passed,
    int? ExitCode,
    string Output,
    TimeSpan Duration,
    bool TimedOut);

/// <summary>
/// Runs a command in an isolated environment (FR-021, Principle II).
/// </summary>
public interface ISandboxRunner
{
    Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default);
}

/// <summary>
/// Raised when no isolated environment is available. The run fails with that
/// reason recorded; there is deliberately no unisolated fallback path.
/// </summary>
public sealed class SandboxUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Raised when the time limit expired and the environment could not be
/// destroyed (FR-023b). The run fails rather than waiting indefinitely.
/// </summary>
public sealed class SandboxNotTerminableException(string message, Exception? inner = null)
    : Exception(message, inner);
