using RepoPilot.Domain.Runs;
using Xunit;

namespace RepoPilot.UnitTests.Domain;

/// <summary>
/// Exhaustive coverage of the run lifecycle (FR-008, FR-009).
/// <para>
/// The expected transitions below are written out independently rather than
/// read from <see cref="RunStateMachine"/>. A test that consults the same table
/// the implementation uses would pass no matter what that table said; this one
/// fails if the lifecycle changes without the specification changing with it.
/// </para>
/// </summary>
public sealed class RunStateMachineTests
{
    /// <summary>
    /// The permitted stage-specific transitions, transcribed from the state
    /// diagram in data-model.md.
    /// </summary>
    private static readonly (RunStage From, RunTrigger Trigger, RunStage To)[] ExpectedSpecific =
    [
        (RunStage.Created, RunTrigger.Start, RunStage.Retrieving),
        (RunStage.Retrieving, RunTrigger.ContextRetrieved, RunStage.Planning),
        (RunStage.Retrieving, RunTrigger.InsufficientContext, RunStage.NoChange),
        (RunStage.Planning, RunTrigger.PlanProduced, RunStage.Proposing),
        (RunStage.Proposing, RunTrigger.ProposalCreated, RunStage.AwaitingApproval),
        (RunStage.Proposing, RunTrigger.NoModificationProposed, RunStage.NoChange),
        (RunStage.AwaitingApproval, RunTrigger.Approved, RunStage.Applying),
        (RunStage.AwaitingApproval, RunTrigger.Rejected, RunStage.Rejected),
        (RunStage.Applying, RunTrigger.PatchApplied, RunStage.Testing),
        (RunStage.Testing, RunTrigger.TestsPassed, RunStage.Succeeded),
        (RunStage.Testing, RunTrigger.TestsFailedRetryAvailable, RunStage.Proposing),
        (RunStage.Testing, RunTrigger.TestsFailedRetriesExhausted, RunStage.Failed),
    ];

    private static readonly RunStage[] TerminalStages =
    [
        RunStage.Succeeded,
        RunStage.Failed,
        RunStage.Rejected,
        RunStage.Cancelled,
        RunStage.NoChange,
    ];

    private static RunStage? ExpectedResult(RunStage from, RunTrigger trigger)
    {
        if (TerminalStages.Contains(from))
        {
            return null;
        }

        foreach (var (f, t, to) in ExpectedSpecific)
        {
            if (f == from && t == trigger)
            {
                return to;
            }
        }

        // Cancellation and failure are permitted from every non-terminal stage.
        return trigger switch
        {
            RunTrigger.Cancelled => RunStage.Cancelled,
            RunTrigger.Failed => RunStage.Failed,
            _ => null,
        };
    }

