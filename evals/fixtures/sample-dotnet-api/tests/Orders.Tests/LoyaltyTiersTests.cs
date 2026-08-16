using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class LoyaltyTiersTests
{
    [Theory]
    [InlineData(0, LoyaltyTier.None)]
    [InlineData(249.99, LoyaltyTier.None)]
    [InlineData(250, LoyaltyTier.Bronze)]
    [InlineData(1499.99, LoyaltyTier.Bronze)]
    [InlineData(1500, LoyaltyTier.Silver)]
    [InlineData(4999.99, LoyaltyTier.Silver)]
    [InlineData(5000, LoyaltyTier.Gold)]
    public void For_PlacesSpendInTheRightTier(decimal annualSpend, LoyaltyTier expected)
    {
        Assert.Equal(expected, LoyaltyTiers.For(annualSpend));
    }

    [Theory]
    [InlineData(LoyaltyTier.None, 0)]
    [InlineData(LoyaltyTier.Bronze, 1)]
    [InlineData(LoyaltyTier.Silver, 2)]
    [InlineData(LoyaltyTier.Gold, 3)]
    public void PointsPerPound_MatchesTheTier(LoyaltyTier tier, int expected)
    {
        Assert.Equal(expected, LoyaltyTiers.PointsPerPound(tier));
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(250, 100, 100)]
    [InlineData(1500, 100, 200)]
    [InlineData(5000, 100, 300)]
    public void PointsFor_AgreesWithTierTimesSpend(
        decimal annualSpend, decimal orderTotal, int expected)
    {
        Assert.Equal(expected, LoyaltyTiers.PointsFor(annualSpend, orderTotal));

        Assert.Equal(
            expected,
            (int)(orderTotal * LoyaltyTiers.PointsPerPound(LoyaltyTiers.For(annualSpend))));
    }
}
