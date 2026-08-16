namespace Orders;

/// <summary>Where an order is in its lifecycle.</summary>
public enum OrderStatus
{
    Placed,
    Paid,
    Shipped,
    Cancelled,
}

/// <summary>
/// The permitted order status transitions.
/// <para>
/// An order is placed, then paid, then shipped. It may be cancelled at any point
/// before it ships. Once cancelled or shipped it is finished — neither leads
/// anywhere else.
/// </para>
/// </summary>
public static class OrderStatusMachine
{
    /// <summary>Whether <paramref name="from"/> may move to <paramref name="to"/>.</summary>
    public static bool CanTransition(OrderStatus from, OrderStatus to) => to switch
    {
        OrderStatus.Paid => from is OrderStatus.Placed,
        OrderStatus.Shipped => from is OrderStatus.Paid or OrderStatus.Cancelled,
        OrderStatus.Cancelled => from is not OrderStatus.Shipped,
        _ => false,
    };
}
