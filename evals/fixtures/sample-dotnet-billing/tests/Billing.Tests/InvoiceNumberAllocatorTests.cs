using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class InvoiceNumberAllocatorTests
{
    [Fact]
    public void Next_StartsAtOne()
    {
        Assert.Equal("INV-2026-00001", new InvoiceNumberAllocator().Next(2026));
    }

    [Fact]
    public void Next_RunsSequentiallyWithinAYear()
    {
        var allocator = new InvoiceNumberAllocator();

        Assert.Equal("INV-2026-00001", allocator.Next(2026));
        Assert.Equal("INV-2026-00002", allocator.Next(2026));
        Assert.Equal("INV-2026-00003", allocator.Next(2026));
    }

    [Fact]
    public void Next_RestartsAtOneInANewYear()
    {
        var allocator = new InvoiceNumberAllocator();

        allocator.Next(2026);
        allocator.Next(2026);

        Assert.Equal("INV-2027-00001", allocator.Next(2027));
    }

    [Fact]
    public void Next_NeverRepeatsANumberWithinAYear()
    {
        var allocator = new InvoiceNumberAllocator();

        var allocated = Enumerable.Range(0, 50).Select(_ => allocator.Next(2026)).ToList();

        Assert.Equal(allocated.Count, allocated.Distinct().Count());
    }
}
