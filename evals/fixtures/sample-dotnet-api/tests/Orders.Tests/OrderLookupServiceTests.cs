using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class OrderLookupServiceTests
{
    private sealed class StubRepository(Order? order) : IOrderRepository
    {
        public Order? Find(string orderId) => order;
    }

    [Fact]
    public void Lookup_ReturnsNull_WhenOrderDoesNotExist()
    {
        var service = new OrderLookupService(new StubRepository(null));

        Assert.Null(service.Lookup("missing"));
    }

    [Fact]
    public void Lookup_ProjectsShippingAddress_WhenPresent()
    {
        var order = new Order("ord-1", "Ada", new Address("Cambridge", "CB1"), 42.00m);
        var service = new OrderLookupService(new StubRepository(order));

        var view = service.Lookup("ord-1");

        Assert.NotNull(view);
        Assert.Equal("Cambridge", view!.ShippingCity);
        Assert.Equal("CB1", view.ShippingPostalCode);
    }

    [Fact]
    public void Lookup_ReturnsOrderWithNullShipping_WhenCustomerHasNoAddressOnFile()
    {
        var order = new Order("ord-2", "Grace", ShippingAddress: null, 17.50m);
        var service = new OrderLookupService(new StubRepository(order));

        var view = service.Lookup("ord-2");

        Assert.NotNull(view);
        Assert.Equal("ord-2", view!.Id);
        Assert.Null(view.ShippingCity);
        Assert.Null(view.ShippingPostalCode);
    }
}
