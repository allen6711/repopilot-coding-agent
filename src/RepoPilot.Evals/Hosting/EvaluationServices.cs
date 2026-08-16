using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RepoPilot.Evals.Reporting;
using RepoPilot.Evals.Tasks;

namespace RepoPilot.Evals.Hosting;

/// <summary>
/// Where the harness reads the committed task set from and writes its report to.
/// <para>
/// Configurable so a test can point at a fixture directory of its own, and
/// defaulted to the committed locations so the ordinary invocation needs no
/// configuration at all — an evaluation that only measures the right thing when
/// someone remembers a flag is one that will eventually measure the wrong thing.
/// </para>
/// </summary>
public sealed class EvaluationOptions
{
    public const string SectionName = "RepoPilot:Evaluation";

    /// <summary>Directory holding the committed task definitions.</summary>
    public string TasksDirectory { get; init; } = "./evals/tasks";

    /// <summary>Directory the committed JSON report is written to.</summary>
    public string ResultsDirectory { get; init; } = "./evals/results";
}

/// <summary>
/// Registers the evaluation harness on top of <c>AddRepoPilotCore</c>.
/// </summary>
public static class EvaluationServices
{
    /// <summary>
    /// Adds the task loader, the baseline probe, the report writer, and the
    /// harness itself.
    /// </summary>
    /// <remarks>
    /// Requires the core services. The harness drives <c>RunOrchestrator</c>
    /// directly rather than owning any run machinery of its own, so registering
    /// it without them fails at resolution rather than running a second,
    /// weaker implementation.
    /// </remarks>
    public static IServiceCollection AddEvaluationHarness(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(EvaluationOptions.SectionName)
            .Get<EvaluationOptions>() ?? new EvaluationOptions();

        services.AddSingleton(options);
        services.AddSingleton(new EvaluationTaskLoader(options.TasksDirectory));
        services.AddSingleton(new ReportWriter(options.ResultsDirectory));
        services.AddScoped<FixtureBaselineProbe>();
        services.AddScoped<EvaluationHarness>();

        return services;
    }
}
