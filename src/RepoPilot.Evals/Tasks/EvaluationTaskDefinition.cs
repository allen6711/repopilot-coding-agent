using System.Text.RegularExpressions;

namespace RepoPilot.Evals.Tasks;

/// <summary>
/// The task categories the committed set is required to cover, in the mix the
/// README evaluation plan names: 8 bug fixes, 6 input-validation changes, 6 API
/// behaviour changes, 5 refactors, 5 test tasks.
/// </summary>
public enum EvaluationTaskCategory
{
    BugFix,
    InputValidation,
    ApiBehavior,
    Refactor,
    TestWork,
}

/// <summary>
/// How a task's completion is decided.
/// <para>
/// Every member here is decidable from recorded execution results alone. That is
/// the point of the enum being closed: SC-011 requires every success condition
/// to be checkable without human judgement, and the way to hold that is to make
/// "a human looks at it" inexpressible rather than discouraged.
/// </para>
/// </summary>
public enum SuccessConditionKind
{
    /// <summary>The success command passed.</summary>
    TestsPass,

    /// <summary>
    /// The success command passed <em>and</em> the baseline command failed before
    /// the change. The stronger form: it rules out a task that was already
    /// passing, where an agent that changed nothing would score a completion.
    /// </summary>
    TestsPassAndBaselineFailed,

    /// <summary>The success command's output contains a substring.</summary>
    OutputContains,

    /// <summary>The success command's output matches a regular expression.</summary>
    OutputMatches,
}

/// <summary>
/// One task's machine-checkable completion criterion.
/// </summary>
/// <param name="Kind">Which check applies.</param>
/// <param name="Substring">Required for <see cref="SuccessConditionKind.OutputContains"/>.</param>
/// <param name="Pattern">Required for <see cref="SuccessConditionKind.OutputMatches"/>.</param>
/// <param name="Note">Dataset-review note. Never consulted by the check.</param>
public sealed record SuccessCondition(
    SuccessConditionKind Kind,
    string? Substring = null,
    string? Pattern = null,
    string? Note = null)
{
    /// <summary>
    /// How long a pattern may run before it is treated as not matching. A
    /// committed pattern is reviewed, but a catastrophically backtracking one
    /// would stall the whole evaluation rather than fail one task.
    /// </summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Decides whether the task was completed.
    /// </summary>
    /// <param name="successCommandPassed">Whether the success command passed after the change.</param>
    /// <param name="baselineFailedBeforeChange">
    /// Whether the baseline command failed against the unmodified fixture. Measured
    /// once per (fixture, command) pair, because the fixture is read-only and the
    /// answer cannot vary between tasks that share one.
    /// </param>
    /// <param name="successCommandOutput">Recorded output of the success command.</param>
    public bool IsMet(
        bool successCommandPassed,
        bool baselineFailedBeforeChange,
        string successCommandOutput) => Kind switch
    {
        SuccessConditionKind.TestsPass => successCommandPassed,

        SuccessConditionKind.TestsPassAndBaselineFailed =>
            successCommandPassed && baselineFailedBeforeChange,

        SuccessConditionKind.OutputContains =>
            Substring is not null &&
            successCommandOutput.Contains(Substring, StringComparison.Ordinal),

        SuccessConditionKind.OutputMatches =>
            Pattern is not null && MatchesPattern(successCommandOutput),

        _ => false,
    };

    private bool MatchesPattern(string output)
    {
        try
        {
            return Regex.IsMatch(output, Pattern!, RegexOptions.None, PatternTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            // Not met, and not an evaluation failure. The task is scored as
            // incomplete and the report still comes out; a pattern that cannot
            // decide in two seconds is a dataset defect to fix, not a reason to
            // lose the other twenty-nine results.
            return false;
        }
    }
}

/// <summary>
/// A committed, reproducible evaluation task (FR-031).
/// <para>
/// Loaded from <c>evals/tasks/</c> and schema-validated before it is used. The
/// definition is the whole of what the harness knows about a task: the agent is
/// given <see cref="Description"/> and nothing else, and
/// <see cref="RelevantFiles"/> is ground truth the agent never sees.
/// </para>
/// </summary>
/// <param name="Id">Stable identifier, also the run's seeded task id.</param>
/// <param name="RepositorySlug">Fixture the task runs against.</param>
/// <param name="Category">Which part of the required mix this task covers.</param>
/// <param name="Description">Given to the agent verbatim.</param>
/// <param name="RelevantFiles">Ground truth for Recall@5 (FR-032).</param>
/// <param name="BaselineTestCommand">Allow-listed command establishing the pre-change state.</param>
/// <param name="SuccessTestCommand">Allow-listed command run after the approved change.</param>
/// <param name="SuccessCondition">The machine-checkable completion criterion.</param>
/// <param name="ExpectedChangedFiles">Diagnostic only; never used to decide success.</param>
/// <param name="ReferencePatch">
/// Optional path under <c>evals/tasks/_reference/</c>, which sits outside every
/// fixture root so no run can resolve it (FR-035).
/// </param>
public sealed record EvaluationTaskDefinition(
    string Id,
    string RepositorySlug,
    EvaluationTaskCategory Category,
    string Description,
    IReadOnlyList<string> RelevantFiles,
    string BaselineTestCommand,
    string SuccessTestCommand,
    SuccessCondition SuccessCondition,
    IReadOnlyList<string> ExpectedChangedFiles,
    string? ReferencePatch);
