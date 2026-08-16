using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class PaymentRetryPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 7)]
    public void DelayDays_FollowsTheSchedule(int attempt, int expected)
    {
        Assert.Equal(expected, PaymentRetryPolicy.DelayDays(attempt));
    }

    [Fact]
    public void DelayDays_HasNoFourthAttempt()
    {
        Assert.Null(PaymentRetryPolicy.DelayDays(PaymentRetryPolicy.MaxAttempts + 1));
    }

    [Fact]
    public void DelayDays_HasNoAttemptBelowOne()
    {
        Assert.Null(PaymentRetryPolicy.DelayDays(0));
    }
}
