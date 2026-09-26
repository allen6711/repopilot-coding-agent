using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.E2ETests;

/// <summary>
/// A test that needs a daemon and the pre-baked .NET fixture image.
/// <para>
/// Probed once per assembly. The image is not pulled on demand the way the
/// sandbox tests pull <c>alpine</c>: this one is built from
/// <c>sandbox/Dockerfile.dotnet</c>, takes minutes, and building it silently
/// inside a test run would hide a setup step behind a timeout. The skip reason
/// names the command instead.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresSandboxImageFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Probe =
        new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    public RequiresSandboxImageFactAttribute()
    {
        var reason = Probe.Value;
        if (reason is not null)
        {
            Skip = reason;
        }
    }

    private static string? Detect()
    {
        var docker = DockerAvailability.UnavailableReason;

        if (docker is not null)
        {
            return $"Requires Docker: {docker}";
        }

        var present = DockerCli.RunAsync($"images -q {RepoPilotHost.SandboxImage}")
            .GetAwaiter()
            .GetResult();

        return present.Trim().Length > 0
            ? null
            : $"Requires the sandbox image '{RepoPilotHost.SandboxImage}'. Build it with: " +
              "docker build -f sandbox/Dockerfile.dotnet -t repopilot/fixture-dotnet:1 .";
    }
}
