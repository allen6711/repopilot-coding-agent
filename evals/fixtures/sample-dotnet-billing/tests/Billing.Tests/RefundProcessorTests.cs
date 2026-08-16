using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class RefundProcessorTests
{
    [Fact]
    public void Refund_ReturnsTheWholeCharge_WhenTheWholeChargeIsAskedFor()
    {
        var processor = new RefundProcessor(100m);

        Assert.Equal(100m, processor.Refund(100m));
        Assert.Equal(100m, processor.Refunded);
    }

    [Fact]
    public void Refund_RefundsPartOfTheCharge()
    {
        var processor = new RefundProcessor(100m);

        Assert.Equal(30m, processor.Refund(30m));
        Assert.Equal(30m, processor.Refunded);
    }

    [Fact]
    public void Refund_AllowsSeveralPartsUpToTheCharge()
    {
        var processor = new RefundProcessor(100m);

        processor.Refund(30m);
        processor.Refund(70m);

        Assert.Equal(100m, processor.Refunded);
    }

    [Fact]
    public void Refund_RefusesToGoBeyondTheCharge()
    {
        var processor = new RefundProcessor(100m);

        processor.Refund(100m);

        Assert.Throws<InvalidOperationException>(() => processor.Refund(1m));
    }

    [Fact]
    public void Refund_RejectsANonPositiveRequest()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RefundProcessor(100m).Refund(0m));
    }
}
