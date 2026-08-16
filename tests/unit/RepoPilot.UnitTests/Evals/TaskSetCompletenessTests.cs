using RepoPilot.Evals.Tasks;

namespace RepoPilot.UnitTests.Evals;

/// <summary>
/// The committed task set is complete and machine-checkable (FR-031, SC-011).
/// <para>
/// These assertions are about the dataset, not the code. SC-011 is a claim about
/// what is committed — at least thirty tasks, every one decidable without human
/// judgement — and the only way that claim stays true as tasks are added is for a
/// test to fail when it stops being true.
/// </para>
/// </summary>
public sealed class TaskSetCompletenessTests
{
    /// <summary>The floor SC-011 sets.</summary>
    private const int MinimumTasks = 30;

    [Fact]
    public async Task TheCommittedSetHoldsAtLeastThirtyTasks()
    {
        var tasks = await CommittedTaskSet.LoadAsync();

        Assert.True(
            tasks.Count >= MinimumTasks,
            $"SC-011 requires at least {MinimumTasks} committed tasks; found {tasks.Count}.");
    }

    [Fact]
    public async Task EveryDefinitionSatisfiesTheCommittedSchema()
    {
        // The loader validates as it reads and throws on the first failure, so
        // this passing is the assertion. Stated as its own test because "the set
        // loads" is the precondition every other test here rests on.
        var tasks = await CommittedTaskSet.LoadAsync();

        Assert.NotEmpty(tasks);
    }

    [Fact]
    public async Task EveryTaskIdIsUnique()
    {
        var tasks = await CommittedTaskSet.LoadAsync();

        Assert.Equal(
            tasks.Count,
            tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// SC-011's second half. A condition is machine-checkable when it names a
    /// kind the grader implements <em>and</em> carries whatever that kind needs to
    /// decide — a pattern condition with no pattern would parse and then never
    /// be satisfiable.
    /// </summary>
    [Fact]
    public async Task EverySuccessConditionCanBeCheckedWithoutHumanJudgement()
    {
        var tasks = await CommittedTaskSet.LoadAsync();

        foreach (var task in tasks)
        {
            var condition = task.SuccessCondition;

            Assert.True(
                Enum.IsDefined(condition.Kind),
                $"Task '{task.Id}' names a success condition the grader does not implement.");

            switch (condition.Kind)
            {
                case SuccessConditionKind.OutputContains:
                    Assert.False(
                        string.IsNullOrEmpty(condition.Substring),
                        $"Task '{task.Id}' checks output for a substring but names none.");
                    break;

                case SuccessConditionKind.OutputMatches:
                    Assert.False(
                        string.IsNullOrEmpty(condition.Pattern),
                        $"Task '{task.Id}' matches output against a pattern but names none.");
                    break;
            }
        }
    }

    /// <summary>
    /// A condition that cannot distinguish a real change from no change at all is
    /// not a completion criterion. <c>tests_pass</c> is exempt: the refactor tasks
    /// use it deliberately, and their reference notes say so.
    /// </summary>
    [Fact]
    public async Task EveryNonRefactorTaskFailsBeforeItsChange()
    {
        var tasks = await CommittedTaskSet.LoadAsync();

        foreach (var task in tasks.Where(t => t.Category != EvaluationTaskCategory.Refactor))
        {
            Assert.True(
                task.SuccessCondition.Kind is not SuccessConditionKind.TestsPass,
                $"Task '{task.Id}' would be completed by an agent that changed nothing.");
        }
    }

    [Fact]
    public async Task EveryTaskNamesAtLeastOneRelevantFileThatExists()
    {
        var tasks = await CommittedTaskSet.LoadAsync();

        foreach (var task in tasks)
        {
            Assert.NotEmpty(task.RelevantFiles);

            foreach (var relative in task.RelevantFiles)
            {
                var path = Path.Combine(
                    CommittedTaskSet.FixturesDirectory,
                    task.RepositorySlug,
                    relative.Replace('/', Path.DirectorySeparatorChar));

                Assert.True(
                    File.Exists(path),
                    $"Task '{task.Id}' names relevant file '{relative}', which is not in the fixture. " +
                    "Recall@5 would then be measured against ground truth that cannot be retrieved.");
            }
        }
    }

    /// <summary>
    /// The mix the README evaluation plan commits to. Checked as a floor per
    /// category rather than an exact count, so adding tasks is not a test failure
    /// but dropping a category's coverage is.
    /// </summary>
    [Theory]
    [InlineData(EvaluationTaskCategory.BugFix, 8)]
    [InlineData(EvaluationTaskCategory.InputValidation, 6)]
    [InlineData(EvaluationTaskCategory.ApiBehavior, 6)]
    [InlineData(EvaluationTaskCategory.Refactor, 5)]
    [InlineData(EvaluationTaskCategory.TestWork, 5)]
    public async Task TheSetCoversEachCategory(EvaluationTaskCategory category, int minimum)
    {
        var tasks = await CommittedTaskSet.LoadAsync();

        var found = tasks.Count(t => t.Category == category);

        Assert.True(
            found >= minimum,
            $"The committed set holds {found} {category} tasks; the plan commits to {minimum}.");
    }
}
