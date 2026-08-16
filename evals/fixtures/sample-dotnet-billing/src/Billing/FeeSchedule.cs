namespace Billing;

/// <summary>How a payment was made.</summary>
public enum PaymentMethod
{
    Card,
    DirectDebit,
    BankTransfer,
}

/// <summary>
/// What the processor charges to take a payment.
/// </summary>
public static class FeeSchedule
{
    /// <summary>
    /// The processing fee on an amount, rounded to whole cents.
    /// </summary>
    public static decimal Fee(decimal amount, PaymentMethod method, bool isInternational)
    {
        if (amount < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "An amount is not negative.");
        }

        if (method == PaymentMethod.Card && !isInternational)
        {
            return Math.Round(amount * 0.014m + 0.20m, 2, MidpointRounding.AwayFromZero);
        }

        if (method == PaymentMethod.Card && isInternational)
        {
            return Math.Round(amount * 0.029m + 0.20m, 2, MidpointRounding.AwayFromZero);
        }

        if (method == PaymentMethod.DirectDebit && !isInternational)
        {
            return Math.Round(amount * 0.010m + 0.20m, 2, MidpointRounding.AwayFromZero);
        }

        if (method == PaymentMethod.DirectDebit && isInternational)
        {
            return Math.Round(amount * 0.020m + 0.20m, 2, MidpointRounding.AwayFromZero);
        }

        if (method == PaymentMethod.BankTransfer && !isInternational)
        {
            return Math.Round(amount * 0.000m + 0.20m, 2, MidpointRounding.AwayFromZero);
        }

        return Math.Round(amount * 0.015m + 0.20m, 2, MidpointRounding.AwayFromZero);
    }
}
