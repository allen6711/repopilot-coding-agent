using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;

namespace RepoPilot.IntegrationTests.Sandbox;

/// <summary>
/// Ensures the image the sandbox tests execute in is present on the daemon.
/// <para>
/// The sandbox itself has no network, so an image it needs must exist before the
/// container is created — in production that is a pre-baked fixture image built
/// at deploy time. The test suite has no such build step, and relying on the
/// image happening to be cached makes the tests fail for reasons that have
/// nothing to do with the code: the daemon prunes, and the whole group turns
/// red claiming isolation is broken.
/// </para>
/// <para>
/// Pulling here rather than inside the runner keeps the distinction intact. The
/// test harness may reach the network; the sandbox may not.
/// </para>
/// </summary>
public sealed class SandboxImageFixture : IAsyncLifetime
{
    public const string Image = "alpine:3";

    public async Task InitializeAsync()
    {
        if (DockerAvailability.UnavailableReason is not null)
        {
            // Every test in the collection carries [RequiresDockerFact] and will
            // skip, so pulling nothing is correct rather than a silent failure.
            return;
        }

        var present = await DockerCli.RunAsync($"images -q {Image}");

        if (present.Trim().Length == 0)
        {
            await DockerCli.RunAsync($"pull {Image}");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// Serializes the sandbox test classes and shares the image fixture.
/// <para>
/// Two of them assert that no labelled container survives a run, which is a
/// question about daemon state as a whole — another class creating a container
/// at that moment would answer it wrongly. The tests are not order-dependent;
/// they are dependent on nothing else holding a sandbox container open while
/// they look.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SandboxCollection : ICollectionFixture<SandboxImageFixture>
{
    public const string Name = "Sandbox";
}
