using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class AddressValidatorTests
{
    [Theory]
    [InlineData("CB1")]
    [InlineData("SW1A")]
    [InlineData("M60")]
    public void Validate_AcceptsAWellFormedOutwardCode(string postalCode)
    {
        AddressValidator.Validate(new Address("Cambridge", postalCode));
    }

    [Fact]
    public void Validate_RejectsAMissingCity()
    {
        Assert.Throws<ArgumentException>(
            () => AddressValidator.Validate(new Address("  ", "CB1")));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("CAMBRIDGE")]
    [InlineData("CB1 2AB")]
    [InlineData("C")]
    public void Validate_RejectsAMalformedOutwardCode(string postalCode)
    {
        Assert.Throws<ArgumentException>(
            () => AddressValidator.Validate(new Address("Cambridge", postalCode)));
    }
}
