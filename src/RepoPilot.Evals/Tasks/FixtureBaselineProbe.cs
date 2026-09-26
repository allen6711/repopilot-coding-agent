using RepoPilot.Agent.Capabilities;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;
using RepoPilot.Infrastructure.Workspace;

namespace RepoPilot.Evals.Tasks;

/// <summary>
/// Measures how a fixture's command behaves before any change.
/// <para>
/// The <c>tests_pass_and_baseline_failed</c> condition needs to know that a task
/// was actually failing to begin with — otherwise a task that was already green
/// scores a completion for an agent that changed nothing. That fact cannot be
/// taken from the task definition, because a definition is a claim and this is
/// the check on it.
/// </para>
/// <para>
/// It is not a second run path. Nothing here creates a <see cref="Run"/>, takes a
/// stage transition, proposes, or writes to a fixture: it copies the fixture the
/// way a working copy is built, executes one allow-listed command in the same
/// sandbox every run uses, and deletes the copy. The result is a property of the
/// fixture, so it is measured once per (fixture, command) and reused — which also
/// makes repeat evaluations cheaper without making them different (SC-007).
/// </para>
/// </summary>
public sealed class FixtureBaselineProbe(ISandboxRunner sandbox, WorkspaceOptions workspace)
{
    private readonly Dictionary<(Guid Fixture, string Command), bool> _failed = [];

    /// <summary>
    /// Whether <paramref name="commandName"/> fails against the unmodified
    /// fixture.
    /// </summary>
    public async Task<bool> FailsBeforeChangeAsync(
        RepositoryFixture fixture, string commandName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        var key = (fixture.Id, commandName);

        if (_failed.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var allowed = FixtureConfiguration.ReadCommands(fixture.TestConfigJson);

        var command = allowed.FirstOrDefault(
            c => string.Equals(c.Name, commandName, StringComparison.Ordinal))
            ?? throw new CommandNotAllowedException(commandName, allowed.Select(c => c.Name));

        var probePath = Path.Combine(
            Path.GetFullPath(workspace.Root),
            "baselines",
            $"{fixture.Slug}-{command.Name}");

        try
        {
            if (Directory.Exists(probePath))
            {
                Directory.Delete(probePath, recursive: true);
            }

            WorkingCopyManager.CopyFixtureTo(fixture.RootPath, probePath, ct);

            var result = await sandbox.RunAsync(
                new SandboxRequest(
                    Image: FixtureConfiguration.ReadSandboxSetting(
                        fixture.TestConfigJson, "image", "alpine:3"),
                    Argv: command.Argv,
                    WorkingCopyPath: probePath,
                    WorkDir: FixtureConfiguration.ReadSandboxSetting(
                        fixture.TestConfigJson, "workdir", "/workspace"),
                    Timeout: TimeSpan.FromSeconds(
                        FixtureConfiguration.ReadSandboxSetting(
                            fixture.TestConfigJson, "timeoutSeconds", 300)),
                    MemoryMegabytes: FixtureConfiguration.ReadSandboxSetting(
                        fixture.TestConfigJson, "memoryMegabytes", 2048),
                    CpuCount: FixtureConfiguration.ReadSandboxSetting(
                        fixture.TestConfigJson, "cpuCount", 2.0),
                    PidsLimit: FixtureConfiguration.ReadSandboxSetting(
                        fixture.TestConfigJson, "pidsLimit", 256)),
                ct);

            // A timeout counts as failing. It is not a passing baseline, and
            // treating it as one would let a task whose command never completes
            // score completions for nothing.
            _failed[key] = !result.Passed;

            return _failed[key];
        }
        finally
        {
            if (Directory.Exists(probePath))
            {
                Directory.Delete(probePath, recursive: true);
            }
        }
    }
}
