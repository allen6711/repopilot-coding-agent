namespace Billing;

/// <summary>
/// Checks a stored card's expiry before it is charged.
/// <para>
/// A card expires at the end of its expiry month, so a card expiring in the
/// current month is still good today. The month is 1 to 12 and the year is a
/// four-digit year no more than twenty ahead of today — a card claiming to
/// expire further out than that is a data-entry error, not a long-lived card.
/// </para>
/// </summary>
public static class CardExpiryValidator
{
    /// <summary>How far ahead a plausible expiry year may sit.</summary>
    public const int MaxYearsAhead = 20;

    /// <summary>Validates an expiry, throwing when the card may not be charged.</summary>
    public static void Validate(int month, int year, DateOnly today)
    {
        if (year < today.Year || (year == today.Year && month < today.Month))
        {
            throw new ArgumentException($"The card expired in {month:D2}/{year}.", nameof(month));
        }
    }
}
