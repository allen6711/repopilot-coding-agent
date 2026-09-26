namespace Orders;

/// <summary>
/// Looks up orders and projects them for the API surface.
/// </summary>
public sealed class OrderLookupService(IOrderRepository repository)
{
    private readonly IOrderRepository _repository = repository;

    /// <summary>
    /// Returns a view of the order, or <c>null</c> when no such order exists.
    /// </summary>
    public OrderView? Lookup(string orderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var order = _repository.Find(orderId);
        if (order is null)
        {
            return null;
        }

        return new OrderView(
            order.Id,
            order.CustomerName,
            order.ShippingAddress.City,
            order.ShippingAddress.PostalCode,
            order.TotalAmount);
    }
}