    [Fact]
    public void EveryStageTriggerPair_BehavesAsSpecified()
    {
        var stages = Enum.GetValues<RunStage>();
        var triggers = Enum.GetValues<RunTrigger>();
        var failures = new List<string>();
        var checkedPairs = 0;

        foreach (var from in stages)
        {
            foreach (var trigger in triggers)
            {
                checkedPairs++;
                var expected = ExpectedResult(from, trigger);
                var permitted = RunStateMachine.TryTransition(from, trigger, out var actual);

                if (expected is null && permitted)
                {
                    failures.Add($"{from} + {trigger} should be refused but produced {actual}.");
                }
                else if (expected is not null && !permitted)
                {
                    failures.Add($"{from} + {trigger} should reach {expected} but was refused.");
                }
                else if (expected is not null && actual != expected)
                {
                    failures.Add($"{from} + {trigger} should reach {expected} but reached {actual}.");
                }
            }
        }

        Assert.Equal(stages.Length * triggers.Length, checkedPairs);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void IllegalTransition_ThrowsRatherThanReturningQuietly()
    {
        // FR-009 requires a rejected transition to fail loudly. A caller that
        // ignores a bool would otherwise sail past it.
        var ex = Assert.Throws<IllegalTransitionException>(
            () => RunStateMachine.Transition(RunStage.Created, RunTrigger.Approved));

        Assert.Equal(RunStage.Created, ex.From);
        Assert.Equal(RunTrigger.Approved, ex.Trigger);
    }

    [Theory]
    [InlineData(RunStage.Succeeded)]
    [InlineData(RunStage.Failed)]
    [InlineData(RunStage.Rejected)]
    [InlineData(RunStage.Cancelled)]
    [InlineData(RunStage.NoChange)]
    public void TerminalStages_AcceptNothing_IncludingCancellation(RunStage terminal)
    {
        Assert.True(RunStateMachine.IsTerminal(terminal));

        foreach (var trigger in Enum.GetValues<RunTrigger>())
        {
            Assert.False(
                RunStateMachine.TryTransition(terminal, trigger, out _),
                $"{terminal} accepted {trigger}.");
        }
    }

    [Fact]
    public void CancellationIsReachable_FromEveryNonTerminalStage()
    {
        // FR-008a: a human can abandon a run from any non-terminal stage,
        // including while it sits awaiting approval.
        foreach (var stage in Enum.GetValues<RunStage>())
        {
            if (RunStateMachine.IsTerminal(stage))
            {
                continue;
            }

            Assert.True(RunStateMachine.TryTransition(stage, RunTrigger.Cancelled, out var to));
            Assert.Equal(RunStage.Cancelled, to);
        }
    }

    [Fact]
    public void ApplyingIsReachableOnlyThroughApproval()
    {
        // Principle I as a reachability property: no path into Applying exists
        // that does not pass through an approval decision.
        var intoApplying = RunStateMachine.AllTransitions()
            .Where(t => t.To == RunStage.Applying)
            .ToList();

        Assert.Single(intoApplying);
        Assert.Equal(RunStage.AwaitingApproval, intoApplying[0].From);
        Assert.Equal(RunTrigger.Approved, intoApplying[0].Trigger);
    }

    [Fact]
    public void ProposingIsNotReachableDirectlyFromRetrieving()
    {
        // FR-010: a plan must be produced before a change is proposed, so the
        // run has to pass through Planning.
        Assert.False(
            RunStateMachine.TryTransition(RunStage.Retrieving, RunTrigger.PlanProduced, out _));
        Assert.False(
            RunStateMachine.TryTransition(RunStage.Retrieving, RunTrigger.ProposalCreated, out _));
    }

    [Fact]
    public void RevisionReturnsToProposing_NotDirectlyToApplying()
    {
        // FR-013: each revision attempt requires its own approval, so a failed
        // test run cannot re-enter Applying without passing AwaitingApproval.
        Assert.True(RunStateMachine.TryTransition(
            RunStage.Testing, RunTrigger.TestsFailedRetryAvailable, out var afterFailure));
        Assert.Equal(RunStage.Proposing, afterFailure);

        Assert.False(RunStateMachine.TryTransition(
            RunStage.Testing, RunTrigger.Approved, out _));
    }

    [Fact]
    public void EveryTerminalStage_MapsToAnOutcome()
    {
        foreach (var stage in Enum.GetValues<RunStage>())
        {
            if (RunStateMachine.IsTerminal(stage))
            {
                Assert.Equal(stage.ToString(), RunStateMachine.ToOutcome(stage).ToString());
            }
            else
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => RunStateMachine.ToOutcome(stage));
            }
        }
    }

    [Fact]
    public void EveryNonTerminalStage_HasAtLeastOneWayForward()
    {
        // A stage with no outgoing transition other than cancel/fail would be a
        // dead end the orchestrator could park a run in.
        foreach (var stage in Enum.GetValues<RunStage>())
        {
            if (RunStateMachine.IsTerminal(stage))
            {
                continue;
            }

            var forward = RunStateMachine.AllTransitions()
                .Where(t => t.From == stage
                    && t.Trigger != RunTrigger.Cancelled
                    && t.Trigger != RunTrigger.Failed)
                .ToList();

            Assert.True(forward.Count > 0, $"{stage} has no forward transition.");
        }
    }
}
