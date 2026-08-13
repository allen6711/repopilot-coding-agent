using System.Diagnostics;

namespace RepoPilot.IntegrationTests.Infrastructure;

/// <summary>
/// Runs a <c>docker</c> command and returns its stdout.
/// <para>
/// Used only by tests that need to observe daemon state from outside the runner
/// — asking the API client whether it cleaned up would be asking the thing under
/// test to grade itself.
/// </para>
/// </summary>
internal static class DockerCli
{
    public static async Task<string> RunAsync(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not start the Docker CLI.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return stdout;
    }
}
