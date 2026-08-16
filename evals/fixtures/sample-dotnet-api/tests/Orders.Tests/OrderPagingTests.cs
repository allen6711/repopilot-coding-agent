using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class OrderPagingTests
{
    [Fact]
    public void Resolve_UsesTheDefaults_WhenNothingIsAskedFor()
    {
        var page = OrderPaging.Resolve(null, null);

        Assert.Equal(1, page.Number);
        Assert.Equal(OrderPaging.DefaultSize, page.Size);
    }

    [Fact]
    public void Resolve_FloorsAPageNumberBelowOne()
    {
        Assert.Equal(1, OrderPaging.Resolve(0, null).Number);
    }

    [Fact]
    public void Resolve_HonoursASizeWithinTheCap()
    {
        Assert.Equal(50, OrderPaging.Resolve(1, 50).Size);
    }

    [Fact]
    public void Resolve_CapsAnOversizedPage()
    {
        Assert.Equal(OrderPaging.MaxSize, OrderPaging.Resolve(1, 500).Size);
    }
}
