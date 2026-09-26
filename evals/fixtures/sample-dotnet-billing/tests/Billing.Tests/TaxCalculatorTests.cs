using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class TaxCalculatorTests
{
    [Fact]
    public void WithTax_AddsTaxAtTheGivenRate()
    {
        Assert.Equal(120m, TaxCalculator.WithTax(100m, 20m, isExempt: false));
    }

    [Fact]
    public void WithTax_RoundsTaxToWholeCents()
    {
        Assert.Equal(10.61m, TaxCalculator.WithTax(9.99m, 6.25m, isExempt: false));
    }

    [Fact]
    public void WithTax_ChargesNoTax_ForAnExemptCustomer()
    {
        Assert.Equal(100m, TaxCalculator.WithTax(100m, 20m, isExempt: true));
    }

    [Fact]
    public void WithTax_RejectsANegativeRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TaxCalculator.WithTax(100m, -1m, isExempt: false));
    }
}
