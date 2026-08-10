using RepoPilot.Domain.Capabilities;
using Xunit;

namespace RepoPilot.UnitTests.Domain;

/// <summary>
/// Principle III constrains the tool surface. These tests make the constraint
/// executable: the set is exactly seven, each capability has exactly one
/// permission class, and the write and execution capabilities are not reachable
/// from the model.
/// </summary>
public sealed class CapabilityRegistryTests
{
    [Fact]
    public void TheCapabilitySetIsExactlySeven()
    {
        // A test that would fail if someone quietly adds an eighth. Growing the
        // surface is a constitution amendment, not a code change.
        Assert.Equal(CapabilityRegistry.ExpectedCount, CapabilityRegistry.All.Count);
    }

    [Fact]
    public void TheSetMatchesTheContractExactly()
    {
        var expected = new[]
        {
            "apply_patch", "list_files", "propose_patch", "read_file",
            "run_tests", "search_code", "search_docs",
        };

        var actual = CapabilityRegistry.All.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("list_files", PermissionClass.Read)]
    [InlineData("search_code", PermissionClass.Read)]
    [InlineData("read_file", PermissionClass.Read)]
    [InlineData("search_docs", PermissionClass.Read)]
    [InlineData("propose_patch", PermissionClass.NoDirectWrite)]
    [InlineData("apply_patch", PermissionClass.WriteWithApproval)]
    [InlineData("run_tests", PermissionClass.SandboxExecution)]
    public void EachCapabilityHasItsContractedPermissionClass(string name, PermissionClass expected)
    {
        Assert.Equal(expected, CapabilityRegistry.Require(name).PermissionClass);
    }

    [Fact]
    public void ExactlyOneCapabilityMayWrite()
    {
        var writers = CapabilityRegistry.All
            .Where(c => c.PermissionClass == PermissionClass.WriteWithApproval)
            .ToList();

        Assert.Single(writers);
        Assert.Equal("apply_patch", writers[0].Name);
    }

    [Fact]
    public void ProposePatchIsNotAWriteCapability()
    {
        // Principle I: proposing must not mutate anything. The class is the
        // declaration of that; the invoker enforces it.
        Assert.Equal(
            PermissionClass.NoDirectWrite,
            CapabilityRegistry.Require("propose_patch").PermissionClass);
    }

    [Fact]
    public void WriteAndExecutionCapabilitiesAreNotOfferedToTheModel()
    {
        // Principle IV: the stage transition into applying is decided by backend
        // code, so the model cannot hold the capability that would trigger it.
        var modelVisible = CapabilityRegistry.ForSurface(InvocationSurface.Model).ToList();

        Assert.DoesNotContain(modelVisible, c => c.PermissionClass == PermissionClass.WriteWithApproval);
        Assert.DoesNotContain(modelVisible, c => c.PermissionClass == PermissionClass.SandboxExecution);
        Assert.Equal(5, modelVisible.Count);
    }

    [Fact]
    public void OrchestratorSurfaceIsExactlyApplyAndTest()
    {
        var orchestrated = CapabilityRegistry.ForSurface(InvocationSurface.Orchestrator)
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(new[] { "apply_patch", "run_tests" }, orchestrated);
    }

    [Fact]
    public void AnUnknownCapability_IsRefusedRatherThanDefaulted()
    {
        // Defaulting an unrecognised name to any class would be a permission
        // decision made by accident.
        Assert.Throws<UnknownCapabilityException>(() => CapabilityRegistry.Require("run_shell"));
        Assert.False(CapabilityRegistry.IsKnown("run_shell"));
    }

    [Fact]
    public void ThereIsNoShellOrArbitraryCommandCapability()
    {
        // FR-026. Named explicitly so the absence is asserted rather than merely
        // being true today.
        foreach (var forbidden in new[] { "bash", "shell", "exec", "run_command", "eval" })
        {
            Assert.False(CapabilityRegistry.IsKnown(forbidden), $"'{forbidden}' must not exist.");
        }
    }
}
