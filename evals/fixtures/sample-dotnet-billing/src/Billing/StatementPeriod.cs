namespace Billing;

/// <summary>
/// The span of days one statement covers.
/// <para>
/// A statement period is inclusive at both ends. A monthly statement for June
/// runs from 1 June to 30 June and covers 30 days; a transaction dated 30 June
/// appears on it, and one dated 1 July does not.
/// </para>
/// </summary>
/// <param name="Start">First day covered.</param>
/// <param name="End">Last day covered.</param>
public sealed record StatementPeriod(DateOnly Start, DateOnly End)
{
    /// <summary>How many days the period covers.</summary>
    public int DayCount => End.DayNumber - Start.DayNumber;

    /// <summary>Whether a transaction on <paramref name="date"/> appears on this statement.</summary>
    public bool Contains(DateOnly date) => date >= Start && date < End;
}
