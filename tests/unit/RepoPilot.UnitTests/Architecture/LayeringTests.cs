using System.Reflection;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Runs;
using Xunit;

namespace RepoPilot.UnitTests.Architecture;

/// <summary>
/// The constitution requires model-provider access to sit behind an adapter, and
/// forbids provider-specific types, prompts, and SDK calls from leaking into the
/// Application or Domain layers.
/// <para>
/// That is the kind of rule that erodes one convenient using-directive at a
/// time, and code review is a poor detector of it. These tests read assembly
/// references directly, so a leak fails the build rather than waiting to be
/// noticed.
/// </para>
/// </summary>
public sealed class LayeringTests
{
    private static readonly Assembly DomainAssembly = typeof(RunStage).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(IChatProviderAdapter).Assembly;

    /// <summary>
    /// Assembly-name prefixes that indicate a provider SDK, a database driver, or
    /// any other infrastructure concern.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "Anthropic",        // the configured chat provider
        "OpenAI",           // any alternative provider
        "Azure",
        "Npgsql",           // the database driver
        "Pgvector",
        "Microsoft.EntityFrameworkCore",
        "Docker",           // the sandbox runtime
        "Testcontainers",
        "DiffPlex",         // diff rendering is a presentation concern
        "Microsoft.Extensions.AI",
        "OpenTelemetry",    // instrumentation is wired in Infrastructure
    ];

    private static IEnumerable<string> ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.Length > 0);

    private static List<string> Violations(Assembly assembly) =>
        ReferencedAssemblyNames(assembly)
            .Where(name => ForbiddenPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void DomainReferencesNoInfrastructure()
    {
        var violations = Violations(DomainAssembly);

        Assert.True(
            violations.Count == 0,
            $"RepoPilot.Domain must not reference infrastructure. Found: {string.Join(", ", violations)}");
    }

    [Fact]
    public void ApplicationReferencesNoProviderOrDatabaseSdk()
    {
        var violations = Violations(ApplicationAssembly);

        Assert.True(
            violations.Count == 0,
            $"RepoPilot.Application must not reference provider or database SDKs. " +
            $"Found: {string.Join(", ", violations)}");
    }

    [Fact]
    public void DomainReferencesNoOtherRepoPilotProject()
    {
        // Domain sits at the bottom. A reference to Application would invert the
        // dependency and let use-case concerns reach the governance primitives.
        var repoPilotReferences = ReferencedAssemblyNames(DomainAssembly)
            .Where(n => n.StartsWith("RepoPilot", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(repoPilotReferences);
    }

    [Fact]
    public void ApplicationReferencesOnlyDomain()
    {
        var repoPilotReferences = ReferencedAssemblyNames(ApplicationAssembly)
            .Where(n => n.StartsWith("RepoPilot", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["RepoPilot.Domain"], repoPilotReferences);
    }

    [Fact]
    public void TheChatProviderPortExposesNoProviderTypes()
    {
        // A port can satisfy the reference check and still leak by exposing a
        // provider type through a signature. This walks the public surface of the
        // port and its parameter and return types.
        var offenders = new List<string>();

        foreach (var method in typeof(IChatProviderAdapter).GetMethods())
        {
            Check(method.ReturnType, $"{method.Name} return", offenders);

            foreach (var parameter in method.GetParameters())
            {
                Check(parameter.ParameterType, $"{method.Name}({parameter.Name})", offenders);
            }
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void TheEmbeddingPortExposesNoProviderTypes()
    {
        var offenders = new List<string>();

        foreach (var method in typeof(IEmbeddingProviderAdapter).GetMethods())
        {
            Check(method.ReturnType, $"{method.Name} return", offenders);

            foreach (var parameter in method.GetParameters())
            {
                Check(parameter.ParameterType, $"{method.Name}({parameter.Name})", offenders);
            }
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    private static void Check(Type type, string location, List<string> offenders)
    {
        foreach (var candidate in Unwrap(type))
        {
            var assemblyName = candidate.Assembly.GetName().Name ?? string.Empty;

            if (ForbiddenPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.Ordinal)))
            {
                offenders.Add($"{location} exposes {candidate.FullName} from {assemblyName}");
            }
        }
    }

    /// <summary>
    /// Yields a type and its generic arguments, so a leak hidden inside
    /// <c>Task&lt;T&gt;</c> or <c>IReadOnlyList&lt;T&gt;</c> is still caught.
    /// </summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Unwrap(argument))
            {
                yield return nested;
            }
        }
    }
}
