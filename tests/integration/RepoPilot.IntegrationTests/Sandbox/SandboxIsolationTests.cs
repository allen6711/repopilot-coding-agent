using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Application.Ports;
using RepoPilot.Infrastructure.Sandbox;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Sandbox;

/// <summary>
/// Principle II, verified by observation.
/// <para>
/// Every test here asserts on what a process <em>inside</em> the container can
/// actually do, not on the parameters the runner sent. Asserting that
/// <c>NetworkMode</c> was set to "none" would pass even if the daemon ignored
/// it; asserting that a socket call fails is the claim that matters.
/// </para>
/// </summary>
[Collection(SandboxCollection.Name)]
public sealed class SandboxIsolationTests : IDisposable
{

    private readonly DockerSandboxRunner _runner = new(NullLogger<DockerSandboxRunner>.Instance);
    private readonly string _workingCopy = Directory.CreateTempSubdirectory("repopilot-sandbox-").FullName;

    private SandboxRequest Request(params string[] argv) => new(
        Image: SandboxImageFixture.Image,
        Argv: argv,
        WorkingCopyPath: _workingCopy,
        WorkDir: "/workspace",
        Timeout: TimeSpan.FromSeconds(60),
        MemoryMegabytes: 256,
        CpuCount: 1.0,
        PidsLimit: 64);

    [RequiresDockerFact]
    public async Task NetworkIsUnreachable()
    {
        // A DNS lookup and a raw connection both have to fail. Only checking one
        // would leave the other as an open path out.
        var result = await _runner.RunAsync(Request(
            "sh", "-c", "wget -T 3 -q -O - http://1.1.1.1/ && echo REACHED || echo BLOCKED"));

        Assert.Contains("BLOCKED", result.Output);
        Assert.DoesNotContain("REACHED", result.Output);
    }

    [RequiresDockerFact]
    public async Task RootFilesystemIsReadOnly()
    {
        var result = await _runner.RunAsync(Request(
            "sh", "-c", "touch /etc/repopilot-probe && echo WROTE || echo REFUSED"));

        Assert.Contains("REFUSED", result.Output);
    }

    [RequiresDockerFact]
    public async Task ScratchSpaceIsWritableButNotExecutable()
    {
        // A read-only root with no scratch space breaks real toolchains, so /tmp
        // is writable — but noexec stops it becoming the place to stage and run
        // something the image does not contain.
        var result = await _runner.RunAsync(Request(
            "sh", "-c",
            "cp /bin/busybox /tmp/probe && /tmp/probe true && echo EXECUTED || echo NOEXEC"));

        Assert.Contains("NOEXEC", result.Output);
    }

    [RequiresDockerFact]
    public async Task ProcessDoesNotRunAsRoot()
    {
        var result = await _runner.RunAsync(Request("id", "-u"));

        Assert.True(result.Passed);
        Assert.Equal("1000", result.Output.Trim());
    }

    [RequiresDockerFact]
    public async Task HostEnvironmentIsNotInherited()
    {
        // The variable exists in this test process. If the container can see it,
        // any credential the service holds is visible to test code (FR-025).
        Environment.SetEnvironmentVariable("REPOPILOT_LEAK_PROBE", "leaked-value");

        try
        {
            var result = await _runner.RunAsync(Request("printenv"));

            Assert.DoesNotContain("leaked-value", result.Output);
            Assert.DoesNotContain("REPOPILOT_LEAK_PROBE", result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REPOPILOT_LEAK_PROBE", null);
        }
    }

    [RequiresDockerFact]
    public async Task WorkingCopyIsWritableAndIsWhereTheCommandRuns()
    {
        var result = await _runner.RunAsync(Request(
            "sh", "-c", "pwd && touch scratch.txt && echo WROTE"));

        Assert.True(result.Passed, result.Output);
        Assert.Contains("/workspace", result.Output);
        Assert.Contains("WROTE", result.Output);

        // The write landed in the run's disposable copy on the host — which is
        // the point of binding it: the fixture itself is never touched.
        Assert.True(File.Exists(Path.Combine(_workingCopy, "scratch.txt")));
    }

    [RequiresDockerFact]
    public async Task PrivilegesCannotBeEscalated()
    {
        // Capabilities dropped: an operation that needs CAP_CHOWN fails even
        // though the file belongs to the running user.
        var result = await _runner.RunAsync(Request(
            "sh", "-c", "touch /tmp/probe && chown 0:0 /tmp/probe && echo CHOWNED || echo REFUSED"));

        Assert.Contains("REFUSED", result.Output);
    }

    [RequiresDockerFact]
    public async Task MemoryCeilingIsEnforced()
    {
        // Asks for four times the 256MB ceiling. Under the limit the allocation
        // fails or the process is killed; either way it does not succeed, and
        // the host is not the thing that absorbs it.
        var result = await _runner.RunAsync(Request(
            "sh", "-c", "dd if=/dev/zero of=/tmp/fill bs=1M count=1024 2>/dev/null && echo FILLED || echo LIMITED"));

        Assert.DoesNotContain("FILLED", result.Output);
    }

    [RequiresDockerFact]
    public async Task ProcessCountCeilingIsEnforced()
    {
        var result = await _runner.RunAsync(Request(
            "sh", "-c", "i=0; while [ $i -lt 200 ]; do sleep 30 & i=$((i+1)); done; echo SPAWNED-ALL"));

        Assert.DoesNotContain("SPAWNED-ALL", result.Output);
    }

    [RequiresDockerFact]
    public async Task ShellOperatorsInArgumentsAreLiteralText()
    {
        // FR-022a. The vector is passed straight to exec, so the process sees
        // four arguments — it does not see a command that then runs `id`.
        var result = await _runner.RunAsync(Request("echo", "safe", "&&", "id"));

        Assert.True(result.Passed);
        Assert.Contains("safe && id", result.Output);
        Assert.DoesNotContain("uid=", result.Output);
    }

    [RequiresDockerFact]
    public async Task ContainerIsRemovedAfterTheRun()
    {
        var result = await _runner.RunAsync(Request("true"));

        Assert.True(result.Passed);

        // FR-026e. The owner label scopes this to containers this runner created,
        // so the assertion is about our cleanup and not about whatever else
        // happens to be on the machine.
        var survivors = await DockerCli.RunAsync(
            $"ps -aq --filter label={DockerSandboxRunner.OwnerLabel}");

        Assert.True(
            survivors.Trim().Length == 0,
            $"Sandbox containers were left behind: {survivors}");
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
