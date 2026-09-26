namespace Billing;

/// <summary>
/// Decides whether a charge fits inside an account's credit limit.
/// <para>
/// A charge is allowed when the balance after it would be at or below the limit.
/// Landing exactly on the limit is allowed — the limit is the largest permitted
/// balance, not the first forbidden one. An account with no limit set has no
/// ceiling.
/// </para>
/// </summary>
public static class CreditLimit
{
    /// <summary>
    /// Whether a charge may go through.
    /// </summary>
    /// <param name="currentBalance">What the account already owes.</param>
    /// <param name="charge">The charge being considered.</param>
    /// <param name="limit">The account's credit limit, or null when uncapped.</param>
    public static bool Allows(decimal currentBalance, decimal charge, decimal? limit)
    {
        if (charge <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(charge), charge, "A charge is positive.");
        }

        if (limit is null)
        {
            return true;
        }

        return currentBalance + charge <= limit.Value;
    }
}
