using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class LineItemValidatorTests
{
    [Fact]
    public void Validate_AcceptsAWellFormedLine()
    {
        LineItemValidator.Validate(new LineItem("sku-1", 1, 9.99m));
    }

    [Fact]
    public void Validate_RejectsAMissingSku()
    {
        Assert.Throws<ArgumentException>(
            () => LineItemValidator.Validate(new LineItem("  ", 1, 9.99m)));
    }

    [Fact]
    public void Validate_RejectsAQuantityBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LineItemValidator.Validate(new LineItem("sku-1", 0, 9.99m)));
    }

    [Fact]
    public void Validate_RejectsAQuantityAboveTheMaximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LineItemValidator.Validate(new LineItem("sku-1", 1000, 9.99m)));
    }

    [Fact]
    public void Validate_AcceptsTheMaximumQuantity()
    {
        LineItemValidator.Validate(new LineItem("sku-1", 999, 9.99m));
    }
}
