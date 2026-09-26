using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class FeeScheduleTests
{
    [Theory]
    [InlineData(100, PaymentMethod.Card, false, 1.60)]
    [InlineData(100, PaymentMethod.Card, true, 3.10)]
    [InlineData(100, PaymentMethod.DirectDebit, false, 1.20)]
    [InlineData(100, PaymentMethod.DirectDebit, true, 2.20)]
    [InlineData(100, PaymentMethod.BankTransfer, false, 0.20)]
    [InlineData(100, PaymentMethod.BankTransfer, true, 1.70)]
    public void Fee_MatchesTheSchedule(
        decimal amount, PaymentMethod method, bool isInternational, decimal expected)
    {
        Assert.Equal(expected, FeeSchedule.Fee(amount, method, isInternational));
    }

    [Fact]
    public void Fee_IsTheFixedComponentAlone_OnAZeroAmount()
    {
        Assert.Equal(0.20m, FeeSchedule.Fee(0m, PaymentMethod.Card, isInternational: false));
    }

    [Fact]
    public void Fee_RoundsToWholeCents()
    {
        // 9.99 at 1.4% is 0.13986, which rounds up to 0.14, plus the fixed 0.20.
        Assert.Equal(0.34m, FeeSchedule.Fee(9.99m, PaymentMethod.Card, isInternational: false));
    }

    [Fact]
    public void Fee_RejectsANegativeAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FeeSchedule.Fee(-1m, PaymentMethod.Card, isInternational: false));
    }
}
