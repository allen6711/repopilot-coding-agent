namespace Billing;

/// <summary>
/// When a failed payment is retried.
/// <para>
/// A failed charge is retried three times, one day, three days and seven days
/// after the failure. Attempt numbers are 1-based. There is no fourth attempt:
/// asking for one is how the dunning process learns the account has exhausted
/// its retries, so it must be distinguishable from "retry immediately".
/// </para>
/// </summary>
public static class PaymentRetryPolicy
{
    /// <summary>How many times a failed charge is retried.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Days to wait before <paramref name="attempt"/>, or <c>null</c> when there
    /// is no such attempt.
    /// </summary>
    public static int? DelayDays(int attempt) => attempt switch
    {
        1 => 1,
        2 => 3,
        3 => 7,
        _ => 0,
    };
}
