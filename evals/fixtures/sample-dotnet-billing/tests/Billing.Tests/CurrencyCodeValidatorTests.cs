using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class CurrencyCodeValidatorTests
{
    [Theory]
    [InlineData("GBP")]
    [InlineData("USD")]
    [InlineData("JPY")]
    public void Validate_AcceptsAnAlphabeticCode(string code)
    {
        CurrencyCodeValidator.Validate(code);
    }

    [Fact]
    public void Validate_RejectsAnEmptyCode()
    {
        Assert.Throws<ArgumentException>(() => CurrencyCodeValidator.Validate(""));
    }

    [Theory]
    [InlineData("gbp")]
    [InlineData("GB")]
    [InlineData("GBPX")]
    [InlineData("G8P")]
    [InlineData("GB ")]
    public void Validate_RejectsACodeThatIsNotThreeUpperCaseLetters(string code)
    {
        Assert.Throws<ArgumentException>(() => CurrencyCodeValidator.Validate(code));
    }
}
