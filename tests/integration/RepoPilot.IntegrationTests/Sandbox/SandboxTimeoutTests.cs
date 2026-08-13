using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Application.Ports;
using RepoPilot.Infrastructure.Sandbox;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Sandbox;

/// <summary>
/// FR-023: execution is bounded, and a timeout is reported as its own thing.
/// </summary>
[Collection(SandboxCollection.Name)]
public sealed class SandboxTimeoutTests : IDisposable
{
    private readonly DockerSandboxRunner _runner = new(NullLogger<DockerSandboxRunner>.Instance);
    private readonly string _workingCopy = Directory.CreateTempSubdirectory("repopilot-timeout-").FullName;

    private SandboxRequest Request(TimeSpan timeout, params string[] argv) => new(
        Image: "alpine:3",
        Argv: argv,
        WorkingCopyPath: _workingCopy,
        WorkDir: "/workspace",
        Timeout: timeout,
        MemoryMegabytes: 256,
        CpuCount: 1.0,
        PidsLimit: 64);

    [RequiresDockerFact]
    public async Task ACommandThatOverrunsIsTerminatedAtTheLimit()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await _runner.RunAsync(
            Request(TimeSpan.FromSeconds(3), "sleep", "120"));

        stopwatch.Stop();

        Assert.True(result.TimedOut);

        // The limit actually bounded the wait rather than the command finishing
        // on its own. A generous ceiling keeps this from being a timing test.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(45),
            $"Termination took {stopwatch.Elapsed}, which is not a bound.");
    }

    [RequiresDockerFact]
    public async Task ATimeoutIsNotReportedAsATestFailure()
    {
        var result = await _runner.RunAsync(
            Request(TimeSpan.FromSeconds(3), "sleep", "120"));

        // SC-009 counts clean finishes and forced terminations separately, so a
        // timeout must be distinguishable from a command that ran and failed.
        // If TimedOut collapsed into Passed=false, the two would be identical in
        // the record and the criterion could not be measured.
        Assert.True(result.TimedOut);
        Assert.False(result.Passed);
        Assert.Null(result.ExitCode);
        Assert.Contains("execution time limit reached", result.Output);
    }

    [RequiresDockerFact]
    public async Task ACleanFailureIsDistinguishableFromATimeout()
    {
        var result = await _runner.RunAsync(
            Request(TimeSpan.FromSeconds(60), "sh", "-c", "exit 3"));

        Assert.False(result.TimedOut);
        Assert.False(result.Passed);
        Assert.Equal(3, result.ExitCode);
    }

    [RequiresDockerFact]
    public async Task OutputProducedBeforeTheLimitIsStillReturned()
    {
        // A test suite that printed its failures and then hung should not lose
        // the failures — that output is the evidence for what to do next.
        var result = await _runner.RunAsync(
            Request(TimeSpan.FromSeconds(5), "sh", "-c", "echo before-the-hang; sleep 120"));

        Assert.True(result.TimedOut);
        Assert.Contains("before-the-hang", result.Output);
    }

    [RequiresDockerFact]
    public async Task ATerminatedContainerIsStillRemoved()
    {
        await _runner.RunAsync(Request(TimeSpan.FromSeconds(3), "sleep", "120"));

        var survivors = await DockerCli.RunAsync(
            $"ps -aq --filter label={DockerSandboxRunner.OwnerLabel}");

        Assert.True(
            survivors.Trim().Length == 0,
            $"A timed-out container was left behind: {survivors}");
    }

    public void Dispose()
    {
        _runner.Dispose();

        try
        {
            Directory.Delete(_workingCopy, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
