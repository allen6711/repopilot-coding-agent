using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class OrderStatusMachineTests
{
    [Fact]
    public void Placed_MayBePaid()
    {
        Assert.True(OrderStatusMachine.CanTransition(OrderStatus.Placed, OrderStatus.Paid));
    }

    [Fact]
    public void Paid_MayBeShipped()
    {
        Assert.True(OrderStatusMachine.CanTransition(OrderStatus.Paid, OrderStatus.Shipped));
    }

    [Fact]
    public void Placed_MayBeCancelled()
    {
        Assert.True(OrderStatusMachine.CanTransition(OrderStatus.Placed, OrderStatus.Cancelled));
    }

    [Fact]
    public void Shipped_MayNotBeCancelled()
    {
        Assert.False(OrderStatusMachine.CanTransition(OrderStatus.Shipped, OrderStatus.Cancelled));
    }

    [Fact]
    public void Cancelled_MayNotBeShipped()
    {
        Assert.False(OrderStatusMachine.CanTransition(OrderStatus.Cancelled, OrderStatus.Shipped));
    }
}
