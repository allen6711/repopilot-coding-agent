using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class CardExpiryValidatorTests
{
    private static readonly DateOnly Today = new(2026, 8, 17);

    [Fact]
    public void Validate_AcceptsACardExpiringThisMonth()
    {
        CardExpiryValidator.Validate(8, 2026, Today);
    }

    [Fact]
    public void Validate_RejectsACardThatHasExpired()
    {
        Assert.Throws<ArgumentException>(() => CardExpiryValidator.Validate(7, 2026, Today));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void Validate_RejectsAMonthOutsideTheYear(int month)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CardExpiryValidator.Validate(month, 2030, Today));
    }

    [Fact]
    public void Validate_RejectsAnImplausiblyDistantYear()
    {
        var tooFar = Today.Year + CardExpiryValidator.MaxYearsAhead + 1;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CardExpiryValidator.Validate(6, tooFar, Today));
    }
}
