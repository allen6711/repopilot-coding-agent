using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class OrderProjectionTests
{
    [Fact]
    public void ForDetail_CarriesTheShippingAddress()
    {
        var order = new Order("ord-1", "Ada", new Address("Cambridge", "CB1"), 42m);

        var view = OrderProjection.ForDetail(order);

        Assert.Equal("ord-1", view.Id);
        Assert.Equal("Cambridge", view.ShippingCity);
        Assert.Equal("CB1", view.ShippingPostalCode);
        Assert.Equal(42m, view.TotalAmount);
    }

    [Fact]
    public void ForDetail_LeavesShippingNull_WhenThereIsNoAddress()
    {
        var view = OrderProjection.ForDetail(new Order("ord-2", "Grace", null, 17.50m));

        Assert.Null(view.ShippingCity);
        Assert.Null(view.ShippingPostalCode);
    }

    [Fact]
    public void ForList_ProjectsEveryOrderInOrder()
    {
        Order[] orders =
        [
            new("ord-1", "Ada", new Address("Cambridge", "CB1"), 42m),
            new("ord-2", "Grace", null, 17.50m),
        ];

        var views = OrderProjection.ForList(orders);

        Assert.Equal(["ord-1", "ord-2"], views.Select(v => v.Id));
        Assert.Equal("Cambridge", views[0].ShippingCity);
        Assert.Null(views[1].ShippingCity);
    }

    [Fact]
    public void ForList_AgreesWithForDetail()
    {
        var order = new Order("ord-3", "Katherine", new Address("Hampton", "TW12"), 8m);

        Assert.Equal(OrderProjection.ForDetail(order), OrderProjection.ForList([order])[0]);
    }
}
