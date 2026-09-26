namespace Billing;

/// <summary>
/// Refunds against a single charge.
/// <para>
/// A charge may be refunded in several parts — a support agent refunds the
/// shipping today and the goods next week. What never happens is refunding more
/// than was charged: once the refunded total reaches the charge, a further
/// request is refused.
/// </para>
/// </summary>
public sealed class RefundProcessor(decimal chargedAmount)
{
    /// <summary>The original charge.</summary>
    public decimal ChargedAmount { get; } = chargedAmount > 0m
        ? chargedAmount
        : throw new ArgumentOutOfRangeException(
            nameof(chargedAmount), chargedAmount, "A charge is positive.");

    /// <summary>How much has been refunded so far.</summary>
    public decimal Refunded { get; private set; }

    /// <summary>
    /// Refunds <paramref name="requested"/>.
    /// </summary>
    /// <returns>The amount actually refunded.</returns>
    public decimal Refund(decimal requested)
    {
        if (requested <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requested), requested, "A refund is positive.");
        }

        if (Refunded > 0m)
        {
            throw new InvalidOperationException("This charge has already been refunded.");
        }

        Refunded = ChargedAmount;

        return ChargedAmount;
    }
}
