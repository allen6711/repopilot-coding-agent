namespace Billing;

/// <summary>
/// Splits a monthly subscription charge across the days a subscription was
/// actually active.
/// </summary>
public static class ProrationCalculator
{
    /// <summary>
    /// The share of <paramref name="monthlyCharge"/> owed for an active span.
    /// <para>
    /// The span is inclusive of both ends: a subscription active from the 1st to
    /// the 30th of a 30-day month was active for the whole month and is charged
    /// in full. A subscription active for a single day is charged one day.
    /// </para>
    /// </summary>
    public static decimal Prorate(
        decimal monthlyCharge, DateOnly activeFrom, DateOnly activeTo, int daysInMonth)
    {
        if (daysInMonth < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(daysInMonth), daysInMonth, "A month has at least one day.");
        }

        if (activeTo < activeFrom)
        {
            throw new ArgumentException("The active span ends before it starts.", nameof(activeTo));
        }

        var activeDays = activeTo.DayNumber - activeFrom.DayNumber;

        return Math.Round(
            monthlyCharge * activeDays / daysInMonth, 2, MidpointRounding.AwayFromZero);
    }
}
