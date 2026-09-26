namespace Orders;

/// <summary>
/// Checks a shipping address before it is stored.
/// <para>
/// The carrier integration accepts UK-style outward codes only: one or two
/// letters, then one or two digits, optionally followed by a single letter —
/// for example <c>CB1</c>, <c>SW1A</c>, or <c>M60</c>. Anything else is rejected
/// here rather than at hand-off, where the failure is a shipment that never
/// leaves the depot.
/// </para>
/// </summary>
public static class AddressValidator
{
    /// <summary>
    /// Validates <paramref name="address"/>, throwing when it may not be stored.
    /// </summary>
    public static void Validate(Address address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (string.IsNullOrWhiteSpace(address.City))
        {
            throw new ArgumentException("An address needs a city.", nameof(address));
        }

        if (string.IsNullOrWhiteSpace(address.PostalCode))
        {
            throw new ArgumentException("An address needs a postal code.", nameof(address));
        }
    }
}
