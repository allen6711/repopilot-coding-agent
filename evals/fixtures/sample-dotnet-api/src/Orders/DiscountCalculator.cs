namespace Orders;

/// <summary>
/// Applies promotional discounts to an order amount.
/// </summary>
public static class DiscountCalculator
{
    /// <summary>
    /// Applies <paramref name="percentOff"/> to <paramref name="amount"/> and
    /// rounds the result to whole cents.
    /// <para>
    /// Money is rounded half away from zero, which is what the billing team and
    /// the printed receipts both assume: a half-cent always rounds up in the
    /// customer's favour.
    /// </para>
    /// </summary>
    public static decimal Apply(decimal amount, int percentOff)
    {
        if (percentOff is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentOff), percentOff, "A discount is between 0 and 100 percent.");
        }

        var discounted = amount - (amount * percentOff / 100m);

        return Math.Round(discounted, 2);
    }
}
