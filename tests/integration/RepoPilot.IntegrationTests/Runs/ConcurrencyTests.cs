using Microsoft.Extensions.Options;
using RepoPilot.Application.Configuration;
using RepoPilot.Application.Runs;
using Xunit;

namespace RepoPilot.IntegrationTests.Runs;

/// <summary>
/// FR-013a and FR-013b: what happens above the concurrency limit, and what a run
/// awaiting approval costs while it waits.
/// </summary>
public sealed class ConcurrencyTests
{
    private static RunQueue Queue(int maxConcurrent) =>
        new(Options.Create(new RunConcurrencyOptions { MaxConcurrent = maxConcurrent }));

    [Fact]
    public async Task SixRunsAtALimitOfFourAllCompleteRatherThanFailing()
    {
        using var queue = Queue(4);

        var executing = 0;
        var peak = 0;
        var release = new TaskCompletionSource();
        var allStarted = new CountdownEvent(4);

        var runs = Enumerable.Range(0, 6).Select(_ => queue.WithSlotAsync(async ct =>
        {
            var current = Interlocked.Increment(ref executing);

            // Track the high-water mark rather than sampling: a limit that is
            // exceeded briefly is still exceeded.
            int observed;
            do
            {
                observed = Volatile.Read(ref peak);
            }
            while (current > observed &&
                   Interlocked.CompareExchange(ref peak, current, observed) != observed);

            if (!allStarted.IsSet)
            {
                allStarted.Signal();
            }

            await release.Task;
            Interlocked.Decrement(ref executing);
            return 1;
        })).ToList();

        // Four get in; the other two are queued, not rejected.
        Assert.True(allStarted.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(4, Volatile.Read(ref peak));

        release.SetResult();
        var results = await Task.WhenAll(runs);

        // The point of FR-013a: excess load queues. All six ran.
        Assert.Equal(6, results.Length);
        Assert.Equal(4, Volatile.Read(ref peak));
    }

    [Fact]
    public async Task ARunAwaitingApprovalHoldsNoSlot()
    {
        using var queue = Queue(1);

        // Segment one: the run executes up to awaiting approval and returns.
        // Returning is what releases the slot — the approval wait happens outside
        // any slot at all.
        await queue.WithSlotAsync(_ => Task.FromResult(0));

        Assert.Equal(1, queue.AvailableSlots);

        // With the limit at one, a second run can only proceed if the first
        // really did let go. If the slot were held across the approval gate,
        // this would block until the timeout.
        var second = queue.WithSlotAsync(async ct =>
        {
            await Task.Yield();
            return 42;
        });

        var completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(second, completed);
        Assert.Equal(42, await second);
    }

    [Fact]
    public async Task AnApprovedRunWaitsItsTurnToApply()
    {
        using var queue = Queue(1);

        var occupied = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var holder = queue.WithSlotAsync(async ct =>
        {
            occupied.SetResult();
            await release.Task;
            return 0;
        });

        await occupied.Task;

        // FR-013b's other half: re-acquiring is a real wait. An approved run does
        // not get to skip the queue just because a human already said yes —
        // otherwise a batch of approvals arriving at once would put every one of
        // them into execution simultaneously.
        var applying = queue.WithSlotAsync(_ => Task.FromResult(1));

        var raced = await Task.WhenAny(applying, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(applying, raced);

        release.SetResult();
        await holder;

        Assert.Equal(1, await applying);
    }

    [Fact]
    public async Task SlotsAreReturnedWhenASegmentThrows()
    {
        using var queue = Queue(1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.WithSlotAsync<int>(_ => throw new InvalidOperationException("segment failed")));

        // A failing run that kept its slot would shrink the pool by one every
        // time something went wrong, until nothing could run at all.
        Assert.Equal(1, queue.AvailableSlots);
    }
}
