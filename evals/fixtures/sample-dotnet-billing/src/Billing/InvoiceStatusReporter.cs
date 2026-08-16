namespace Billing;

/// <summary>Where an invoice stands against what has been received for it.</summary>
public enum InvoiceStatus
{
    /// <summary>Nothing has been received.</summary>
    Unpaid,

    /// <summary>Something has been received, but less than the invoiced amount.</summary>
    PartiallyPaid,

    /// <summary>Exactly the invoiced amount has been received.</summary>
    Paid,

    /// <summary>More than the invoiced amount has been received.</summary>
    Overpaid,
}

/// <summary>
/// Reports an invoice's payment status for the collections screen.
/// </summary>
public static class InvoiceStatusReporter
{
    /// <summary>The status of an invoice given what has been received for it.</summary>
    public static InvoiceStatus Status(decimal invoiced, decimal received)
    {
        if (invoiced < 0m || received < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(invoiced), "Invoiced and received amounts are not negative.");
        }

        return received >= invoiced ? InvoiceStatus.Paid : InvoiceStatus.Unpaid;
    }
}
