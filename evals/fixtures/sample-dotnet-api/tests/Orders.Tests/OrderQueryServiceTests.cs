using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class OrderQueryServiceTests
{
    private sealed class MapRepository(Dictionary<string, Order> orders) : IOrderRepository
    {
        public Order? Find(string orderId) =>
            orders.TryGetValue(orderId, out var order) ? order : null;
    }

    private static OrderQueryService Service(params Order[] orders) =>
        new(new MapRepository(orders.ToDictionary(o => o.Id)));

    [Fact]
    public void FindMany_ReturnsNothing_ForAnEmptyRequest()
    {
        Assert.Empty(Service().FindMany([]));
    }

    [Fact]
    public void FindMany_ReturnsEveryFoundOrder()
    {
        var first = new Order("ord-1", "Ada", null, 10m);
        var second = new Order("ord-2", "Grace", null, 20m);

        var found = Service(first, second).FindMany(["ord-1", "ord-2"]);

        Assert.Equal(["ord-1", "ord-2"], found.Select(o => o!.Id));
    }

    [Fact]
    public void FindMany_KeepsAPositionForEveryRequestedId()
    {
        var first = new Order("ord-1", "Ada", null, 10m);
        var third = new Order("ord-3", "Grace", null, 30m);

        var found = Service(first, third).FindMany(["ord-1", "ord-missing", "ord-3"]);

        Assert.Equal(3, found.Count);
        Assert.Equal("ord-1", found[0]!.Id);
        Assert.Null(found[1]);
        Assert.Equal("ord-3", found[2]!.Id);
    }
}
