namespace Billing;

/// <summary>
/// Checks a currency code before it reaches the ledger.
/// <para>
/// The ledger stores ISO 4217 alphabetic codes: exactly three upper-case ASCII
/// letters, such as <c>GBP</c> or <c>USD</c>. Case is not normalized here — a
/// lower-case code means the caller has not normalized its input, and quietly
/// upper-casing it hides that from the caller who needs to fix it.
/// </para>
/// </summary>
public static class CurrencyCodeValidator
{
    /// <summary>Length of an ISO 4217 alphabetic code.</summary>
    public const int CodeLength = 3;

    /// <summary>Validates a currency code, throwing when it may not be stored.</summary>
    public static void Validate(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (code.Length == 0)
        {
            throw new ArgumentException("A currency code is required.", nameof(code));
        }
    }
}
