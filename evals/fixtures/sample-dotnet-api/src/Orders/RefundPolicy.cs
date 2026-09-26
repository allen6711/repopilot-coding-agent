namespace Orders;

/// <summary>
/// Decides whether an order can still be refunded.
/// <para>
/// The refund window is 30 days from delivery, counted inclusively: a request on
/// day 30 is inside the window and a request on day 31 is outside it. A request
/// on the delivery day itself — day 0 — is inside the window. An order that was
/// never delivered cannot be refunded through this path at all.
/// </para>
/// </summary>
public static class RefundPolicy
{
    /// <summary>Length of the refund window, in days.</summary>
    public const int WindowDays = 30;

    /// <summary>
    /// Whether a refund may still be requested.
    /// </summary>
    /// <param name="deliveredOn">Delivery date, or null when never delivered.</param>
    /// <param name="requestedOn">The date the refund is being requested.</param>
    public static bool IsRefundable(DateOnly? deliveredOn, DateOnly requestedOn)
    {
        if (deliveredOn is null)
        {
            return false;
        }

        var elapsed = requestedOn.DayNumber - deliveredOn.Value.DayNumber;

        return elapsed >= 0 && elapsed <= WindowDays;
    }
}
