namespace Orders;

/// <summary>Loyalty standing, earned on the last twelve months of spend.</summary>
public enum LoyaltyTier
{
    None,
    Bronze,
    Silver,
    Gold,
}

/// <summary>
/// Places a customer in a loyalty tier and works out what that tier earns.
/// </summary>
public static class LoyaltyTiers
{
    /// <summary>The tier a customer's trailing twelve-month spend earns.</summary>
    public static LoyaltyTier For(decimal annualSpend)
    {
        if (annualSpend >= 5000m)
        {
            return LoyaltyTier.Gold;
        }

        if (annualSpend >= 1500m)
        {
            return LoyaltyTier.Silver;
        }

        if (annualSpend >= 250m)
        {
            return LoyaltyTier.Bronze;
        }

        return LoyaltyTier.None;
    }

    /// <summary>Points earned per pound spent at a given tier, in whole points.</summary>
    public static int PointsPerPound(LoyaltyTier tier)
    {
        if (tier == LoyaltyTier.Gold)
        {
            return 3;
        }

        if (tier == LoyaltyTier.Silver)
        {
            return 2;
        }

        if (tier == LoyaltyTier.Bronze)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>The points an order earns for a customer at a given spend level.</summary>
    public static int PointsFor(decimal annualSpend, decimal orderTotal)
    {
        if (annualSpend >= 5000m)
        {
            return (int)(orderTotal * 3);
        }

        if (annualSpend >= 1500m)
        {
            return (int)(orderTotal * 2);
        }

        if (annualSpend >= 250m)
        {
            return (int)(orderTotal * 1);
        }

        return 0;
    }
}
