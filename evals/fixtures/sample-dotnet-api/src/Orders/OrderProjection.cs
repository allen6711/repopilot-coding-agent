namespace Orders;

/// <summary>
/// Projects orders for the two surfaces that render them: the single-order
/// screen and the orders list.
/// </summary>
public static class OrderProjection
{
    /// <summary>Projects one order for the detail screen.</summary>
    public static OrderView ForDetail(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var city = order.ShippingAddress is null ? null : order.ShippingAddress.City;
        var postalCode = order.ShippingAddress is null ? null : order.ShippingAddress.PostalCode;

        return new OrderView(
            order.Id,
            order.CustomerName,
            city,
            postalCode,
            order.TotalAmount);
    }

    /// <summary>Projects a page of orders for the list screen.</summary>
    public static IReadOnlyList<OrderView> ForList(IReadOnlyList<Order> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);

        var views = new List<OrderView>(orders.Count);

        foreach (var order in orders)
        {
            var city = order.ShippingAddress is null ? null : order.ShippingAddress.City;
            var postalCode = order.ShippingAddress is null ? null : order.ShippingAddress.PostalCode;

            views.Add(new OrderView(
                order.Id,
                order.CustomerName,
                city,
                postalCode,
                order.TotalAmount));
        }

        return views;
    }
}
