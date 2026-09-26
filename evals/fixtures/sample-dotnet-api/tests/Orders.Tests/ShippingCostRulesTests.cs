using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class ShippingCostRulesTests
{
    [Theory]
    [InlineData(10, ShippingSpeed.Standard, true, 0)]
    [InlineData(10, ShippingSpeed.Standard, false, 4.99)]
    [InlineData(50, ShippingSpeed.Standard, false, 0)]
    [InlineData(60, ShippingSpeed.Standard, false, 0)]
    public void Charge_ForStandardShipping(
        decimal orderTotal, ShippingSpeed speed, bool isMember, decimal expected)
    {
        Assert.Equal(expected, ShippingCostRules.Charge(orderTotal, speed, isMember));
    }

    [Theory]
    [InlineData(10, ShippingSpeed.Express, true, 4.99)]
    [InlineData(50, ShippingSpeed.Express, true, 0)]
    [InlineData(10, ShippingSpeed.Express, false, 9.99)]
    [InlineData(50, ShippingSpeed.Express, false, 4.99)]
    public void Charge_ForExpressShipping(
        decimal orderTotal, ShippingSpeed speed, bool isMember, decimal expected)
    {
        Assert.Equal(expected, ShippingCostRules.Charge(orderTotal, speed, isMember));
    }

    [Fact]
    public void Charge_TreatsFiftyAsInsideTheFreeShippingThreshold()
    {
        Assert.Equal(0m, ShippingCostRules.Charge(50m, ShippingSpeed.Standard, false));
        Assert.Equal(4.99m, ShippingCostRules.Charge(49.99m, ShippingSpeed.Standard, false));
    }
}
