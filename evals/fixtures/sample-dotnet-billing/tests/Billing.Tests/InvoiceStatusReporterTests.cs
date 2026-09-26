using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class InvoiceStatusReporterTests
{
    [Fact]
    public void Status_IsUnpaid_WhenNothingHasBeenReceived()
    {
        Assert.Equal(InvoiceStatus.Unpaid, InvoiceStatusReporter.Status(100m, 0m));
    }

    [Fact]
    public void Status_IsPaid_WhenExactlyTheInvoicedAmountArrives()
    {
        Assert.Equal(InvoiceStatus.Paid, InvoiceStatusReporter.Status(100m, 100m));
    }

    [Fact]
    public void Status_IsPartiallyPaid_WhenSomethingButNotEverythingArrives()
    {
        Assert.Equal(InvoiceStatus.PartiallyPaid, InvoiceStatusReporter.Status(100m, 40m));
    }

    [Fact]
    public void Status_IsOverpaid_WhenMoreThanTheInvoicedAmountArrives()
    {
        Assert.Equal(InvoiceStatus.Overpaid, InvoiceStatusReporter.Status(100m, 120m));
    }
}
