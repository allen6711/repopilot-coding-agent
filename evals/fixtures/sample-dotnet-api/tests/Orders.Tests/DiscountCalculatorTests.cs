using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class DiscountCalculatorTests
{
    [Fact]
    public void Apply_ReturnsFullAmount_WhenDiscountIsZero()
    {
        Assert.Equal(19.99m, DiscountCalculator.Apply(19.99m, 0));
    }

    [Fact]
    public void Apply_RejectsDiscountAbove100()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiscountCalculator.Apply(10m, 101));
    }

    [Fact]
    public void Apply_RoundsHalfAwayFromZero()
    {
        // 10.05 less 50% is 5.025, and a half-cent rounds up in the customer's
        // favour.
        Assert.Equal(5.03m, DiscountCalculator.Apply(10.05m, 50));
    }
}
