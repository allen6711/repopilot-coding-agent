using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class OrderTotalsTests
{
    [Fact]
    public void Sum_IsZero_ForAnOrderWithNoLines()
    {
        Assert.Equal(0m, OrderTotals.Sum([]));
    }

    [Fact]
    public void Sum_CountsASingleLine()
    {
        Assert.Equal(20m, OrderTotals.Sum([new LineItem("sku-1", 2, 10m)]));
    }

    [Fact]
    public void Sum_CountsEveryLine()
    {
        LineItem[] items =
        [
            new("sku-1", 2, 10m),
            new("sku-2", 1, 5.50m),
            new("sku-3", 3, 2m),
        ];

        Assert.Equal(31.50m, OrderTotals.Sum(items));
    }
}
