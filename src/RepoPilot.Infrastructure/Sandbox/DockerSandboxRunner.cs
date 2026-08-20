using System.Diagnostics;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using RepoPilot.Application.Ports;

namespace RepoPilot.Infrastructure.Sandbox;

/// <summary>
/// Runs allow-listed commands in a Docker container (Principle II).
/// <para>
/// The isolation controls are not configurable per run. They are applied to
/// every container this type creates, because a control that a caller can turn
/// off is a control that will eventually be turned off — and the value of the
/// sandbox is that reasoning about it does not depend on the caller.
/// </para>
/// </summary>
public sealed class DockerSandboxRunner : ISandboxRunner, IDisposable
{
    /// <summary>
    /// Stamped on every container this type creates. It is what makes an orphan
    /// identifiable: startup recovery can find containers this process left
    /// behind without needing a record of them (FR-026e).
    /// </summary>
    public const string OwnerLabel = "repopilot.sandbox";

    /// <summary>
    /// The label's value: unique per runner instance.
    /// <para>
    /// A sweep for orphans filters on the key alone and so still finds
    /// containers from any previous process. The value narrows the other
    /// direction — it lets a caller ask about containers <em>this</em> runner
    /// created, without a query over one daemon accidentally answering for
    /// every process sharing it.
    /// </para>
    /// </summary>
    public string InstanceId { get; } = Guid.CreateVersion7().ToString("N");

    private readonly Lazy<DockerClient> _client;
    private readonly ILogger<DockerSandboxRunner> _logger;

