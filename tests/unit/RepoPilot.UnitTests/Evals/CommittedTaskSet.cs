using RepoPilot.Evals.Tasks;

namespace RepoPilot.UnitTests.Evals;

/// <summary>
/// Locates the committed evaluation artefacts from a test binary.
/// <para>
/// By walking up for the repository marker rather than counting <c>..</c>
/// segments: the number of segments depends on the target framework and
/// configuration in the output path, so a counted path is one build-property
/// change away from silently resolving to nothing and every assertion below
/// passing over an empty set.
/// </para>
/// </summary>
internal static class CommittedTaskSet
{
    /// <summary>The repository root.</summary>
    internal static string RepositoryRoot { get; } = FindRoot();

    /// <summary>The committed task directory.</summary>
    internal static string TasksDirectory { get; } =
        Path.Combine(RepositoryRoot, "evals", "tasks");

    /// <summary>Where reference solutions live — outside every fixture root (FR-035).</summary>
    internal static string ReferenceDirectory { get; } =
        Path.Combine(TasksDirectory, "_reference");

    /// <summary>The committed fixture roots.</summary>
    internal static string FixturesDirectory { get; } =
        Path.Combine(RepositoryRoot, "evals", "fixtures");

    /// <summary>Loads the committed set, schema-validating every definition.</summary>
    internal static Task<IReadOnlyList<EvaluationTaskDefinition>> LoadAsync() =>
        new EvaluationTaskLoader(TasksDirectory).LoadAsync();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RepoPilot.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No repository root above {AppContext.BaseDirectory}.");
    }
}
