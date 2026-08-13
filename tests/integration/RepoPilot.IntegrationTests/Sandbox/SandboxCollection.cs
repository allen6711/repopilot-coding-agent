using Xunit;

namespace RepoPilot.IntegrationTests.Sandbox;

/// <summary>
/// Serializes the sandbox test classes.
/// <para>
/// Two of them assert that no labelled container survives a run, which is a
/// question about daemon state as a whole — another class creating a container
/// at that moment would answer it wrongly. The tests are not order-dependent;
/// they are dependent on nothing else holding a sandbox container open while
/// they look.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SandboxCollection
{
    public const string Name = "Sandbox";
}
