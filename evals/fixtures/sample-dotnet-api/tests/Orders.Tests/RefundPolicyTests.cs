using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class RefundPolicyTests
{
    private static readonly DateOnly Delivered = new(2026, 5, 1);

    [Fact]
    public void IsRefundable_IsFalse_ForAnOrderThatWasNeverDelivered()
    {
        Assert.False(RefundPolicy.IsRefundable(null, Delivered));
    }

    [Fact]
    public void IsRefundable_IsTrue_InsideTheWindow()
    {
        Assert.True(RefundPolicy.IsRefundable(Delivered, Delivered.AddDays(10)));
    }
}
