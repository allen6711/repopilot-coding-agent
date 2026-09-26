namespace Orders;

/// <summary>
/// Renders order identifiers for anything a customer sees.
/// <para>
/// The customer-facing form is <c>ORD-</c> followed by the numeric part padded to
/// eight digits — order 42 prints as <c>ORD-00000042</c>. Support staff read
/// these out over the phone, so the width is fixed and the prefix is upper case.
/// </para>
/// </summary>
public static class OrderIdFormatter
{
    /// <summary>Digits in the numeric part of a formatted identifier.</summary>
    public const int Digits = 8;

    /// <summary>Formats <paramref name="orderNumber"/> for display.</summary>
    public static string Format(long orderNumber)
    {
        if (orderNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderNumber), orderNumber, "Order numbers start at 1.");
        }

        return "ORD-" + orderNumber.ToString(new string('0', Digits));
    }
}
