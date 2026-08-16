namespace Billing;

/// <summary>
/// Works out when an account is next billed.
/// <para>
/// An account is billed on the same day of the month every month. When that day
/// does not exist in the next month — the 31st in a 30-day month, the 30th in
/// February — the charge falls on the last day of that month instead, and the
/// anchor day is not lost: the month after that returns to the anchor.
/// </para>
/// </summary>
public static class BillingCycle
{
    /// <summary>
    /// The next billing date after <paramref name="current"/>.
    /// </summary>
    /// <param name="current">The date just billed.</param>
    /// <param name="anchorDay">The day of the month the account is billed on, 1 to 31.</param>
    public static DateOnly Next(DateOnly current, int anchorDay)
    {
        if (anchorDay is < 1 or > 31)
        {
            throw new ArgumentOutOfRangeException(
                nameof(anchorDay), anchorDay, "A billing anchor is a day of the month.");
        }

        var next = current.AddMonths(1);
        var daysInMonth = DateTime.DaysInMonth(next.Year, next.Month);

        return new DateOnly(next.Year, next.Month, Math.Min(anchorDay, daysInMonth));
    }
}
