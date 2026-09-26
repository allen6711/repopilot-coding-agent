namespace Billing;

/// <summary>
/// Checks a billable amount before it reaches the ledger.
/// <para>
/// The ledger holds amounts in whole cents, so an amount carries at most two
/// decimal places; anything finer would be silently rounded and the invoice
/// would not add up. Amounts are not negative — a refund is recorded as a
/// refund, not as a negative charge — and a single line does not exceed one
/// million, which is the manual-review threshold.
/// </para>
/// </summary>
public static class AmountValidator
{
    /// <summary>Largest amount a single line may carry.</summary>
    public const decimal MaxAmount = 1_000_000m;

    /// <summary>Decimal places the ledger holds.</summary>
    public const int Scale = 2;

    /// <summary>Validates an amount, throwing when it may not be stored.</summary>
    public static void Validate(decimal amount)
    {
        if (amount < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "A billable amount is not negative.");
        }
    }
}
