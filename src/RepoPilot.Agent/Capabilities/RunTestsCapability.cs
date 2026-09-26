using System.Text.Json;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;

namespace RepoPilot.Agent.Capabilities;

/// <summary>
/// A command the fixture permits.
/// </summary>
/// <param name="Name">Referenced by <c>run_tests</c>.</param>
/// <param name="Argv">Argument vector, executed directly.</param>
public sealed record AllowedCommand(string Name, IReadOnlyList<string> Argv);

/// <summary>Raised when a command outside the fixture's allow-list is requested.</summary>
public sealed class CommandNotAllowedException(string requested, IEnumerable<string> permitted)
    : InvalidOperationException(
        $"'{requested}' is not in the fixture's command allow-list. Permitted: " +
        $"{string.Join(", ", permitted)}. Test commands come from committed repository " +
        "configuration, never from a model-produced string (FR-022).")
{
    public string Requested { get; } = requested;
}

/// <summary>
/// Reads a fixture's committed configuration.
/// </summary>
public static class FixtureConfiguration
{
    /// <summary>Parses the allow-list.</summary>
    public static IReadOnlyList<AllowedCommand> ReadCommands(string testConfigJson)
    {
        using var config = JsonDocument.Parse(testConfigJson);

        if (!config.RootElement.TryGetProperty("commands", out var commands) ||
            commands.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<AllowedCommand>();

        foreach (var command in commands.EnumerateArray())
        {
            var name = command.GetProperty("name").GetString();
            if (name is null)
            {
                continue;
            }

            var argv = command.TryGetProperty("argv", out var argvElement) &&
                       argvElement.ValueKind == JsonValueKind.Array
                ? argvElement.EnumerateArray().Select(a => a.GetString()).OfType<string>().ToList()
                : [];

            if (argv.Count > 0)
            {
                result.Add(new AllowedCommand(name, argv));
            }
        }

        return result;
    }

    /// <summary>Reads a sandbox setting, falling back to the documented default.</summary>
    public static T ReadSandboxSetting<T>(string testConfigJson, string property, T fallback)
    {
        using var config = JsonDocument.Parse(testConfigJson);

        if (config.RootElement.TryGetProperty("sandbox", out var sandbox) &&
            sandbox.TryGetProperty(property, out var value))
        {
            try
            {
                return value.Deserialize<T>() ?? fallback;
            }
            catch (JsonException)
            {
                return fallback;
            }
        }

        return fallback;
    }
}

/// <summary>
/// Executes an allow-listed test command (permission class: sandbox execution).
/// <para>
/// The schema accepts a command <em>name</em> and nothing else. There is no
/// parameter that takes a command line, so the shape of the interface is itself
/// part of the control — a model cannot ask for something outside the list
/// because there is no way to express it (FR-022).
/// </para>
/// </summary>
public sealed class RunTestsCapability(
    ISandboxRunner sandbox,
    IRepositoryFixtureStore repositories,
    ITestResultStore results,
    IRunStore runs,
    Func<Guid, string> workingCopyPathFor) : ICapability
{
    public string Name => "run_tests";

    public async Task<CapabilityResult> InvokeAsync(
        CapabilityContext context, string argumentsJson, CancellationToken ct = default)
    {
        var args = Arguments.Parse(argumentsJson);
        var commandName = Arguments.RequireString(args, "command_name");

        var fixture = await repositories.FindByIdAsync(context.RepositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {context.RepositoryId} not found.");

        var allowed = FixtureConfiguration.ReadCommands(fixture.TestConfigJson);

        // Resolved before anything is created. A refusal here means no container
        // was ever requested, let alone started.
        var command = allowed.FirstOrDefault(
            c => string.Equals(c.Name, commandName, StringComparison.Ordinal))
            ?? throw new CommandNotAllowedException(commandName, allowed.Select(c => c.Name));

        var run = await runs.FindAsync(context.RunId, ct)
            ?? throw new InvalidOperationException($"Run {context.RunId} not found.");

        var request = new SandboxRequest(
            Image: FixtureConfiguration.ReadSandboxSetting(fixture.TestConfigJson, "image", "alpine:3"),
            Argv: command.Argv,
            WorkingCopyPath: workingCopyPathFor(context.RunId),
            WorkDir: FixtureConfiguration.ReadSandboxSetting(fixture.TestConfigJson, "workdir", "/workspace"),
            Timeout: TimeSpan.FromSeconds(
                FixtureConfiguration.ReadSandboxSetting(fixture.TestConfigJson, "timeoutSeconds", 300)),
            MemoryMegabytes:
                FixtureConfiguration.ReadSandboxSetting(fixture.TestConfigJson, "memoryMegabytes", 2048),
            CpuCount:
                FixtureConfiguration.ReadSandboxSetting(fixture.TestConfigJson, "cpuCount", 2.0),
            PidsLimit:
                FixtureConfiguration.ReadSandboxSetting(fixture.TestConfigJson, "pidsLimit", 256));

        var result = await sandbox.RunAsync(request, ct);

        await results.AddAsync(
            new TestResult
            {
                RunId = context.RunId,
                RevisionAttempt = run.RevisionAttempt,
                CommandName = command.Name,
                Passed = result.Passed,
                ExitCode = result.ExitCode,
                Output = result.Output,
                DurationMs = (int)result.Duration.TotalMilliseconds,
                TimedOut = result.TimedOut,
            },
            ct);

        var payload = JsonSerializer.Serialize(new
        {
            passed = result.Passed,
            exit_code = result.ExitCode,
            output = result.Output,
            duration_ms = (int)result.Duration.TotalMilliseconds,
            timed_out = result.TimedOut,
        });

        // Charged against the budget: this output does reach model context on a
        // revision attempt, and a large failing test log would otherwise consume
        // it without accounting.
        return new CapabilityResult(
            payload,
            payload.Length,
            $$"""{"command":"{{command.Name}}","passed":{{result.Passed.ToString().ToLowerInvariant()}},"timed_out":{{result.TimedOut.ToString().ToLowerInvariant()}}}""");
    }
}
