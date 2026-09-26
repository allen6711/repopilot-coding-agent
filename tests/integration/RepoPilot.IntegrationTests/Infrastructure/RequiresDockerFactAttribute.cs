using System.Diagnostics;
using Xunit;

namespace RepoPilot.IntegrationTests.Infrastructure;

/// <summary>
/// Probes for a usable Docker daemon exactly once per test assembly.
/// </summary>
internal static class DockerAvailability
{
    private static readonly Lazy<string?> Probe = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The reason Docker cannot be used, or <c>null</c> when it can.
    /// </summary>
    public static string? UnavailableReason => Probe.Value;

    private static string? Detect()
    {
        // An environment variable wins over probing so CI can insist the tests
        // run. Without that, a broken daemon in CI would look like a clean pass
        // made entirely of skips.
        if (Environment.GetEnvironmentVariable("REPOPILOT_REQUIRE_DOCKER") == "1")
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info --format {{.ServerVersion}}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return "Docker CLI could not be started.";
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return "Docker daemon did not respond within 10s.";
            }

            var version = process.StandardOutput.ReadToEnd().Trim();

            return process.ExitCode == 0 && version.Length > 0
                ? null
                : "Docker daemon is not running.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return "Docker CLI is not installed.";
        }
    }
}

/// <summary>
/// A test that needs a Docker daemon — Testcontainers-backed PostgreSQL, or the
/// sandbox runner.
/// <para>
/// Skipped rather than failed when no daemon is present, so a developer without
/// Docker still gets a clean local run, and the skip reason states exactly what
/// is missing rather than leaving a silent gap. CI sets
/// <c>REPOPILOT_REQUIRE_DOCKER=1</c>, which turns the skip off — a governance
/// control that skips everywhere would be a control that is never verified.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        var reason = DockerAvailability.UnavailableReason;
        if (reason is not null)
        {
            Skip = $"Requires Docker: {reason}";
        }
    }
}

/// <summary>Theory equivalent of <see cref="RequiresDockerFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresDockerTheoryAttribute : TheoryAttribute
{
    public RequiresDockerTheoryAttribute()
    {
        var reason = DockerAvailability.UnavailableReason;
        if (reason is not null)
        {
            Skip = $"Requires Docker: {reason}";
        }
    }
}

/// <summary>Whether a model-provider credential is configured.</summary>
internal static class ProviderCredential
{
    public static string? UnavailableReason =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"))
            ? null
            : "no ANTHROPIC_API_KEY or ANTHROPIC_AUTH_TOKEN.";
}

/// <summary>
/// A test that needs a model-provider credential. Compilation and unit coverage
/// do not, but anything driving a real completion does.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresProviderKeyFactAttribute : FactAttribute
{
    public RequiresProviderKeyFactAttribute()
    {
        var reason = ProviderCredential.UnavailableReason;
        if (reason is not null)
        {
            Skip = $"Requires a model-provider credential: {reason}";
        }
    }
}

/// <summary>
/// A test that needs both a daemon and a credential — an end-to-end measurement
/// against a real model over a real index.
/// <para>
/// The skip reason names every missing piece rather than the first one found. A
/// developer who sets a key and re-runs should not then discover they also
/// needed Docker.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresLiveStackFactAttribute : FactAttribute
{
    public RequiresLiveStackFactAttribute()
    {
        string?[] reasons = [DockerAvailability.UnavailableReason, ProviderCredential.UnavailableReason];
        var missing = reasons.Where(r => r is not null).ToArray();

        if (missing.Length > 0)
        {
            Skip = $"Requires a live stack: {string.Join(" Also: ", missing)}";
        }
    }
}
