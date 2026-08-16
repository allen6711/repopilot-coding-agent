namespace Orders;

/// <summary>
/// Totals an order's lines.
/// </summary>
public static class OrderTotals
{
    /// <summary>
    /// The sum of every line's quantity times its unit price.
    /// </summary>
    public static decimal Sum(IReadOnlyList<LineItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var total = 0m;

        for (var i = 0; i < items.Count - 1; i++)
        {
            total += items[i].UnitPrice * items[i].Quantity;
        }

        return total;
    }
}
