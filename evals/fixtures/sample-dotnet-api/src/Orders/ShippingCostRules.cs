namespace Orders;

/// <summary>How an order is being shipped.</summary>
public enum ShippingSpeed
{
    Standard,
    Express,
}

/// <summary>
/// Works out what an order pays for shipping.
/// </summary>
public static class ShippingCostRules
{
    /// <summary>
    /// The shipping charge for an order.
    /// </summary>
    /// <param name="orderTotal">Order total before shipping.</param>
    /// <param name="speed">Requested shipping speed.</param>
    /// <param name="isMember">Whether the customer holds a membership.</param>
    public static decimal Charge(decimal orderTotal, ShippingSpeed speed, bool isMember)
    {
        if (speed == ShippingSpeed.Standard)
        {
            if (isMember)
            {
                return 0m;
            }
            else
            {
                if (orderTotal >= 50m)
                {
                    return 0m;
                }
                else
                {
                    return 4.99m;
                }
            }
        }
        else
        {
            if (isMember)
            {
                if (orderTotal >= 50m)
                {
                    return 0m;
                }
                else
                {
                    return 4.99m;
                }
            }
            else
            {
                if (orderTotal >= 50m)
                {
                    return 4.99m;
                }
                else
                {
                    return 9.99m;
                }
            }
        }
    }
}
