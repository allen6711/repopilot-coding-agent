using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class AmountValidatorTests
{
    [Fact]
    public void Validate_AcceptsAWholeCentAmount()
    {
        AmountValidator.Validate(19.99m);
    }

    [Fact]
    public void Validate_AcceptsZero()
    {
        AmountValidator.Validate(0m);
    }

    [Fact]
    public void Validate_RejectsANegativeAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AmountValidator.Validate(-0.01m));
    }

    [Fact]
    public void Validate_RejectsMoreThanTwoDecimalPlaces()
    {
        Assert.Throws<ArgumentException>(() => AmountValidator.Validate(10.005m));
    }

    [Fact]
    public void Validate_AcceptsTheReviewThreshold()
    {
        AmountValidator.Validate(AmountValidator.MaxAmount);
    }

    [Fact]
    public void Validate_RejectsAnAmountAboveTheReviewThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AmountValidator.Validate(AmountValidator.MaxAmount + 0.01m));
    }
}
