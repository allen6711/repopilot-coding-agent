using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class OrderIdFormatterTests
{
    [Fact]
    public void Format_PadsTheNumericPart()
    {
        Assert.Equal("ORD-0000042", OrderIdFormatter.Format(42));
    }

    [Fact]
    public void Format_LeavesAFullWidthNumberAlone()
    {
        Assert.Equal("ORD-12345678", OrderIdFormatter.Format(12_345_678));
    }

    [Fact]
    public void Format_RejectsAnOrderNumberBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderIdFormatter.Format(0));
    }
}
