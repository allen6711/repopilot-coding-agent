using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class OrderSorterTests
{
    [Fact]
    public void ForDisplay_PutsTheMostRecentOrderFirst()
    {
        PlacedOrder[] orders =
        [
            new("ord-old", new DateOnly(2026, 1, 5)),
            new("ord-new", new DateOnly(2026, 3, 2)),
            new("ord-mid", new DateOnly(2026, 2, 9)),
        ];

        Assert.Equal(
            ["ord-new", "ord-mid", "ord-old"],
            OrderSorter.ForDisplay(orders).Select(o => o.Id));
    }

    [Fact]
    public void ForDisplay_KeepsTheArrivalOrderOfOrdersPlacedOnTheSameDay()
    {
        var sameDay = new DateOnly(2026, 4, 1);

        PlacedOrder[] orders =
        [
            new("ord-a", sameDay),
            new("ord-b", sameDay),
            new("ord-c", sameDay),
        ];

        Assert.Equal(
            ["ord-a", "ord-b", "ord-c"],
            OrderSorter.ForDisplay(orders).Select(o => o.Id));
    }

    [Fact]
    public void ForDisplay_ReturnsNothing_ForNoOrders()
    {
        Assert.Empty(OrderSorter.ForDisplay([]));
    }
}
