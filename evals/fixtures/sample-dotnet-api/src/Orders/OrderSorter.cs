namespace Orders;

/// <summary>An order with the date it was placed.</summary>
/// <param name="Id">Order identifier.</param>
/// <param name="PlacedOn">When the order was placed.</param>
public sealed record PlacedOrder(string Id, DateOnly PlacedOn);

/// <summary>
/// Orders the rows of the orders list screen.
/// <para>
/// The screen shows the most recent order first — that is what an operator is
/// looking for when they open it. Two orders placed on the same day keep the
/// relative order they arrived in, so a re-sort never shuffles equal rows.
/// </para>
/// </summary>
public static class OrderSorter
{
    /// <summary>Returns <paramref name="orders"/> in display order.</summary>
    public static IReadOnlyList<PlacedOrder> ForDisplay(IReadOnlyList<PlacedOrder> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);

        return [.. orders.OrderBy(o => o.PlacedOn)];
    }
}
