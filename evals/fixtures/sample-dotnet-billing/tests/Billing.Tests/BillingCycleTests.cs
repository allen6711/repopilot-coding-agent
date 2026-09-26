using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class BillingCycleTests
{
    [Fact]
    public void Next_KeepsTheAnchorDay_WhenTheNextMonthHasIt()
    {
        Assert.Equal(
            new DateOnly(2026, 2, 15),
            BillingCycle.Next(new DateOnly(2026, 1, 15), 15));
    }

    [Fact]
    public void Next_FallsBackToTheLastDay_WhenTheNextMonthIsShorter()
    {
        Assert.Equal(
            new DateOnly(2026, 2, 27),
            BillingCycle.Next(new DateOnly(2026, 1, 31), 31));
    }

    [Fact]
    public void Next_ReturnsToTheAnchor_TheMonthAfterAShortMonth()
    {
        Assert.Equal(
            new DateOnly(2026, 3, 31),
            BillingCycle.Next(new DateOnly(2026, 2, 28), 31));
    }

    [Fact]
    public void Next_RejectsAnAnchorThatIsNotADayOfTheMonth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BillingCycle.Next(new DateOnly(2026, 1, 15), 32));
    }
}
