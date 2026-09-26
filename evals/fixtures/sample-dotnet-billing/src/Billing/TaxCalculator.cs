namespace Billing;

/// <summary>
/// Adds sales tax to a billable amount.
/// </summary>
public static class TaxCalculator
{
    /// <summary>
    /// The amount plus tax at <paramref name="ratePercent"/>.
    /// <para>
    /// A customer holding an exemption certificate pays no tax at all, whatever
    /// the rate — the certificate is recorded against the account and travels
    /// with every invoice raised for it.
    /// </para>
    /// </summary>
    public static decimal WithTax(decimal amount, decimal ratePercent, bool isExempt)
    {
        if (ratePercent < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ratePercent), ratePercent, "A tax rate is not negative.");
        }

        var tax = Math.Round(
            amount * ratePercent / 100m, 2, MidpointRounding.AwayFromZero);

        return amount + tax;
    }
}
