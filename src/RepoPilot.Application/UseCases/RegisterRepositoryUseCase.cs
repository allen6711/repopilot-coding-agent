using System.Text.Json;
using Microsoft.Extensions.Options;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Schemas;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Workspace;

namespace RepoPilot.Application.UseCases;

/// <summary>Why a registration was refused.</summary>
public enum RegistrationRefusalReason
{
    NotInAllowedSet,
    AlreadyRegistered,
    FixtureNotFound,
    ConfigMissing,
    ConfigInvalid,
    UnsafeCommand,
}

/// <summary>Raised when a repository cannot be registered.</summary>
public sealed class RegistrationRefusedException(RegistrationRefusalReason reason, string detail)
    : InvalidOperationException(detail)
{
    public RegistrationRefusalReason Reason { get; } = reason;
}

/// <summary>
/// Registers a repository fixture from the configured allowed set (FR-001).
/// <para>
/// Everything a fixture supplies is treated as input to be checked, not
/// configuration to be trusted. A fixture names the commands that will execute
/// inside the sandbox, so its config is the one place where a bad value turns
/// into execution — which is why the schema check and the argv check both happen
/// here, at registration, rather than at the point of running a command.
/// </para>
/// </summary>
public sealed class RegisterRepositoryUseCase(
    IRepositoryFixtureStore repositories,
    IOptions<AllowedRepositoryOptions> allowed,
    IOptions<WorkspaceOptions> workspace)
{
    /// <summary>
    /// Characters that mean something to a shell.
    /// <para>
    /// The sandbox executes an argument vector and never a command line, so
    /// these have no special meaning at execution time (FR-022a). They are
    /// refused anyway: an argv entry containing <c>&amp;&amp;</c> is far more
    /// likely to be someone assuming a shell than a genuine argument, and
    /// discovering that assumption at registration is better than discovering it
    /// when a test silently does not run.
    /// </para>
    /// </summary>
    private static readonly char[] ShellMetacharacters =
        ['&', '|', ';', '>', '<', '`', '$', '\n', '\r'];

    /// <summary>
    /// Registers a fixture by slug.
    /// </summary>
    /// <exception cref="RegistrationRefusedException">The fixture is not acceptable.</exception>
    public async Task<RepositoryFixture> RegisterAsync(string slug, CancellationToken ct = default)
    {
        var trimmed = (slug ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new RegistrationRefusedException(
                RegistrationRefusalReason.NotInAllowedSet, "A slug is required.");
        }

        // FR-001. The allowed set is deployment configuration, not something a
        // request can extend — which is what makes "the agent can only touch
        // repositories someone chose" a property of the system rather than of
        // how it happens to be called.
        if (!allowed.Value.Slugs.Contains(trimmed, StringComparer.Ordinal))
        {
            throw new RegistrationRefusedException(
                RegistrationRefusalReason.NotInAllowedSet,
                $"'{trimmed}' is not in the configured allowed set. Add it to " +
                $"{AllowedRepositoryOptions.SectionName}:Slugs to permit it.");
        }

        if (await repositories.FindBySlugAsync(trimmed, ct) is not null)
        {
            throw new RegistrationRefusedException(
                RegistrationRefusalReason.AlreadyRegistered,
                $"'{trimmed}' is already registered.");
        }

        var fixturesRoot = WorkspaceRoot.ReadOnly(Path.GetFullPath(workspace.Value.FixturesRoot));
        var resolved = PathGuard.Resolve(fixturesRoot, trimmed, AccessIntent.Read);

        if (!resolved.IsAllowed)
        {
            // A slug that escapes the fixtures root is refused by the same guard
            // that governs every other path, rather than by a check written just
            // for this call site.
            throw new RegistrationRefusedException(
                RegistrationRefusalReason.NotInAllowedSet,
                $"'{trimmed}' does not resolve inside the fixtures root ({resolved.Reason}).");
        }

        var root = resolved.FullPath!;

        if (!Directory.Exists(root))
        {
            throw new RegistrationRefusedException(
                RegistrationRefusalReason.FixtureNotFound,
                $"No fixture directory at '{root}'.");
        }

        var configPath = Path.Combine(root, "repopilot.fixture.json");

        if (!File.Exists(configPath))
        {
            throw new RegistrationRefusedException(
                RegistrationRefusalReason.ConfigMissing,
                $"'{trimmed}' has no repopilot.fixture.json. A fixture without one declares no " +
                "test commands, and the commands are the only thing that may execute.");
        }

        var configJson = await File.ReadAllTextAsync(configPath, ct);

        ValidateConfig(trimmed, configJson);

        var fixture = new RepositoryFixture
        {
            Slug = trimmed,
            DisplayName = ReadDisplayName(configJson) ?? trimmed,
            RootPath = root,
            TestConfigJson = configJson,
        };

        await repositories.AddAsync(fixture, ct);

        return fixture;
    }

    /// <summary>
    /// Checks the committed config against its schema and its commands against
    /// the argv rule (FR-022a, FR-022b).
    /// </summary>
    private static void ValidateConfig(string slug, string configJson)
    {
        var errors = SchemaValidator.Validate(configJson, SchemaKind.FixtureConfig);

        if (errors.Count > 0)
        {
            throw new RegistrationRefusedException(
                RegistrationRefusalReason.ConfigInvalid,
                $"'{slug}' has an invalid repopilot.fixture.json: " +
                string.Join("; ", errors.Select(e => e.ToString())));
        }

        using var config = JsonDocument.Parse(configJson);

        if (!config.RootElement.TryGetProperty("commands", out var commands))
        {
            return;
        }

        foreach (var command in commands.EnumerateArray())
        {
            var name = command.TryGetProperty("name", out var n) ? n.GetString() : "(unnamed)";

            if (!command.TryGetProperty("argv", out var argv))
            {
                continue;
            }

            foreach (var argument in argv.EnumerateArray())
            {
                var value = argument.GetString() ?? string.Empty;

                if (value.IndexOfAny(ShellMetacharacters) >= 0)
                {
                    throw new RegistrationRefusedException(
                        RegistrationRefusalReason.UnsafeCommand,
                        $"Command '{name}' in '{slug}' has an argument containing a shell " +
                        $"operator: '{value}'. Commands are executed as an argument vector with " +
                        "no shell, so this would be passed through as literal text rather than " +
                        "doing what it appears to do.");
                }
            }
        }
    }

    private static string? ReadDisplayName(string configJson)
    {
        using var config = JsonDocument.Parse(configJson);

        return config.RootElement.TryGetProperty("displayName", out var name)
            ? name.GetString()
            : null;
    }
}
