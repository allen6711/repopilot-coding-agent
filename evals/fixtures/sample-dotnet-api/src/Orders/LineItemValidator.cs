namespace Orders;

/// <summary>
/// Checks a line item before it is accepted onto an order.
/// <para>
/// A line needs a SKU, and its quantity must be between 1 and 999 inclusive —
/// the warehouse cannot pick a zero-unit line, and anything above 999 is a
/// wholesale order that goes through a different channel.
/// </para>
/// </summary>
public static class LineItemValidator
{
    /// <summary>Smallest quantity a line may carry.</summary>
    public const int MinQuantity = 1;

    /// <summary>Largest quantity a line may carry.</summary>
    public const int MaxQuantity = 999;

    /// <summary>
    /// Validates <paramref name="item"/>, throwing when it may not be accepted.
    /// </summary>
    public static void Validate(LineItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.Sku))
        {
            throw new ArgumentException("A line item needs a SKU.", nameof(item));
        }
    }
}
