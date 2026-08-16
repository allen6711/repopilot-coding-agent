namespace Orders;

/// <summary>
/// Bulk order lookups for the orders list screen.
/// </summary>
public sealed class OrderQueryService(IOrderRepository repository)
{
    private readonly IOrderRepository _repository = repository;

    /// <summary>
    /// Looks up several orders at once.
    /// <para>
    /// The list screen renders one row per requested id, in the order requested,
    /// showing a placeholder where the order could not be found. So the result is
    /// positional: it has exactly as many entries as <paramref name="orderIds"/>,
    /// and entry <c>i</c> is the order for id <c>i</c> or <c>null</c>.
    /// </para>
    /// </summary>
    public IReadOnlyList<Order?> FindMany(IReadOnlyList<string> orderIds)
    {
        ArgumentNullException.ThrowIfNull(orderIds);

        var found = new List<Order?>();

        foreach (var id in orderIds)
        {
            var order = _repository.Find(id);

            if (order is not null)
            {
                found.Add(order);
            }
        }

        return found;
    }
}