    public DockerSandboxRunner(ILogger<DockerSandboxRunner> logger)
    {
        _logger = logger;

        // Lazy on purpose. Constructing this type must not require a daemon, or
        // the whole service would refuse to start without one — and the endpoints
        // that read a finished run's diff and test output have no need of it.
        // A missing daemon fails the run that needs the sandbox, at the point it
        // needs it, which is where the failure is meaningful.
        _client = new Lazy<DockerClient>(
            () =>
            {
                try
                {
                    // Endpoint and transport come from the ambient Docker
                    // configuration, the same way Testcontainers resolves them,
                    // so the tests and the service talk to one daemon.
                    return new DockerClientBuilder().Build();
                }
                catch (Exception ex)
                {
                    throw new SandboxUnavailableException(
                        "Could not connect to a container runtime. There is no unisolated " +
                        "fallback: a run that cannot execute tests in isolation fails rather " +
                        "than executing them without it.",
                        ex);
                }
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public async Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Argv.Count == 0)
        {
            throw new ArgumentException("An empty argument vector is not executable.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        string? containerId = null;

        try
        {
            containerId = await CreateAsync(request, ct);
            await _client.Value.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(request.Timeout);

            long? exitCode = null;
            var timedOut = false;

            try
            {
                var wait = await _client.Value.Containers.WaitContainerAsync(containerId, deadline.Token);
                exitCode = wait.StatusCode;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The limit expired rather than the caller cancelling.
                timedOut = true;
                await KillAsync(containerId);
            }

            stopwatch.Stop();

            var output = await ReadLogsAsync(containerId, timedOut);

            return new SandboxResult(
                Passed: !timedOut && exitCode == 0,
                ExitCode: timedOut ? null : (int?)exitCode,
                Output: output,
                Duration: stopwatch.Elapsed,
                TimedOut: timedOut);
        }
        catch (DockerApiException ex)
        {
            throw new SandboxUnavailableException(
                $"The container runtime refused the request: {ex.Message}", ex);
        }
        finally
        {
            if (containerId is not null)
            {
                await RemoveAsync(containerId);
            }
        }
    }

    private async Task<string> CreateAsync(SandboxRequest request, CancellationToken ct)
    {
        var parameters = new CreateContainerParameters
        {
            Image = request.Image,

            // The allow-listed argument vector, passed directly. No shell is
            // involved, so operators such as && or | are literal arguments with
            // no special meaning (FR-022a).
            Cmd = [.. request.Argv],

            WorkingDir = request.WorkDir,

            Labels = new Dictionary<string, string> { [OwnerLabel] = InstanceId },

            // Nothing from the host environment. A credential exported into the
            // service's process must not become visible to test code (FR-025).
            Env = [],

            AttachStdout = true,
            AttachStderr = true,
            NetworkDisabled = true,

            // Non-root, and specifically the identity that owns the working copy.
            // See SandboxUser.
            User = SandboxUser(),

            HostConfig = new HostConfig
            {
                // FR-021a. Belt and braces with NetworkDisabled above: one sets
                // the container's network mode, the other detaches it entirely.
                NetworkMode = "none",

                ReadonlyRootfs = true,

                // A read-only root would break most toolchains without somewhere
                // to write scratch data. noexec stops that becoming a way to
                // stage and run a binary.
                //
                // 256MB rather than a token amount: a real toolchain writes
                // package extraction and build scratch here, and 64MB was not
                // enough for a .NET restore. It is still a bound, and it is
                // still non-executable.
                Tmpfs = new Dictionary<string, string>
                {
                    ["/tmp"] = "rw,noexec,nosuid,size=256m",
                },

                Binds = [$"{request.WorkingCopyPath}:{request.WorkDir}:rw"],

                // FR-021b: a runaway or hostile test cannot exhaust the host.
                Memory = (long)request.MemoryMegabytes * 1024 * 1024,
                NanoCPUs = (long)(request.CpuCount * 1_000_000_000),
                PidsLimit = request.PidsLimit,

                CapDrop = ["ALL"],
                SecurityOpt = ["no-new-privileges"],

                AutoRemove = false,
            },
        };

        try
        {
            var created = await _client.Value.Containers.CreateContainerAsync(parameters, ct);
            return created.ID;
        }
        catch (DockerImageNotFoundException ex)
        {
            throw new SandboxUnavailableException(
                $"Image '{request.Image}' is not available. Fixture images are pre-baked because " +
                "the sandbox has no network and nothing can be fetched at test time.", ex);
        }
    }

    private async Task<string> ReadLogsAsync(string containerId, bool timedOut)
    {
        try
        {
            using var stream = await _client.Value.Containers.GetContainerLogsAsync(
                containerId,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true },
                CancellationToken.None);

            var (stdout, stderr) = await stream.ReadOutputToEndAsync(CancellationToken.None);

            // FR-025b: redacted before it is stored, displayed, or returned to
            // the agent for a revision attempt. Test output is one of the places
            // content reaches model context, and the only one where it arrives
            // from a process the model influenced.
            return OutputRedaction.Prepare(stdout, stderr, timedOut);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read container logs for {ContainerId}.", containerId);
            return "[output unavailable]";
        }
    }

    private async Task KillAsync(string containerId)
    {
        try
        {
            await _client.Value.Containers.KillContainerAsync(
                containerId, new ContainerKillParameters(), CancellationToken.None);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone; the timeout still stands.
        }
        catch (Exception ex)
        {
            // FR-023b: the limit expired and the environment will not stop. The
            // run fails with that recorded rather than waiting indefinitely for
            // something that is not going to happen.
            throw new SandboxNotTerminableException(
                $"The execution time limit expired but container {containerId} could not be " +
                "terminated.", ex);
        }
    }

    private async Task RemoveAsync(string containerId)
    {
        try
        {
            await _client.Value.Containers.RemoveContainerAsync(
                containerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Logged, not thrown: a leaked container is a real problem but the
            // caller needs the execution result, and startup recovery sweeps
            // what is left behind (FR-026e).
            _logger.LogError(ex, "Could not remove container {ContainerId}.", containerId);
        }
    }

    /// <summary>
    /// The identity the container runs as: the one that owns the bind-mounted
    /// working copy.
    /// <para>
    /// A hardcoded <c>1000:1000</c> works on Docker Desktop, whose bind mounts
    /// translate ownership, and fails on Linux, where they do not. There the
    /// working copy is a real directory owned by whoever runs this service, and a
    /// container running as a different uid cannot write to it — so
    /// <c>apply_patch</c> would succeed and the test command that follows would
    /// fail on a permission error that looks like a broken fixture. CI found this;
    /// every local run had passed.
    /// </para>
    /// <para>
    /// The image is built for uid 1000 but does not require it: the root
    /// filesystem is read-only and every writable path a toolchain needs is
    /// redirected to the tmpfs at <c>/tmp</c>, while the baked package cache is
    /// world-readable. Any uid can therefore run it.
    /// </para>
    /// </summary>
    private static string SandboxUser()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return "1000:1000";
        }

        var uid = GetEuid();

        // Running as root is the one case where matching the host identity would
        // be a downgrade. Principle II says the sandbox process is non-root, and
        // that holds regardless of what this service was started as.
        return uid == 0 ? "1000:1000" : $"{uid}:{GetEgid()}";
    }

    // DllImport rather than LibraryImport: the source generator requires
    // AllowUnsafeBlocks across the whole project, which is a large permission to
    // take for two argument-free syscalls returning a blittable integer.
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint GetEgid();

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
