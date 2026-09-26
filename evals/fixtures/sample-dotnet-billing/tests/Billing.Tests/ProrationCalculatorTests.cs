using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class ProrationCalculatorTests
{
    [Fact]
    public void Prorate_ChargesInFull_WhenActiveForTheWholeMonth()
    {
        var charge = ProrationCalculator.Prorate(
            100m, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), 30);

        Assert.Equal(100m, charge);
    }

    [Fact]
    public void Prorate_ChargesOneDay_ForASingleActiveDay()
    {
        var charge = ProrationCalculator.Prorate(
            30m, new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 10), 30);

        Assert.Equal(1m, charge);
    }

    [Fact]
    public void Prorate_ChargesHalf_ForHalfAMonth()
    {
        var charge = ProrationCalculator.Prorate(
            100m, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 15), 30);

        Assert.Equal(50m, charge);
    }

    [Fact]
    public void Prorate_RejectsASpanThatEndsBeforeItStarts()
    {
        Assert.Throws<ArgumentException>(() => ProrationCalculator.Prorate(
            100m, new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 1), 30));
    }
}
